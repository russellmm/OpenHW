using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenHW.App.Sensors;
using OpenHW.Hardware;

namespace OpenHW.App.Alerts;

public enum AlertCondition { Above, Below }

/// <summary>
/// One alert: fire when <see cref="SensorId"/> stays above (or below) <see cref="Threshold"/>, in the unit the window
/// shows, for at least <see cref="ForSeconds"/>. Yes/No sensors use Above 0.5 ("becomes Yes") or Below 0.5 ("becomes No").
/// </summary>
public sealed class AlertRule
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string SensorId { get; set; } = "";
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public AlertCondition Condition { get; set; } = AlertCondition.Above;
    public double Threshold { get; set; }
    public int ForSeconds { get; set; }
    public bool Enabled { get; set; } = true;

    /// <summary>HWiNFO's "Change Item Color": the sensor's Current cell turns red while this rule fires.</summary>
    public bool ChangeColor { get; set; } = true;

    public static readonly int[] DurationChoices = [0, 5, 10, 30, 60, 300];

    public bool IsMet(double value) => Condition == AlertCondition.Above ? value > Threshold : value < Threshold;

    /// <summary>"above 85 °C for 10 s", "becomes Yes".</summary>
    public string Describe(string unit)
    {
        var what = unit == "flag"
            ? Condition == AlertCondition.Above ? "becomes Yes" : "becomes No"
            : $"{(Condition == AlertCondition.Above ? "above" : "below")} {SensorPresentation.Format(Threshold, unit)}";
        return ForSeconds > 0 ? $"{what} for {Duration(ForSeconds)}" : what;
    }

    public static string Duration(int seconds) => seconds >= 60 && seconds % 60 == 0 ? $"{seconds / 60} min" : $"{seconds} s";
}

/// <summary>
/// The rules, kept in ~/.config/openhw/alerts.json; <see cref="Changed"/> after every edit. Edited on the UI thread and
/// read by the poll thread, so readers take <see cref="Snapshot"/>.
/// </summary>
public sealed class AlertStore
{
    private readonly object _gate = new();

    public List<AlertRule> Rules { get; set; } = [];

    public IReadOnlyList<AlertRule> Snapshot()
    {
        lock (_gate) return Rules.ToList();
    }

    [JsonIgnore] public string Path { get; private set; } = DefaultPath;
    public event Action? Changed;

    private static string DefaultPath => System.IO.Path.Combine(
        Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } config
            ? config
            : System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"),
        "openhw", "alerts.json");

    public static AlertStore Load()
    {
        AlertStore store;
        try { store = JsonSerializer.Deserialize<AlertStore>(File.ReadAllText(DefaultPath)) ?? new AlertStore(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { store = new AlertStore(); }
        store.Path = DefaultPath;
        store.Rules.RemoveAll(r => string.IsNullOrEmpty(r.SensorId) || !double.IsFinite(r.Threshold));
        return store;
    }

    public IEnumerable<AlertRule> For(string sensorId) => Snapshot().Where(r => r.SensorId == sensorId);

    public void Add(AlertRule rule) => Change(() => Rules.Add(rule));
    public void Remove(AlertRule rule) => Change(() => Rules.Remove(rule));
    public void Update(Action edit) => Change(edit);

    private void Change(Action change)
    {
        string json;
        lock (_gate)
        {
            change();
            json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        }
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            File.WriteAllText(Path, json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"openhw: could not save alerts ({ex.Message})");
        }
        Changed?.Invoke();
    }
}

/// <summary>A rule fired, or came back to normal.</summary>
public sealed record AlertEvent(AlertRule Rule, string SensorName, string Value, bool Firing, string Description);

/// <summary>
/// Checks every enabled rule against the latest readings once per poll. A rule fires when its condition has held for
/// its duration, then stays quiet until the condition clears (one notification per episode, plus "back to normal").
/// A poll with no reading for the sensor changes nothing. Time is passed in, so the logic is testable without waiting.
/// </summary>
public sealed class AlertMonitor(AlertStore store)
{
    private readonly Dictionary<string, double> _metSince = [];   // rule id → time its condition first held
    private readonly HashSet<string> _firing = [];
    private readonly object _gate = new(); // Evaluate runs on the poll thread; the UI reads the state

    public AlertStore Store => store;

    /// <summary>Sensors with a rule currently firing that changes the item's color (their Current cell turns red).</summary>
    public IReadOnlySet<string> AlertingSensors
    {
        get
        {
            lock (_gate) return store.Snapshot().Where(r => r.ChangeColor && _firing.Contains(r.Id)).Select(r => r.SensorId).ToHashSet(StringComparer.Ordinal);
        }
    }

    public bool IsFiring(AlertRule rule)
    {
        lock (_gate) return _firing.Contains(rule.Id);
    }

    /// <summary>Sensors with an enabled rule: kept live even in collapsed groups.</summary>
    public IEnumerable<string> WatchedSensors => store.Snapshot().Where(r => r.Enabled).Select(r => r.SensorId).Distinct().ToList();

    /// <param name="now">Seconds on any steady clock.</param>
    /// <param name="value">The sensor's current reading in display units, or null if it wasn't read.</param>
    public IReadOnlyList<AlertEvent> Evaluate(double now, Func<string, double?> value, Func<string, (string Name, string Unit)> describe)
    {
        var events = new List<AlertEvent>();
        var rules = store.Snapshot();
        lock (_gate)
        {
            foreach (var rule in rules)
            {
                if (!rule.Enabled)
                {
                    _metSince.Remove(rule.Id);
                    _firing.Remove(rule.Id); // switching a rule off clears it quietly
                    continue;
                }
                if (value(rule.SensorId) is not { } v) continue;
                var (name, unit) = describe(rule.SensorId);
                if (rule.IsMet(v))
                {
                    if (!_metSince.ContainsKey(rule.Id)) _metSince[rule.Id] = now;
                    if (!_firing.Contains(rule.Id) && now - _metSince[rule.Id] >= rule.ForSeconds - 0.001)
                    {
                        _firing.Add(rule.Id);
                        events.Add(new AlertEvent(rule, name, SensorPresentation.Format(v, unit), true, rule.Describe(unit)));
                    }
                }
                else
                {
                    _metSince.Remove(rule.Id);
                    if (_firing.Remove(rule.Id))
                        events.Add(new AlertEvent(rule, name, SensorPresentation.Format(v, unit), false, rule.Describe(unit)));
                }
            }
            foreach (var gone in _firing.Where(id => rules.All(r => r.Id != id)).ToList()) _firing.Remove(gone); // deleted rules
        }
        return events;
    }

    /// <summary>The value the window would show for a sensor right now (scaled to display units).</summary>
    public static double? DisplayValue(SensorHub hub, HardwareItem? item) =>
        item is null || hub.Get(item.Id).Current is not { } raw ? null : SensorPresentation.Scale(item, raw);

    public static string ThresholdText(double value) => value.ToString("0.###", CultureInfo.CurrentCulture);
}
