using Avalonia.Headless.XUnit;
using OpenHW.App.Tray;
using Xunit;

namespace OpenHW.App.Tests;

public sealed class TrayIconTests
{
    // Writes a strip of rendered icons (several values and colours, at the sizes GNOME asks for) for a visual check.
    [AvaloniaFact]
    public void RendersEveryValueWithinTheTile()
    {
        var samples = new[]
        {
            ("45", "#C0392B", ""), ("100", "#2E86DE", ""), ("250", "#F0B429", ""), ("5.3k", "#27AE60", ""), ("1.25", "#ECF0F1", ""), ("--", "#5D6D7E", ""),
            ("32°", "#C0392B", ""), ("55", "#2E86DE", "W"), ("250", "#2E86DE", "W"), ("87", "#27AE60", "%"), ("100", "#27AE60", "%"), ("1.25", "#ECF0F1", "V"),
        };
        foreach (var (text, colour, unit) in samples)
        {
            var icons = TrayIconRenderer.Render(text, colour, unit);
            Assert.Contains(icons, i => i.Width == 44);
            Assert.All(icons, i => Assert.Equal(i.Width * i.Height * 4, i.Argb.Length));
            // Nothing is clipped at any size: the outer ring only gets anti-aliasing (bare "100" reaches 113 at 22 px;
            // a glyph cut off by the edge would read near 255).
            Assert.All(icons, i => Assert.True(EdgeInk(i) <= 160, $"'{text}{unit}' is clipped at {i.Width} px ({EdgeInk(i)})"));
        }
        var path = Path.Combine(Path.GetTempPath(), "openhw-tray-icons.ppm");
        WriteStrip(path, samples.Select(s => TrayIconRenderer.Render(s.Item1, s.Item2, s.Item3).First(i => i.Width == 44)).ToArray());
    }

    [AvaloniaFact]
    public void AUnitIsDrawnAfterTheDigits()
    {
        var bare = TrayIconRenderer.Render("55", "#2E86DE").First(i => i.Width == 44);
        var watts = TrayIconRenderer.Render("55", "#2E86DE", "W").First(i => i.Width == 44);
        Assert.NotEqual(bare.Argb, watts.Argb);
    }

    // How far the outermost ring of pixels strays from the tile color (0 = untouched, 255 = fully covered by ink).
    private static int EdgeInk(IconPixmap icon)
    {
        int Diff(int x, int y)
        {
            var p = (y * icon.Width + x) * 4;
            return Enumerable.Range(1, 3).Max(c => Math.Abs(icon.Argb[p + c] - icon.Argb[c]));
        }
        var n = icon.Width - 1;
        return Enumerable.Range(0, icon.Width).Max(k => new[] { Diff(k, 0), Diff(k, n), Diff(0, k), Diff(n, k) }.Max());
    }

    // Plain PPM so the check needs no image library: icons side by side, 4 px apart, on dark grey like the top bar.
    private static void WriteStrip(string path, IconPixmap[] icons)
    {
        const int gap = 4;
        var h = icons.Max(i => i.Height);
        var w = icons.Sum(i => i.Width + gap) + gap;
        var rgb = Enumerable.Repeat((byte)0x24, w * h * 3).ToArray();
        var x0 = gap;
        foreach (var icon in icons)
        {
            for (var y = 0; y < icon.Height; y++)
                for (var x = 0; x < icon.Width; x++)
                {
                    var s = (y * icon.Width + x) * 4;
                    var d = (y * w + x0 + x) * 3;
                    rgb[d] = icon.Argb[s + 1]; rgb[d + 1] = icon.Argb[s + 2]; rgb[d + 2] = icon.Argb[s + 3];
                }
            x0 += icon.Width + gap;
        }
        using var file = File.Create(path);
        var header = System.Text.Encoding.ASCII.GetBytes($"P6\n{w} {h}\n255\n");
        file.Write(header);
        file.Write(rgb);
    }
}
