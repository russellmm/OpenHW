using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using OpenHW.Hardware;
using OpenHW.App.Sensors;
using OpenHW.App.Tray;

namespace OpenHW.App;

public sealed class App : Application
{
    private const string TrayBackground = "#C0392B";
    private static readonly TimeSpan PollPeriod = TimeSpan.FromSeconds(2); // HWiNFO's default polling period

    private readonly SensorHub _hub = new();
    private MainWindow? _window;
    private StatusNotifierItem? _cpuTempIcon;
    private Timer? _poll;
    private int _sampling; // 1 while a poll is running: a slow driver read must not stack polls up

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime lifetime)
        {
            // Closing the window keeps the tray icons running, as HWiNFO does; Exit in the tray menu quits.
            lifetime.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _hub.Sample(); // first discovery + sample before the window builds its tree
            _window = new MainWindow(_hub);
            _window.Show();

            lifetime.Exit += (_, _) =>
            {
                _poll?.Dispose();
                _cpuTempIcon?.Dispose();
                _hub.Dispose();
            };

            _hub.InventoryChanged += () => Dispatcher.UIThread.Post(() => _window?.RebuildTree());
            _poll = new Timer(_ => Poll(), null, PollPeriod, PollPeriod);
            _ = StartTrayAsync(lifetime);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private async Task StartTrayAsync(IClassicDesktopStyleApplicationLifetime lifetime)
    {
        var menu = new[]
        {
            new TrayMenuItem("Show Sensors", () => Dispatcher.UIThread.Post(ShowWindow)),
            TrayMenuItem.Separator,
            new TrayMenuItem("Exit", () => Dispatcher.UIThread.Post(() => lifetime.Shutdown())),
        };

        try
        {
            _cpuTempIcon = await StatusNotifierItem.CreateAsync("openhw-cpu-temp", "OpenHW", menu);
            _cpuTempIcon.Activated += () => Dispatcher.UIThread.Post(ToggleWindow);
            UpdateTray();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"openhw: tray unavailable ({ex.Message})");
        }
    }

    // Runs on a timer thread: hardware reads stay off the UI thread; only the repaint is posted to it.
    private void Poll()
    {
        if (Interlocked.Exchange(ref _sampling, 1) == 1) return;
        try
        {
            _hub.Sample();
        }
        finally
        {
            Volatile.Write(ref _sampling, 0);
        }
        Dispatcher.UIThread.Post(() =>
        {
            if (_window is { IsVisible: true }) _window.RefreshValues();
            UpdateTray();
        });
    }

    private void UpdateTray()
    {
        if (_cpuTempIcon is null) return;
        var id = _hub.CpuTemperatureId();
        _hub.SetPinned(id is null ? [] : [id]); // the tray keeps its sensor live even inside a collapsed group
        var temp = id is null ? null : _hub.Get(id).Current;
        _cpuTempIcon.Update(
            TrayIconRenderer.Render(temp is { } t ? Math.Round(t).ToString("0") : "--", TrayBackground),
            _hub.Machine.CpuModel,
            temp is { } c ? $"CPU temperature: {c:0.0} °C" : "CPU temperature unavailable");
    }

    private void ShowWindow()
    {
        if (_window is null) return;
        _window.RefreshValues(); // hidden windows skip repaints; catch up before showing
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    private void ToggleWindow()
    {
        if (_window is { IsVisible: true }) _window.Hide();
        else ShowWindow();
    }
}
