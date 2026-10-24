using System.Text.Json;

namespace OpenHW.App.Sensors;

/// <summary>
/// The user's per-sensor choices, from the sensor right-click menu: new names, hidden rows, sensors whose monitoring
/// is off, and the sensors CSV logs include (opt-in: none until chosen, so logs stay small). Kept in
/// ~/.config/openhw/sensors.json by sensor id, so they survive restarts and apply wherever the sensor appears.
/// </summary>
public sealed class SensorCustomizations
{
    public Dictionary<string, string> Names { get; set; } = [];
    public HashSet<string> Hidden { get; set; } = [];
    public HashSet<string> Disabled { get; set; } = [];
    public HashSet<string> Logged { get; set; } = [];

    /// <summary>Something changed; raised on the thread that changed it (the UI thread).</summary>
    public event Action? Changed;

    private static readonly object Gate = new();
    private static SensorCustomizations? _current;
    private static string? _currentPath;

    private static string FilePath => Path.Combine(
        Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } config
            ? config
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"),
        "openhw", "sensors.json");

    /// <summary>The one instance for this config directory (tests point XDG_CONFIG_HOME elsewhere and get their own).</summary>
    public static SensorCustomizations Current
    {
        get
        {
            lock (Gate)
            {
                var path = FilePath;
                if (_current is null || _currentPath != path)
                {
                    _current = Load(path);
                    _currentPath = path;
                }
                return _current;
            }
        }
    }

    private static SensorCustomizations Load(string path)
    {
        try { return JsonSerializer.Deserialize<SensorCustomizations>(File.ReadAllText(path)) ?? new(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }

    public string? NameOf(string id) => Names.TryGetValue(id, out var name) ? name : null;
    public bool IsHidden(string id) => Hidden.Contains(id);
    public bool IsDisabled(string id) => Disabled.Contains(id);
    public bool IsLogged(string id) => Logged.Contains(id);

    /// <summary>A new name, or null/blank to go back to OpenHW's own.</summary>
    public void Rename(string id, string? name) => Change(() =>
    {
        if (string.IsNullOrWhiteSpace(name)) Names.Remove(id);
        else Names[id] = name.Trim();
    });

    public void SetHidden(string id, bool hidden) => Change(() => { if (hidden) Hidden.Add(id); else Hidden.Remove(id); });
    public void SetDisabled(string id, bool disabled) => Change(() => { if (disabled) Disabled.Add(id); else Disabled.Remove(id); });
    public void SetLogged(string id, bool logged) => Change(() => { if (logged) Logged.Add(id); else Logged.Remove(id); });
    public void UnhideAll() => Change(Hidden.Clear);

    private void Change(Action change)
    {
        change();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"openhw: could not save sensor choices ({ex.Message})");
        }
        Changed?.Invoke();
    }
}
