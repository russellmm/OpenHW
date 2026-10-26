using Avalonia.Media;
using OpenHW.Hardware;

namespace OpenHW.App.SensorWindow;

/// <summary>
/// 16×16 vector stand-ins for HWiNFO's row glyphs: red thermometer, yellow bolt, fan, and the grey gauge used
/// for everything else (clocks, usage, memory, counters). Drawn as geometry so they stay sharp at any scale.
/// </summary>
public static class Icons
{
    public static readonly Geometry Thermometer = Geometry.Parse(
        "M7,1.5 A1.5,1.5 0 0 1 10,1.5 V9.6 A3.2,3.2 0 1 1 7,9.6 Z");
    public static readonly Geometry Bolt = Geometry.Parse("M10,0 L3,9.5 H7.2 L5.5,16 L13,5.8 H8.8 Z");
    public static readonly Geometry Fan = Geometry.Parse(
        "M8,8 C8,4 11,2 12,4 C13,6 10,7 8,8 Z M8,8 C12,8 14,11 12,12 C10,13 9,10 8,8 Z " +
        "M8,8 C8,12 5,14 4,12 C3,10 6,9 8,8 Z M8,8 C4,8 2,5 4,4 C6,3 7,6 8,8 Z " +
        "M6.6,8 A1.4,1.4 0 1 0 9.4,8 A1.4,1.4 0 1 0 6.6,8 Z");
    public static readonly Geometry Gauge = Geometry.Parse("M13.2,4.8 A6,6 0 1 1 8,2 M8,2 L10.2,0.6 M8,2 L10.2,3.6 M8,4.8 V8 H10.8");
    public static readonly Geometry Chip = Geometry.Parse(
        "M3.5,3.5 H12.5 V12.5 H3.5 Z M5,1 V3.5 M8,1 V3.5 M11,1 V3.5 M5,12.5 V15 M8,12.5 V15 M11,12.5 V15 " +
        "M1,5 H3.5 M1,8 H3.5 M1,11 H3.5 M12.5,5 H15 M12.5,8 H15 M12.5,11 H15");
    public static readonly Geometry ChevronDown = Geometry.Parse("M3,5.5 L8,10.5 L13,5.5");
    public static readonly Geometry ChevronRight = Geometry.Parse("M5.5,3 L10.5,8 L5.5,13");

    private static readonly IBrush Red = new SolidColorBrush(Color.Parse("#E5251E"));
    private static readonly IBrush Yellow = new SolidColorBrush(Color.Parse("#F7C21A"));
    private static readonly IBrush Grey = new SolidColorBrush(Color.Parse("#C9C9C9"));
    public static readonly IBrush ChipGrey = new SolidColorBrush(Color.Parse("#8C8C8C"));
    public static readonly IBrush ChevronGrey = new SolidColorBrush(Color.Parse("#BDBDBD"));

    /// <summary>Geometry plus fill or stroke brush (the gauge is a line drawing, the others are solid).</summary>
    public static (Geometry Icon, IBrush? Fill, IBrush? Stroke) For(HardwareKind kind) => kind switch
    {
        HardwareKind.Temperature => (Thermometer, Red, null),
        HardwareKind.Voltage or HardwareKind.Current or HardwareKind.Power or HardwareKind.Energy => (Bolt, Yellow, null),
        HardwareKind.Tach => (Fan, Grey, null),
        _ => (Gauge, null, Grey), // includes GPU fan %, which HWiNFO also draws as a gauge
    };
}

/// <summary>
/// Bottom toolbar glyphs after HWiNFO's (reference/ captures): light-blue outward arrows (expand all), steel-blue
/// inward arrows (collapse all), a white clock face (reset), a silver gear (settings) and a glossy blue X (close),
/// each layered on a 32×32 grid so it stays sharp at any display scale.
/// </summary>
public static class FooterIcons
{
    public static readonly IBrush ButtonFace = new SolidColorBrush(Color.Parse("#2A2D33"));
    public static readonly IBrush ButtonEdge = new SolidColorBrush(Color.Parse("#4B5058"));

    /// <summary>The toolbar buttons' own face: near-black with a faint light top, as HWiNFO's.</summary>
    public static readonly IBrush ToolbarFace = Vertical("#2E3033", "#08090A");
    public static readonly IBrush ToolbarEdge = new SolidColorBrush(Color.Parse("#5E6166"));
    public static readonly IBrush ToolbarHover = Vertical("#45484D", "#16181A");
    public static readonly IBrush ToolbarPressed = Vertical("#121314", "#2A2C2F");

    public static readonly IImage Expand = Icon(
        Shape("M1,16 L10.5,6 V11.5 H14.2 V20.5 H10.5 V26 Z M31,16 L21.5,6 V11.5 H17.8 V20.5 H21.5 V26 Z",
              Vertical("#A9D8FF", "#2C82DC"), "#E4F2FF", 0.9));

    public static readonly IImage Collapse = Icon(
        Shape("M1,11.5 H7.5 V6 L16,16 L7.5,26 V20.5 H1 Z M31,11.5 H24.5 V6 L16,16 L24.5,26 V20.5 H31 Z",
              Vertical("#4F84B8", "#21507E"), "#6D97C0", 0.7));

    public static readonly IImage Clock = Icon(
        [
            Shape("M1.5,16 A14.5,14.5 0 1 1 30.5,16 A14.5,14.5 0 1 1 1.5,16 Z", Vertical("#FFFFFF", "#E2E2E2"), "#6A6A6A", 1.2),
            Line(Ticks(), "#202020", 1.3),
            Line("M16,16 V4.6", "#111111", 1.8),   // minute hand on 12
            Line("M16,16 H24", "#111111", 2.6),    // hour hand on 3
            Shape("M14.6,16 A1.4,1.4 0 1 1 17.4,16 A1.4,1.4 0 1 1 14.6,16 Z", Solid("#111111"), null, 0),
        ]);

    public static readonly IImage Gear = Icon(
        [
            Shape(GearOutline(teeth: 8, outer: 15, root: 11.6, hole: 6.6), Diagonal("#FAFAFA", "#85888C"), "#55585C", 0.8, evenOdd: true),
            Line("M9.4,16 A6.6,6.6 0 1 1 22.6,16 A6.6,6.6 0 1 1 9.4,16 Z", "#3A3C40", 1.2), // the hole's dark rim
        ]);

    public static readonly IImage Close = Icon(
        Shape(XOutline(arm: 4.4, reach: 8.6), Diagonal("#9AD6FF", "#2A86D6"), "#C4E6FF", 0.7));

    // ── drawing helpers ───────────────────────────────────────────────────────────────────────────────────────

    private static IImage Icon(params GeometryDrawing[] layers)
    {
        var group = new DrawingGroup();
        // A transparent 32×32 frame keeps every icon on the same grid whatever its own bounds.
        group.Children.Add(new GeometryDrawing { Geometry = new RectangleGeometry(new Avalonia.Rect(0, 0, 32, 32)), Brush = Brushes.Transparent });
        foreach (var layer in layers) group.Children.Add(layer);
        return new DrawingImage(group);
    }

    private static GeometryDrawing Shape(string path, IBrush fill, string? stroke, double width, bool evenOdd = false)
    {
        var geometry = StreamGeometry.Parse((evenOdd ? "F0 " : "") + path);
        return new GeometryDrawing
        {
            Geometry = geometry, Brush = fill,
            Pen = stroke is null ? null : new Pen(Solid(stroke), width, lineJoin: PenLineJoin.Round),
        };
    }

    private static GeometryDrawing Line(string path, string stroke, double width) => new()
    {
        Geometry = StreamGeometry.Parse(path),
        Pen = new Pen(Solid(stroke), width, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round),
    };

    private static IBrush Solid(string color) => new SolidColorBrush(Color.Parse(color));

    private static IBrush Vertical(string top, string bottom) => new LinearGradientBrush
    {
        StartPoint = new Avalonia.RelativePoint(0.5, 0, Avalonia.RelativeUnit.Relative),
        EndPoint = new Avalonia.RelativePoint(0.5, 1, Avalonia.RelativeUnit.Relative),
        GradientStops = { new GradientStop(Color.Parse(top), 0), new GradientStop(Color.Parse(bottom), 1) },
    };

    private static IBrush Diagonal(string light, string dark) => new LinearGradientBrush
    {
        StartPoint = new Avalonia.RelativePoint(0.15, 0, Avalonia.RelativeUnit.Relative),
        EndPoint = new Avalonia.RelativePoint(0.85, 1, Avalonia.RelativeUnit.Relative),
        GradientStops = { new GradientStop(Color.Parse(light), 0), new GradientStop(Color.Parse(dark), 1) },
    };

    private static string F(double v) => v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);

    // Twelve hour marks, inside the rim.
    private static string Ticks() => string.Concat(Enumerable.Range(0, 12).Select(i =>
    {
        var a = i * Math.PI / 6;
        var (inner, outer) = (i % 3 == 0 ? 10.2 : 11.0, 13.0);
        return $"M{F(16 + inner * Math.Sin(a))},{F(16 - inner * Math.Cos(a))} L{F(16 + outer * Math.Sin(a))},{F(16 - outer * Math.Cos(a))} ";
    }));

    // A gear: flat-topped teeth around a root circle, with a round hole (even-odd fill).
    private static string GearOutline(int teeth, double outer, double root, double hole)
    {
        var points = new List<string>();
        var step = 2 * Math.PI / teeth;
        for (var t = 0; t < teeth; t++)
        {
            var c = t * step;
            foreach (var (angle, r) in new[] { (c - step * 0.32, root), (c - step * 0.2, outer), (c + step * 0.2, outer), (c + step * 0.32, root) })
                points.Add($"{F(16 + r * Math.Sin(angle))},{F(16 - r * Math.Cos(angle))}");
        }
        return "M" + string.Join(" L", points) + $" Z M{F(16 - hole)},16 A{F(hole)},{F(hole)} 0 1 1 {F(16 + hole)},16 A{F(hole)},{F(hole)} 0 1 1 {F(16 - hole)},16 Z";
    }

    // A thick X: two bars of half-width arm crossing at the centre, as one outline.
    private static string XOutline(double arm, double reach)
    {
        var d = arm / Math.Sqrt(2);
        var (c, e) = (16.0, reach);
        (double, double)[] p =
        [
            (c, c - 2 * d), (c + e - d, c - e - d), (c + e + d, c - e + d), (c + 2 * d, c), (c + e + d, c + e - d), (c + e - d, c + e + d),
            (c, c + 2 * d), (c - e + d, c + e + d), (c - e - d, c + e - d), (c - 2 * d, c), (c - e - d, c - e + d), (c - e + d, c - e - d),
        ];
        return "M" + string.Join(" L", p.Select(q => $"{F(q.Item1)},{F(q.Item2)}")) + " Z";
    }
}
