using System.Runtime.Versioning;
using Avalonia.Threading;
using OpenHW.App.Tray;

namespace OpenHW.App.Alerts;

/// <summary>Desktop notifications: <see cref="DesktopNotifier"/> on Linux, <see cref="WindowsNotifier"/> on Windows.</summary>
public interface IDesktopNotifier : IDisposable
{
    /// <param name="actions">Buttons as (key, label); "default" is a click on the notification. Their handler gets the key.</param>
    Task NotifyAsync(string key, string summary, string body, bool critical,
        IReadOnlyList<(string Key, string Label)>? actions = null, Action<string>? onAction = null);
}

/// <summary>
/// Notifications on Windows as notification-area balloons, which Windows 10/11 show as toasts (and keep in the
/// notification centre) without an AppUserModelID or the Windows App SDK. Shown on a tray icon; with none in the tray,
/// on a small "HW" icon of its own that goes away a minute later. Buttons aren't available this way: a click on the
/// toast counts as "default".
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsNotifier : IDesktopNotifier
{
    private const uint OwnSlot = 0xFFF0; // the notifier's own icon: outside the sensors' uIDs (Win32TrayIcon.IdFor)
    private Win32TrayIcon? _own;
    private DispatcherTimer? _removeOwn;

    public Task NotifyAsync(string key, string summary, string body, bool critical,
        IReadOnlyList<(string Key, string Label)>? actions = null, Action<string>? onAction = null) =>
        Dispatcher.UIThread.InvokeAsync(() =>
        {
            try
            {
                var icon = Win32TrayIcon.Any() is { } shown && shown != _own ? shown : OwnIcon();
                icon.ShowBalloon(summary, body, critical, onAction is null ? null : () => onAction("default"));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Console.Error.WriteLine($"openhw: notification failed ({ex.Message})");
            }
        }).GetTask();

    private Win32TrayIcon OwnIcon()
    {
        if (_own is null)
        {
            _own = Win32TrayIcon.Create(OwnSlot, []);
            _own.Update(TrayIconRenderer.Render("HW", "#2E86DE", "", [Win32TrayIcon.IconSize]), "OpenHW", "");
        }
        _removeOwn?.Stop();
        _removeOwn = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _removeOwn.Tick += (_, _) =>
        {
            _removeOwn?.Stop();
            _own?.Dispose();
            _own = null;
        };
        _removeOwn.Start();
        return _own;
    }

    public void Dispose()
    {
        _removeOwn?.Stop();
        _own?.Dispose();
    }
}
