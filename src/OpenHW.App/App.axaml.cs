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
            _window = new MainWindow(_hub, _power, new WindowCommands(ShowCpuFrequency, ShowPowerLimits, ShowSettings, () => lifetime.Shutdown())
            {
                TrayStateOf = TrayStateOf, AddToTray = AddToTray, RemoveFromTray = RemoveFromTray,
            });
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
                var icon = await StatusNotifierItem.CreateAsync($"openhw-tray-{index++}", "OpenHW",
                    [.. SensorInfoItems(config), TrayMenuItem.Separator, .. IconItems(config), TrayMenuItem.Separator, .. _trayMenu]);
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

    private TrayState TrayStateOf(string sensorId)
    {
        if (_settings.TrayIcons.Any(t => ResolveTrayId(t) == sensorId)) return new TrayState(true, false, null);
        var item = _hub.Inventory.FirstOrDefault(i => i.Id == sensorId);
        if (item?.Unit == "flag") return new TrayState(false, false, "Yes/No readings can't be shown as a tray value");
        if (_settings.TrayIcons.Count >= AppSettings.MaxTrayIcons)
            return new TrayState(false, false, $"The tray already has {AppSettings.MaxTrayIcons} icons, the most OpenHW shows");
        return new TrayState(false, true, null);
    }

    // Same rules as Settings → Tray icons: the next colour not in use, saved and applied at once.
    private void AddToTray(string sensorId)
    {
        if (!TrayStateOf(sensorId).CanAdd) return;
        var used = _settings.TrayIcons.Select(t => t.Colour).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var colour = AppSettings.Colours.Select(c => c.Hex).FirstOrDefault(h => !used.Contains(h)) ?? AppSettings.Colours[0].Hex;
        _settings.TrayIcons.Add(new TraySensor { SensorId = sensorId, Colour = colour });
        _settings.Save();
        ApplySettings();
        _settingsWindow?.RefreshTrayRows();
    }

    private void RemoveFromTray(string sensorId)
    {
        if (_settings.TrayIcons.RemoveAll(t => ResolveTrayId(t) == sensorId) == 0) return;
        _settings.Save();
        ApplySettings();
        _settingsWindow?.RefreshTrayRows();
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

    private const int InfoLines = 8;

    // Each icon's info lines, computed once per poll on the UI thread and read by the menu on the D-Bus thread:
    // replaced whole, never mutated.
    private volatile IReadOnlyDictionary<TraySensor, IReadOnlyList<string>> _trayInfo = new Dictionary<TraySensor, IReadOnlyList<string>>();

    private IReadOnlyList<string> CachedInfo(TraySensor config) =>
        _trayInfo.TryGetValue(config, out var lines) ? lines : [];

    // The first lines of each icon's own menu describe its sensor. GNOME's tray extension never shows tooltips, so
    // this is where the expanded information can be seen there; the same text is the tooltip on hosts that show one.
    private IEnumerable<TrayMenuItem> SensorInfoItems(TraySensor config) =>
        Enumerable.Range(0, InfoLines).Select(i => new TrayMenuItem("", null)
        {
            Enabled = false,
            DynamicLabel = () => CachedInfo(config).ElementAtOrDefault(i) ?? "",
            Visible = () => i < CachedInfo(config).Count,
        });

    // This icon's own settings, in its own menu: take it off the tray, or give it another tile colour. Menu events
    // arrive on the D-Bus thread of the icon being changed, so the work is posted to the UI thread.
    private IEnumerable<TrayMenuItem> IconItems(TraySensor config) =>
    [
        new("Remove from tray", () => Dispatcher.UIThread.Post(() => RemoveTrayIcon(config))),
        new("Colour", null)
        {
            Children = AppSettings.Colours.Select(c => new TrayMenuItem(c.Name, () => Dispatcher.UIThread.Post(() => SetTrayColour(config, c.Hex)))
            {
                Checked = () => config.Colour.Equals(c.Hex, StringComparison.OrdinalIgnoreCase),
            }).ToList(),
        },
    ];

    private void RemoveTrayIcon(TraySensor config)
    {
        if (!_settings.TrayIcons.Remove(config)) return;
        _settings.Save();
        ApplySettings();
        _settingsWindow?.RefreshTrayRows();
        if (_settings.TrayIcons.Count == 0) ShowWindow(); // with no icons left, the window is the only way back in
    }

    private void SetTrayColour(TraySensor config, string hex)
    {
        config.Colour = hex;
        _settings.Save();
        UpdateTray(); // repaints the tile and refreshes the menu's tick
        _settingsWindow?.RefreshTrayRows();
    }

    /// <summary>Name and reading, min/max/average, the device it belongs to, then what it measures (wrapped).</summary>
    private IReadOnlyList<string> SensorInfo(TraySensor config, IReadOnlyList<SensorGroupView> groups)
    {
        var id = ResolveTrayId(config);
        var item = id is null ? null : _hub.Inventory.FirstOrDefault(i => i.Id == id);
        if (item is null) return ["Sensor not present now"];
        var name = SensorPresentation.DisplayName(item);
        var stats = _hub.Get(item.Id);
        string F(double? v) => v is { } x ? SensorPresentation.Format(SensorPresentation.Scale(item, x), item.Unit) : "–";
        var lines = new List<string>
        {
            $"{name}: {F(stats.Current)}",
            $"Min {F(stats.Minimum)}  ·  Max {F(stats.Maximum)}  ·  Avg {F(stats.Average)}",
        };
        var group = groups.FirstOrDefault(g => Flatten(g.Sensors).Any(v => v.Id == item.Id))?.Title;
        if (group is not null) lines.Add(group);
        var explanation = SensorExplanations.Explain(item, name);
        if (explanation.Length > 0) lines.AddRange(SensorPresentation.Wrap(explanation, 52).Split('\n'));
        return lines.Take(InfoLines).ToList();
    }

    private static IEnumerable<SensorView> Flatten(IEnumerable<SensorView> views) =>
        views.SelectMany(v => Flatten(v.Children).Prepend(v));

    private void UpdateTray()
    {
        var ids = _settings.TrayIcons.Select(ResolveTrayId).OfType<string>().ToArray();
        _hub.SetPinned(ids); // tray sensors stay live even inside a collapsed group
        var items = _hub.Inventory.ToDictionary(i => i.Id, StringComparer.Ordinal);
        var groups = _trayIcons.Count == 0 ? [] : SensorPresentation.Build(_hub.Inventory, _hub.Children, _hub.Machine);
        _trayInfo = _trayIcons.ToDictionary(t => t.Config, t => SensorInfo(t.Config, groups));
        foreach (var (config, icon) in _trayIcons)
        {
            var id = ResolveTrayId(config);
            var item = id is null ? null : items.GetValueOrDefault(id);
            var value = item is null ? null : _hub.Get(item.Id).Current is { } v ? SensorPresentation.Scale(item, v) : (double?)null;
            var info = CachedInfo(config);
            icon.Update(TrayIconRenderer.Render(TrayText(value), config.Colour), "OpenHW · " + info[0], string.Join("\n", info.Skip(1)));
            icon.RefreshMenu(); // the info lines change every poll
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
