using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using OpenHW.App.Sensors;
using OpenHW.Hardware;

namespace OpenHW.App.SensorWindow;

/// <summary>
/// Sensor menu → Show Graph: one sensor's last 30 minutes as a line, with Current/Min/Max/Average above it.
/// Refreshed on every poll by the app. Gaps (the sensor wasn't read: collapsed group, monitoring off) stay gaps.
/// </summary>
public sealed class SensorGraphWindow : Window
{
    private readonly SensorHub _hub;
    private readonly HardwareItem _item;
    private readonly GraphView _graph = new();
    private readonly TextBlock _stats = new() { Margin = new Thickness(10, 8, 10, 4), FontFamily = new FontFamily("monospace") };

    public string SensorId => _item.Id;

    public SensorGraphWindow(SensorHub hub, HardwareItem item)
    {
        _hub = hub;
        _item = item;
        Width = 640;
        Height = 320;
        MinWidth = 320;
        MinHeight = 200;
        Background = Palette.Window;
        Foreground = Palette.Text;
        FontSize = 12.5;
        _graph.Unit = SensorPresentation.DisplayUnit(item);
        Content = new DockPanel { Children = { Top(_stats), _graph } };
        Refresh();
    }

    private static Control Top(Control c)
    {
        DockPanel.SetDock(c, Dock.Top);
        return c;
    }

    /// <summary>Re-reads statistics and history. UI thread.</summary>
    public void Refresh()
    {
        var name = SensorPresentation.DisplayName(_item);
        Title = $"OpenHW — {name}";
        _graph.Unit = SensorPresentation.DisplayUnit(_item); // Customize values can change it while the graph is open
        var s = _hub.Get(_item.Id);
        string F(double? v) => SensorPresentation.Format(_item, v is { } x ? SensorPresentation.Scale(_item, x) : null) is { Length: > 0 } t ? t : "–";
        _stats.Text = $"{name}\nCurrent {F(s.Current)}   Min {F(s.Minimum)}   Max {F(s.Maximum)}   Avg {F(s.Average)}";
        _graph.Points = _hub.History.Get(_item.Id).Select(p => (p.Age, SensorPresentation.Scale(_item, p.Value))).ToList();
        _graph.InvalidateVisual();
    }

    /// <summary>The chart: value axis auto-scaled to the data (with a little room), time axis −30 min … now.</summary>
    private sealed class GraphView : Control
    {
        private static readonly IBrush Line = new SolidColorBrush(Color.Parse("#3D93F0"));
        private static readonly IBrush Grid = new SolidColorBrush(Color.Parse("#2A2D33"));
        private static readonly IBrush Label = new SolidColorBrush(Color.Parse("#8A8A8A"));
        private static readonly Typeface Face = new(FontFamily.Default);
        private const double Left = 70, Right = 12, TopPad = 8, Bottom = 22;

        public IReadOnlyList<(double Age, double Value)> Points { get; set; } = [];
        public string Unit { get; set; } = "";

        public override void Render(DrawingContext context)
        {
            var b = Bounds;
            context.FillRectangle(Palette.RowA, new Rect(b.Size));
            var plot = new Rect(Left, TopPad, Math.Max(10, b.Width - Left - Right), Math.Max(10, b.Height - TopPad - Bottom));
            var span = SensorHistory.Span.TotalSeconds;

            var (lo, hi, step) = Ticks();
            var decimals = Math.Max(0, (int)-Math.Floor(Math.Log10(step) + 1e-9));
            for (var v = lo; v <= hi + step / 2; v += step)
            {
                var y = plot.Bottom - plot.Height * (v - lo) / (hi - lo);
                context.DrawLine(new Pen(Grid, 1), new Point(plot.Left, y), new Point(plot.Right, y));
                var label = v.ToString("N" + decimals, CultureInfo.CurrentCulture) + (Unit is "" or "flag" ? "" : " " + Unit);
                Text(context, label, new Point(4, y - 8));
            }
            foreach (var minutes in new[] { 30, 20, 10, 0 })
            {
                var x = plot.Right - plot.Width * minutes * 60 / span;
                context.DrawLine(new Pen(Grid, 1), new Point(x, plot.Top), new Point(x, plot.Bottom));
                Text(context, minutes == 0 ? "now" : $"−{minutes} min", new Point(Math.Min(x - 14, b.Width - 34), plot.Bottom + 4));
            }
            if (Points.Count == 0)
            {
                Text(context, "No readings yet — the sensor is read while its row is on screen (or it's in the tray).", new Point(plot.Left + 8, plot.Top + 8));
                return;
            }

            // A gap longer than a few polls (the sensor wasn't read) breaks the line.
            var geometry = new StreamGeometry();
            using (var g = geometry.Open())
            {
                (double Age, double Value)? previous = null;
                foreach (var p in Points)
                {
                    var point = new Point(plot.Right - plot.Width * p.Age / span, plot.Bottom - plot.Height * (p.Value - lo) / (hi - lo));
                    if (previous is null || previous.Value.Age - p.Age > 12) g.BeginFigure(point, false);
                    else g.LineTo(point);
                    previous = p;
                }
            }
            context.DrawGeometry(null, new Pen(Line, 2), geometry);
            // The latest reading as a dot, so a fresh graph (seconds of history in a 30-minute frame) shows something.
            var last = Points[^1];
            context.DrawEllipse(Line, null, new Point(plot.Right - plot.Width * last.Age / span, plot.Bottom - plot.Height * (last.Value - lo) / (hi - lo)), 3.5, 3.5);
        }

        // About four gridlines at a round step (1, 2 or 5 × 10ⁿ) that covers the data, so labels never repeat.
        private (double Lo, double Hi, double Step) Ticks()
        {
            if (Points.Count == 0) return (0, 1, 0.25);
            var lo = Points.Min(p => p.Value);
            var hi = Points.Max(p => p.Value);
            if (hi - lo < 1e-9) { lo -= 0.5; hi += 0.5; }
            var raw = (hi - lo) / 4;
            var magnitude = Math.Pow(10, Math.Floor(Math.Log10(raw)));
            var step = new[] { 1.0, 2, 5, 10 }.Select(m => m * magnitude).First(s => s >= raw);
            var start = Math.Floor(lo / step) * step;
            var end = Math.Ceiling(hi / step) * step;
            if (start < 0 && lo >= 0) start = 0; // positive data doesn't get a negative axis
            return (start, Math.Max(end, start + step), step);
        }

        private static void Text(DrawingContext context, string text, Point at) =>
            context.DrawText(new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Face, 11, Label), at);
    }
}
