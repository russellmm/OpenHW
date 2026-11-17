using System.Globalization;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using OpenHW.App.Alerts;
using OpenHW.App.SensorWindow;
using OpenHW.Hardware;

namespace OpenHW.App.Power;

/// <summary>A temperature a curve can follow. <see cref="Bmc"/>: one of the BMC's own sensors (no flash writes).</summary>
public sealed record FanSensorChoice(string Id, string Label, bool Bmc);

/// <summary>The temperature sensors curves can use and their live readings — from the sensor hub, BMC sensors first.</summary>
public sealed class FanCurveSensors(Sensors.SensorHub hub, FanCurveRunner runner)
{
    public FanCurveRunner Runner => runner;

    public IReadOnlyList<FanSensorChoice> Choices()
    {
        var groups = Sensors.SensorPresentation.Build(hub.Inventory, hub.Children, hub.Machine);
        string Group(string id) => groups.FirstOrDefault(g => Flatten(g.Sensors).Any(v => v.Id == id))?.Title ?? "";
        return hub.Inventory.Where(i => i.Kind == HardwareKind.Temperature)
            .Select(i =>
            {
                var bmc = runner.BmcNumber(i.Id) is not null;
                var name = Sensors.SensorPresentation.DisplayName(i);
                return new FanSensorChoice(i.Id, bmc ? $"BMC · {name}" : $"{name} · {Group(i.Id)}", bmc);
            })
            .OrderBy(c => c.Bmc ? 0 : 1).ThenBy(c => c.Label, StringComparer.CurrentCulture).ToList();
    }

    public double? Reading(string id) => hub.Get(id).Current;

    private static IEnumerable<Sensors.SensorView> Flatten(IEnumerable<Sensors.SensorView> views) => views.SelectMany(v => Flatten(v.Children).Prepend(v));
}

/// <summary>
/// The temperature source of a graph: one sensor or several ("bulk": the hottest counts — what the BMC does with a fan's
/// ticked sensors, as in its web console's checkbox list). A dropdown-like button; its flyout has a checkbox per sensor,
/// the BMC's own first (no flash writes), quick All DIMMs / All PCIe slots toggles, and each sensor's live reading.
/// </summary>
public sealed class SensorPicker : Button
{
    private readonly IReadOnlyList<FanSensorChoice> _choices;
    private readonly Func<string, double?> _reading;
    private readonly Action<IReadOnlyList<string>> _changed;
    private readonly List<(FanSensorChoice Choice, CheckBox Box)> _boxes = [];
    private readonly TextBlock _summary = new() { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    private List<string> _selected;
    private bool _suppress;

    protected override Type StyleKeyOverride => typeof(Button);

    public SensorPicker(IReadOnlyList<FanSensorChoice> choices, IReadOnlyList<string> selected, Func<string, double?> reading,
        Action<IReadOnlyList<string>> changed, double width = 270)
    {
        _choices = choices;
        _reading = reading;
        _changed = changed;
        _selected = [.. selected];
        Width = width;
        HorizontalAlignment = HorizontalAlignment.Left;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        Padding = new Thickness(10, 4);
        Background = Palette.Window;
        BorderBrush = FooterIcons.ButtonEdge;
        BorderThickness = new Thickness(1);
        Foreground = Palette.Text;
        var arrow = new TextBlock { Text = "▾", Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(arrow, Dock.Right);
        Content = new DockPanel { Children = { arrow, _summary } };

        var list = new StackPanel { Spacing = 1 };
        Button Group(string text, Func<FanSensorChoice, bool> which)
        {
            var b = AlertUi.Button(text, () => Toggle(which));
            b.Padding = new Thickness(8, 1);
            return b;
        }
        list.Children.Add(AlertUi.Row(Group("All DIMMs", c => c.Bmc && c.Label.Contains("DIMM", StringComparison.Ordinal)),
            Group("All PCIe slots", c => c.Bmc && c.Label.Contains("PCIE", StringComparison.Ordinal))));
        void Header(string text, IBrush brush) =>
            list.Children.Add(new TextBlock { Text = text, Foreground = brush, FontSize = 11, Margin = new Thickness(0, 6, 0, 2) });
        Header("BMC sensors — the BMC can run these (no flash writes after Apply)", CurveUi.BmcLine);
        foreach (var c in choices.Where(c => c.Bmc)) Add(list, c);
        Header("Other sensors — ⚠ on a BMC fan, OpenHW drives it (flash writes)", CurveUi.Warn);
        foreach (var c in choices.Where(c => !c.Bmc)) Add(list, c);
        foreach (var id in _selected.Where(id => choices.All(c => c.Id != id)))
            Add(list, new FanSensorChoice(id, id + " (not present)", false));
        var flyout = new Flyout
        {
            Placement = PlacementMode.BottomEdgeAlignedLeft,
            Content = new ScrollViewer { MaxHeight = 520, Width = 440, Content = list },
        };
        flyout.Opening += (_, _) => Refresh();
        Flyout = flyout;
        UpdateSummary();
    }

    public IReadOnlyList<string> Selected => _selected;

    private void Add(StackPanel list, FanSensorChoice c)
    {
        var box = new CheckBox { Content = new TextBlock { Text = c.Label }, IsChecked = _selected.Contains(c.Id), Padding = new Thickness(4, 0) };
        box.IsCheckedChanged += (_, _) =>
        {
            if (_suppress) return;
            var now = _boxes.Where(b => b.Box.IsChecked == true).Select(b => b.Choice.Id).ToList();
            if (now.Count == 0) { _suppress = true; box.IsChecked = true; _suppress = false; return; } // a graph needs a sensor
            Commit(now);
        };
        _boxes.Add((c, box));
        list.Children.Add(box);
    }

    /// <summary>Ticks every sensor of a group, or unticks them all when they're all ticked (one stays if nothing else is).</summary>
    public void Toggle(Func<FanSensorChoice, bool> which)
    {
        var group = _boxes.Where(b => which(b.Choice)).ToList();
        if (group.Count == 0) return;
        var all = group.All(b => b.Box.IsChecked == true);
        _suppress = true;
        foreach (var b in group) b.Box.IsChecked = !all;
        if (_boxes.All(b => b.Box.IsChecked != true)) group[0].Box.IsChecked = true;
        _suppress = false;
        Commit(_boxes.Where(b => b.Box.IsChecked == true).Select(b => b.Choice.Id).ToList());
    }

    /// <summary>Ticks exactly these sensors (tests).</summary>
    public void Select(IEnumerable<string> ids)
    {
        var set = ids.ToHashSet();
        _suppress = true;
        foreach (var b in _boxes) b.Box.IsChecked = set.Contains(b.Choice.Id);
        _suppress = false;
        Commit(_boxes.Where(b => b.Box.IsChecked == true).Select(b => b.Choice.Id).ToList());
    }

    private void Commit(List<string> ids)
    {
        if (ids.SequenceEqual(_selected)) return;
        _selected = ids;
        UpdateSummary();
        _changed(ids);
    }

    /// <summary>Live readings in the list (on opening).</summary>
    public void Refresh()
    {
        foreach (var (c, box) in _boxes)
            ((TextBlock)box.Content!).Text = _reading(c.Id) is { } v ? $"{c.Label}   —   {v:0.#} °C" : c.Label;
    }

    private void UpdateSummary()
    {
        _summary.Text = Summary(_selected, _choices);
        ToolTip.SetTip(this, _selected.Count > 1
            ? "Follows the hottest of:\n" + string.Join("\n", _selected.Select(id => _choices.FirstOrDefault(c => c.Id == id)?.Label ?? id))
            : "Click to choose one sensor or several (the hottest counts)");
    }

    public static string Summary(IReadOnlyList<string> ids, IReadOnlyList<FanSensorChoice> choices)
    {
        string Label(string id) => (choices.FirstOrDefault(c => c.Id == id)?.Label ?? id).Replace("BMC · ", "") is var l && l.StartsWith(BmcEngine.IdPrefix, StringComparison.Ordinal) ? l[BmcEngine.IdPrefix.Length..] : l;
        return ids.Count switch
        {
            0 => "choose a temperature",
            1 => Label(ids[0]),
            _ => $"Hottest of {ids.Count}: " + string.Join(", ", ids.Select(Label)),
        };
    }
}

/// <summary>Shared look for the curve windows.</summary>
internal static class CurveUi
{
    public static readonly IBrush GridLine = new SolidColorBrush(Color.Parse("#262E34"));
    public static readonly IBrush AxisText = new SolidColorBrush(Color.Parse("#7D8B94"));
    public static readonly IBrush AreaFill = new SolidColorBrush(Color.FromArgb(0x59, 0xF0, 0xA0, 0x3C));
    public static readonly IBrush Dot = new SolidColorBrush(Color.Parse("#F0A03C"));
    public static readonly IBrush Marker = new SolidColorBrush(Color.Parse("#4FC3F7"));
    public static readonly IBrush BmcLine = new SolidColorBrush(Color.Parse("#7BC97B"));
    public static readonly IBrush Warn = new SolidColorBrush(Color.Parse("#E5C05B"));

    public static Window Dialog(Window window, string title, double width)
    {
        window.Title = title;
        window.Width = width;
        window.SizeToContent = SizeToContent.Height;
        window.Background = Palette.Window;
        window.Foreground = Palette.Text;
        window.FontSize = 12.5;
        return window;
    }

    public static NumericUpDown Number(double value, double min, double max, double step, string format = "0", double width = 120) =>
        new() { Minimum = (decimal)min, Maximum = (decimal)max, Increment = (decimal)step, FormatString = format, Value = (decimal)value, Width = width };

    public static TextBlock Label(string text, double width = 0) => new()
    {
        Text = text, VerticalAlignment = VerticalAlignment.Center, Foreground = Palette.Footnote, Width = width > 0 ? width : double.NaN,
    };

    /// <summary>One line on where a curve runs: on the BMC (no flash writes) or driven by OpenHW (flash writes).</summary>
    public static (string Text, IBrush Brush) Where(BmcCurvePlan plan, IReadOnlyList<FanSensorChoice> choices)
    {
        if (plan.OnBmc)
            return ($"Runs on the BMC: one write to put it there, then no flash writes. BMC curve: {Points(plan.Points)}" +
                    (plan.MaxExtraPercent >= 0.5 ? $" (at most {plan.MaxExtraPercent:0} % faster than drawn, at {plan.MaxExtraAtC:0.#} °C)." : "."), BmcLine);
        var names = plan.OtherSensors.Select(id => choices.FirstOrDefault(c => c.Id == id)?.Label ?? id);
        return ($"⚠ Reads {string.Join(", ", names)}, which the BMC can't see: OpenHW drives a fan on this curve, and every " +
                "duty change is a write to the BMC's flash (5 % steps, slow-downs held a minute, counted per day).", Warn);
    }

    public static string Describe(FanCurve c, IReadOnlyList<FanSensorChoice> choices) => c.Kind switch
    {
        FanCurveKind.Flat => $"flat {c.Percent:0} %",
        FanCurveKind.Graph => "graph on " + SensorPicker.Summary(c.Inputs, choices),
        _ => $"{c.Function.ToString().ToLowerInvariant()} of {c.Children.Count}",
    };

    public static string Points(IReadOnlyList<FanCurvePoint> p) => string.Join("  ", p.Select(x => $"{x.TemperatureC}°→{x.DutyPercent}%"));
}

/// <summary>
/// Edits a graph curve the way OpenFan does (ported from OpenFan's GraphEditorWindow): drag points, click to add,
/// numeric edit of the selected point, sensor, hysteresis, axis range and max speed; a blue line at the sensor's current
/// temperature. Adds what this board needs: the green dashed line is what the BMC would run (four free points plus
/// one at 100 °C, never slower than drawn) and a note whether the curve runs on the BMC or costs flash writes.
/// Edits go to a copy; Ok saves.
/// </summary>
public sealed class GraphEditorWindow : Window
{
    private const double Pad = 36;
    private readonly FanCurveSensors _sensors;
    private readonly FanCurve _edit;
    private readonly Action<FanCurve> _onOk;
    private readonly Canvas _plot = new() { Height = 360, Background = new SolidColorBrush(Color.Parse("#11161A")), ClipToBounds = true };
    private SensorPicker _sensor = null!;
    private readonly TextBlock _temp = new() { Foreground = Palette.Footnote };
    private readonly TextBlock _where = new() { TextWrapping = TextWrapping.Wrap };
    private readonly NumericUpDown _selTemp = CurveUi.Number(0, 0, 100, 0.5, "0.#", 130), _selPct = CurveUi.Number(0, 0, 100, 1, "0", 130);
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _fitDelay = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private IReadOnlyList<FanSensorChoice> _choices = [];
    private BmcCurvePlan? _plan;
    private int _dragging = -1, _selected = -1;
    private bool _suppress;

    public GraphEditorWindow(FanCurveSensors sensors, FanCurve curve, Action<FanCurve> onOk)
    {
        _sensors = sensors;
        _edit = curve.Clone();
        _onOk = onOk;
        CurveUi.Dialog(this, $"Edit graph — {curve.Name}", 900);

        var name = new TextBox { Text = _edit.Name, Width = 220 };
        name.TextChanged += (_, _) => { if (!string.IsNullOrWhiteSpace(name.Text)) _edit.Name = name.Text.Trim(); };
        _choices = sensors.Choices();
        _sensor = new SensorPicker(_choices, _edit.Inputs, sensors.Reading, ids => { _edit.SetInputs(ids); RefreshTemp(); Refit(); }, 420);

        var hystC = CurveUi.Number(_edit.HysteresisC, 0, 20, 0.5, "0.#", 130);
        var hystS = CurveUi.Number(_edit.HysteresisS, 0, 120, 1, "0", 130);
        var maxSpeed = CurveUi.Number(_edit.MaxSpeedPercent, 1, 100, 1, "0", 130);
        var minTemp = CurveUi.Number(_edit.MinTempC, 0, 90, 5, "0", 130);
        var maxTemp = CurveUi.Number(_edit.MaxTempC, 20, 110, 5, "0", 130);
        hystC.ValueChanged += (_, e) => { if (e.NewValue is { } v) _edit.HysteresisC = (double)v; };
        hystS.ValueChanged += (_, e) => { if (e.NewValue is { } v) _edit.HysteresisS = (double)v; };
        maxSpeed.ValueChanged += (_, e) => { if (e.NewValue is { } v) { _edit.MaxSpeedPercent = (int)v; Render(); Refit(); } };
        minTemp.ValueChanged += (_, e) => { if (e.NewValue is { } v && (double)v < TMax - 5) { _edit.MinTempC = (double)v; Render(); } };
        maxTemp.ValueChanged += (_, e) => { if (e.NewValue is { } v && (double)v > TMin + 5) { _edit.MaxTempC = (double)v; Render(); } };
        _selTemp.ValueChanged += (_, e) =>
        {
            if (_suppress || _selected < 0 || e.NewValue is null) return;
            _edit.Points[_selected] = _edit.Points[_selected] with { TemperatureC = (double)e.NewValue.Value };
            Render(); Refit();
        };
        _selPct.ValueChanged += (_, e) =>
        {
            if (_suppress || _selected < 0 || e.NewValue is null) return;
            _edit.Points[_selected] = _edit.Points[_selected] with { Percent = (double)e.NewValue.Value };
            Render(); Refit();
        };

        _plot.PointerPressed += OnPressed;
        _plot.PointerMoved += OnMoved;
        _plot.PointerReleased += (_, e) => { if (_dragging >= 0) { _dragging = -1; e.Pointer.Capture(null); Refit(); } };
        _plot.SizeChanged += (_, _) => Render();
        _fitDelay.Tick += (_, _) => { _fitDelay.Stop(); Fit(); };

        var heading = new StackPanel { Spacing = 2 };
        heading.Children.Add(new TextBlock { Text = $"Edit graph — {curve.Name}", FontSize = 18, FontWeight = FontWeight.SemiBold });
        heading.Children.Add(_temp);
        var body = new StackPanel { Spacing = 8, Margin = new Thickness(14, 10) };
        var hysteresis = AlertUi.Row(CurveUi.Label("Hysteresis"), hystC, CurveUi.Label("°C ·"), hystS, CurveUi.Label("sec"));
        hysteresis.VerticalAlignment = VerticalAlignment.Top;
        DockPanel.SetDock(hysteresis, Dock.Right);
        body.Children.Add(new DockPanel { Children = { hysteresis, heading } });
        body.Children.Add(new Border { Child = _plot, BorderBrush = CurveUi.GridLine, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6) });
        body.Children.Add(AlertUi.Row(CurveUi.Label("Name", 70), name, CurveUi.Label("Sensor"), _sensor));
        body.Children.Add(AlertUi.Row(CurveUi.Label("Selected", 70), _selTemp, CurveUi.Label("°C"), _selPct, CurveUi.Label("%"),
            AlertUi.Button("+ Point", AddPoint), AlertUi.Button("− Point", RemoveSelected)));
        body.Children.Add(AlertUi.Row(CurveUi.Label("Max speed", 70), maxSpeed, CurveUi.Label("Min temp"), minTemp, CurveUi.Label("Max temp"), maxTemp));
        body.Children.Add(_where);
        body.Children.Add(new TextBlock { Foreground = Palette.Footnote, TextWrapping = TextWrapping.Wrap,
            Text = "Click the graph to add a point, drag to move one. Orange: the curve as drawn. Green dashed: what the BMC runs " +
                   "for it (a BMC curve has four free points and one at 100 °C, and the fan follows the hottest of its sensors). " +
                   "Hysteresis applies when OpenHW drives the fan; the BMC uses its own." });
        var ok = AlertUi.Button("Ok", () =>
        {
            _edit.Points = [.. _edit.Points.OrderBy(p => p.TemperatureC)];
            _onOk(_edit);
            Close();
        });
        ok.Background = CurveUi.Dot;
        ok.Foreground = Brushes.Black;
        var buttons = AlertUi.Row(AlertUi.Button("Cancel", Close), ok);
        buttons.HorizontalAlignment = HorizontalAlignment.Right;
        body.Children.Add(buttons);
        Content = body;

        _timer.Tick += (_, _) => RefreshTemp();
        Opened += (_, _) => { _timer.Start(); RefreshTemp(); Fit(); };
        Closed += (_, _) => { _timer.Stop(); _fitDelay.Stop(); };
        Fit();
    }

    /// <summary>The curve as edited so far (tests).</summary>
    public FanCurve Working => _edit;
    public BmcCurvePlan? Plan => _plan;

    private double TMin => _edit.MinTempC;
    private double TMax => Math.Max(_edit.MinTempC + 10, _edit.MaxTempC);
    private double W => Math.Max(_plot.Bounds.Width, 100);
    private double H => Math.Max(_plot.Bounds.Height, 100);
    private double X(double t) => Pad + (t - TMin) / (TMax - TMin) * (W - 2 * Pad);
    private double Y(double p) => H - Pad - p / 100.0 * (H - 2 * Pad);
    private double InvX(double x) => TMin + (x - Pad) / (W - 2 * Pad) * (TMax - TMin);
    private double InvY(double y) => (H - Pad - y) / (H - 2 * Pad) * 100.0;

    private void Refit()
    {
        _fitDelay.Stop();
        _fitDelay.Start();
    }

    private void Fit()
    {
        var library = new Dictionary<string, FanCurve>(_sensors.Runner.Store.Library) { [_edit.Id] = _edit };
        _plan = BmcCurveFit.Plan(_edit, library, _sensors.Runner.BmcNumber, 1, _sensors.Runner.Store.FourthAt70);
        var (text, brush) = _edit.Inputs.Count == 0 ? ("Choose a temperature sensor.", Palette.Footnote) : CurveUi.Where(_plan, _choices);
        _where.Text = text;
        _where.Foreground = brush;
        Render();
    }

    private void Render()
    {
        _plot.Children.Clear();
        for (var p = 0.0; p <= 100; p += 25)
        {
            Line(Pad, Y(p), W - Pad / 2, Y(p), CurveUi.GridLine);
            Text($"{p:0}", 8, Y(p) - 7);
        }
        for (var t = Math.Ceiling(TMin / 10) * 10; t <= TMax + 0.01; t += 10)
        {
            Line(X(t), Pad / 2, X(t), H - Pad, CurveUi.GridLine);
            Text($"{t:0}°", X(t) - 9, H - Pad + 8);
        }
        if (FanCurveMath.Hottest(_edit, _sensors.Reading) is { } now && now >= TMin && now <= TMax)
            Line(X(now), Pad / 2, X(now), H - Pad, CurveUi.Marker, 1.5, [3, 3]);
        if (_edit.MaxSpeedPercent is > 0 and < 100)
            Line(Pad, Y(_edit.MaxSpeedPercent), W - Pad / 2, Y(_edit.MaxSpeedPercent), CurveUi.Dot, 1.5, [4, 3]);

        var ordered = _edit.Points.OrderBy(p => p.TemperatureC).ToList();
        if (ordered.Count >= 2)
        {
            // As OpenFan draws it: from the axis start to the end, flat outside the points, capped at max speed.
            var xs = new List<double> { TMin };
            xs.AddRange(ordered.Select(p => p.TemperatureC).Where(t => t > TMin && t < TMax));
            xs.Add(TMax);
            var line = xs.Select(t => new Point(X(t), Y(FanCurveMath.Graph(ordered, t, _edit.MaxSpeedPercent)))).ToList();
            var area = new AvaloniaList<Point>(line) { new(X(TMax), Y(0)), new(X(TMin), Y(0)) };
            _plot.Children.Add(new Polygon { Fill = CurveUi.AreaFill, Points = area });
            _plot.Children.Add(new Polyline { Stroke = Brushes.White, StrokeThickness = 2.5, Points = new AvaloniaList<Point>(line) });
        }
        if (_plan is { OnBmc: true } plan && _edit.Inputs.Count > 0)
        {
            var temps = new List<double> { TMin };
            temps.AddRange(plan.Points.Select(p => (double)p.TemperatureC).Where(t => t > TMin && t < TMax));
            temps.Add(TMax);
            _plot.Children.Add(new Polyline
            {
                Stroke = CurveUi.BmcLine, StrokeThickness = 1.6, StrokeDashArray = [5, 3],
                Points = new AvaloniaList<Point>(temps.Select(t => new Point(X(t), Y(BmcCurveFit.Duty(plan.Points, t))))),
            });
        }
        for (var i = 0; i < _edit.Points.Count; i++)
        {
            var pt = _edit.Points[i];
            var size = i == _selected ? 15.0 : 11.0;
            var dot = new Ellipse { Width = size, Height = size, Fill = CurveUi.Dot, Stroke = i == _selected ? Brushes.White : null, StrokeThickness = 2 };
            Canvas.SetLeft(dot, X(pt.TemperatureC) - size / 2);
            Canvas.SetTop(dot, Y(pt.Percent) - size / 2);
            _plot.Children.Add(dot);
        }
    }

    private void Line(double x1, double y1, double x2, double y2, IBrush brush, double thickness = 1, double[]? dash = null) =>
        _plot.Children.Add(new Line
        {
            StartPoint = new Point(x1, y1), EndPoint = new Point(x2, y2), Stroke = brush, StrokeThickness = thickness,
            StrokeDashArray = dash is null ? null : new AvaloniaList<double>(dash),
        });

    private void Text(string text, double x, double y)
    {
        var tb = new TextBlock { Text = text, FontSize = 10, Foreground = CurveUi.AxisText };
        Canvas.SetLeft(tb, x);
        Canvas.SetTop(tb, y);
        _plot.Children.Add(tb);
    }

    private void RefreshTemp()
    {
        var t = FanCurveMath.Hottest(_edit, _sensors.Reading);
        _temp.Text = t is null ? (_edit.Inputs.Count == 0 ? "no sensor selected" : "reading unavailable")
            : $"{t:0.0} °C" + (_edit.Inputs.Count > 1 ? $" (hottest of {_edit.Inputs.Count})" : "");
        Render();
    }

    private void Select(int index)
    {
        _selected = index;
        _suppress = true;
        _selTemp.Value = index >= 0 ? (decimal)_edit.Points[index].TemperatureC : null;
        _selPct.Value = index >= 0 ? (decimal)_edit.Points[index].Percent : null;
        _suppress = false;
    }

    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        var pos = e.GetPosition(_plot);
        var hit = -1;
        for (var i = 0; i < _edit.Points.Count && hit < 0; i++)
        {
            var dx = X(_edit.Points[i].TemperatureC) - pos.X;
            var dy = Y(_edit.Points[i].Percent) - pos.Y;
            if (dx * dx + dy * dy <= 13 * 13) hit = i;
        }
        if (hit < 0)
        {
            _edit.Points.Add(new FanGraphPoint(Math.Round(Math.Clamp(InvX(pos.X), TMin, TMax) * 2) / 2, Math.Round(Math.Clamp(InvY(pos.Y), 0, 100))));
            hit = _edit.Points.Count - 1;
        }
        _dragging = hit;
        Select(hit);
        e.Pointer.Capture(_plot);
        Render();
    }

    private void OnMoved(object? sender, PointerEventArgs e)
    {
        if (_dragging < 0 || _dragging >= _edit.Points.Count) return;
        var pos = e.GetPosition(_plot);
        _edit.Points[_dragging] = new FanGraphPoint(Math.Round(Math.Clamp(InvX(pos.X), TMin, TMax) * 2) / 2, Math.Round(Math.Clamp(InvY(pos.Y), 0, 100)));
        Select(_dragging);
        Render();
    }

    private void AddPoint()
    {
        var o = _edit.Points.OrderBy(p => p.TemperatureC).ToList();
        FanGraphPoint added;
        if (o.Count == 0) added = new((TMin + TMax) / 2, 50);
        else if (o.Count == 1) added = new(Math.Min(TMax, o[0].TemperatureC + 10), o[0].Percent);
        else
        {
            // At the middle of the widest gap, where a point helps most (OpenFan's rule).
            var at = Enumerable.Range(1, o.Count - 1).MaxBy(i => o[i].TemperatureC - o[i - 1].TemperatureC);
            added = new(Math.Round((o[at - 1].TemperatureC + o[at].TemperatureC)) / 2, Math.Round((o[at - 1].Percent + o[at].Percent) / 2));
        }
        _edit.Points.Add(added);
        Select(_edit.Points.Count - 1);
        Render();
        Refit();
    }

    private void RemoveSelected()
    {
        if (_selected < 0 || _edit.Points.Count <= 2) return; // a graph needs two points
        _edit.Points.RemoveAt(_selected);
        Select(-1);
        Render();
        Refit();
    }
}

