using System.Globalization;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using OpenHW.App.Alerts;
using OpenHW.App.SensorWindow;
using OpenHW.Hardware;

namespace OpenHW.App.Power;

/// <summary>
/// Power → Fan Control…: one screen, as OpenFan's Home — <b>Controls</b> (a card per fan: the BMC's seven zones and every
/// NVIDIA fan, with a Curve box and the curve it runs) and <b>Curves</b> (graph / flat / mix cards, each with its live
/// output; graph cards show the sensor's reading as a dot on a small preview of the curve). A curve on BMC fans runs
/// on the BMC when it reads only BMC sensors (written once, no flash writes afterwards), otherwise OpenHW drives the
/// fan and every duty change is a flash write (warned before, counted per day). NVIDIA fans are driven through NVML:
/// nothing is stored, so no budget. ⋮ on a BMC fan: its raw BMC curve (Edit…), Restore original.
/// </summary>
public sealed class FanControlWindow : Window
{
    private static readonly IBrush CardBg = new SolidColorBrush(Color.Parse("#1E2429"));
    private static readonly IBrush CardBorder = new SolidColorBrush(Color.Parse("#2C363D"));
    private static readonly IBrush PageBg = new SolidColorBrush(Color.Parse("#14191D"));
    private static readonly IBrush Secondary = new SolidColorBrush(Color.Parse("#8A969E"));
    private static readonly IBrush ValueText = new SolidColorBrush(Color.Parse("#F2F2F2"));

    /// <param name="Title">The user's name for the fan, else <paramref name="Original"/>.</param>
    private sealed record Control0(string Id, string Title, string Original, string Group, string? RpmId, FanZoneStatus? Zone, GpuFanStatus? Gpu);

    private readonly Sensors.SensorHub _hub;
    private readonly FanClient _client;
    private readonly FanCurveRunner _runner;
    private readonly FanCurveSensors _sensors;
    private readonly WrapPanel _controls = new();
    private readonly WrapPanel _curves = new();
    private readonly CardDrag _controlDrag, _curveDrag;
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Foreground = Secondary, FontSize = 13 };
    private readonly TextBlock _result = new() { TextWrapping = TextWrapping.Wrap, FontSize = 13 };
    private readonly Button _login;
    private readonly Button _applyAll;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly List<Action> _updaters = [];
    private FanDriverStatus? _bmc;
    private GpuFanDriverStatus? _gpu;
    private IReadOnlyDictionary<int, long> _sources = new Dictionary<int, long>();
    private string? _loginUser, _error;
    private IReadOnlyList<FanSensorChoice> _choices = [];
    private string _controlsShape = "";
    private bool _busy, _ownSave;
    private int _ticks;

    public FanControlWindow(Sensors.SensorHub hub, FanClient? client = null, FanCurveRunner? runner = null)
    {
        _hub = hub;
        _client = client ?? runner?.Client ?? new FanClient();
        _runner = runner ?? new FanCurveRunner(_client, FanCurveStore.Load());
        _sensors = new FanCurveSensors(hub, _runner);
        _controlDrag = new CardDrag(_controls, (id, to) => MoveCardTo(id, to));
        _curveDrag = new CardDrag(_curves, (id, to) => MoveCurveTo(id, to));
        Title = "Fan Control";
        Width = 1620;
        Height = 1000;
        Background = PageBg;
        Foreground = Palette.Text;
        FontSize = 13;

        _login = AlertUi.Button("BMC login…", () =>
        {
            var dialog = new BmcLoginDialog(_client);
            Settings.WindowPlaces.Track(dialog, "bmc-login");
            dialog.Closed += (_, _) => RefreshStatus(rebuild: true);
            dialog.ShowDialog(this);
        });
        _applyAll = Fab("Apply all to the BMC", () => _ = ApplyAllAsync());
        _applyAll.IsVisible = false;
        var more = new Button { Content = "⋮", Padding = new Thickness(12, 3), Background = FooterIcons.ButtonFace, BorderBrush = FooterIcons.ButtonEdge, BorderThickness = new Thickness(1), Foreground = Palette.Text };
        MenuItem Item(string header, string gesture, Action action)
        {
            var item = new MenuItem { Header = header, InputGesture = KeyGesture.Parse(gesture) };
            item.Click += (_, _) => action();
            return item;
        }
        more.Flyout = new MenuFlyout
        {
            Items =
            {
                Item("Create new configuration…", "Ctrl+N", () => _ = NewConfigAsync()),
                Item("Save configuration", "Ctrl+S", SaveConfig),
                Item("Save configuration as…", "Ctrl+Shift+S", () => _ = SaveConfigAsAsync()),
                Item("Load configuration…", "Ctrl+L", () => _ = LoadConfigAsync()),
            },
        };
        ToolTip.SetTip(more, "Configurations: the curves, which fan runs which, and the fans' names");
        KeyDown += (_, e) =>
        {
            if (!e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;
            var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
            switch (e.Key)
            {
                case Key.N when !shift: _ = NewConfigAsync(); break;
                case Key.S when shift: _ = SaveConfigAsAsync(); break;
                case Key.S: SaveConfig(); break;
                case Key.L when !shift: _ = LoadConfigAsync(); break;
                default: return;
            }
            e.Handled = true;
        };
        var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14 };
        title.Children.Add(new TextBlock { Text = "Fan Control", FontSize = 26, FontWeight = FontWeight.Bold });
        title.Children.Add(new TextBlock { Text = "BMC + NVML", Foreground = Secondary, FontSize = 14, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 4) });
        var top = new DockPanel();
        var right = AlertUi.Row(_applyAll, _login, more);
        right.VerticalAlignment = VerticalAlignment.Top;
        DockPanel.SetDock(right, Dock.Right);
        top.Children.Add(right);
        top.Children.Add(title);

        var newButtons = AlertUi.Row(NewButton("+ Flat", FanCurveKind.Flat), NewButton("+ Graph", FanCurveKind.Graph), NewButton("+ Mix", FanCurveKind.Mix));
        var curvesHead = new DockPanel { Margin = new Thickness(6, 18, 6, 4) };
        DockPanel.SetDock(newButtons, Dock.Right);
        curvesHead.Children.Add(newButtons);
        curvesHead.Children.Add(Heading("Curves"));

        var page = new StackPanel { Spacing = 4, Margin = new Thickness(24, 16) };
        page.Children.Add(top);
        page.Children.Add(_status);
        page.Children.Add(_result);
        page.Children.Add(Heading("Controls", new Thickness(6, 12, 6, 4)));
        page.Children.Add(_controls);
        page.Children.Add(curvesHead);
        page.Children.Add(_curves);
        page.Children.Add(new TextBlock
        {
            Foreground = Secondary, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(6, 14, 6, 0), FontSize = 12,
            Text = "BMC fans: a curve that reads only the BMC's own sensors (CPU Package, T_Sensor, LAN, the DIMMs, the PCIe slots — " +
                   "which read the GPU in each slot) runs on the BMC: its sensors and five points are written once, and the BMC runs it " +
                   "with or without OpenHW. Any other sensor makes OpenHW drive the fan, and every duty change is a write to the BMC's " +
                   "flash. NVIDIA fans are driven through the NVIDIA driver (nothing stored); unticked, the card controls its fans itself.",
        });
        Content = new ScrollViewer { Content = page, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };

        _runner.Store.Changed += OnStoreChanged;
        _timer.Tick += (_, _) =>
        {
            if (++_ticks % 3 == 0) RefreshStatus(rebuild: false);
            Update();
        };
        Opened += (_, _) => { _timer.Start(); Update(); };
        Closed += (_, _) => { _closed = true; _timer.Stop(); _runner.Store.Changed -= OnStoreChanged; };
        RefreshStatus(rebuild: true, wait: true);
        BuildCurves();
    }

    private static TextBlock Heading(string text, Thickness? margin = null) =>
        new() { Text = text, FontSize = 19, FontWeight = FontWeight.SemiBold, Margin = margin ?? new Thickness(0), VerticalAlignment = VerticalAlignment.Center };

    private static Button Fab(string text, Action click)
    {
        var b = AlertUi.Button(text, click);
        b.Background = CurveUi.Dot;
        b.Foreground = Brushes.Black;
        b.FontWeight = FontWeight.SemiBold;
        b.Padding = new Thickness(14, 5);
        // Stay orange under the pointer (Fluent's own hover look turns it gray).
        b.Resources["ButtonBackgroundPointerOver"] = new SolidColorBrush(Color.Parse("#F5B562"));
        b.Resources["ButtonBackgroundPressed"] = new SolidColorBrush(Color.Parse("#D88A2A"));
        b.Resources["ButtonForegroundPointerOver"] = Brushes.Black;
        b.Resources["ButtonForegroundPressed"] = Brushes.Black;
        return b;
    }

    /// <summary>+ Flat / + Graph / + Mix: a menu — a new curve, or a copy of one of that kind already made.</summary>
    private Button NewButton(string text, FanCurveKind kind)
    {
        Button? button = null;
        // Built on each click, then shown: an empty MenuFlyout filled in its own Opening event never opens.
        button = Fab(text, () => NewMenu(kind).ShowAt(button!));
        return button;
    }

    /// <summary>The + Flat / + Graph / + Mix menu (tests open it too).</summary>
    public MenuFlyout NewMenu(FanCurveKind kind)
    {
        var menu = new MenuFlyout();
        var fresh = new MenuItem { Header = "New " + kind.ToString().ToLowerInvariant() };
        fresh.Click += (_, _) => NewCurve(kind);
        menu.Items.Add(fresh);
        var existing = _runner.Store.Curves.Where(c => c.Kind == kind).OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        if (existing.Count > 0) menu.Items.Add(new Separator());
        foreach (var c in existing)
        {
            var copy = new MenuItem { Header = new TextBlock { Text = $"Duplicate \"{c.Name}\"" } };
            var source = c;
            copy.Click += (_, _) => Duplicate(source);
            menu.Items.Add(copy);
        }
        return menu;
    }

    /// <summary>A copy of a curve under a new name (" copy", " copy 2", …); a graph opens in the editor right away.</summary>
    public FanCurve Duplicate(FanCurve source)
    {
        var copy = source.Clone();
        copy.Id = FanCurveStore.NewId();
        var names = _runner.Store.Curves.Select(c => c.Name).ToHashSet(StringComparer.CurrentCultureIgnoreCase);
        copy.Name = source.Name + " copy";
        for (var n = 2; names.Contains(copy.Name); n++) copy.Name = $"{source.Name} copy {n}";
        _runner.Store.Curves.Add(copy);
        Save();
        BuildCurves();
        BuildControls();
        if (copy.Kind == FanCurveKind.Graph) EditGraph(copy);
        return copy;
    }

    private static Border Card(Control child, double width = 300) => new()
    {
        Width = width, Margin = new Thickness(6), Padding = new Thickness(14, 12), Background = CardBg, BorderBrush = CardBorder,
        BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Child = child,
    };

    private void OnStoreChanged() => Dispatcher.UIThread.Post(() =>
    {
        UpdateTitle();
        if (_ownSave) return;
        BuildControls();
        BuildCurves();
    });

    private void Save()
    {
        _ownSave = true;
        try { _runner.Store.Save(); }
        finally { _ownSave = false; }
    }

    private void Show(string text, bool bad)
    {
        _result.Text = text;
        _result.Foreground = bad ? AlertUi.Alarm : AlertUi.Ok;
    }

    // ── status from the helper ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>One round of the helper's fan status; fields left null when that request failed (the last answer stays).</summary>
    private sealed record StatusReply(FanDriverStatus? Bmc, GpuFanDriverStatus? Gpu, IReadOnlyDictionary<int, long>? Sources,
        bool LoginKnown, string? LoginUser, string? Error);

    /// <summary>Asks the helper; no UI access, so it runs off the window thread (a slow service answer used to freeze the window).</summary>
    private StatusReply FetchStatus()
    {
        static bool Soft(Exception ex) => ex is IOException or System.Net.Sockets.SocketException or System.Text.Json.JsonException or FormatException;
        FanDriverStatus? bmc = null;
        GpuFanDriverStatus? gpu = null;
        IReadOnlyDictionary<int, long>? sources = null;
        string? error = null, user = null;
        var loginKnown = false;
        try { bmc = _client.Status(); }
        catch (Exception ex) when (Soft(ex))
        {
            error = ex.Message.Contains("unknown command", StringComparison.Ordinal)
                ? "openhw-helper is too old for fan control: install the current OpenHW .deb."
                : "openhw-helper isn't answering: " + ex.Message.Replace("openhw-helper: err ", "");
        }
        try { gpu = _client.GpuStatus(); } catch (Exception ex) when (Soft(ex)) { }
        try { sources = _client.Sources(); } catch (Exception ex) when (Soft(ex)) { }
        try { (user, loginKnown) = (_client.LoginUser(), true); } catch (Exception ex) when (Soft(ex)) { }
        return new(bmc, gpu, sources, loginKnown, user, error);
    }

    private bool _fetching, _rebuildQueued, _closed;

    /// <summary>
    /// Refreshes the status in the background and applies it on the window thread; a refresh asked for while one is
    /// running is folded into it. <paramref name="wait"/> (the constructor) asks right away, so the window opens filled in.
    /// </summary>
    private void RefreshStatus(bool rebuild, bool wait = false)
    {
        if (wait) { ApplyStatus(FetchStatus(), rebuild); return; }
        _rebuildQueued |= rebuild;
        if (_fetching) return;
        _fetching = true;
        Task.Run(FetchStatus).ContinueWith(t => Dispatcher.UIThread.Post(() =>
        {
            _fetching = false;
            var again = _rebuildQueued;
            _rebuildQueued = false;
            if (!_closed && t.IsCompletedSuccessfully) ApplyStatus(t.Result, again);
        }), TaskScheduler.Default);
    }

    private void ApplyStatus(StatusReply reply, bool rebuild)
    {
        _error = reply.Error;
        _bmc = reply.Bmc;
        _gpu = reply.Gpu;
        if (reply.Sources is not null) _sources = reply.Sources;
        if (reply.LoginKnown) _loginUser = reply.LoginUser;
        _choices = _sensors.Choices();

        var notes = new List<string>();
        if (_error is not null) notes.Add(_error);
        if (_bmc is { Available: false, Reason: { } r }) notes.Add("BMC fans: " + r);
        if (_bmc?.OtherController is { } other) notes.Add(other + " OpenHW won't drive BMC fans until it lets go.");
        if (_gpu is null && _error is null) notes.Add("GPU fans need openhw-helper protocol 15: install the current OpenHW .deb.");
        if (_gpu?.Reason is { } gr) notes.Add("GPU fans: " + gr);
        notes.Add(_loginUser is null ? "No BMC login stored (needed to change which sensors a BMC fan follows)." : $"BMC login: {_loginUser}");
        _status.Text = string.Join("   ·   ", notes);
        _status.Foreground = _error is not null || _bmc?.OtherController is not null || _gpu?.Reason is not null ? AlertUi.Alarm : Secondary;
        _login.Content = _loginUser is null ? "Give OpenHW the BMC login…" : "BMC login…";

        var shape = string.Join("|", Controls().Select(c => c.Id + (c.Zone?.Owned ?? false))) + "#" + _bmc?.Available + _bmc?.OtherController;
        if (rebuild || shape != _controlsShape) BuildControls();
        UpdateTitle();
    }

    /// <summary>The configuration file in the title (as OpenFan) and the count of BMC fans whose curve isn't on the BMC yet.</summary>
    private void UpdateTitle()
    {
        var file = _runner.Store.ConfigFile;
        Title = file is null ? "Fan Control" : $"Fan Control — {System.IO.Path.GetFileName(file)}";
        var pending = Pending().Count;
        _applyAll.IsVisible = pending > 1;
        _applyAll.Content = $"Apply all to the BMC ({pending})";
    }

    /// <summary>BMC fans with a BMC-run curve that isn't on the BMC yet (e.g. after loading a configuration).</summary>
    private List<Control0> Pending()
    {
        var store = _runner.Store;
        return Controls().Where(c => c.Zone is { } z && store.AssignedTo(z.Id) is { } curve && _runner.Plan(z.Id, curve) is { OnBmc: true } plan &&
            !(z.Active is { } a && plan.Points.SequenceEqual(a) &&
              (plan.SourceMask is null || (_sources.TryGetValue(FanCurveRunner.ZoneIndex(z.Id), out var m) && plan.SourceMask == m)))).ToList();
    }

    private async Task ApplyAllAsync()
    {
        var pending = Pending();
        if (pending.Count == 0) return;
        var text = $"Write these fans' curves to the BMC: {string.Join(", ", pending.Select(c => c.Title))}?\n\nOne write to the BMC's flash per " +
                   "fan (two where its sensors change); after that the BMC runs them with no further writes.";
        if (!await new ConfirmDialog("Fan Control", text, $"Write {pending.Count} fans", false).ShowDialog<bool>(this)) return;
        var results = new List<string>();
        foreach (var c in pending)
        {
            long? mask = _sources.TryGetValue(FanCurveRunner.ZoneIndex(c.Id), out var m) ? m : null;
            try { results.Add($"{c.Title}: {_runner.ApplyBmc(c.Zone!, mask)}"); }
            catch (IOException ex) { results.Add($"{c.Title}: {ex.Message.Replace("openhw-helper: err ", "")}"); }
        }
        Show(string.Join("  ·  ", results), results.Any(r => r.Contains("refused", StringComparison.Ordinal)));
        RefreshStatus(rebuild: true);
    }

    // ── configurations (⋮ menu, OpenFan's Save / Load setup) ──────────────────────────────────────────────────────

    private static FilePickerFileType ConfigType => new("OpenHW fan configuration") { Patterns = ["*.json"] };

    private async Task<IStorageFolder?> ProfilesFolder()
    {
        try { Directory.CreateDirectory(FanCurveStore.ProfilesDir); } catch (IOException) { }
        return await StorageProvider.TryGetFolderFromPathAsync(FanCurveStore.ProfilesDir);
    }

    private void SaveConfig()
    {
        if (_runner.Store.ConfigFile is null) { _ = SaveConfigAsAsync(); return; }
        try
        {
            _runner.Store.Export(_runner.Store.ConfigFile);
            Show($"Configuration saved — {System.IO.Path.GetFileName(_runner.Store.ConfigFile)}", false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Show("Save failed: " + ex.Message, true); }
    }

    private async Task SaveConfigAsAsync()
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save configuration as", SuggestedStartLocation = await ProfilesFolder(), DefaultExtension = "json",
            SuggestedFileName = _runner.Store.ConfigFile is { } f ? System.IO.Path.GetFileName(f) : "fans.json", FileTypeChoices = [ConfigType],
        });
        if (file?.TryGetLocalPath() is not { } path) return;
        try
        {
            _runner.Store.Export(path);
            _runner.Store.ConfigFile = path;
            Save();
            Show($"Configuration saved — {System.IO.Path.GetFileName(path)} (changes save here)", false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Show("Save failed: " + ex.Message, true); }
        UpdateTitle();
    }

    private async Task NewConfigAsync()
    {
        if (!await ConfirmReplaceAsync("Create a new, empty configuration")) return;
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Create new configuration", SuggestedStartLocation = await ProfilesFolder(), DefaultExtension = "json",
            SuggestedFileName = "new-config.json", FileTypeChoices = [ConfigType],
        });
        if (file?.TryGetLocalPath() is not { } path) return;
        try
        {
            _runner.Store.StartNew(path);
            Save();
            Show($"New configuration — {System.IO.Path.GetFileName(path)} (changes save here)", false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Show("Create failed: " + ex.Message, true); }
        AfterSwitch();
    }

    private async Task LoadConfigAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Load configuration", SuggestedStartLocation = await ProfilesFolder(), FileTypeFilter = [ConfigType],
        });
        if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path) return;
        if (!await ConfirmReplaceAsync($"Load {System.IO.Path.GetFileName(path)}")) return;
        try
        {
            LoadConfig(path);
            Show($"Loaded {System.IO.Path.GetFileName(path)} (changes save here)." +
                 (Pending().Count > 0 ? " BMC fans it puts on BMC curves wait for Apply (one write each)." : ""), false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { Show("Load failed: " + ex.Message, true); }
    }

    /// <summary>Loads a configuration file into the live setup (tests call it directly).</summary>
    public void LoadConfig(string path)
    {
        _runner.Store.Import(path);
        Save();
        AfterSwitch();
    }

    private void AfterSwitch()
    {
        BuildCurves();
        BuildControls();
        UpdateTitle();
        RefreshStatus(rebuild: true);
    }

    /// <summary>
    /// What replacing the setup does, before it happens: NVIDIA and OpenHW-driven fans follow at once; BMC fans keep the
    /// curve the BMC has until Apply (one write each). Says so when the current setup isn't in any file yet.
    /// </summary>
    private async Task<bool> ConfirmReplaceAsync(string what)
    {
        var store = _runner.Store;
        var text = $"{what}?\n\nIt replaces the curves, which fan runs which, and the fans' names. NVIDIA fans and OpenHW-driven " +
                   "fans follow it at once. BMC fans keep the curve the BMC has until you Apply (one write each)." +
                   (store.ConfigFile is null && store.Curves.Count > 0 ? "\n\nThe current setup isn't saved in a configuration file: use Save configuration as… first to keep it." : "");
        return await new ConfirmDialog("Fan Control", text, "Continue", store.ConfigFile is null && store.Curves.Count > 0).ShowDialog<bool>(this);
    }

    private List<Control0> Controls()
    {
        var list = new List<Control0>();
        var names = _runner.Store.Names;
        string Name(string id, string original) => names.TryGetValue(id, out var n) && n.Length > 0 ? n : original;
        foreach (var z in _bmc?.Zones ?? [])
        {
            var original = z.Name == "CPU_FAN" ? "CPU_FAN / CPU_OPT" : z.Name;
            list.Add(new(z.Id, Name(z.Id, original), original, "ASUS BMC · " + z.Label, z.RpmSensorId, z, null));
        }
        foreach (var g in _gpu?.Fans ?? [])
        {
            var original = $"{ShortGpu(g.Card)} {ShortPci(g.Pci)} Fan {g.Fan}";
            list.Add(new(g.Id, Name(g.Id, original), original, $"{g.Card} {ShortPci(g.Pci)}", g.Id.Replace(":fan:", ":tach:"), null, g));
        }
        var order = _runner.Store.Ordered(list.Select(c => c.Id));
        return list.OrderBy(c => order.IndexOf(c.Id)).ToList();
    }

    private static string ShortPci(string pci) => pci.Length >= 12 ? pci[^7..] : pci;

    /// <summary>"NVIDIA RTX PRO 6000 Blackwell Workstation Edition" → "RTX PRO 6000" (the card line keeps the full name).</summary>
    private static string ShortGpu(string name)
    {
        foreach (var part in new[] { "NVIDIA ", " Blackwell", " Workstation Edition", " Max-Q", " Server Edition" }) name = name.Replace(part, "");
        return name.Trim();
    }

    // ── control cards ────────────────────────────────────────────────────────────────────────────────────────────

    private void BuildControls()
    {
        _controls.Children.Clear();
        _controlDrag.Clear();
        _updaters.RemoveAll(u => u.Target is ControlCard);
        var controls = Controls();
        _controlsShape = string.Join("|", controls.Select(c => c.Id + (c.Zone?.Owned ?? false))) + "#" + _bmc?.Available + _bmc?.OtherController;
        foreach (var c in controls)
        {
            var card = new ControlCard(this, c);
            _controls.Children.Add(card.Root);
            _controlDrag.Attach(card.Root, c.Id);
            _updaters.Add(card.Update);
        }
        if (controls.Count == 0) _controls.Children.Add(new TextBlock { Text = "No fans to control.", Foreground = Secondary, Margin = new Thickness(6) });
    }

    private sealed class ControlCard
    {
        private readonly FanControlWindow _w;
        private readonly Control0 _c;
        private readonly TextBlock _value = new() { FontSize = 18, FontWeight = FontWeight.SemiBold, Foreground = ValueText };
        private readonly TextBlock _note = new() { FontSize = 11.5, TextWrapping = TextWrapping.Wrap, MaxWidth = 270, VerticalAlignment = VerticalAlignment.Center };
        private readonly Button _apply;
        public readonly Border Root;

        public ControlCard(FanControlWindow w, Control0 c)
        {
            _w = w;
            _c = c;
            var store = w._runner.Store;
            var assigned = store.AssignedTo(c.Id);
            var chosen = assigned ?? store.Find(store.Chosen.GetValueOrDefault(c.Id));
            var enabled = (c.Zone is null || (w._bmc is { Available: true } && w._bmc.OtherController is null)) && (c.Gpu is null || w._gpu?.Reason is null);

            var check = new CheckBox { Content = "Curve", IsChecked = assigned is not null, IsEnabled = enabled };
            var mode = new ComboBox { Width = 270, IsEnabled = enabled, HorizontalAlignment = HorizontalAlignment.Left, HorizontalContentAlignment = HorizontalAlignment.Left };
            mode.Items.Add(new ComboBoxItem { Content = new TextBlock { Text = "Monitor" }, Tag = null });
            foreach (var curve in store.Curves) mode.Items.Add(new ComboBoxItem { Content = new TextBlock { Text = curve.Name }, Tag = curve.Id });
            mode.SelectedIndex = chosen is null ? 0 : Math.Max(0, store.Curves.FindIndex(x => x.Id == chosen.Id) + 1);
            var suppress = false;
            check.IsCheckedChanged += (_, _) =>
            {
                if (suppress) return;
                if (check.IsChecked == true)
                {
                    if ((mode.SelectedItem as ComboBoxItem)?.Tag is string id) _ = w.ChooseAsync(c, id);
                    else { suppress = true; check.IsChecked = false; suppress = false; } // nothing to run until a curve is chosen
                }
                else w.Unassign(c);
            };
            mode.SelectionChanged += (_, _) =>
            {
                if (suppress) return;
                var id = (mode.SelectedItem as ComboBoxItem)?.Tag as string;
                if (id is null) { w.Unassign(c, forget: true); return; }
                if (id != store.AssignedTo(c.Id)?.Id) _ = w.ChooseAsync(c, id); // picking a curve arms the fan (OpenFan)
            };

            _apply = AlertUi.Button("Apply", () => w.ApplyBmc(c));
            _apply.Padding = new Thickness(8, 1);
            _apply.IsVisible = false;

            var head = new DockPanel();
            {
                var menu = new Button { Content = "⋮", Background = Brushes.Transparent, BorderThickness = new Thickness(0), Foreground = Secondary, Padding = new Thickness(6, 0) };
                var items = new List<Control>();
                MenuItem Move(string header, Func<int, int> to)
                {
                    var item = new MenuItem { Header = header };
                    item.Click += (_, _) => w.MoveCard(c, to);
                    return item;
                }
                items.Add(Move("Move left", i => i - 1));
                items.Add(Move("Move right", i => i + 1));
                items.Add(Move("Move to start", _ => 0));
                items.Add(Move("Move to end", _ => int.MaxValue));
                if (c.Zone is { } zone)
                {
                    var edit = new MenuItem { Header = "Edit BMC curve…", IsEnabled = enabled && assigned is null && !zone.Owned };
                    edit.Click += (_, _) => w.EditBmcCurve(zone);
                    var restore = new MenuItem { Header = "Restore original curve", IsEnabled = enabled && zone.SavedOriginal is not null && !zone.Owned };
                    restore.Click += (_, _) => w.RestoreOriginal(zone);
                    items.Add(new Separator());
                    items.Add(edit);
                    items.Add(restore);
                    var thresholds = new MenuItem { Header = "Alarm thresholds…", IsEnabled = enabled };
                    thresholds.Click += (_, _) => w.EditThresholds(zone);
                    ToolTip.SetTip(thresholds, "The RPM below which the BMC logs this fan as slow (its event log)");
                    items.Add(thresholds);
                    ToolTip.SetTip(edit, assigned is null ? "This fan's own BMC curve" : "Untick Curve to edit this fan's BMC curve directly");
                }
                var flyout = new MenuFlyout();
                foreach (var item in items) flyout.Items.Add(item);
                menu.Flyout = flyout;
                DockPanel.SetDock(menu, Dock.Right);
                head.Children.Add(menu);
            }
            // Click to rename (as in OpenFan); empty = the original name, which the tooltip always shows.
            var title = new TextBox
            {
                Text = c.Title, FontWeight = FontWeight.SemiBold, FontSize = 14, BorderThickness = new Thickness(0), Background = Brushes.Transparent,
                Padding = new Thickness(0), MinHeight = 0, VerticalAlignment = VerticalAlignment.Center,
            };
            ToolTip.SetTip(title, $"{c.Original}\n{c.Id}\nClick to rename.");
            title.LostFocus += (_, _) => w.Rename(c, title);
            title.KeyDown += (_, e) => { if (e.Key == Key.Enter) TopLevel.GetTopLevel(title)?.FocusManager?.ClearFocus(); };
            head.Children.Add(title);

            var body = new StackPanel { Spacing = 5 };
            body.Children.Add(head);
            body.Children.Add(new TextBlock { Text = c.Group, FontSize = 11, Foreground = Secondary, TextTrimming = TextTrimming.CharacterEllipsis });
            body.Children.Add(check);
            body.Children.Add(mode);
            body.Children.Add(_value);
            body.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _note, _apply } });
            Root = Card(body);
            Update();
        }

        public void Update()
        {
            var w = _w;
            var reading = (Func<string, double?>)(id => w._hub.Get(id).Current);
            var rpm = _c.RpmId is { } r ? reading(r) : null;
            var rpmText = rpm is { } x ? $"     {x.ToString("N0", CultureInfo.CurrentCulture)} RPM" : "";
            var store = w._runner.Store;
            var assigned = store.AssignedTo(_c.Id);
            var state = w._runner.State(_c.Id);
            _apply.IsVisible = false;
            if (_c.Zone is { } z)
            {
                long? mask = w._sources.TryGetValue(FanCurveRunner.ZoneIndex(z.Id), out var m) ? m : null;
                if (state.Duty is { } duty) _value.Text = $"{duty} % target{rpmText}";
                else if (z.Target is { } t) _value.Text = $"{t} % (held){rpmText}";
                else _value.Text = (w._runner.BmcDuty(z, mask, reading) is { } d ? $"{d:0} % (BMC)" : "auto") + rpmText;
                _value.Foreground = assigned is null ? Secondary : ValueText;
                if (assigned is null)
                    Note($"BMC curve, follows {(mask is { } mm ? FanClient.DescribeSource(mm, w._sensorsNames) : "–")}", Secondary);
                else
                {
                    var plan = w._runner.Plan(z.Id, assigned);
                    if (plan.OnBmc)
                    {
                        var applied = z.Active is { } a && plan.Points.SequenceEqual(a) && (plan.SourceMask is null || plan.SourceMask == mask);
                        if (applied) Note("On the BMC ✓ — written once, no further flash writes", CurveUi.BmcLine);
                        else { Note("Not on the BMC yet:", CurveUi.Warn); _apply.IsVisible = true; }
                    }
                    else Note($"⚠ OpenHW-driven · {state.WritesToday} flash writes today", CurveUi.Warn);
                }
            }
            else
            {
                var pct = reading(_c.Id);
                _value.Text = state.Duty is { } duty ? (duty == 0 ? "0 % (auto)" : $"{duty} %") + rpmText
                    : (pct is { } p ? $"auto  {p:0} %" : "auto") + rpmText;
                _value.Foreground = assigned is null ? Secondary : ValueText;
                Note(assigned is null ? "The card's own control" : "OpenHW via NVIDIA driver (no flash)", assigned is null ? Secondary : CurveUi.BmcLine);
            }
            if (state.Problem is { } problem) Note(problem, AlertUi.Alarm);
        }

        private void Note(string text, IBrush brush)
        {
            _note.Text = text;
            _note.Foreground = brush;
        }
    }

    private IReadOnlyList<(int Number, string Name)> _sensorsNames => _runner.BmcSensors();

    /// <summary>When the BMC's version of a mix differs from what the mix means, and how (null when it's the same).</summary>
    public static string? MixWarning(FanCurve mix, IReadOnlyDictionary<string, FanCurve> library, BmcCurvePlan plan) =>
        CurveCard.MixWarning(mix, library, plan);

    /// <summary>Curve cards as shown: the saved order first, then flat, graph, mix, each by name.</summary>
    public List<FanCurve> CurvesInOrder()
    {
        var kinds = new Dictionary<FanCurveKind, int> { [FanCurveKind.Flat] = 0, [FanCurveKind.Graph] = 1, [FanCurveKind.Mix] = 2 };
        var store = _runner.Store;
        var natural = store.Curves.OrderBy(c => kinds[c.Kind]).ThenBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).Select(c => c.Id);
        return store.OrderedCurves(natural).Select(id => store.Find(id)!).ToList();
    }

    /// <summary>Drag and drop: a fan card to position <paramref name="to"/> among the shown ones.</summary>
    public void MoveCardTo(string id, int to)
    {
        if (!_runner.Store.Move(Controls().Select(c => c.Id).ToList(), id, to)) return;
        Save();
        Dispatcher.UIThread.Post(BuildControls);
    }

    /// <summary>Drag and drop: a curve card to position <paramref name="to"/>.</summary>
    public void MoveCurveTo(string id, int to)
    {
        if (!_runner.Store.MoveCurve(CurvesInOrder().Select(c => c.Id).ToList(), id, to)) return;
        Save();
        Dispatcher.UIThread.Post(BuildCurves);
    }

    /// <summary>Card ⋮ → Move left / right / to start / to end (<paramref name="to"/> maps the card's index to its new one).</summary>
    private void MoveCard(Control0 c, Func<int, int> to)
    {
        var shown = Controls().Select(x => x.Id).ToList();
        var index = shown.IndexOf(c.Id);
        if (!_runner.Store.Move(shown, c.Id, to(index))) return;
        Save();
        Dispatcher.UIThread.Post(BuildControls);
    }

    /// <summary>Card order (tests).</summary>
    public IReadOnlyList<string> CardOrder() => Controls().Select(c => c.Id).ToList();

    /// <summary>A fan's friendly name: saved per fan id; an empty (or original) name goes back to the original.</summary>
    private void Rename(Control0 c, TextBox box)
    {
        var text = (box.Text ?? "").Trim();
        var names = _runner.Store.Names;
        if (text.Length == 0 || text == c.Original) { names.Remove(c.Id); box.Text = c.Original; }
        else names[c.Id] = text;
        if ((names.TryGetValue(c.Id, out var n) ? n : c.Original) == c.Title) return;
        Save();
        Dispatcher.UIThread.Post(() => { BuildControls(); BuildCurves(); }); // the curve cards' "Fans:" lines use the name
    }

    private void Unassign(Control0 c, bool forget = false)
    {
        var store = _runner.Store;
        if (store.AssignedTo(c.Id) is { } current && !forget) store.Chosen[c.Id] = current.Id;
        if (forget) store.Chosen.Remove(c.Id);
        _ownSave = true;
        try { _runner.Unassign(c.Id); }
        finally { _ownSave = false; }
        Show(c.Zone is null ? $"{c.Title}: back to the card's own control." : $"{c.Title} keeps the BMC curve it has now.", false);
        BuildControls();
    }

    /// <summary>A fan gets a curve: for a BMC fan, what that writes (or that it's OpenHW-driven) is confirmed first.</summary>
    private async Task ChooseAsync(Control0 c, string curveId)
    {
        if (_busy) return;
        _busy = true;
        try
        {
            var curve = _runner.Store.Find(curveId)!;
            if (c.Zone is { } zone)
            {
                long? mask = _sources.TryGetValue(FanCurveRunner.ZoneIndex(zone.Id), out var m) ? m : null;
                var plan = _runner.Plan(zone.Id, curve);
                var text = plan.OnBmc
                    ? $"{c.Title} will run \"{curve.Name}\" on the BMC.\n\nWritten once: " +
                      (plan.SourceMask is { } sm && sm != mask ? $"its sensors ({FanClient.DescribeSource(sm, _sensorsNames)}) and " : "") +
                      $"the curve {CurveUi.Points(plan.Points)}" +
                      (plan.MaxExtraPercent >= 0.5 ? $" — at most {plan.MaxExtraPercent:0} % faster than drawn, at {plan.MaxExtraAtC:0.#} °C" : "") +
                      ".\nThis is one write to the BMC's flash (two if its sensors change), like Save in the BMC web console. After that " +
                      "the BMC runs it by itself with no further writes, with or without OpenHW; only changing the curve again writes again."
                    : $"⚠ \"{curve.Name}\" reads {string.Join(", ", plan.OtherSensors.Select(id => _choices.FirstOrDefault(x => x.Id == id)?.Label ?? id))}, " +
                      $"which the BMC can't see.\n\nOpenHW will drive {c.Title} itself: every duty change is a write to the BMC's flash. " +
                      $"Duties move in {FanCurveRunner.DrivenStep} % steps, faster at once, slower only after a minute; the writes are counted " +
                      "per day on the fan's card. Starting and stopping OpenHW cost a write each (the fan returns to its BMC curve when OpenHW stops).";
                var ok = await new ConfirmDialog("Fan Control — " + c.Title, text, plan.OnBmc ? "Write to the BMC" : "Let OpenHW drive it", !plan.OnBmc).ShowDialog<bool>(this);
                if (!ok) return;
                Assign(c, curveId);
                if (plan.OnBmc) ApplyBmc(c);
                else Show($"OpenHW drives {c.Title} from its next reading.", false);
            }
            else
            {
                Assign(c, curveId);
                Show($"OpenHW drives {c.Title} with \"{curve.Name}\".", false);
            }
        }
        finally
        {
            _busy = false;
            RefreshStatus(rebuild: true);
        }
    }

    private void Assign(Control0 c, string curveId)
    {
        _runner.Store.Chosen.Remove(c.Id);
        _ownSave = true;
        try { _runner.Assign(c.Id, curveId); }
        finally { _ownSave = false; }
    }

    private void ApplyBmc(Control0 c)
    {
        if (c.Zone is not { } zone) return;
        long? mask = _sources.TryGetValue(FanCurveRunner.ZoneIndex(zone.Id), out var m) ? m : null;
        try { Show($"{c.Title}: " + _runner.ApplyBmc(zone, mask), false); }
        catch (IOException ex) { Show($"{c.Title}: " + ex.Message.Replace("openhw-helper: err ", ""), true); }
        RefreshStatus(rebuild: true);
    }

    private void EditBmcCurve(FanZoneStatus zone)
    {
        _timer.Stop();
        var mask = _sources.TryGetValue(FanCurveRunner.ZoneIndex(zone.Id), out var m) ? m : 0;
        var dialog = new FanCurveDialog(_client, zone, mask, _sensorsNames, _loginUser is not null);
        Settings.WindowPlaces.Track(dialog, "fan-curve");
        dialog.Closed += (_, _) => { RefreshStatus(rebuild: true); _timer.Start(); };
        dialog.ShowDialog(this);
    }

    private void EditThresholds(FanZoneStatus zone)
    {
        var dialog = new FanThresholdDialog(_client, zone.Name);
        Settings.WindowPlaces.Track(dialog, "fan-thresholds");
        dialog.ShowDialog(this);
    }

    private void RestoreOriginal(FanZoneStatus zone)
    {
        try { _client.RestoreOriginal(zone.Id); Show($"{zone.Name}: original curve restored.", false); }
        catch (IOException ex) { Show(ex.Message.Replace("openhw-helper: err ", ""), true); }
        RefreshStatus(rebuild: true);
    }

    // ── curve cards ──────────────────────────────────────────────────────────────────────────────────────────────

    private void BuildCurves()
    {
        _curves.Children.Clear();
        _curveDrag.Clear();
        _updaters.RemoveAll(u => u.Target is CurveCard);
        foreach (var curve in CurvesInOrder())
        {
            var card = new CurveCard(this, curve);
            _curves.Children.Add(card.Root);
            _curveDrag.Attach(card.Root, curve.Id);
            _updaters.Add(card.Update);
        }
        if (_runner.Store.Curves.Count == 0)
            _curves.Children.Add(new TextBlock { Text = "No curves yet: + Flat, + Graph or + Mix.", Foreground = Secondary, Margin = new Thickness(6) });
    }

    private sealed class CurveCard
    {
        private readonly FanControlWindow _w;
        private readonly FanCurve _curve;
        private readonly TextBlock _output = new() { FontSize = 24, FontWeight = FontWeight.Bold, Foreground = ValueText, VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBlock _where = new() { FontSize = 11, TextWrapping = TextWrapping.Wrap };
        private readonly TextBlock _sensorNow = new() { FontSize = 12, Foreground = Secondary };
        private readonly Canvas _preview = new() { Height = 60, ClipToBounds = true };
        private readonly TextBlock _mixNote = new() { FontSize = 11, TextWrapping = TextWrapping.Wrap, Foreground = CurveUi.Warn, IsVisible = false };
        private BmcCurvePlan? _mixPlan;
        private double[]? _mixWanted;
        public readonly Border Root;

        public CurveCard(FanControlWindow w, FanCurve curve)
        {
            _w = w;
            _curve = curve;
            var name = new TextBox { Text = curve.Name, BorderThickness = new Thickness(0), Background = Brushes.Transparent, FontWeight = FontWeight.SemiBold, FontSize = 14, Padding = new Thickness(2, 0), MinWidth = 120 };
            name.LostFocus += (_, _) =>
            {
                var text = (name.Text ?? "").Trim();
                if (text.Length == 0) { name.Text = curve.Name; return; }
                if (text == curve.Name) return;
                curve.Name = text;
                w.Save();
                w.BuildControls(); // the fans' dropdowns show the new name
            };
            name.KeyDown += (_, e) => { if (e.Key == Key.Enter) TopLevel.GetTopLevel(name)?.FocusManager?.ClearFocus(); };
            var delete = new Button { Content = "×", Background = Brushes.Transparent, BorderThickness = new Thickness(0), Foreground = Secondary, FontSize = 15, Padding = new Thickness(7, 0) };
            delete.Click += (_, _) => _ = w.DeleteAsync(curve);
            var head = new DockPanel();
            DockPanel.SetDock(delete, Dock.Right);
            head.Children.Add(delete);
            head.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 8,
                Children = { new TextBlock { Text = curve.Kind.ToString().ToUpperInvariant(), FontSize = 9, Foreground = Secondary, VerticalAlignment = VerticalAlignment.Center }, name },
            });
            var body = new StackPanel { Spacing = 5 };
            body.Children.Add(head);
            switch (curve.Kind)
            {
                case FanCurveKind.Flat: BuildFlat(body); break;
                case FanCurveKind.Graph: BuildGraph(body); break;
                default: BuildMix(body); break;
            }
            body.Children.Add(_where);
            Root = Card(body, curve.Kind == FanCurveKind.Mix ? 320 : 300);
            Update();
        }

        private void BuildFlat(StackPanel body)
        {
            body.Children.Add(new TextBlock { Text = "Fan speed", FontSize = 11, Foreground = Secondary });
            var value = new TextBox
            {
                Text = $"{_curve.Percent:0}", FontSize = 20, FontWeight = FontWeight.Bold, Foreground = ValueText, BorderThickness = new Thickness(0),
                Background = Brushes.Transparent, Padding = new Thickness(0), MinWidth = 48, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
            };
            void Commit()
            {
                if (double.TryParse(value.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var v)) { _curve.Percent = Math.Clamp(Math.Round(v), 0, 100); _w.Save(); }
                value.Text = $"{_curve.Percent:0}";
            }
            value.LostFocus += (_, _) => Commit();
            value.KeyDown += (_, e) => { if (e.Key == Key.Enter) TopLevel.GetTopLevel(value)?.FocusManager?.ClearFocus(); };
            void Adjust(double delta)
            {
                _curve.Percent = Math.Clamp(_curve.Percent + delta, 0, 100);
                value.Text = $"{_curve.Percent:0}";
                _w.Save();
                Update();
            }
            var minus = AlertUi.Button("−", () => Adjust(-5));
            var plus = AlertUi.Button("+", () => Adjust(5));
            minus.Width = plus.Width = 40;
            body.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 10,
                Children = { minus, value, new TextBlock { Text = "%", FontSize = 20, FontWeight = FontWeight.Bold, Foreground = ValueText, VerticalAlignment = VerticalAlignment.Center }, plus },
            });
        }

        private void BuildGraph(StackPanel body)
        {
            body.Children.Add(new TextBlock { Text = "Temperature source", FontSize = 11, Foreground = Secondary });
            var sensor = new SensorPicker(_w._choices, _curve.Inputs, id => _w._hub.Get(id).Current, ids =>
            {
                _curve.SetInputs(ids);
                _w.Save();
                Update();
            });
            var edit = new Button { Content = "Edit", Background = Brushes.Transparent, BorderThickness = new Thickness(0), Foreground = CurveUi.Dot, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Bottom };
            edit.Click += (_, _) => _w.EditGraph(_curve);
            var row = new DockPanel();
            DockPanel.SetDock(edit, Dock.Right);
            row.Children.Add(edit);
            row.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { _output, _sensorNow } });
            _sensorNow.VerticalAlignment = VerticalAlignment.Center;
            body.Children.Add(sensor);
            body.Children.Add(row);
            body.Children.Add(_preview);
            _preview.SizeChanged += (_, _) => DrawPreview();
        }

        private void BuildMix(StackPanel body)
        {
            var store = _w._runner.Store;
            body.Children.Add(new TextBlock { Text = "Function", FontSize = 11, Foreground = Secondary });
            var function = new ComboBox { Width = 200, ItemsSource = new[] { "Max", "Min", "Average" }, SelectedIndex = (int)_curve.Function };
            function.SelectionChanged += (_, _) => { _curve.Function = (FanMixFunction)Math.Max(0, function.SelectedIndex); _w.Save(); Update(); };
            var add = new ComboBox { Width = 270, PlaceholderText = "Add fan curve", HorizontalContentAlignment = HorizontalAlignment.Left };
            foreach (var c in store.Curves.Where(c => c.Kind != FanCurveKind.Mix && c.Id != _curve.Id && !_curve.Children.Contains(c.Id)).OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase))
                add.Items.Add(new ComboBoxItem { Content = new TextBlock { Text = c.Name }, Tag = c.Id });
            add.SelectionChanged += (_, _) =>
            {
                if ((add.SelectedItem as ComboBoxItem)?.Tag is not string id) return;
                _curve.Children.Add(id);
                _w.Save();
                Dispatcher.UIThread.Post(_w.BuildCurves);
            };
            var list = new StackPanel { Spacing = 2 };
            foreach (var id in _curve.Children.ToList())
            {
                var remove = new Button { Content = "×", Background = Brushes.Transparent, BorderThickness = new Thickness(0), Foreground = Secondary, Padding = new Thickness(5, 0) };
                remove.Click += (_, _) => { _curve.Children.Remove(id); _w.Save(); Dispatcher.UIThread.Post(_w.BuildCurves); };
                list.Children.Add(new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 8,
                    Children =
                    {
                        new TextBlock { Text = "●", FontSize = 8, Foreground = CurveUi.Dot, VerticalAlignment = VerticalAlignment.Center },
                        new TextBlock { Text = store.Find(id)?.Name ?? id, VerticalAlignment = VerticalAlignment.Center }, remove,
                    },
                });
            }
            body.Children.Add(function);
            body.Children.Add(add);
            body.Children.Add(list);
            body.Children.Add(_output);
            body.Children.Add(_preview);
            body.Children.Add(_mixNote);
            ToolTip.SetTip(_preview, "White: this mix with every sensor at the same temperature (what the BMC's one curve is fitted to).\n" +
                                     "Green dashed: the 5-point curve the BMC would run. Dot: now, at the hottest of the mix's sensors.");
            _preview.SizeChanged += (_, _) => DrawMixPreview();
        }

        /// <summary>
        /// The mix as the BMC would run it: white = the mix with every sensor at the same temperature (what the BMC's one
        /// curve is fitted to), green dashed = the fitted 5-point BMC curve, dot = now (the hottest of the mix's sensors).
        /// </summary>
        private void DrawMixPreview()
        {
            _preview.Children.Clear();
            if (_mixPlan is not { OnBmc: true } plan || _mixWanted is not { } g) return;
            var w = Math.Max(_preview.Bounds.Width, 40);
            var h = _preview.Height;
            const double pad = 4, tMin = 20, tMax = 100;
            double X(double t) => pad + (t - tMin) / (tMax - tMin) * (w - 2 * pad);
            double Y(double p) => h - pad - p / 100.0 * (h - 2 * pad);
            var wanted = Enumerable.Range((int)tMin * 2, (int)(tMax - tMin) * 2 + 1).Select(i => new Point(X(i / 2.0), Y(g[i]))).ToList();
            _preview.Children.Add(new Polygon { Fill = CurveUi.AreaFill, Points = new AvaloniaList<Point>(wanted) { new(X(tMax), Y(0)), new(X(tMin), Y(0)) } });
            _preview.Children.Add(new Polyline { Stroke = Brushes.White, StrokeThickness = 1.5, Points = new AvaloniaList<Point>(wanted) });
            var temps = new List<double> { tMin };
            temps.AddRange(plan.Points.Select(p => (double)p.TemperatureC).Where(t => t > tMin && t < tMax));
            temps.Add(tMax);
            _preview.Children.Add(new Polyline
            {
                Stroke = CurveUi.BmcLine, StrokeThickness = 1.5, StrokeDashArray = [4, 3],
                Points = new AvaloniaList<Point>(temps.Select(t => new Point(X(t), Y(BmcCurveFit.Duty(plan.Points, t))))),
            });
            var library = _w._runner.Store.Library;
            if (FanCurveMath.Sensors(_curve, library).Select(id => _w._hub.Get(id).Current).OfType<double>().DefaultIfEmpty(double.NaN).Max() is var now && !double.IsNaN(now))
            {
                var t = Math.Clamp(now, tMin, tMax);
                var dot = new Ellipse { Width = 9, Height = 9, Fill = CurveUi.BmcLine, Stroke = Brushes.White, StrokeThickness = 1 };
                Canvas.SetLeft(dot, X(t) - 4.5);
                Canvas.SetTop(dot, Y(BmcCurveFit.Duty(plan.Points, now)) - 4.5);
                _preview.Children.Add(dot);
            }
        }

        /// <summary>When the BMC's version differs from what the mix means, and how.</summary>
        public static string? MixWarning(FanCurve mix, IReadOnlyDictionary<string, FanCurve> library, BmcCurvePlan plan)
        {
            if (!plan.OnBmc) return null;
            var children = mix.Children.Where(library.ContainsKey).Select(id => library[id]).ToList();
            var graphs = children.Where(c => c.Kind == FanCurveKind.Graph).ToList();
            var inputs = graphs.Select(c => string.Join("|", c.Inputs.OrderBy(x => x))).Distinct().Count();
            var notes = new List<string>();
            if (inputs > 1)
            {
                if (mix.Function != FanMixFunction.Max)
                    notes.Add($"⚠ The BMC follows only the hottest sensor, so {mix.Function.ToString().ToLowerInvariant()} can't be kept: " +
                              "the fan runs as if every sensor were that hot (never slower than this mix, often faster).");
                else if (graphs.Select(c => string.Join(";", c.Points.OrderBy(p => p.TemperatureC)) + c.MaxSpeedPercent).Distinct().Count() > 1)
                    notes.Add("⚠ Different curves on different sensors: the BMC uses the higher curve at the hottest sensor, so the fan " +
                              "runs faster than this mix when a cooler sensor has the higher curve (never slower).");
            }
            if (plan.MaxExtraPercent >= 1)
                notes.Add($"The 5-point BMC curve runs up to {plan.MaxExtraPercent:0} % above it (at {plan.MaxExtraAtC:0.#} °C).");
            return notes.Count == 0 ? null : string.Join(" ", notes);
        }

        public void Update()
        {
            var reading = (Func<string, double?>)(id => _w._hub.Get(id).Current);
            _output.Text = _w._runner.Output(_curve, reading) is { } o ? $"{o:0.#} %" : "—";
            if (_curve.Kind == FanCurveKind.Graph)
            {
                var t = FanCurveMath.Hottest(_curve, reading);
                _sensorNow.Text = t is { } v ? $"at {v:0.#} °C" : _curve.Inputs.Count == 0 ? "no sensor" : "no reading";
                DrawPreview();
            }
            var library = _w._runner.Store.Library;
            if (_curve.Kind == FanCurveKind.Mix)
            {
                var plan = _curve.Children.Count == 0 ? null : _w._runner.PlanCurve(_curve);
                _mixPlan = plan;
                _mixWanted = plan is { OnBmc: true } ? BmcCurveFit.Requested(_curve, library, 1) : null;
                var warning = plan is null ? null : MixWarning(_curve, library, plan);
                _mixNote.Text = warning ?? "";
                _mixNote.IsVisible = warning is not null;
                _mixNote.Foreground = warning?.StartsWith('⚠') == true ? CurveUi.Warn : Secondary;
                DrawMixPreview();
            }
            var sensors = FanCurveMath.Sensors(_curve, library);
            var onBmc = sensors.All(s => _w._runner.BmcNumber(s) is not null);
            var fans = _w._runner.Store.Assignments.Where(a => a.Value == _curve.Id).Select(a => _w.Controls().FirstOrDefault(c => c.Id == a.Key)?.Title ?? a.Key).ToList();
            _where.Text = (onBmc ? "BMC sensors — runs on the BMC: one write to put it there, then none" : "⚠ Not a BMC sensor — on a BMC fan, every change is a flash write") +
                          (fans.Count > 0 ? "\nFans: " + string.Join(", ", fans) : "");
            _where.Foreground = onBmc ? CurveUi.BmcLine : CurveUi.Warn;
        }

        /// <summary>OpenFan's mini preview: white line, orange fill, the sensor's reading as a dot on the curve.</summary>
        private void DrawPreview()
        {
            _preview.Children.Clear();
            var w = Math.Max(_preview.Bounds.Width, 40);
            var h = _preview.Height;
            const double pad = 4;
            var tMin = _curve.MinTempC;
            var tMax = Math.Max(_curve.MinTempC + 10, _curve.MaxTempC);
            double X(double t) => pad + (t - tMin) / (tMax - tMin) * (w - 2 * pad);
            double Y(double p) => h - pad - p / 100.0 * (h - 2 * pad);
            var pts = _curve.Points.OrderBy(p => p.TemperatureC).ToList();
            if (pts.Count < 2) return;
            var xs = new List<double> { tMin };
            xs.AddRange(pts.Select(p => p.TemperatureC).Where(t => t > tMin && t < tMax));
            xs.Add(tMax);
            var line = xs.Select(t => new Point(X(t), Y(FanCurveMath.Graph(pts, t, _curve.MaxSpeedPercent)))).ToList();
            _preview.Children.Add(new Polygon { Fill = CurveUi.AreaFill, Points = new AvaloniaList<Point>(line) { new(X(tMax), Y(0)), new(X(tMin), Y(0)) } });
            _preview.Children.Add(new Polyline { Stroke = Brushes.White, StrokeThickness = 1.5, Points = new AvaloniaList<Point>(line) });
            if (FanCurveMath.Hottest(_curve, id => _w._hub.Get(id).Current) is { } now)
            {
                var t = Math.Clamp(now, tMin, tMax);
                var dot = new Ellipse { Width = 9, Height = 9, Fill = CurveUi.Dot, Stroke = Brushes.White, StrokeThickness = 1 };
                Canvas.SetLeft(dot, X(t) - 4.5);
                Canvas.SetTop(dot, Y(FanCurveMath.Graph(pts, now, _curve.MaxSpeedPercent)) - 4.5);
                _preview.Children.Add(dot);
            }
        }
    }

    private void NewCurve(FanCurveKind kind)
    {
        var id = FanCurveStore.NewId();
        var curve = kind switch
        {
            FanCurveKind.Graph => FanCurve.NewGraph(id, "New graph", _choices.FirstOrDefault(c => c.Bmc)?.Id),
            FanCurveKind.Flat => new FanCurve { Id = id, Name = "New flat", Kind = FanCurveKind.Flat, Percent = 50 },
            _ => new FanCurve { Id = id, Name = "New mix", Kind = FanCurveKind.Mix },
        };
        _runner.Store.Curves.Add(curve);
        Save();
        BuildCurves();
        BuildControls(); // the new curve appears in the fans' dropdowns
        if (kind == FanCurveKind.Graph) EditGraph(curve); // pick the sensor and shape right away (OpenFan)
    }

    private void EditGraph(FanCurve curve)
    {
        var editor = new GraphEditorWindow(_sensors, curve, edited =>
        {
            var at = _runner.Store.Curves.FindIndex(c => c.Id == edited.Id);
            if (at >= 0) _runner.Store.Curves[at] = edited;
            Save();
            BuildCurves();
            BuildControls();
        });
        Settings.WindowPlaces.Track(editor, "fan-curve-graph");
        editor.ShowDialog(this);
    }

    private async Task DeleteAsync(FanCurve curve)
    {
        var store = _runner.Store;
        var fans = store.Assignments.Count(a => a.Value == curve.Id);
        var mixes = store.Curves.Count(c => c.Children.Contains(curve.Id));
        var text = $"Delete \"{curve.Name}\"?" +
                   (fans > 0 ? $"\n\n{fans} fan(s) run it: BMC fans keep the BMC curve they have now; OpenHW-driven and NVIDIA fans go back to their own control." : "") +
                   (mixes > 0 ? $"\n\nIt's taken out of {mixes} mix(es)." : "");
        if (!await new ConfirmDialog("Fan Control", text, "Delete", false).ShowDialog<bool>(this)) return;
        store.Remove(curve.Id);
        Save();
        BuildCurves();
        BuildControls();
    }

    // ── every 2 s ────────────────────────────────────────────────────────────────────────────────────────────────

    private void Update()
    {
        foreach (var update in _updaters.ToList())
        {
            try { update(); }
            catch (InvalidOperationException) { } // a card mid-rebuild: the next tick is fine
        }
    }

    /// <summary>
    /// Sensors this window shows — every fan's RPM and duty and every curve's temperatures — so they stay live while
    /// it's open, even when their group is collapsed in the sensor window.
    /// </summary>
    public IReadOnlyList<string> LiveSensorIds()
    {
        var library = _runner.Store.Library;
        return Controls().SelectMany(c => c.RpmId is { } r ? new[] { r, c.Id } : [c.Id])
            .Concat(library.Values.SelectMany(c => FanCurveMath.Sensors(c, library)))
            .Concat(_runner.BmcSensors().Select(x => BmcEngine.IdPrefix + x.Name)).Distinct().ToList();
    }

    /// <summary>Text of every card (tests).</summary>
    public IReadOnlyList<string> Texts() => this.GetLogicalDescendantsTexts();
}

internal static class LogicalTexts
{
    public static IReadOnlyList<string> GetLogicalDescendantsTexts(this Avalonia.Controls.Control root) =>
        Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(root)
            .Select(c => c switch { TextBlock t => t.Text, TextBox b => b.Text, _ => null }).OfType<string>().ToList();
}
