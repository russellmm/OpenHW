using Tmds.DBus.Protocol;

namespace OpenHW.App.Tray;

/// <summary>A tray right-click entry; <see cref="Separator"/> draws a divider.</summary>
public sealed record TrayMenuItem(string Label, Action? Clicked)
{
    public bool IsSeparator { get; private init; }
    public static TrayMenuItem Separator { get; } = new("", null) { IsSeparator = true };

    /// <summary>Makes this a radio item; returns whether it is the selected one.</summary>
    public Func<bool>? Checked { get; init; }

    /// <summary>Hides the item while this returns false.</summary>
    public Func<bool>? Visible { get; init; }

    /// <summary>Label that changes with state; <see cref="Label"/> is used when null.</summary>
    public Func<string>? DynamicLabel { get; init; }

    /// <summary>False for information lines: shown, not clickable.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Makes this a submenu of these items.</summary>
    public IReadOnlyList<TrayMenuItem>? Children { get; init; }
}

/// <summary>
/// Minimal com.canonical.dbusmenu: the items under root id 0, numbered depth-first from 1 (a submenu's items follow
/// it). This is what the panel renders when a tray icon is right-clicked. Items are fixed; their labels, radio state
/// and visibility are read on each request, and <see cref="Invalidate"/> tells the panel to re-read them.
/// </summary>
public sealed class DBusMenu : DBusObject
{
    public const string MenuPath = "/MenuBar";

    // _nodes[id - 1] is item id; _children[id] its child ids (0 = the root). Ids follow position, so one item
    // instance (the shared Separator) can appear in several places.
    private readonly List<TrayMenuItem> _nodes = [];
    private readonly Dictionary<int, List<int>> _children = [];

    public DBusMenu(IReadOnlyList<TrayMenuItem> items) => Number(items, 0);

    private void Number(IReadOnlyList<TrayMenuItem> items, int parent)
    {
        _children[parent] = [];
        foreach (var item in items)
        {
            _nodes.Add(item);
            var id = _nodes.Count;
            _children[parent].Add(id);
            if (item.Children is { } children) Number(children, id);
        }
    }
    private uint _revision = 1;
    private uint Revision => _revision;

    private static readonly string[] Properties = ["Version", "TextDirection", "Status", "IconThemePath"];

    public override string Path => MenuPath;
    protected override string Interface => "com.canonical.dbusmenu";
    protected override IReadOnlyList<string> PropertyNames => Properties;

    protected override bool HandleMethod(MethodContext context, string member)
    {
        var reader = context.Request.GetBodyReader();
        switch (member)
        {
            case "GetLayout":
            {
                var parentId = reader.ReadInt32();
                Reply(context, "u(ia{sv}av)", (ref MessageWriter writer) =>
                {
                    writer.WriteUInt32(Revision);
                    if (parentId == 0) WriteRoot(ref writer);
                    else WriteNode(ref writer, parentId);
                });
                return true;
            }
            case "GetGroupProperties":
            {
                var ids = reader.ReadArrayOfInt32();
                if (ids.Length == 0) ids = Enumerable.Range(1, _nodes.Count).ToArray();
                Reply(context, "a(ia{sv})", (ref MessageWriter writer) =>
                {
                    var array = writer.WriteArrayStart(DBusType.Struct);
                    foreach (var id in ids.Where(IsItem))
                    {
                        writer.WriteStructureStart();
                        writer.WriteInt32(id);
                        WriteItemProperties(ref writer, id);
                    }
                    writer.WriteArrayEnd(array);
                });
                return true;
            }
            case "Event":
            {
                var id = reader.ReadInt32();
                var eventId = reader.ReadString();
                if (eventId == "clicked" && IsItem(id)) _nodes[id - 1].Clicked?.Invoke();
                ReplyEmpty(context);
                return true;
            }
            case "EventGroup":
            {
                var end = reader.ReadArrayStart(DBusType.Struct);
                while (reader.HasNext(end))
                {
                    reader.AlignStruct();
                    var id = reader.ReadInt32();
                    var eventId = reader.ReadString();
                    reader.ReadVariantValue();
                    reader.ReadUInt32();
                    if (eventId == "clicked" && IsItem(id)) _nodes[id - 1].Clicked?.Invoke();
                }
                using var writer = context.CreateReplyWriter("ai");
                writer.WriteArray(Array.Empty<int>());
                context.Reply(writer.CreateMessage());
                return true;
            }
            case "AboutToShow":
            {
                using var writer = context.CreateReplyWriter("b");
                writer.WriteBool(false);
                context.Reply(writer.CreateMessage());
                return true;
            }
            case "AboutToShowGroup":
            {
                using var writer = context.CreateReplyWriter("aiai");
                writer.WriteArray(Array.Empty<int>());
                writer.WriteArray(Array.Empty<int>());
                context.Reply(writer.CreateMessage());
                return true;
            }
            default:
                return false;
        }
    }

    private bool IsItem(int id) => id >= 1 && id <= _nodes.Count;

    /// <summary>State behind a label, radio or visibility changed: the panel re-reads the layout.</summary>
    public void Invalidate(Connection connection)
    {
        var revision = Interlocked.Increment(ref _revision);
        using var writer = connection.GetMessageWriter();
        writer.WriteSignalHeader(destination: null, path: MenuPath, @interface: "com.canonical.dbusmenu", member: "LayoutUpdated", signature: "ui");
        writer.WriteUInt32(revision);
        writer.WriteInt32(0);
        connection.TrySendMessage(writer.CreateMessage());
    }

    private void WriteRoot(ref MessageWriter writer)
    {
        writer.WriteStructureStart();
        writer.WriteInt32(0);
        var props = writer.WriteDictionaryStart();
        writer.WriteDictionaryEntryStart();
        writer.WriteString("children-display");
        writer.WriteVariantString("submenu");
        writer.WriteDictionaryEnd(props);

        WriteChildren(ref writer, 0);
    }

    private void WriteNode(ref MessageWriter writer, int id)
    {
        writer.WriteStructureStart();
        writer.WriteInt32(id);
        WriteItemProperties(ref writer, id);
        WriteChildren(ref writer, id);
    }

    private void WriteChildren(ref MessageWriter writer, int id)
    {
        var children = writer.WriteArrayStart(DBusType.Variant);
        foreach (var child in _children.GetValueOrDefault(id) ?? [])
        {
            writer.WriteSignature("(ia{sv}av)");
            WriteNode(ref writer, child);
        }
        writer.WriteArrayEnd(children);
    }

    private void WriteItemProperties(ref MessageWriter writer, int id)
    {
        var props = writer.WriteDictionaryStart();
        if (IsItem(id))
        {
            var item = _nodes[id - 1];
            if (item.Children is not null)
            {
                writer.WriteDictionaryEntryStart();
                writer.WriteString("children-display");
                writer.WriteVariantString("submenu");
            }
            if (item.IsSeparator)
            {
                writer.WriteDictionaryEntryStart();
                writer.WriteString("type");
                writer.WriteVariantString("separator");
            }
            else
            {
                writer.WriteDictionaryEntryStart();
                writer.WriteString("label");
                writer.WriteVariantString(item.DynamicLabel?.Invoke() ?? item.Label);
                if (item.Checked is { } isChecked)
                {
                    writer.WriteDictionaryEntryStart();
                    writer.WriteString("toggle-type");
                    writer.WriteVariantString("radio");
                    writer.WriteDictionaryEntryStart();
                    writer.WriteString("toggle-state");
                    writer.WriteSignature("i");
                    writer.WriteInt32(isChecked() ? 1 : 0);
                }
            }
            if (!item.Enabled)
            {
                writer.WriteDictionaryEntryStart();
                writer.WriteString("enabled");
                writer.WriteVariantBool(false);
            }
            if (item.Visible is { } visible)
            {
                writer.WriteDictionaryEntryStart();
                writer.WriteString("visible");
                writer.WriteVariantBool(visible());
            }
        }
        writer.WriteDictionaryEnd(props);
    }

    protected override void WriteProperty(ref MessageWriter writer, string name)
    {
        switch (name)
        {
            case "Version": writer.WriteVariantUInt32(3); break;
            case "TextDirection": writer.WriteVariantString("ltr"); break;
            case "Status": writer.WriteVariantString("normal"); break;
            case "IconThemePath":
                writer.WriteSignature("as");
                writer.WriteArray(Array.Empty<string>());
                break;
        }
    }

    protected override string IntrospectXml => """
        <interface name="com.canonical.dbusmenu">
          <property name="Version" type="u" access="read"/>
          <property name="TextDirection" type="s" access="read"/>
          <property name="Status" type="s" access="read"/>
          <property name="IconThemePath" type="as" access="read"/>
          <method name="GetLayout">
            <arg type="i" name="parentId" direction="in"/><arg type="i" name="recursionDepth" direction="in"/>
            <arg type="as" name="propertyNames" direction="in"/>
            <arg type="u" name="revision" direction="out"/><arg type="(ia{sv}av)" name="layout" direction="out"/>
          </method>
          <method name="GetGroupProperties">
            <arg type="ai" name="ids" direction="in"/><arg type="as" name="propertyNames" direction="in"/>
            <arg type="a(ia{sv})" name="properties" direction="out"/>
          </method>
          <method name="Event">
            <arg type="i" name="id" direction="in"/><arg type="s" name="eventId" direction="in"/>
            <arg type="v" name="data" direction="in"/><arg type="u" name="timestamp" direction="in"/>
          </method>
          <method name="EventGroup">
            <arg type="a(isvu)" name="events" direction="in"/><arg type="ai" name="idErrors" direction="out"/>
          </method>
          <method name="AboutToShow">
            <arg type="i" name="id" direction="in"/><arg type="b" name="needUpdate" direction="out"/>
          </method>
          <method name="AboutToShowGroup">
            <arg type="ai" name="ids" direction="in"/>
            <arg type="ai" name="updatesNeeded" direction="out"/><arg type="ai" name="idErrors" direction="out"/>
          </method>
          <signal name="LayoutUpdated"><arg type="u" name="revision"/><arg type="i" name="parent"/></signal>
          <signal name="ItemsPropertiesUpdated">
            <arg type="a(ia{sv})" name="updatedProps"/><arg type="a(ias)" name="removedProps"/>
          </signal>
        </interface>
        """;
}
