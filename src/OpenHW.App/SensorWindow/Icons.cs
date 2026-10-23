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

/// <summary>Bottom toolbar glyphs, after HWiNFO's: blue arrows, clock (reset), blue X (close).</summary>
public static class FooterIcons
{
    private static readonly IBrush Blue = new SolidColorBrush(Color.Parse("#3D93F0"));
    private static readonly IBrush DimBlue = new SolidColorBrush(Color.Parse("#4F7FB5"));
    private static readonly IBrush White = new SolidColorBrush(Color.Parse("#F2F2F2"));
    public static readonly IBrush ButtonFace = new SolidColorBrush(Color.Parse("#2A2D33"));
    public static readonly IBrush ButtonEdge = new SolidColorBrush(Color.Parse("#4B5058"));

    public static readonly (Geometry, IBrush?, IBrush?) Expand =
        (Geometry.Parse("M0,8 L5,3 V6 H11 V3 L16,8 L11,13 V10 H5 V13 Z"), Blue, null);
    public static readonly (Geometry, IBrush?, IBrush?) Collapse =
        (Geometry.Parse("M0,3 L6,8 L0,13 Z M16,3 L10,8 L16,13 Z M6,7 H10 V9 H6 Z"), DimBlue, null);
    public static readonly (Geometry, IBrush?, IBrush?) Clock =
        (Geometry.Parse("M1,8 A7,7 0 1 1 15,8 A7,7 0 1 1 1,8 Z M8,3.5 V8 H11.5"), null, White);
    public static readonly (Geometry, IBrush?, IBrush?) Close =
        (Geometry.Parse("M2,2 L14,14 M14,2 L2,14"), null, Blue);
    public static readonly (Geometry, IBrush?, IBrush?) Gauge =
        (Geometry.Parse("M1.5,12 A6.5,6.5 0 1 1 14.5,12 M8,11 L11.5,5.5"), null, White);
    public static readonly (Geometry, IBrush?, IBrush?) Bolt =
        (Geometry.Parse("M9.5,0.5 L2.5,9 H7 L5.5,15.5 L13.5,6.5 H9 Z"), new SolidColorBrush(Color.Parse("#F0B429")), null);
}
