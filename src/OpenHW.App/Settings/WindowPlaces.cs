using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;

namespace OpenHW.App.Settings;

/// <summary>Where a window was last: its top-left corner in screen pixels, and its size when the user can resize it.</summary>
public sealed record WindowPlace(int X, int Y, double? Width = null, double? Height = null);

/// <summary>
/// Remembers where each of OpenHW's windows was (~/.config/openhw/window-places.json, one entry per key) and puts it
/// back there the next time OpenHW starts. A window with nothing saved — or saved on a monitor that's gone — opens
/// centred on the primary monitor; Avalonia's default is the desktop's (0, 0), the top-left of whichever monitor sits
/// leftmost. Size is kept only for windows the user resizes freely (not ones that size to their content).
/// The main window keeps its own place in the view state (with maximized), and dialogs open centred on their owner.
/// </summary>
public static class WindowPlaces
{
    private static readonly string FilePath = Path.Combine(AppSettings.ConfigDir, "window-places.json");
    private static Dictionary<string, WindowPlace>? _places;
    private static DispatcherTimer? _saveTimer;

    private static Dictionary<string, WindowPlace> Places => _places ??= Load(FilePath);

    /// <summary>Call once, before the window is first shown.</summary>
    public static void Track(Window window, string key)
    {
        var sizes = window.CanResize && window.SizeToContent == SizeToContent.Manual;
        var saved = Places.GetValueOrDefault(key);
        if (saved is not null)
        {
            if (sizes && saved.Width is >= 100 and < 10000 && saved.Height is >= 100 and < 10000)
                (window.Width, window.Height) = (saved.Width.Value, saved.Height.Value);
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Position = new PixelPoint(saved.X, saved.Y);
        }
        else if (window.Screens.Primary is { } primary)
        {
            // A first guess from the window's set size, so it appears on the primary monitor; refined once it's shown.
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Position = Centered(primary.WorkingArea, Frame(window, primary.Scaling));
        }

        var placed = false;
        window.Opened += (_, _) =>
        {
            if (placed) return; // only the first time: after that it reopens where it was hidden
            placed = true;
            var screens = window.Screens.All.Select(s => s.WorkingArea).ToList();
            if (window.Screens.Primary is { } primary && (saved is null || !IsOnScreen(new PixelPoint(saved.X, saved.Y), screens)))
                window.Position = Centered(primary.WorkingArea, Frame(window, primary.Scaling));
        };
        window.PositionChanged += (_, _) => Remember(window, key, sizes);
        window.Resized += (_, _) => Remember(window, key, sizes);
    }

    private static PixelSize Frame(Window window, double scaling)
    {
        var size = window.IsVisible ? window.FrameSize ?? window.ClientSize : new Size(
            double.IsNaN(window.Width) ? 600 : window.Width, double.IsNaN(window.Height) ? 400 : window.Height);
        return new PixelSize((int)(size.Width * scaling), (int)(size.Height * scaling));
    }

    private static void Remember(Window window, string key, bool sizes)
    {
        if (!window.IsVisible || window.WindowState != WindowState.Normal) return;
        Places[key] = new WindowPlace(window.Position.X, window.Position.Y,
            sizes ? window.Width : null, sizes ? window.Height : null);
        // Written a second after the last move, so dragging doesn't write the file at every step.
        _saveTimer ??= new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => Flush());
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    /// <summary>Writes pending changes now (also called at exit).</summary>
    public static void Flush()
    {
        _saveTimer?.Stop();
        if (_places is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(_places, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"openhw: could not save window places ({ex.Message})");
        }
    }

    internal static Dictionary<string, WindowPlace> Load(string path)
    {
        try { return JsonSerializer.Deserialize<Dictionary<string, WindowPlace>>(File.ReadAllText(path)) ?? []; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return []; }
    }

    /// <summary>The top-left corner that centres a frame of this size in a monitor's working area (never above or left of it).</summary>
    internal static PixelPoint Centered(PixelRect area, PixelSize frame) =>
        new(area.X + Math.Max(0, (area.Width - frame.Width) / 2), area.Y + Math.Max(0, (area.Height - frame.Height) / 2));

    /// <summary>Whether a window's title bar near this corner would be on one of the monitors.</summary>
    internal static bool IsOnScreen(PixelPoint corner, IEnumerable<PixelRect> areas) =>
        areas.Any(a => a.Contains(new PixelPoint(corner.X + 40, corner.Y + 10)));
}
