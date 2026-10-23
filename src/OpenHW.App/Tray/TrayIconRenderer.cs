using System.Globalization;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace OpenHW.App.Tray;

/// <summary>
/// Draws a sensor value onto a solid square tile, HWiNFO-style. Several sizes are sent because the panel picks
/// the closest one to its own icon size (16 px × display scale on GNOME); scaling a single bitmap blurs the digits.
/// Must run on the UI thread (Avalonia rendering).
/// </summary>
public static class TrayIconRenderer
{
    private static readonly int[] Sizes = [16, 22, 24, 32, 44, 48, 64]; // 44: 22 px icons at 2× scale

    public static IconPixmap[] Render(string text, string backgroundHex)
    {
        var background = Color.Parse(backgroundHex);
        var foreground = TextColorFor(background); // readable on any tile colour
        return Sizes.Select(size => RenderOne(text, background, foreground, size)).ToArray();
    }

    private static IconPixmap RenderOne(string text, Color background, Color foreground, int size)
    {
        using var bitmap = new RenderTargetBitmap(new PixelSize(size, size), new Vector(96, 96));
        using (var context = bitmap.CreateDrawingContext())
        {
            context.FillRectangle(new SolidColorBrush(background), new Rect(0, 0, size, size));
            var formatted = FitText(text, size, foreground);
            // Centre the glyph outlines, not the line box: digits have no descenders, so line-box centring sits them high.
            var ink = formatted.BuildGeometry(default)?.Bounds ?? new Rect(0, 0, formatted.Width, formatted.Height);
            context.DrawText(formatted, new Point(Math.Round((size - ink.Width) / 2 - ink.X),
                                                  Math.Round((size - ink.Height) / 2 - ink.Y)));
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
    // across and ~6 % top and bottom, so "45" fills the tile and "5.3k" still fits.
    private static FormattedText FitText(string text, int size, Color foreground)
    {
        var typeface = new Typeface(Narrow, FontStyle.Normal, FontWeight.Bold);
        var brush = new SolidColorBrush(foreground);
        for (var em = size * 1.4; ; em -= 0.25)
        {
            var formatted = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, em, brush);
            var ink = formatted.BuildGeometry(default)?.Bounds ?? new Rect(0, 0, formatted.Width, formatted.Height);
            if (ink.Width <= size - 2 && ink.Height <= size * 0.88 || em <= 4) return formatted;
        }
    }

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
