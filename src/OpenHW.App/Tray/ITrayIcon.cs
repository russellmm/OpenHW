namespace OpenHW.App.Tray;

/// <summary>
/// One sensor's tray icon, whatever shows it: <see cref="StatusNotifierItem"/> on Linux (D-Bus, GNOME's AppIndicators)
/// or <see cref="Win32TrayIcon"/> on Windows (Shell_NotifyIcon).
/// </summary>
public interface ITrayIcon : IDisposable
{
    /// <summary>A left click (or Enter on the focused icon).</summary>
    event Action? Activated;

    /// <summary>The tray host came back and the icons must be registered again (Linux; Windows re-adds them itself).</summary>
    event Action? WatcherReturned;

    /// <summary>Replaces the picture and tooltip.</summary>
    /// <param name="iconName">Linux only: a wide SVG tile's path, or "".</param>
    void Update(IconPixmap[] icon, string title, string tooltip, string iconName = "");

    Task RegisterAgainAsync();

    /// <summary>The menu's items or their number changed.</summary>
    void RefreshMenu();

    /// <summary>These items' labels changed (ids count depth-first from 1).</summary>
    void RefreshMenuItems(IEnumerable<int> ids);
}
