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
            ("32Â°", "#C0392B", ""), ("55", "#2E86DE", "W"), ("250", "#2E86DE", "W"), ("87", "#27AE60", "%"), ("100", "#27AE60", "%"), ("1.25", "#ECF0F1", "V"),
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

    // Windows shows tray icons only at the small-icon size (24 px at 150 %): the digits must fill the tile's height
    // (checked from 24 px; at 16 px three characters are narrowed as far as they go and then made smaller).
    [AvaloniaFact]
    public void OnWindowsTheDigitsFillTheTile()
    {
        if (!OperatingSystem.IsWindows()) return;
        var samples = new[] { ("41Â°", "#C0392B", ""), ("14", "#2E86DE", "W"), ("100", "#27AE60", "%"), ("250", "#2E86DE", "W"), ("4500", "#27AE60", ""), ("13k", "#5D6D7E", ""), ("1", "#ECF0F1", "V"), ("87", "#F0B429", "%"), ("--", "#5D6D7E", "") }; // whole numbers only (TrayText)
        var sizes = new[] { 16, 24, 32 };
        var strips = sizes.ToDictionary(size => size, size => samples.Select(s => TrayIconRenderer.Render(s.Item1, s.Item2, s.Item3, [size]).Single()).ToArray());
        foreach (var (size, icons) in strips) WriteStrip(Path.Combine(Path.GetTempPath(), $"openhw-tray-icons-{size}.ppm"), icons);
        foreach (var (size, icons) in strips)
        {
            foreach (var (icon, (text, _, unit)) in icons.Zip(samples))
            {
                Assert.True(EdgeInk(icon) <= 200, $"'{text}{unit}' is clipped at {size} px ({EdgeInk(icon)})");
                if (text != "--" && size >= 24) Assert.True(InkRows(icon) >= size * 0.55, $"'{text}{unit}' is only {InkRows(icon)} px tall at {size} px");
            }
        }
    }

    // Windows remembers where the user dragged an icon by its uID: it must come from the sensor, not the list position.
    [Fact]
    public void OnWindowsAnIconsIdFollowsItsSensor()
    {
        if (!OperatingSystem.IsWindows()) return;
        var a = Win32TrayIcon.IdFor("nvml:GPU-x:temp:core#1", new HashSet<uint>());
        Assert.Equal(a, Win32TrayIcon.IdFor("nvml:GPU-x:temp:core#1", new HashSet<uint> { 0x100, 0x2000 }.Where(i => i != a).ToHashSet()));
        Assert.InRange(a, 0x100u, 0xFEFFu); // 16 bits (version 4 notifications), clear of old slot ids and the notifier's
        Assert.NotEqual(a, Win32TrayIcon.IdFor("nvml:GPU-x:temp:core#2", new HashSet<uint>()));
        Assert.NotEqual(a, Win32TrayIcon.IdFor("nvml:GPU-x:temp:core#1", new HashSet<uint> { a })); // a clash moves on
    }

    // Rows with ink on them (any pixel well away from the tile colour).
    private static int InkRows(IconPixmap icon) =>
        Enumerable.Range(0, icon.Height).Count(y => Enumerable.Range(0, icon.Width).Any(x =>
            Enumerable.Range(1, 3).Max(c => Math.Abs(icon.Argb[(y * icon.Width + x) * 4 + c] - icon.Argb[c])) > 96));

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
