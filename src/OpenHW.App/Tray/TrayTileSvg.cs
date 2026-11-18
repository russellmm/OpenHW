using System.Globalization;
using System.Security;
using Avalonia.Media;

namespace OpenHW.App.Tray;

/// <summary>
/// Large panel tiles for GNOME (0.39.0): the value on a coloured rounded tile, in the panel's own font at panel text
/// size — like an indicator's text label ("225 W"), but with the tile colour.
/// <para>
/// How: GNOME's AppIndicators extension squeezes every pixmap icon into a square (22 px here), but an icon <b>file</b>
/// in a user-writable folder that is at least 1.5× as wide as tall is shown at its own size ("Hello
/// indicator-multiload!" in its appIndicator.js), and an SVG is rasterised at the screen's scale, so it stays sharp at
/// 2×. The item's IconName is set to the file's absolute path. The shell caches textures by path, so every new picture
/// gets a new file name; older files are removed.
/// </para>
/// </summary>
public static class TrayTileSvg
{
    /// <summary>Tile height in logical pixels: the AppIndicators icon size the owner set (22).</summary>
    public const double Height = 22;
    public const double DigitsSize = 15;   // ≈ the panel font (Ubuntu Sans 11 pt)
    public const double UnitSize = 11;
    private const double Padding = 6;
    private const string FontFamilyName = "Ubuntu Sans";

    private static readonly Typeface Bold = new(new FontFamily(FontFamilyName + ", Cantarell, DejaVu Sans"), FontStyle.Normal, FontWeight.Bold);
    private static readonly Dictionary<string, (string Signature, string Path, string? Previous)> Last = new(StringComparer.Ordinal);
    private static long _counter;
    private static string? _dir;

    /// <summary>$XDG_RUNTIME_DIR/openhw/tray-&lt;pid&gt; (cleared when first used; gone at logout).</summary>
    public static string Directory
    {
        get
        {
            if (_dir is not null) return _dir;
            var runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") is { Length: > 0 } r ? r : Path.GetTempPath();
            var root = Path.Combine(runtime, "openhw");
            System.IO.Directory.CreateDirectory(root);
            // Leftovers from earlier runs (another pid) go first.
            foreach (var old in System.IO.Directory.GetDirectories(root, "tray-*"))
                try { System.IO.Directory.Delete(old, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            _dir = Path.Combine(root, $"tray-{Environment.ProcessId}");
            System.IO.Directory.CreateDirectory(_dir);
            return _dir;
        }
    }

    /// <summary>The SVG for a tile (exposed for tests).</summary>
    public static (string Svg, double Width) Build(string text, string unit, string backgroundHex)
    {
        var background = Color.Parse(backgroundHex);
        var foreground = (0.299 * background.R + 0.587 * background.G + 0.114 * background.B) / 255.0 > 0.62 ? "#141414" : "#ffffff";
        var textWidth = Measure(text, DigitsSize) + (unit.Length > 0 ? 1 + Measure(unit, UnitSize) : 0);
        var width = Math.Ceiling(Math.Max(textWidth + 2 * Padding, Height * 1.5)); // ≥ 1.5 × height: shown at full width
        string F(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
        var esc = SecurityElement.Escape(text);
        var unitSpan = unit.Length > 0 ? $"<tspan font-size=\"{F(UnitSize)}\" dx=\"1\">{SecurityElement.Escape(unit)}</tspan>" : "";
        // Baseline: digits' cap height (~0.72 em) centred in the tile.
        var baseline = (Height + 0.72 * DigitsSize) / 2;
        var svg =
            $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{F(width)}\" height=\"{F(Height)}\" viewBox=\"0 0 {F(width)} {F(Height)}\">" +
            $"<rect width=\"{F(width)}\" height=\"{F(Height)}\" rx=\"4\" ry=\"4\" fill=\"{backgroundHex}\"/>" +
            $"<text x=\"{F(width / 2)}\" y=\"{F(baseline)}\" text-anchor=\"middle\" font-family=\"{FontFamilyName}, Cantarell, sans-serif\" " +
            $"font-weight=\"700\" font-size=\"{F(DigitsSize)}\" fill=\"{foreground}\">{esc}{unitSpan}</text></svg>";
        return (svg, width);
    }

    private static double Measure(string text, double size) =>
        text.Length == 0 ? 0 : new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Bold, size, Brushes.White).WidthIncludingTrailingWhitespace;

    /// <summary>
    /// Writes the tile for one tray icon and returns its absolute path. Unchanged text/unit/colour → the same file (no
    /// write, no NewIcon); a change → a new file name, and the one before the previous is removed.
    /// </summary>
    public static string Write(string key, string text, string unit, string backgroundHex)
    {
        var signature = text + "\u0001" + unit + "\u0001" + backgroundHex;
        lock (Last)
        {
            if (Last.TryGetValue(key, out var last) && last.Signature == signature && File.Exists(last.Path)) return last.Path;
            var safe = new string(key.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
            var path = Path.Combine(Directory, $"{safe}-{++_counter}.svg");
            File.WriteAllText(path, Build(text, unit, backgroundHex).Svg);
            // Keep the previous file a little longer: the shell may still be loading it when the next one arrives.
            if (last.Previous is { } older) try { File.Delete(older); } catch (IOException) { }
            Last[key] = (signature, path, last.Path);
            return path;
        }
    }
}
