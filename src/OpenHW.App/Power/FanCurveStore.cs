using System.Text.Json;
using OpenHW.Hardware;

namespace OpenHW.App.Power;

/// <summary>
/// The user's fan curves and which fan runs which curve: ~/.config/openhw/fan-curves.json. A missing or unreadable file
/// means no curves (every fan keeps the BMC curve it has).
/// </summary>
public sealed class FanCurveStore
{
    public List<FanCurve> Curves { get; set; } = [];

    /// <summary>Fan zone ("bmc:3") → curve id. A zone without an entry keeps the BMC curve it has.</summary>
    public Dictionary<string, string> Assignments { get; set; } = [];

    /// <summary>A fan's chosen curve while its Curve box is unticked (kept so ticking it again restores the choice).</summary>
    public Dictionary<string, string> Chosen { get; set; } = [];

    /// <summary>Set if the BMC ever refuses a curve whose point 4 isn't 70 °C: fits keep point 4 at 70 from then on.</summary>
    public bool FourthAt70 { get; set; }

    /// <summary>OpenHW-driven duty writes today, per zone (each is a write to the BMC's flash).</summary>
    public string WritesDay { get; set; } = "";
    public Dictionary<string, int> WritesToday { get; set; } = [];

    public static string DefaultPath => Path.Combine(Settings.AppSettings.ConfigDir, "fan-curves.json");

    [System.Text.Json.Serialization.JsonIgnore] public string FilePath { get; private set; } = DefaultPath;

    public static FanCurveStore Load(string? path = null)
    {
        path ??= DefaultPath;
        FanCurveStore store;
        try { store = JsonSerializer.Deserialize<FanCurveStore>(File.ReadAllText(path)) ?? new(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { store = new(); }
        store.FilePath = path;
        store.Curves = store.Curves.Where(c => !string.IsNullOrWhiteSpace(c.Id)).DistinctBy(c => c.Id).ToList();
        foreach (var c in store.Curves)
        {
            c.Percent = Math.Clamp(c.Percent, 0, 100);
            c.MaxSpeedPercent = Math.Clamp(c.MaxSpeedPercent, 1, 100);
            c.Children = c.Children.Where(id => store.Curves.Any(x => x.Id == id && x.Kind != FanCurveKind.Mix)).Distinct().ToList();
        }
        store.Assignments = store.Assignments.Where(a => store.Curves.Any(c => c.Id == a.Value)).ToDictionary(a => a.Key, a => a.Value);
        store.Chosen = store.Chosen.Where(a => store.Curves.Any(c => c.Id == a.Value)).ToDictionary(a => a.Key, a => a.Value);
        return store;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, FilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Console.Error.WriteLine("openhw: saving fan curves: " + ex.Message); }
        Changed?.Invoke();
    }

    /// <summary>Raised after every save, so the open fan windows redraw.</summary>
    public event Action? Changed;

    public IReadOnlyDictionary<string, FanCurve> Library => Curves.ToDictionary(c => c.Id, StringComparer.Ordinal);

    public FanCurve? Find(string? id) => id is null ? null : Curves.FirstOrDefault(c => c.Id == id);

    public FanCurve? AssignedTo(string zoneId) => Assignments.TryGetValue(zoneId, out var id) ? Find(id) : null;

    public static string NewId() => "curve-" + Guid.NewGuid().ToString("N");

    /// <summary>Removes a curve, takes it out of every mix, and unassigns it from every fan.</summary>
    public void Remove(string id)
    {
        Curves.RemoveAll(c => c.Id == id);
        foreach (var c in Curves) c.Children.Remove(id);
        foreach (var zone in Assignments.Where(a => a.Value == id).Select(a => a.Key).ToList()) Assignments.Remove(zone);
        foreach (var zone in Chosen.Where(a => a.Value == id).Select(a => a.Key).ToList()) Chosen.Remove(zone);
    }

    /// <summary>Counts one OpenHW-driven write for a zone (the count starts again every day).</summary>
    public int CountWrite(string zoneId, DateTime now)
    {
        var day = now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        if (WritesDay != day) { WritesDay = day; WritesToday.Clear(); }
        WritesToday[zoneId] = WritesToday.GetValueOrDefault(zoneId) + 1;
        return WritesToday[zoneId];
    }

    public int WritesOn(string zoneId, DateTime now) =>
        WritesDay == now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) ? WritesToday.GetValueOrDefault(zoneId) : 0;
}
