using System.Globalization;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace OpenHW.App.Tray;

/// <summary>
/// Draws a sensor value onto a solid square tile, HWiNFO-style, optionally with a small unit after the digits
/// ("55" + "W"). The tile stays square: GNOME's tray fits every pixmap icon into a square, so a wider one would only
/// be shrunk. Several sizes are sent because the panel picks
/// the closest one to its own icon size (16 px × display scale on GNOME); scaling a single bitmap blurs the digits.
/// Must run on the UI thread (Avalonia rendering).
/// </summary>
public static class TrayIconRenderer
{
    private static readonly int[] Sizes = [16, 22, 24, 32, 44, 48, 64]; // 44: 22 px icons at 2× scale

    /// <summary>The unit's size relative to the digits: readable at 22 px, while the digits stay the main thing.</summary>
    private const double UnitScale = 0.6;

    public static IconPixmap[] Render(string text, string backgroundHex, string unit = "") => Render(text, backgroundHex, unit, Sizes);

    /// <summary>At these sizes only (Windows: the one size its taskbar draws at this DPI).</summary>
    public static IconPixmap[] Render(string text, string backgroundHex, string unit, IReadOnlyList<int> sizes)
    {
        var background = Color.Parse(backgroundHex);
        var foreground = TextColorFor(background); // readable on any tile colour
        return sizes.Select(size => RenderOne(text, unit, background, foreground, size)).ToArray();
    }

    private static IconPixmap RenderOne(string text, string unit, Color background, Color foreground, int size)
    {
        using var bitmap = new RenderTargetBitmap(new PixelSize(size, size), new Vector(96, 96));
        using (var context = bitmap.CreateDrawingContext())
        {
            context.FillRectangle(new SolidColorBrush(background), new Rect(0, 0, size, size));
            if (OperatingSystem.IsWindows()) DrawTall(context, text, unit, size, foreground);
            else
            {
            var (digits, small, gap) = FitText(text, unit, size, foreground);
            // Centre the glyph outlines, not the line box: digits have no descenders, so line-box centring sits them high.
            var ink = Ink(digits);
            var width = ink.Width + (small is null ? 0 : gap + UnitInk(small).Width);
            var left = Math.Round((size - width) / 2);
            var bottom = Math.Round((size + ink.Height) / 2); // the digits' baseline, shared by the unit
            context.DrawText(digits, new Point(left - ink.X, bottom - ink.Bottom));
            if (small is not null)
            {
                var unitInk = UnitInk(small);
                context.DrawText(small, new Point(left + ink.Width + gap - unitInk.X, bottom - unitInk.Bottom));
            }
            }
        }

        var stride = size * 4;
        var bgra = new byte[stride * size];
        var handle = GCHandle.Alloc(bgra, GCHandleType.Pinned);
        try
        {
            bitmap.CopyPixels(new PixelRect(0, 0, size, size), handle.AddrOfPinnedObject(), bgra.Length, stride);
        }
        finally
        {
            handle.Free();
        }
        return new IconPixmap(size, size, ToArgb(bgra));
    }

    // BT.601 luminance: a pale tile gets dark text, anything else white.
    private static Color TextColorFor(Color background) =>
        (0.299 * background.R + 0.587 * background.G + 0.114 * background.B) / 255.0 > 0.62
            ? Color.Parse("#141414")
            : Colors.White;

    /// <summary>
    /// Windows draws notification icons only at the small-icon size (24 px at 150 %), so the digits have to fill it: full
    /// height less a pixel or two, narrowed sideways (down to <see cref="MinSqueeze"/>) rather than made smaller
    /// when they're too wide, and the unit smaller and raised to the top, where it costs the digits the least width (left out after three
    /// characters).
    /// </summary>
    private static void DrawTall(DrawingContext context, string text, string unit, int size, Color foreground)
    {
        var typeface = new Typeface(Narrow, FontStyle.Normal, FontWeight.Bold);
        var brush = new SolidColorBrush(foreground);
        FormattedText Text(string s, double em) => new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, em, brush);
        if (text.Length >= 3) unit = ""; // "100%", "250W": the unit would squeeze the digits unreadable (the tooltip has it)
        var height = size - 3.0; // hinting can push a glyph a pixel past its outline: keep it off the edge
        var across = size - 2.0;
        var gap = unit.Length > 0 ? Math.Max(1, Math.Round(size / 24.0)) : 0;
        for (var em = size * 1.6; ; em -= 0.25)
        {
            var digits = Text(text, em);
            var ink = Ink(digits);
            if (ink.Height > height && em > 4) continue;
            var small = unit.Length > 0 ? Text(unit, em * WindowsUnitScale) : null;
            var unitInk = small is null ? default : UnitInk(small);
            var width = ink.Width + gap + unitInk.Width;
            var squeeze = Math.Min(1, across / width);
            if (squeeze < (text.Length >= 4 ? MinSqueezeLong : MinSqueeze) && em > 4) continue;
            var left = (size - width * squeeze) / 2;
            var top = Math.Round((size - ink.Height) / 2);
            // Greyscale anti-aliasing: sub-pixel (ClearType) text leaves coloured fringes on a solid tile.
            using (context.PushRenderOptions(new RenderOptions { TextRenderingMode = TextRenderingMode.Antialias, EdgeMode = EdgeMode.Antialias }))
            using (context.PushTransform(Matrix.CreateScale(squeeze, 1) * Matrix.CreateTranslation(left, 0)))
            {
                context.DrawText(digits, new Point(-ink.X, top - ink.Y));
                if (small is not null) context.DrawText(small, new Point(ink.Width + gap - unitInk.X, top - unitInk.Y));
            }
            return;
        }
    }

    /// <summary>How far digits may be narrowed on Windows before they're made smaller instead (still readable).</summary>
    private const double MinSqueeze = 0.62;

    /// <summary>Four characters ("4500", whole numbers up to 9999) may be narrowed further: taller but thinner reads better.</summary>
    private const double MinSqueezeLong = 0.48;

    /// <summary>The unit on Windows: smaller than on Linux, so the digits keep the width.</summary>
    private const double WindowsUnitScale = 0.58;

    // A narrow bold face fits more digit height into a square tile ("250" is three digits wide); the default font is
    // the fallback where it isn't installed.
    private static readonly FontFamily Narrow = new("Liberation Sans Narrow, Nimbus Sans Narrow, Arial Narrow, Bahnschrift SemiCondensed, " + FontFamily.Default.Name);

    // Largest size whose glyph outlines (not the font's line box or advance widths) fit the tile with a 1 px margin
    // across and ~6 % top and bottom, so "45" fills the tile and "5.3k" still fits. With a unit, digits + gap + unit
    // must fit across together.
    private static (FormattedText Digits, FormattedText? Unit, double Gap) FitText(string text, string unit, int size, Color foreground)
    {
        var typeface = new Typeface(Narrow, FontStyle.Normal, FontWeight.Bold);
        var brush = new SolidColorBrush(foreground);
        FormattedText Text(string s, double em) => new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, em, brush);
        var gap = Math.Max(1, Math.Round(size / 22.0)); // 1 px at 22 px, 2 px at 44
        for (var em = size * 1.4; ; em -= 0.25)
        {
            var digits = Text(text, em);
            var small = unit.Length > 0 ? Text(unit, em * UnitScale) : null;
            var ink = Ink(digits);
            var width = ink.Width + (small is null ? 0 : gap + UnitInk(small).Width);
            if (width <= size - Margin && ink.Height <= size * 0.88 || em <= 4) return (digits, small, gap);
        }
    }

    // Pixels kept free across the tile. Windows hints glyphs onto the pixel grid, which draws them up to a pixel wider
    // than their outlines at small sizes, so it gets one more.
    private static readonly int Margin = OperatingSystem.IsWindows() ? 3 : 2;

    // The unit's extent. On Windows a glyph like "%" is drawn wider than its outline geometry reports (seen at 48 px:
    // "87%" ran off the tile), so there the advance width counts too, which never under-measures.
    private static Rect UnitInk(FormattedText text)
    {
        var ink = Ink(text);
        if (!OperatingSystem.IsWindows()) return ink;
        var left = Math.Min(ink.X, 0);
        return new Rect(left, ink.Y, Math.Max(ink.Right, text.WidthIncludingTrailingWhitespace) - left, ink.Height);
    }

    private static Rect Ink(FormattedText text) =>
        text.BuildGeometry(default)?.Bounds ?? new Rect(0, 0, text.Width, text.Height);

    // Skia hands back premultiplied BGRA (little-endian); SNI wants straight ARGB in network byte order.
    private static byte[] ToArgb(byte[] bgra)
    {
        var argb = new byte[bgra.Length];
        for (var i = 0; i < bgra.Length; i += 4)
        {
            var a = bgra[i + 3];
            argb[i] = a;
            if (a == 0) continue;
            argb[i + 1] = (byte)Math.Min(255, bgra[i + 2] * 255 / a);
            argb[i + 2] = (byte)Math.Min(255, bgra[i + 1] * 255 / a);
            argb[i + 3] = (byte)Math.Min(255, bgra[i] * 255 / a);
        }
        return argb;
    }
}
