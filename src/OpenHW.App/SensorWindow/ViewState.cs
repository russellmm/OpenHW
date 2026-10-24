using System.Text.Json;

namespace OpenHW.App.SensorWindow;

/// <summary>
/// Which groups are collapsed and which per-core rows are expanded, kept in ~/.config/openhw/view.json so the
/// layout (and the reads it saves) survive restarts. A missing or unreadable file means the defaults: every
/// group open, every per-core row closed.
/// </summary>
public sealed class ViewState
{
    public HashSet<string> CollapsedGroups { get; set; } = [];
    public HashSet<string> ExpandedSensors { get; set; } = [];

    /// <summary>Column widths (Sensor, Current, Minimum, Maximum, Average); null = defaults.</summary>
    public List<double>? ColumnWidths { get; set; }

    /// <summary>Window size and position in device pixels when last hidden or closed; null = defaults.</summary>
    public double? WindowWidth { get; set; }
    public double? WindowHeight { get; set; }
    public int? WindowX { get; set; }
    public int? WindowY { get; set; }
    public bool Maximized { get; set; }

    private static string FilePath => Path.Combine(
        Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } config
            ? config
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"),
        "openhw", "view.json");

    public static ViewState Load()
    {
        try
        {
            return JsonSerializer.Deserialize<ViewState>(File.ReadAllText(FilePath)) ?? new ViewState();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new ViewState();
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"openhw: could not save view state ({ex.Message})");
        }
    }
}
