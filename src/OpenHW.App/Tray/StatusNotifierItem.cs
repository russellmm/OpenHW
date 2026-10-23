using Tmds.DBus.Protocol;

namespace OpenHW.App.Tray;

/// <summary>One square ARGB32 image (network byte order, unpremultiplied), as the SNI spec's a(iiay) wants.</summary>
public sealed record IconPixmap(int Width, int Height, byte[] Argb);

/// <summary>
/// A StatusNotifierItem (KDE/freedesktop tray icon) whose picture we draw ourselves, so a sensor value can sit
/// on a coloured tile exactly like HWiNFO's tray icons. Avalonia's TrayIcon cannot do this (it takes an icon
/// file and no text), and the appindicator text label (XAyatanaLabel) cannot be coloured.
///
/// Each item owns its own session-bus connection: watchers identify items by bus name and every item lives at
/// /StatusNotifierItem, so one connection per icon is how several sensors get several icons.
/// </summary>
public sealed class StatusNotifierItem : DBusObject, IDisposable
{
    private const string ItemPath = "/StatusNotifierItem";
    private const string ItemInterface = "org.kde.StatusNotifierItem";
    private const string WatcherName = "org.kde.StatusNotifierWatcher";

    private static readonly string[] Properties =
    [
        "Category", "Id", "Title", "Status", "WindowId", "IconThemePath", "IconName", "IconPixmap",
        "OverlayIconName", "OverlayIconPixmap", "AttentionIconName", "AttentionIconPixmap", "AttentionMovieName",
        "ToolTip", "ItemIsMenu", "Menu",
    ];

    private readonly Connection _connection;
    private readonly string _id;
    private readonly DBusMenu _menu;
    private readonly object _gate = new();
    private IconPixmap[] _icon = [];
    private string _title;
    private string _tooltip = "";
    private IDisposable? _watcherMatch;

    /// <summary>Left click on the icon. Raised on the D-Bus thread.</summary>
    public event Action? Activated;

    private StatusNotifierItem(Connection connection, string id, string title, IReadOnlyList<TrayMenuItem> menu)
    {
        _connection = connection;
        _id = id;
        _title = title;
        _menu = new DBusMenu(menu);
    }

    public override string Path => ItemPath;
    protected override string Interface => ItemInterface;
    protected override IReadOnlyList<string> PropertyNames => Properties;

    public static async Task<StatusNotifierItem> CreateAsync(string id, string title, IReadOnlyList<TrayMenuItem> menu)
    {
        var connection = new Connection(Address.Session ?? throw new InvalidOperationException("No session bus"));
        await connection.ConnectAsync();

        var item = new StatusNotifierItem(connection, id, title, menu);
        connection.AddMethodHandler(item);
        connection.AddMethodHandler(item._menu);

        // The watcher lives in gnome-shell (ubuntu-appindicators); re-register whenever it (re)appears, e.g. after
        // a shell restart or the extension being toggled. A missing watcher at startup is not an error.
        item._watcherMatch = await connection.AddMatchAsync(
            new MatchRule
            {
                Type = MessageType.Signal,
                Sender = "org.freedesktop.DBus",
                Interface = "org.freedesktop.DBus",
                Member = "NameOwnerChanged",
            },
            static (Message message, object? _) =>
            {
                var reader = message.GetBodyReader();
                var name = reader.ReadString();
                reader.ReadString();
                return name == WatcherName && reader.ReadString().Length > 0;
            },
            (Exception? ex, bool watcherAppeared, object? state, object? _) =>
            {
                if (ex is null && watcherAppeared) _ = ((StatusNotifierItem)state!).RegisterAsync();
            },
            ObserverFlags.None,
            readerState: null,
            handlerState: item,
            emitOnCapturedContext: false);

        await item.RegisterAsync();
        return item;
    }

    /// <summary>Replace the picture and tooltip. Hosts only re-read on a signal, so unchanged values emit nothing.</summary>
    public void Update(IconPixmap[] icon, string title, string tooltip)
    {
        bool iconChanged, tooltipChanged;
        lock (_gate)
        {
            iconChanged = !SamePixels(_icon, icon);
            tooltipChanged = _title != title || _tooltip != tooltip;
            _icon = icon;
            _title = title;
            _tooltip = tooltip;
        }
        if (iconChanged) EmitSignal(_connection, ItemPath, ItemInterface, "NewIcon");
        if (tooltipChanged)
        {
            EmitSignal(_connection, ItemPath, ItemInterface, "NewTitle");
            EmitSignal(_connection, ItemPath, ItemInterface, "NewToolTip");
        }
    }

    private async Task RegisterAsync()
    {
        try
        {
            await _connection.CallMethodAsync(CreateRegisterMessage());
        }
        catch (DBusException ex)
        {
            Console.Error.WriteLine($"openhw: tray icon '{_id}' waiting for a StatusNotifierWatcher ({ex.ErrorName})");
        }
    }

    private MessageBuffer CreateRegisterMessage()
    {
        using var writer = _connection.GetMessageWriter();
        writer.WriteMethodCallHeader(destination: WatcherName, path: "/StatusNotifierWatcher",
                                     @interface: WatcherName, signature: "s", member: "RegisterStatusNotifierItem");
        writer.WriteString(_connection.UniqueName!);
        return writer.CreateMessage();
    }

    protected override bool HandleMethod(MethodContext context, string member)
    {
        switch (member)
        {
            case "Activate":
                ReplyEmpty(context);
                Activated?.Invoke();
                return true;
            case "SecondaryActivate":
            case "ContextMenu": // hosts show our dbusmenu themselves; nothing to do here
            case "Scroll":
                ReplyEmpty(context);
                return true;
            default:
                return false;
        }
    }

    protected override void WriteProperty(ref MessageWriter writer, string name)
    {
        lock (_gate)
        {
            switch (name)
            {
                case "Category": writer.WriteVariantString("Hardware"); break;
                case "Id": writer.WriteVariantString(_id); break;
                case "Title": writer.WriteVariantString(_title); break;
                case "Status": writer.WriteVariantString("Active"); break;
                case "WindowId": writer.WriteVariantInt32(0); break;
                case "ItemIsMenu": writer.WriteVariantBool(false); break;
                case "Menu": writer.WriteVariantObjectPath(DBusMenu.MenuPath); break;
                case "IconPixmap":
                    writer.WriteSignature("a(iiay)");
                    WritePixmaps(ref writer, _icon);
                    break;
                case "OverlayIconPixmap":
                case "AttentionIconPixmap":
                    writer.WriteSignature("a(iiay)");
                    WritePixmaps(ref writer, []);
                    break;
                case "ToolTip":
                    writer.WriteSignature("(sa(iiay)ss)");
                    writer.WriteStructureStart();
                    writer.WriteString("");
                    WritePixmaps(ref writer, []);
                    writer.WriteString(_title);
                    writer.WriteString(_tooltip);
                    break;
                default: writer.WriteVariantString(""); break; // icon names / theme path: unused, we send pixmaps
            }
        }
    }

    private static void WritePixmaps(ref MessageWriter writer, IconPixmap[] pixmaps)
    {
        var array = writer.WriteArrayStart(DBusType.Struct);
        foreach (var pixmap in pixmaps)
        {
            writer.WriteStructureStart();
            writer.WriteInt32(pixmap.Width);
            writer.WriteInt32(pixmap.Height);
            writer.WriteArray(pixmap.Argb);
        }
        writer.WriteArrayEnd(array);
    }

    private static bool SamePixels(IconPixmap[] a, IconPixmap[] b)
        => a.Length == b.Length && a.Zip(b).All(p => p.First.Argb.AsSpan().SequenceEqual(p.Second.Argb));

    protected override string IntrospectXml => """
        <interface name="org.kde.StatusNotifierItem">
          <property name="Category" type="s" access="read"/>
          <property name="Id" type="s" access="read"/>
          <property name="Title" type="s" access="read"/>
          <property name="Status" type="s" access="read"/>
          <property name="WindowId" type="i" access="read"/>
          <property name="IconThemePath" type="s" access="read"/>
          <property name="IconName" type="s" access="read"/>
          <property name="IconPixmap" type="a(iiay)" access="read"/>
          <property name="OverlayIconName" type="s" access="read"/>
          <property name="OverlayIconPixmap" type="a(iiay)" access="read"/>
          <property name="AttentionIconName" type="s" access="read"/>
          <property name="AttentionIconPixmap" type="a(iiay)" access="read"/>
          <property name="AttentionMovieName" type="s" access="read"/>
          <property name="ToolTip" type="(sa(iiay)ss)" access="read"/>
          <property name="ItemIsMenu" type="b" access="read"/>
          <property name="Menu" type="o" access="read"/>
          <method name="ContextMenu"><arg name="x" type="i" direction="in"/><arg name="y" type="i" direction="in"/></method>
          <method name="Activate"><arg name="x" type="i" direction="in"/><arg name="y" type="i" direction="in"/></method>
          <method name="SecondaryActivate"><arg name="x" type="i" direction="in"/><arg name="y" type="i" direction="in"/></method>
          <method name="Scroll"><arg name="delta" type="i" direction="in"/><arg name="orientation" type="s" direction="in"/></method>
          <signal name="NewTitle"/>
          <signal name="NewIcon"/>
          <signal name="NewAttentionIcon"/>
          <signal name="NewOverlayIcon"/>
          <signal name="NewToolTip"/>
          <signal name="NewStatus"><arg name="status" type="s"/></signal>
        </interface>
        """;

    public void Dispose()
    {
        _watcherMatch?.Dispose();
        _connection.Dispose(); // dropping the bus name is what removes the icon from the panel
    }
}
