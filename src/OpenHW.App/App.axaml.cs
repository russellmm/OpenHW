using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using OpenHW.Hardware;
using OpenHW.App.Power;
using OpenHW.App.Sensors;
using OpenHW.App.Settings;
using OpenHW.App.Tray;

namespace OpenHW.App;

public sealed class App : Application
{
    private readonly SensorHub _hub = new();
    private readonly PowerModeController _power = new(new CpuFrequencyLimits());
    private MainWindow? _window;
    private CpuFrequencyWindow? _cpuFrequency;
    private PowerLimitsWindow? _powerLimits;
    private readonly GpuPowerLimits _gpuPower = new();
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly List<(TraySensor Config, StatusNotifierItem Icon)> _trayIcons = [];
    private List<TrayMenuItem> _trayMenu = [];
    private SettingsWindow? _settingsWindow;
    private Timer? _poll;
    private int _sampling; // 1 while a poll is running: a slow driver read must not stack polls up
    private int _polls;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime lifetime)
        {
            // Closing the window keeps the tray icons running, as HWiNFO does; Exit in the tray menu quits.
            lifetime.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _hub.Sample(); // first discovery + sample before the window builds its tree
            _window = new MainWindow(_hub, _power, new WindowCommands(ShowCpuFrequency, ShowPowerLimits, ShowSettings, () => lifetime.Shutdown()));
            if (!Program.StartInTray) _window.Show(); // started at login with --tray: icons only until asked
            if (Program.Instance is { } instance) instance.ShowRequested += () => Dispatcher.UIThread.Post(ShowWindow);

            lifetime.Exit += (_, _) =>
            {
                _poll?.Dispose();
                foreach (var (_, icon) in _trayIcons) icon.Dispose();
                Program.Instance?.Dispose();
                _power.Dispose();
                _gpuPower.Dispose();
                _hub.Dispose();
            };

            _power.Changed += () => Dispatcher.UIThread.Post(PowerChanged);
            _ = _power.StartAsync();

            _hub.InventoryChanged += () => Dispatcher.UIThread.Post(() => _window?.RebuildTree());
            var period = TimeSpan.FromSeconds(_settings.PollSeconds); // 2 s by default, HWiNFO's polling period
            _poll = new Timer(_ => Poll(), null, period, period);
            BuildTrayMenu(lifetime);
            _ = RebuildTrayAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void BuildTrayMenu(IClassicDesktopStyleApplicationLifetime lifetime)
    {
        // power-profiles-daemon offers these three; ones it doesn't list on this machine stay hidden.
        var modes = new[] { "power-saver", "balanced", "performance" }.Select(profile =>
            new TrayMenuItem(PowerModeController.Label(profile), () => _ = _power.SetProfileAsync(profile))
            {
                Checked = () => _power.ActiveProfile == profile,
                Visible = () => _power.Profiles.Contains(profile),
            });
        _trayMenu =
        [
            new("Show Sensors", () => Dispatcher.UIThread.Post(ShowWindow)),
            TrayMenuItem.Separator,
            .. modes,
            new("", () => _ = _power.RestoreCapsAsync())
            {
                DynamicLabel = () => $"Remove unexpected CPU cap ({PowerModeController.Ghz(_power.UnexpectedCapKHz ?? 0)})",
                Visible = () => _power.UnexpectedCapKHz is not null,
            },
            new("CPU Frequency…", () => Dispatcher.UIThread.Post(ShowCpuFrequency)),
            new("Power Limits…", () => Dispatcher.UIThread.Post(ShowPowerLimits)),
            TrayMenuItem.Separator,
            new("Settings…", () => Dispatcher.UIThread.Post(ShowSettings)),
            new("Exit", () => Dispatcher.UIThread.Post(() => lifetime.Shutdown())),
        ];
    }

    /// <summary>One icon per configured tray sensor, in order. Each has its own bus connection and the same menu.</summary>
    private async Task RebuildTrayAsync()
    {
        foreach (var (_, icon) in _trayIcons) icon.Dispose();
        _trayIcons.Clear();
        var index = 0;
        foreach (var config in _settings.TrayIcons)
        {
            try
            {
                var icon = await StatusNotifierItem.CreateAsync($"openhw-tray-{index++}", "OpenHW", _trayMenu);
                icon.Activated += () => Dispatcher.UIThread.Post(ToggleWindow);
                _trayIcons.Add((config, icon));
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"openhw: tray unavailable ({ex.Message})");
                break;
            }
        }
        UpdateTray();
    }

    /// <summary>Settings changed: poll interval and tray icons take effect now.</summary>
    private void ApplySettings()
    {
        var period = TimeSpan.FromSeconds(_settings.PollSeconds);
        _poll?.Change(period, period);
        _ = RebuildTrayAsync();
    }

    // Runs on a timer thread: hardware reads stay off the UI thread; only the repaint is posted to it.
    private void Poll()
    {
        if (Interlocked.Exchange(ref _sampling, 1) == 1) return;
        try
        {
            _hub.Sample();
            // Another tool (or the power menu while OpenHW was closed) can change the ceilings; re-check every 10 s.
            if (++_polls % 5 == 0)
            {
                var before = (_power.UnexpectedCapKHz, _power.Cpu.LowestMaxKHz);
                _power.Refresh();
                if (before != (_power.UnexpectedCapKHz, _power.Cpu.LowestMaxKHz)) Dispatcher.UIThread.Post(PowerChanged);
            }
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

    private string? ResolveTrayId(TraySensor config) =>
        config.SensorId == AppSettings.CpuTemperature ? _hub.CpuTemperatureId() : config.SensorId;

    private void UpdateTray()
    {
        var ids = _settings.TrayIcons.Select(ResolveTrayId).OfType<string>().ToArray();
        _hub.SetPinned(ids); // tray sensors stay live even inside a collapsed group
        var items = _hub.Inventory.ToDictionary(i => i.Id, StringComparer.Ordinal);
        foreach (var (config, icon) in _trayIcons)
        {
            var id = ResolveTrayId(config);
            var item = id is null ? null : items.GetValueOrDefault(id);
            var value = item is null ? null : _hub.Get(item.Id).Current is { } v ? SensorPresentation.Scale(item, v) : (double?)null;
            var name = item is null ? "Sensor unavailable" : SensorPresentation.DisplayName(item);
            icon.Update(
                TrayIconRenderer.Render(TrayText(value), config.Colour),
                _hub.Machine.CpuModel,
                item is null ? name : $"{name}: {(value is null ? "–" : SensorPresentation.Format(value, item.Unit))}");
        }
    }

    /// <summary>A value short enough for a 16 px tile: 45, 250, 5.3k, 1.25.</summary>
    internal static string TrayText(double? value)
    {
        if (value is not { } v || !double.IsFinite(v)) return "--";
        var a = Math.Abs(v);
        return a >= 9999.5 ? $"{v / 1000:0}k"
            : a >= 999.5 ? $"{v / 1000:0.0}k"
            : a >= 9.95 ? $"{v:0}"
            : a >= 0.995 ? $"{v:0.0#}" // volts and small powers keep their decimals
            : $"{v:0.##}";
    }

    private void PowerChanged()
    {
        _window?.RefreshPower();
        _cpuFrequency?.RefreshFromController();
        foreach (var (_, icon) in _trayIcons) icon.RefreshMenu();
    }

    private void ShowSettings()
    {
        _settingsWindow ??= new SettingsWindow(_settings, _hub, ApplySettings);
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    private void ShowPowerLimits()
    {
        _powerLimits ??= new PowerLimitsWindow(_gpuPower, new CpuPowerLimits());
        _powerLimits.Show();
        _powerLimits.Activate();
    }

    private void ShowCpuFrequency()
    {
        _cpuFrequency ??= new CpuFrequencyWindow(_power);
        _cpuFrequency.Show();
        _cpuFrequency.Activate();
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
