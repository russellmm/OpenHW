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

    public static IconPixmap[] Render(string text, string backgroundHex, string unit = "")
    {
        var background = Color.Parse(backgroundHex);
        var foreground = TextColorFor(background); // readable on any tile colour
        return Sizes.Select(size => RenderOne(text, unit, background, foreground, size)).ToArray();
    }

    private static IconPixmap RenderOne(string text, string unit, Color background, Color foreground, int size)
    {
        using var bitmap = new RenderTargetBitmap(new PixelSize(size, size), new Vector(96, 96));
        using (var context = bitmap.CreateDrawingContext())
        {
            context.FillRectangle(new SolidColorBrush(background), new Rect(0, 0, size, size));
            var (digits, small, gap) = FitText(text, unit, size, foreground);
            // Centre the glyph outlines, not the line box: digits have no descenders, so line-box centring sits them high.
            var ink = Ink(digits);
            var width = ink.Width + (small is null ? 0 : gap + Ink(small).Width);
            var left = Math.Round((size - width) / 2);
            var bottom = Math.Round((size + ink.Height) / 2); // the digits' baseline, shared by the unit
            context.DrawText(digits, new Point(left - ink.X, bottom - ink.Bottom));
            if (small is not null)
            {
                var unitInk = Ink(small);
                context.DrawText(small, new Point(left + ink.Width + gap - unitInk.X, bottom - unitInk.Bottom));
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

    // A narrow bold face fits more digit height into a square tile ("250" is three digits wide); the default font is
    // the fallback where it isn't installed.
    private static readonly FontFamily Narrow = new("Liberation Sans Narrow, Nimbus Sans Narrow, " + FontFamily.Default.Name);

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
            var width = ink.Width + (small is null ? 0 : gap + Ink(small).Width);
            if (width <= size - 2 && ink.Height <= size * 0.88 || em <= 4) return (digits, small, gap);
        }
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
