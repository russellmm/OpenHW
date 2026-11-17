using System.Globalization;
using System.Security.Cryptography;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using OpenHW.App.Alerts;
using OpenHW.App.SensorWindow;
using OpenHW.Hardware;

namespace OpenHW.App.Power;

/// <summary>Talks to openhw-helper's fan driver (protocol 13) as one client with its own random token.</summary>
public sealed class FanClient(Func<string, string>? request = null)
{
    private readonly Func<string, string> _request = request ?? (line =>
        HelperClient.Request(line, receiveTimeoutMs: 15000, maxReplyBytes: GpuWire.MaxReplyBytes));

    public string Token { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    public FanDriverStatus? Status(bool asOwner = false) => GpuWire.Decode<FanDriverStatus>(_request(asOwner ? "fans " + Token : "fans"));
    public string Set(string zone, int percent) => _request($"fanset {Token} {zone} {percent.ToString(CultureInfo.InvariantCulture)}");
    public string Release(string zone = "all") => _request($"fanrelease {Token} {zone}");
    public string Beat() => _request("fanbeat " + Token);

    // Configured curves and sources (protocol 14): written once, run by the BMC.
    public string SetCurve(string zone, IReadOnlyList<FanCurvePoint> curve) =>
        _request($"fancurve {zone} {string.Join(',', curve.Select(p => $"{p.TemperatureC}:{p.DutyPercent}"))}");
    public string RestoreOriginal(string zone) => _request("fanoriginal " + zone);
    public IReadOnlyDictionary<int, long> Sources() => GpuWire.Decode<Dictionary<int, long>>(_request("fansources")) ?? [];
    public string SetSource(string zone, long mask) => _request($"fansource {zone} {mask.ToString(CultureInfo.InvariantCulture)}");

    /// <summary>The user the helper's BMC login is for, or null when none is stored.</summary>
    public string? LoginUser() => _request("bmclogin") is var user && user != "none" ? user : null;
    public string SetLogin(string user, string password) =>
        _request($"bmclogin {Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(user))} {Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(password))}");
    public string Logout() => _request("bmclogout");

    /// <summary>The BMC temperature sensors a fan can follow: number 1–18 → name (bit = number − 1 in a source mask).</summary>
    public IReadOnlyList<(int Number, string Name)> SourceSensors()
    {
        var bmc = GpuWire.Decode<BmcWireReply>(_request("bmc"));
        return bmc?.Sensors.Where(x => x.Kind == HardwareKind.Temperature && x.Number is >= 1 and <= BmcRedfish.SourceBits)
            .Select(x => ((int)x.Number, x.Name)).OrderBy(x => x.Item1).ToList() ?? [];
    }

    public static string DescribeSource(long mask, IReadOnlyList<(int Number, string Name)> sensors)
    {
        if (mask <= 0 || mask >= 1L << BmcRedfish.SourceBits) return "–";
        var names = Enumerable.Range(1, BmcRedfish.SourceBits).Where(n => (mask & 1L << (n - 1)) != 0)
            .Select(n => sensors.FirstOrDefault(x => x.Number == n).Name ?? $"sensor {n}").ToList();
        return names.Count == 1 ? names[0] : "max of " + string.Join(", ", names);
    }

    public static string Describe(IReadOnlyList<FanCurvePoint>? curve) =>
        curve is null ? "–" : string.Join("  ", curve.Select(p => $"{p.TemperatureC}°→{p.DutyPercent}%"));

    public static string Controller(FanZoneStatus z) =>
        z.Target is { } t ? $"OpenHW: {t} %" + (z.Pending is { } p ? $" (→ {p} % when allowed)" : "") : "BMC curve";
}

/// <summary>
/// Power → Fan Control…: the BMC's fan zones — live RPM, the curve each fan runs (Power → Fan Curves…) and how: on the
/// BMC (its sensors and five points written once, no flash writes afterwards) or driven by OpenHW (a curve on a sensor
/// the BMC can't see: every duty change is a flash write, counted per day) — which BMC sensors it follows, its active
/// curve and the writes since the helper started. A fan without a curve keeps its BMC curve, editable with Edit….
/// Zone checks still run from <c>openhw --fan-test</c>.
/// </summary>
public sealed class FanControlWindow : Window
{
    private readonly Sensors.SensorHub _hub;
    private readonly FanClient _client;
    private readonly FanCurveRunner _runner;
    private readonly StackPanel _body = new() { Spacing = 6, Margin = new Thickness(14, 10) };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly TextBlock _result = new() { TextWrapping = TextWrapping.Wrap };
    private IReadOnlyList<(int Number, string Name)> _sensors = [];
    private bool _choosing;

    public FanControlWindow(Sensors.SensorHub hub, FanClient? client = null, FanCurveRunner? runner = null, Action? showCurves = null)
    {
        _hub = hub;
        _client = client ?? runner?.Client ?? new FanClient();
        _runner = runner ?? new FanCurveRunner(_client, FanCurveStore.Load());
        ShowCurves = showCurves;
        Title = "Fan Control";
        Width = 1280;
        SizeToContent = SizeToContent.Height;
        Background = Palette.Window;
        Foreground = Palette.Text;
        FontSize = 12.5;
        Content = _body;
        _timer.Tick += (_, _) => { if (!_choosing) Refresh(); };
        Opened += (_, _) => { Refresh(); _timer.Start(); };
        Closed += (_, _) => { _timer.Stop(); _runner.Store.Changed -= OnStoreChanged; };
        _runner.Store.Changed += OnStoreChanged;
        Refresh();
    }

    private Action? ShowCurves { get; }

    private void OnStoreChanged() => Dispatcher.UIThread.Post(() => { if (!_choosing) Refresh(); });

    public void Refresh()
    {
        FanDriverStatus? status = null;
        string? error = null;
        try { status = _client.Status(); }
        catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or System.Text.Json.JsonException or FormatException)
        {
            error = ex.Message.Contains("unknown command", StringComparison.Ordinal)
                ? "openhw-helper is too old for fan control: install the current OpenHW .deb."
                : "openhw-helper isn't answering: " + ex.Message.Replace("openhw-helper: err ", "");
        }
        Draw(status, error);
    }

    private void Draw(FanDriverStatus? status, string? error)
    {
        _body.Children.Clear();
        _body.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Foreground = Palette.Footnote,
            Text = "Pick a curve for each fan (make curves in Power → Fan Curves…). A curve that reads only the BMC's own " +
                   "sensors runs on the BMC: its sensors and five points are written once and the BMC runs it from then on, " +
                   "with or without OpenHW. A curve that reads any other sensor (an NVIDIA GPU, a drive…) is driven by OpenHW: " +
                   "every duty change is a write to the BMC's flash. A fan without a curve keeps its BMC curve (Edit… changes it " +
                   "directly). The curve a fan had before OpenHW first changed it is kept for Restore original.",
        });
        if (error is not null || status is null)
        {
            _body.Children.Add(new TextBlock { Text = error ?? "No answer.", Foreground = AlertUi.Alarm, TextWrapping = TextWrapping.Wrap });
            return;
        }
        if (!status.Available)
            _body.Children.Add(new TextBlock { Text = "Fan control is off: " + status.Reason, Foreground = AlertUi.Alarm, TextWrapping = TextWrapping.Wrap });
        if (status.OtherController is { } other)
            _body.Children.Add(new TextBlock { Text = other + " OpenHW won't take any fan over until it lets go.", Foreground = AlertUi.Alarm, TextWrapping = TextWrapping.Wrap });

        string? login = null;
        IReadOnlyDictionary<int, long> sources = new Dictionary<int, long>();
        // An older helper (protocol 13) has none of these: the page still shows the zones.
        static bool Soft(Exception ex) => ex is IOException or System.Net.Sockets.SocketException or System.Text.Json.JsonException or FormatException;
        try { login = _client.LoginUser(); } catch (Exception ex) when (Soft(ex)) { }
        try { sources = _client.Sources(); } catch (Exception ex) when (Soft(ex)) { }
        try { if (_sensors.Count == 0) _sensors = _client.SourceSensors(); } catch (Exception ex) when (Soft(ex)) { }
        var loginRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 4, 0, 0) };
        loginRow.Children.Add(new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            Text = login is null ? "BMC login for changing temperature sources: none stored." : $"BMC login for temperature sources: {login}",
            Foreground = login is null ? Palette.Footnote : Palette.Text,
        });
        loginRow.Children.Add(AlertUi.Button(login is null ? "Give OpenHW the BMC login…" : "Change…", () =>
        {
            var dialog = new BmcLoginDialog(_client);
            Settings.WindowPlaces.Track(dialog, "bmc-login");
            dialog.ShowDialog(this);
        }));
        if (ShowCurves is { } curves) loginRow.Children.Add(AlertUi.Button("Fan Curves…", curves));
        _body.Children.Add(loginRow);

        var store = _runner.Store;
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("140,60,190,230,200,*,50,90"), Margin = new Thickness(0, 6, 0, 0) };
        void Put(Control c, int row, int col) { Grid.SetRow(c, row); Grid.SetColumn(c, col); grid.Children.Add(c); }
        void Cell(int row, int col, string text, IBrush? brush = null, FontWeight weight = FontWeight.Normal) =>
            Put(new TextBlock { Text = text, Foreground = brush ?? Palette.Text, FontWeight = weight, Margin = new Thickness(0, 2, 10, 2),
                TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center }, row, col);
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        string[] headers = ["Fan", "RPM", "Curve", "Runs", "Follows", "Active curve (°C → duty)", "Writes", ""];
        for (var c = 0; c < headers.Length; c++) Cell(0, c, headers[c], Palette.Footnote, FontWeight.SemiBold);
        var row = 1;
        var choices = new List<(string? Id, string Name)> { (null, "BMC curve (as set)") };
        choices.AddRange(store.Curves.Select(c => ((string?)c.Id, c.Name)));
        foreach (var z in status.Zones)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var rpm = _hub.Get(z.RpmSensorId).Current is { } r ? r.ToString("N0", CultureInfo.CurrentCulture) : "–";
            var index = z.Id[^1] - '0';
            long? mask = sources.TryGetValue(index, out var m) ? m : null;
            Cell(row, 0, z.Name == "CPU_FAN" ? "CPU_FAN + CPU_OPT" : z.Name);
            Cell(row, 1, rpm);

            var assigned = store.AssignedTo(z.Id);
            var pick = new ComboBox { Width = 180, Margin = new Thickness(0, 1, 10, 1), IsEnabled = status.Available && status.OtherController is null };
            foreach (var (id, name) in choices) pick.Items.Add(new ComboBoxItem { Content = new TextBlock { Text = name }, Tag = id });
            pick.SelectedIndex = assigned is null ? 0 : Math.Max(0, choices.FindIndex(c => c.Id == assigned.Id));
            var zone = z;
            pick.SelectionChanged += (_, _) =>
            {
                var chosen = (pick.SelectedItem as ComboBoxItem)?.Tag as string;
                if (chosen != store.AssignedTo(zone.Id)?.Id) _ = ChooseAsync(zone, chosen, mask);
            };
            Put(pick, row, 2);

            var state = _runner.State(z.Id);
            BmcCurvePlan? plan = assigned is null ? null : _runner.Plan(z.Id, assigned);
            var applied = plan is { OnBmc: true } && z.Active is { } a && plan.Points.SequenceEqual(a) && (plan.SourceMask is null || plan.SourceMask == mask);
            var (runs, brush) = assigned is null ? (FanClient.Controller(z), z.Target is null ? Palette.Footnote : Palette.Text)
                : plan!.OnBmc ? (applied ? "On the BMC ✓ (no flash writes)" : "On the BMC — not applied yet", applied ? CurveUi.BmcLine : CurveUi.Warn)
                : ($"OpenHW-driven: {(state.Duty is { } d ? $"{d} %" : "starting")} · {state.WritesToday} writes today", CurveUi.Warn);
            if (state.Problem is { } problem) (runs, brush) = (runs + " — " + problem, AlertUi.Alarm);
            Cell(row, 3, runs, brush);
            Cell(row, 4, mask is { } mm ? FanClient.DescribeSource(mm, _sensors) : "–");
            Cell(row, 5, FanClient.Describe(z.Active));
            Cell(row, 6, z.Writes.ToString(CultureInfo.CurrentCulture));
            Button button;
            if (assigned is not null && plan!.OnBmc && !applied)
                button = AlertUi.Button("Apply", () => Apply(zone, mask));
            else
            {
                button = AlertUi.Button("Edit…", () =>
                {
                    _timer.Stop();
                    var dialog = new FanCurveDialog(_client, zone, mask ?? 0, _sensors, login is not null);
                    Settings.WindowPlaces.Track(dialog, "fan-curve");
                    dialog.Closed += (_, _) => { Refresh(); _timer.Start(); };
                    dialog.ShowDialog(this);
                });
                button.IsEnabled = status.Available && !z.Owned && status.OtherController is null && assigned is null;
                if (assigned is not null) ToolTip.SetTip(button, "This fan runs a curve from Fan Curves: edit that curve, or pick \"BMC curve (as set)\".");
            }
            button.Padding = new Thickness(8, 1);
            Put(button, row, 7);
            row++;
        }
        _body.Children.Add(grid);
        _body.Children.Add(_result);
        _body.Children.Add(new TextBlock
        {
            Foreground = Palette.Footnote, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0),
            Text = $"Writes counted since the helper started ({status.SinceUtc.ToLocalTime():yyyy-MM-dd HH:mm}). " +
                   "Check a fan: openhw --fan-test bmc:<n> <1–100> --seconds 10 (it takes the fan, holds the duty, then gives it back " +
                   "and compares the curve).",
        });
    }

    private void Apply(FanZoneStatus zone, long? mask)
    {
        try { Show(_runner.ApplyBmc(zone, mask), false); }
        catch (IOException ex) { Show(ex.Message.Replace("openhw-helper: err ", ""), true); }
        Refresh();
    }

    /// <summary>A fan gets a curve (or goes back to its BMC curve): says what that costs, then does it.</summary>
    private async Task ChooseAsync(FanZoneStatus zone, string? curveId, long? mask)
    {
        _choosing = true;
        try
        {
            var name = zone.Name == "CPU_FAN" ? "CPU_FAN + CPU_OPT" : zone.Name;
            if (curveId is null)
            {
                _runner.Unassign(zone.Id);
                Show($"{name} keeps the BMC curve it has now.", false);
                return;
            }
            var curve = _runner.Store.Find(curveId)!;
            var plan = _runner.Plan(zone.Id, curve);
            var choices = new FanCurveSensors(_hub, _runner).Choices();
            var text = plan.OnBmc
                ? $"{name} will run \"{curve.Name}\" on the BMC.\n\nWritten once: " +
                  (plan.SourceMask is { } sm && sm != mask ? $"its sensors ({FanClient.DescribeSource(sm, _sensors)}) and " : "") +
                  $"the curve {CurveUi.Points(plan.Points)}" +
                  (plan.MaxExtraPercent >= 0.5 ? $" — at most {plan.MaxExtraPercent:0} % faster than drawn, at {plan.MaxExtraAtC:0.#} °C" : "") +
                  ".\nAfter that the BMC runs it with no further writes, with or without OpenHW."
                : $"⚠ \"{curve.Name}\" reads {string.Join(", ", plan.OtherSensors.Select(id => choices.FirstOrDefault(c => c.Id == id)?.Label ?? id))}, " +
                  "which the BMC can't see.\n\nOpenHW will drive " + name + " itself: every duty change is a write to the BMC's flash. " +
                  $"Duties move in {FanCurveRunner.DrivenStep} % steps, faster at once, slower only after a minute; each write is counted " +
                  "per day on this page. Starting and stopping OpenHW cost a write each (the fan returns to its BMC curve when " +
                  "OpenHW stops).";
            var ok = await new ConfirmDialog("Fan Control — " + name, text, plan.OnBmc ? "Write to the BMC" : "Let OpenHW drive it", !plan.OnBmc).ShowDialog<bool>(this);
            if (!ok) return;
            _runner.Assign(zone.Id, curveId);
            if (plan.OnBmc) Apply(zone, mask);
            else Show($"OpenHW drives {name} from its next reading.", false);
        }
        finally
        {
            _choosing = false;
            Refresh();
        }
    }

    private void Show(string text, bool bad)
    {
        _result.Text = text;
        _result.Foreground = bad ? AlertUi.Alarm : AlertUi.Ok;
    }
}

/// <summary>A yes/no question with the costs spelled out; true when confirmed.</summary>
public sealed class ConfirmDialog : Window
{
    public ConfirmDialog(string title, string text, string yes, bool warn)
    {
        CurveUi.Dialog(this, title, 560);
        var body = new StackPanel { Spacing = 10, Margin = new Thickness(14, 12) };
        body.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = warn ? CurveUi.Warn : Palette.Text });
        var buttons = AlertUi.Row(AlertUi.Button("Cancel", () => Close(false)), AlertUi.Button(yes, () => Close(true)));
        buttons.HorizontalAlignment = HorizontalAlignment.Right;
        body.Children.Add(buttons);
        Content = body;
        Settings.WindowPlaces.Track(this, "fan-confirm");
    }
}

/// <summary><c>openhw --fan-test bmc:N PERCENT [--seconds S]</c>: one guarded zone check from a terminal.</summary>
public static class FanTest
{
    public static int Run(string[] args)
    {
        var i = Array.IndexOf(args, "--fan-test");
        if (i < 0 || i + 2 >= args.Length || !int.TryParse(args[i + 2], NumberStyles.None, CultureInfo.InvariantCulture, out var percent))
        {
            Console.Error.WriteLine("usage: openhw --fan-test bmc:<0-6> <1-100> [--seconds <5-120>]");
            return 2;
        }
        var zone = args[i + 1];
        var s = Array.IndexOf(args, "--seconds");
        var seconds = s >= 0 && s + 1 < args.Length && int.TryParse(args[s + 1], out var v) ? Math.Clamp(v, 5, 120) : 10;
        var client = new FanClient();
        var before = client.Status()?.Zones.FirstOrDefault(z => z.Id == zone);
        if (before is null) { Console.Error.WriteLine($"No fan zone {zone}."); return 2; }
        Console.WriteLine($"{zone} {before.Name}: {FanClient.Controller(before)}; curve {FanClient.Describe(before.Active)}");
        double? Rpm()
        {
            try
            {
                var bmc = GpuWire.Decode<BmcWireReply>(HelperClient.Request("bmc", receiveTimeoutMs: 15000, maxReplyBytes: GpuWire.MaxReplyBytes));
                return bmc?.Sensors.FirstOrDefault(x => x.Name == before.Name)?.Value;
            }
            catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or System.Text.Json.JsonException or FormatException) { return null; }
        }
        Console.WriteLine($"  before: {Rpm()?.ToString("N0", CultureInfo.InvariantCulture) ?? "–"} RPM");
        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
        try
        {
            Console.WriteLine("  set: " + client.Set(zone, percent));
            for (var t = 0; t < seconds && !stop.IsCancellationRequested; t += 2)
            {
                stop.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(2));
                client.Beat();
                Console.WriteLine($"  +{t + 2,3} s: {Rpm()?.ToString("N0", CultureInfo.InvariantCulture) ?? "–"} RPM");
            }
        }
        catch (IOException ex) { Console.Error.WriteLine("  " + ex.Message.Replace("openhw-helper: err ", "")); }
        finally
        {
            try { Console.WriteLine("  release: " + client.Release(zone)); }
            catch (IOException ex) { Console.Error.WriteLine("  release failed (the 15 s lease will restore it): " + ex.Message); }
        }
        var after = client.Status()?.Zones.FirstOrDefault(z => z.Id == zone);
        var same = after?.Active is { } a && before.Active is { } b && a.SequenceEqual(b);
        Console.WriteLine($"  after: {FanClient.Controller(after!)}; curve {FanClient.Describe(after?.Active)} — " +
                          (same ? "matches the curve before the test" : "DIFFERS from the curve before the test"));
        stop.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(4));
        Console.WriteLine($"  4 s later: {Rpm()?.ToString("N0", CultureInfo.InvariantCulture) ?? "–"} RPM");
        return same ? 0 : 1;
    }
}
