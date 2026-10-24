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

    /// <summary>Sensor menu → Customize values…: value × Multiply + Offset, a unit of the user's, and decimal places.</summary>
    public Dictionary<string, ValueCustomization> Values { get; set; } = [];

    /// <summary>
    /// Layout settings: the user's order of groups (by group key) and of rows within each group (by sensor id).
    /// Lists may be partial or stale; <see cref="Arrange{T}"/> places whatever they name and leaves the rest alone.
    /// </summary>
    public List<string> GroupOrder { get; set; } = [];
    public Dictionary<string, List<string>> RowOrder { get; set; } = [];

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
        SensorCustomizations custom;
        try { custom = JsonSerializer.Deserialize<SensorCustomizations>(File.ReadAllText(path)) ?? new(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return new(); }
        custom.Values = custom.Values.Where(v => v.Value is { IsDefault: false })
            .ToDictionary(v => v.Key, v => v.Value.Normalised(), StringComparer.Ordinal);
        return custom;
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

    public ValueCustomization? ValuesOf(string id) => Values.GetValueOrDefault(id);

    /// <summary>New value settings for a sensor; null (or all defaults) goes back to OpenHW's own.</summary>
    public void Customize(string id, ValueCustomization? values) => Change(() =>
    {
        if (values is null || values.IsDefault) Values.Remove(id);
        else Values[id] = values.Normalised();
    });

    public void SetGroupOrder(IEnumerable<string> keys) => Change(() => GroupOrder = keys.ToList());
    public void SetRowOrder(string groupKey, IEnumerable<string> ids) => Change(() => RowOrder[groupKey] = ids.ToList());
    public void RestoreOriginalOrder() => Change(() => { GroupOrder.Clear(); RowOrder.Clear(); });
    public bool HasCustomOrder => GroupOrder.Count > 0 || RowOrder.Count > 0;

    /// <summary>
    /// Puts <paramref name="items"/> in the user's order: the items <paramref name="order"/> names take the
    /// positions those items already hold, in the order given; everything else stays where it was. So a sensor or
    /// group that appears later (new drive, driver update) keeps its built-in place, and a stale id is ignored.
    /// </summary>
    public static List<T> Arrange<T>(IReadOnlyList<T> items, Func<T, string> key, IReadOnlyList<string>? order)
    {
        if (order is not { Count: > 0 }) return items.ToList();
        var rank = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var id in order) rank.TryAdd(id, rank.Count);
        var placed = new Queue<T>(items.Where(i => rank.ContainsKey(key(i))).OrderBy(i => rank[key(i)]));
        return items.Select(i => rank.ContainsKey(key(i)) ? placed.Dequeue() : i).ToList();
    }

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

/// <summary>
/// How one sensor's values are shown: reading × <see cref="Multiply"/> + <see cref="Offset"/>, in <see cref="Unit"/>
/// (null keeps OpenHW's, "" shows none) with <see cref="Decimals"/> places (null keeps OpenHW's). Applies everywhere the value
/// appears: the window, graphs, tray icons and menus, logs and alert thresholds.
/// </summary>
public sealed record ValueCustomization
{
    public const int MaxDecimals = 6;

    public double Multiply { get; init; } = 1;
    public double Offset { get; init; }
    public string? Unit { get; init; }
    public int? Decimals { get; init; }

    public bool IsDefault => Multiply == 1 && Offset == 0 && Unit is null && Decimals is null;

    public double Apply(double value) => value * Multiply + Offset;

    // A hand-edited file can hold anything: a zero or non-finite factor would blank the sensor.
    public ValueCustomization Normalised() => this with
    {
        Multiply = double.IsFinite(Multiply) && Multiply != 0 ? Multiply : 1,
        Offset = double.IsFinite(Offset) ? Offset : 0,
        Unit = Unit?.Trim(), // "" = show no unit; null = OpenHW's unit
        Decimals = Decimals is { } d ? Math.Clamp(d, 0, MaxDecimals) : null,
    };
}
