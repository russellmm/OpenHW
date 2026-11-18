using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace OpenHW.App.Tray;

/// <summary>
/// A Windows notification-area icon (Shell_NotifyIcon, version 4): one per tray sensor, with a native tooltip (HWiNFO's
/// hover text), a native right-click menu built from the same <see cref="TrayMenuItem"/>s as on Linux, and balloon
/// notifications (shown by Windows 11 as toasts). All icons share one hidden window that receives their messages;
/// it lives on the UI thread, whose message loop (Avalonia's) delivers them. Icons are told apart by their uID, which
/// is also how Windows remembers each one (shown on the taskbar or not, and where the user dragged it): the uID comes
/// from the sensor (<see cref="IdFor"/>, as HWiNFO does), so adding, removing or recolouring another icon never
/// moves this one.
/// Explorer restarting (TaskbarCreated) re-adds every icon.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class Win32TrayIcon : ITrayIcon
{
    private const uint CallbackMessage = 0x8000 + 0x51; // WM_APP + n
    private const uint NimAdd = 0, NimModify = 1, NimDelete = 2, NimSetVersion = 4;
    private const uint NifMessage = 1, NifIcon = 2, NifTip = 4, NifInfo = 0x10, NifShowTip = 0x80;
    private const uint NotifyIconVersion4 = 4;
    private const int WmContextMenu = 0x7B, NinSelect = 0x400, NinKeySelect = 0x401, NinBalloonUserClick = 0x405;

    private static nint _window;
    private static uint _taskbarCreated;
    private static WndProc? _wndProc; // kept alive: the window calls it
    private static readonly Dictionary<uint, Win32TrayIcon> Icons = [];

    private readonly uint _id;
    private IReadOnlyList<TrayMenuItem> _menu;
    private nint _icon;
    private byte[]? _pixels;
    private string _tip = "OpenHW";
    private bool _added, _disposed;
    private Action? _balloonClicked;

    public event Action? Activated;
    public event Action? WatcherReturned { add { } remove { } }

    private Win32TrayIcon(uint id, IReadOnlyList<TrayMenuItem> menu)
    {
        _id = id;
        _menu = menu;
    }

    /// <summary>The icon size the taskbar draws (16 px at 100 %, 24 at 150 %, 32 at 200 %): tiles are rendered at exactly this.</summary>
    public static int IconSize
    {
        get
        {
            EnsureWindow();
            var dpi = _window != 0 ? GetDpiForWindow(_window) : 96;
            var size = GetSystemMetricsForDpi(49 /* SM_CXSMICON */, dpi == 0 ? 96 : dpi);
            return size > 0 ? size : 16;
        }
    }

    /// <summary>The icon's uID (<see cref="IdFor"/>).</summary>
    public uint Id => _id;

    /// <summary>
    /// A stable uID for a tray icon's key (its sensor, and which copy of it): a 16-bit hash, because version 4
    /// notifications carry the uID in 16 bits. <paramref name="taken"/> = uIDs already given out; a clash moves on to
    /// the next free one.
    /// </summary>
    public static uint IdFor(string key, ISet<uint> taken)
    {
        uint hash = 2166136261;
        foreach (var c in key) { hash ^= c; hash *= 16777619; }
        var id = 0x100 + (hash ^ (hash >> 16)) % 0xFE00; // 0x100..0xFEFF: clear of the 1..n slot ids older builds used
        while (taken.Contains(id)) id = id >= 0xFEFF ? 0x100 : id + 1;
        return id;
    }

    /// <summary>Must run on the UI thread. <paramref name="id"/> comes from <see cref="IdFor"/>.</summary>
    public static Win32TrayIcon Create(uint id, IReadOnlyList<TrayMenuItem> menu)
    {
        EnsureWindow();
        if (Icons.Remove(id, out var old)) old.Dispose();
        var icon = new Win32TrayIcon(id, menu);
        Icons[id] = icon;
        return icon;
    }

    public void Update(IconPixmap[] icon, string title, string tooltip, string iconName = "")
    {
        if (_disposed || icon.Length == 0) return;
        var tip = Tip(title, tooltip);
        var best = icon.OrderBy(p => Math.Abs(p.Width - IconSize)).First();
        var changed = _pixels is null || !_pixels.AsSpan().SequenceEqual(best.Argb);
        if (!changed && tip == _tip && _added) return;
        if (changed)
        {
            var next = CreateIcon(best);
            if (next == 0) return;
            if (_icon != 0) DestroyIcon(_icon);
            _icon = next;
            _pixels = best.Argb;
        }
        _tip = tip;
        Send(_added ? NimModify : NimAdd);
    }

    /// <summary>"OpenHW · CPU: 45 °C" over the min/max line and the group: what fits Windows' 127 characters.</summary>
    private static string Tip(string title, string tooltip)
    {
        var text = tooltip.Length > 0 ? title + "\n" + tooltip : title;
        if (text.Length <= 127) return text;
        var cut = text[..127];
        var line = cut.LastIndexOf('\n');
        return line > title.Length ? cut[..line] : cut[..126] + "…";
    }

    public Task RegisterAgainAsync()
    {
        if (!_disposed && _icon != 0) Send(NimAdd);
        return Task.CompletedTask;
    }

    // The menu is built when it opens, so labels, ticks and visibility are always current.
    public void RefreshMenu() { }

    /// <summary>A kept icon's menu, rebuilt for its (possibly new) settings entry.</summary>
    public void SetMenu(IReadOnlyList<TrayMenuItem> menu) => _menu = menu;
    public void RefreshMenuItems(IEnumerable<int> ids) { }

    /// <summary>A balloon on this icon (a toast on Windows 11). <paramref name="clicked"/> runs if the user clicks it.</summary>
    public void ShowBalloon(string title, string text, bool warning, Action? clicked)
    {
        if (_disposed || !_added) return;
        _balloonClicked = clicked;
        var data = Data(NifInfo);
        data.szInfoTitle = Truncate(title, 63);
        data.szInfo = Truncate(text, 255);
        data.dwInfoFlags = warning ? 2u : 1u; // NIIF_WARNING / NIIF_INFO
        Shell_NotifyIconW(NimModify, ref data);
    }

    /// <summary>Any icon that is in the tray now, for notifications.</summary>
    public static Win32TrayIcon? Any() => Icons.Values.FirstOrDefault(i => i._added && !i._disposed);

    private void Send(uint message)
    {
        var data = Data(NifMessage | NifIcon | NifTip | NifShowTip);
        if (!Shell_NotifyIconW(message, ref data))
        {
            // NIM_ADD fails if it's still there (e.g. after an Explorer restart race): modify instead, and the other way round.
            if (!Shell_NotifyIconW(message == NimAdd ? NimModify : NimAdd, ref data)) return;
        }
        _added = true;
        data.uVersionOrTimeout = NotifyIconVersion4;
        Shell_NotifyIconW(NimSetVersion, ref data);
    }

    private NotifyIconData Data(uint flags) => new()
    {
        cbSize = (uint)Marshal.SizeOf<NotifyIconData>(),
        hWnd = _window,
        uID = _id,
        uFlags = flags,
        uCallbackMessage = CallbackMessage,
        hIcon = _icon,
        szTip = _tip,
        szInfo = "",
        szInfoTitle = "",
    };

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";

    private void ShowMenu()
    {
        var commands = new Dictionary<int, TrayMenuItem>();
        var menu = BuildMenu(_menu, commands);
        GetCursorPos(out var point);
        SetForegroundWindow(_window); // so the menu closes when the user clicks elsewhere
        var chosen = TrackPopupMenuEx(menu, 0x100 /* TPM_RETURNCMD */ | 0x2 /* TPM_RIGHTBUTTON */, point.X, point.Y, _window, 0);
        PostMessageW(_window, 0, 0, 0); // WM_NULL: the documented fix for the menu's second-click problem
        DestroyMenu(menu);
        if (chosen > 0 && commands.TryGetValue(chosen, out var item)) item.Clicked?.Invoke();
    }

    private static nint BuildMenu(IReadOnlyList<TrayMenuItem> items, Dictionary<int, TrayMenuItem> commands)
    {
        var menu = CreatePopupMenu();
        var lastWasSeparator = true; // no separator first, none twice in a row (hidden items between them)
        foreach (var item in items)
        {
            if (item.Visible is { } visible && !visible()) continue;
            if (item.IsSeparator)
            {
                if (!lastWasSeparator) AppendMenuW(menu, 0x800 /* MF_SEPARATOR */, 0, null);
                lastWasSeparator = true;
                continue;
            }
            var label = (item.DynamicLabel?.Invoke() ?? item.Label).Replace("&", "&&");
            if (label.Length == 0 && item.Children is null) continue; // an empty information line
            lastWasSeparator = false;
            if (item.Children is { } children)
            {
                AppendMenuW(menu, 0x10 /* MF_POPUP */, BuildMenu(children, commands), label);
                continue;
            }
            uint flags = 0;
            if (!item.Enabled || item.Clicked is null) flags |= 0x1; // MF_GRAYED
            if (item.Checked is { } isChecked && isChecked()) flags |= 0x8; // MF_CHECKED
            var id = commands.Count + 1;
            commands[id] = item;
            AppendMenuW(menu, flags, id, label);
        }
        return menu;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_added)
        {
            var data = Data(0);
            Shell_NotifyIconW(NimDelete, ref data);
        }
        if (_icon != 0) DestroyIcon(_icon);
        _icon = 0;
        if (Icons.TryGetValue(_id, out var registered) && registered == this) Icons.Remove(_id);
    }

    // ---- the shared message window ----

    private static void EnsureWindow()
    {
        if (_window != 0) return;
        _wndProc = Procedure;
        var instance = GetModuleHandleW(null);
        var windowClass = new WndClassEx
        {
            cbSize = (uint)Marshal.SizeOf<WndClassEx>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = instance,
            lpszClassName = "OpenHW.Tray",
        };
        RegisterClassExW(ref windowClass);
        // A hidden top-level window, not a message-only one: only top-level windows hear TaskbarCreated.
        _window = CreateWindowExW(0, "OpenHW.Tray", "OpenHW tray", 0, 0, 0, 0, 0, 0, 0, instance, 0);
        _taskbarCreated = RegisterWindowMessageW("TaskbarCreated");
    }

    private static nint Procedure(nint hwnd, uint message, nint wParam, nint lParam)
    {
        try
        {
            if (message == CallbackMessage)
            {
                var notification = (int)(lParam.ToInt64() & 0xFFFF);
                var id = (uint)((lParam.ToInt64() >> 16) & 0xFFFF);
                if (!Icons.TryGetValue(id, out var icon)) return 0;
                switch (notification)
                {
                    case NinSelect or NinKeySelect: icon.Activated?.Invoke(); break;
                    case WmContextMenu: icon.ShowMenu(); break;
                    case NinBalloonUserClick:
                        var clicked = icon._balloonClicked;
                        icon._balloonClicked = null;
                        clicked?.Invoke();
                        break;
                }
                return 0;
            }
            if (message == _taskbarCreated && _taskbarCreated != 0)
            {
                foreach (var icon in Icons.Values.OrderBy(i => i._id).ToList())
                    if (!icon._disposed && icon._icon != 0) icon.Send(NimAdd);
                return 0;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Console.Error.WriteLine($"openhw: tray: {ex.Message}"); // never let an exception unwind into Windows
        }
        return DefWindowProcW(hwnd, message, wParam, lParam);
    }

    // ---- icons ----

    // A 32-bit icon from the tile's pixels: SNI's ARGB (network order) becomes a top-down BGRA DIB; the mask is unused
    // when the colour bitmap has alpha, but CreateIconIndirect still wants one.
    private static nint CreateIcon(IconPixmap pixmap)
    {
        var header = new BitmapInfoHeader
        {
            biSize = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
            biWidth = pixmap.Width,
            biHeight = -pixmap.Height, // top-down
            biPlanes = 1,
            biBitCount = 32,
        };
        var color = CreateDIBSection(0, ref header, 0, out var bits, 0, 0);
        if (color == 0) return 0;
        var bgra = new byte[pixmap.Width * pixmap.Height * 4];
        for (var i = 0; i < bgra.Length; i += 4)
        {
            bgra[i] = pixmap.Argb[i + 3];     // B
            bgra[i + 1] = pixmap.Argb[i + 2]; // G
            bgra[i + 2] = pixmap.Argb[i + 1]; // R
            bgra[i + 3] = pixmap.Argb[i];     // A
        }
        Marshal.Copy(bgra, 0, bits, bgra.Length);
        var mask = CreateBitmap(pixmap.Width, pixmap.Height, 1, 1, 0);
        var info = new IconInfo { fIcon = true, hbmMask = mask, hbmColor = color };
        var icon = CreateIconIndirect(ref info);
        DeleteObject(color);
        DeleteObject(mask);
        return icon;
    }

    // ---- interop ----

    private delegate nint WndProc(nint hwnd, uint message, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint cbSize;
        public nint hWnd;
        public uint uID, uFlags, uCallbackMessage;
        public nint hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState, dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uVersionOrTimeout;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public nint hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClassEx
    {
        public uint cbSize, style;
        public nint lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public nint hInstance, hIcon, hCursor, hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public nint hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint biSize;
        public int biWidth, biHeight;
        public ushort biPlanes, biBitCount;
        public uint biCompression, biSizeImage;
        public int biXPelsPerMeter, biYPelsPerMeter;
        public uint biClrUsed, biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IconInfo
    {
        [MarshalAs(UnmanagedType.Bool)] public bool fIcon;
        public uint xHotspot, yHotspot;
        public nint hbmMask, hbmColor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point { public int X, Y; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern bool Shell_NotifyIconW(uint message, ref NotifyIconData data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern ushort RegisterClassExW(ref WndClassEx windowClass);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint CreateWindowExW(uint exStyle, string className, string title, uint style, int x, int y, int width, int height,
        nint parent, nint menu, nint instance, nint param);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessageW(string name);
    [DllImport("user32.dll")] private static extern nint DefWindowProcW(nint hwnd, uint message, nint wParam, nint lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandleW(string? name);
    [DllImport("user32.dll")] private static extern nint CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenuW(nint menu, uint flags, nint id, string? text);
    [DllImport("user32.dll")] private static extern int TrackPopupMenuEx(nint menu, uint flags, int x, int y, nint hwnd, nint parameters);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(nint menu);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool PostMessageW(nint hwnd, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern int GetSystemMetricsForDpi(int index, uint dpi);
    [DllImport("user32.dll")] private static extern nint CreateIconIndirect(ref IconInfo info);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(nint icon);
    [DllImport("gdi32.dll")] private static extern nint CreateDIBSection(nint hdc, ref BitmapInfoHeader header, uint usage, out nint bits, nint section, uint offset);
    [DllImport("gdi32.dll")] private static extern nint CreateBitmap(int width, int height, uint planes, uint bitCount, nint bits);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint handle);
}
