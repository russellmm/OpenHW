using Avalonia.Headless.XUnit;
using OpenHW.App.Tray;
using Xunit;

namespace OpenHW.App.Tests;

/// <summary>Large panel tiles (0.39.0): wide SVG files GNOME's AppIndicators shows at their own size.</summary>
public sealed class TrayTileTests
{
    [AvaloniaFact]
    public void TilesAreWideEnoughToBeShownFullSizeAndEscapeTheirText()
    {
        foreach (var text in new[] { "5", "41°", "9.12", "1.2k" })
        {
            var (svg, width) = TrayTileSvg.Build(text, text.EndsWith('°') ? "" : "W", "#C0392B");
            Assert.True(width >= TrayTileSvg.Height * 1.5, $"{text}: {width}"); // AppIndicators' wide-image rule
            Assert.Contains($"width=\"{width:0.##}\" height=\"22\"", svg);
            Assert.Contains("fill=\"#ffffff\"", svg); // white on a dark tile
        }
        Assert.Contains("fill=\"#141414\"", TrayTileSvg.Build("32°", "", "#E5A823").Svg); // dark text on yellow
        Assert.Contains("&lt;5", TrayTileSvg.Build("<5", "", "#000000").Svg);
        Assert.True(TrayTileSvg.Build("225", "W", "#000000").Width > TrayTileSvg.Build("5", "W", "#000000").Width);

        // Writes a new file only when the picture changes; keeps the previous one, removes older ones.
        var a = TrayTileSvg.Write("test@0", "41°", "", "#C0392B");
        Assert.Equal(a, TrayTileSvg.Write("test@0", "41°", "", "#C0392B"));
        var b = TrayTileSvg.Write("test@0", "42°", "", "#C0392B");
        var c = TrayTileSvg.Write("test@0", "43°", "", "#C0392B");
        Assert.NotEqual(a, b);
        Assert.True(Path.IsPathRooted(c) && c.EndsWith(".svg", StringComparison.Ordinal));
        Assert.False(File.Exists(a));
        Assert.True(File.Exists(b) && File.Exists(c));
        TrayTileSvg.Prune(["other"]); // test@0's icon is gone
        Assert.False(File.Exists(b) || File.Exists(c));

        if (Environment.GetEnvironmentVariable("OPENHW_CAPTURES") is { } dir)
        {
            var i = 0;
            foreach (var (t, u, col) in new[] { ("62", "W", "#C0392B"), ("41°", "", "#C0392B"), ("17", "W", "#8E44AD"), ("38°", "", "#8E44AD"),
                         ("18", "W", "#1E7B1E"), ("9.12", "W", "#1E7B1E"), ("32°", "", "#E5A823"), ("34°", "", "#E5A823"),
                         ("51°", "", "#D35400"), ("38°", "", "#27AE60"), ("225", "W", "#000000") })
                File.WriteAllText(Path.Combine(dir, $"tile-{i++:00}.svg"), TrayTileSvg.Build(t, u, col).Svg);
        }
    }
}
