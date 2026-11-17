using System.Globalization;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
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

    private sealed record Control0(string Id, string Title, string Group, string? RpmId, FanZoneStatus? Zone, GpuFanStatus? Gpu);

    private readonly Sensors.SensorHub _hub;
    private readonly FanClient _client;
    private readonly FanCurveRunner _runner;
    private readonly FanCurveSensors _sensors;
    private readonly WrapPanel _controls = new();
    private readonly WrapPanel _curves = new();
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Foreground = Secondary, FontSize = 13 };
    private readonly TextBlock _result = new() { TextWrapping = TextWrapping.Wrap, FontSize = 13 };
    private readonly Button _login;
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
        var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14 };
        title.Children.Add(new TextBlock { Text = "Fan Control", FontSize = 26, FontWeight = FontWeight.Bold });
        title.Children.Add(new TextBlock { Text = "BMC + NVML", Foreground = Secondary, FontSize = 14, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 4) });
        var top = new DockPanel();
        DockPanel.SetDock(_login, Dock.Right);
        top.Children.Add(_login);
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
        Closed += (_, _) => { _timer.Stop(); _runner.Store.Changed -= OnStoreChanged; };
        RefreshStatus(rebuild: true);
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
        var button = Fab(text, () => { });
        var menu = new MenuFlyout();
        button.Flyout = menu;
        menu.Opening += (_, _) =>
        {
            menu.Items.Clear();
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
        };
        return button;
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

    private void RefreshStatus(bool rebuild)
    {
        static bool Soft(Exception ex) => ex is IOException or System.Net.Sockets.SocketException or System.Text.Json.JsonException or FormatException;
        _error = null;
        try { _bmc = _client.Status(); }
        catch (Exception ex) when (Soft(ex))
        {
            _bmc = null;
            _error = ex.Message.Contains("unknown command", StringComparison.Ordinal)
                ? "openhw-helper is too old for fan control: install the current OpenHW .deb."
                : "openhw-helper isn't answering: " + ex.Message.Replace("openhw-helper: err ", "");
        }
        try { _gpu = _client.GpuStatus(); } catch (Exception ex) when (Soft(ex)) { _gpu = null; }
        try { _sources = _client.Sources(); } catch (Exception ex) when (Soft(ex)) { }
        try { _loginUser = _client.LoginUser(); } catch (Exception ex) when (Soft(ex)) { }
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
    }

    private List<Control0> Controls()
    {
        var list = new List<Control0>();
        foreach (var z in _bmc?.Zones ?? [])
            list.Add(new(z.Id, z.Name == "CPU_FAN" ? "CPU_FAN / CPU_OPT" : z.Name, "ASUS BMC · " + z.Label, z.RpmSensorId, z, null));
        foreach (var g in _gpu?.Fans ?? [])
            list.Add(new(g.Id, $"{ShortGpu(g.Card)} {ShortPci(g.Pci)} Fan {g.Fan}", $"{g.Card} {ShortPci(g.Pci)}", g.Id.Replace(":fan:", ":tach:"), null, g));
        return list;
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
        _updaters.RemoveAll(u => u.Target is ControlCard);
        var controls = Controls();
        _controlsShape = string.Join("|", controls.Select(c => c.Id + (c.Zone?.Owned ?? false))) + "#" + _bmc?.Available + _bmc?.OtherController;
        foreach (var c in controls)
        {
            var card = new ControlCard(this, c);
            _controls.Children.Add(card.Root);
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
            if (c.Zone is { } zone)
            {
                var menu = new Button { Content = "⋮", Background = Brushes.Transparent, BorderThickness = new Thickness(0), Foreground = Secondary, Padding = new Thickness(6, 0) };
                var edit = new MenuItem { Header = "Edit BMC curve…", IsEnabled = enabled && assigned is null && !zone.Owned };
                edit.Click += (_, _) => w.EditBmcCurve(zone);
                var restore = new MenuItem { Header = "Restore original curve", IsEnabled = enabled && zone.SavedOriginal is not null && !zone.Owned };
                restore.Click += (_, _) => w.RestoreOriginal(zone);
                menu.Flyout = new MenuFlyout { Items = { edit, restore } };
                ToolTip.SetTip(menu, assigned is null ? "This fan's own BMC curve" : "Untick Curve to edit this fan's BMC curve directly");
                DockPanel.SetDock(menu, Dock.Right);
                head.Children.Add(menu);
            }
            head.Children.Add(new TextBlock { Text = c.Title, FontWeight = FontWeight.SemiBold, FontSize = 14, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });

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
                        if (applied) Note("On the BMC ✓ — no flash writes", CurveUi.BmcLine);
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
                      ".\nAfter that the BMC runs it with no further writes, with or without OpenHW."
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
        _updaters.RemoveAll(u => u.Target is CurveCard);
        var order = new Dictionary<FanCurveKind, int> { [FanCurveKind.Flat] = 0, [FanCurveKind.Graph] = 1, [FanCurveKind.Mix] = 2 };
        foreach (var curve in _runner.Store.Curves.OrderBy(c => order[c.Kind]).ThenBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var card = new CurveCard(this, curve);
            _curves.Children.Add(card.Root);
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
            var sensor = new ComboBox { Width = 270, HorizontalAlignment = HorizontalAlignment.Left, HorizontalContentAlignment = HorizontalAlignment.Left };
            foreach (var c in _w._choices)
                sensor.Items.Add(new ComboBoxItem { Content = new TextBlock { Text = c.Label + (c.Bmc ? "" : "   ⚠") }, Tag = c.Id });
            sensor.SelectedItem = sensor.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string?)i.Tag == _curve.SensorId);
            sensor.PlaceholderText = _curve.SensorId is null ? "choose a temperature" : _curve.SensorId;
            sensor.SelectionChanged += (_, _) =>
            {
                if ((sensor.SelectedItem as ComboBoxItem)?.Tag is not string id || id == _curve.SensorId) return;
                _curve.SensorId = id;
                _w.Save();
                Update();
            };
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
        }

        public void Update()
        {
            var reading = (Func<string, double?>)(id => _w._hub.Get(id).Current);
            _output.Text = _w._runner.Output(_curve, reading) is { } o ? $"{o:0.#} %" : "—";
            if (_curve.Kind == FanCurveKind.Graph)
            {
                var t = _curve.SensorId is { } id ? reading(id) : null;
                _sensorNow.Text = t is { } v ? $"at {v:0.#} °C" : _curve.SensorId is null ? "no sensor" : "no reading";
                DrawPreview();
            }
            var library = _w._runner.Store.Library;
            var sensors = FanCurveMath.Sensors(_curve, library);
            var onBmc = sensors.All(s => _w._runner.BmcNumber(s) is not null);
            var fans = _w._runner.Store.Assignments.Where(a => a.Value == _curve.Id).Select(a => _w.Controls().FirstOrDefault(c => c.Id == a.Key)?.Title ?? a.Key).ToList();
            _where.Text = (onBmc ? "BMC sensors — runs on the BMC (no flash writes)" : "⚠ Not a BMC sensor — on a BMC fan, every change is a flash write") +
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
            if (_curve.SensorId is { } id && _w._hub.Get(id).Current is { } now)
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
        Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(root).OfType<TextBlock>().Select(t => t.Text ?? "").ToList();
}
