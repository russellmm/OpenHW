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

    /// <summary>Friendly fan names (fan id → name), shown on the cards; the original name stays in the tooltip.</summary>
    public Dictionary<string, string> Names { get; set; } = [];

    /// <summary>The order of the fan cards (fan ids); fans not listed follow in their natural order.</summary>
    public List<string> ControlOrder { get; set; } = [];

    /// <summary>The order of the curve cards (curve ids); curves not listed follow (flat, graph, mix, then by name).</summary>
    public List<string> CurveOrder { get; set; } = [];

    /// <summary>A fan's chosen curve while its Curve box is unticked (kept so ticking it again restores the choice).</summary>
    public Dictionary<string, string> Chosen { get; set; } = [];

    /// <summary>Set if the BMC ever refuses a curve whose point 4 isn't 70 °C: fits keep point 4 at 70 from then on.</summary>
    public bool FourthAt70 { get; set; }

    /// <summary>OpenHW-driven duty writes today, per zone (each is a write to the BMC's flash).</summary>
    public string WritesDay { get; set; } = "";
    public Dictionary<string, int> WritesToday { get; set; } = [];

    public static string DefaultPath => Path.Combine(Settings.AppSettings.ConfigDir, "fan-curves.json");

    /// <summary>Where saved configurations go by default (as OpenFan's profiles folder).</summary>
    public static string ProfilesDir => Path.Combine(Settings.AppSettings.ConfigDir, "fan-profiles");

    /// <summary>
    /// The configuration file in use (Save as… / Load… / New…), or null. While set, every change is also saved there,
    /// as in OpenFan ("changes save here").
    /// </summary>
    public string? ConfigFile { get; set; }

    /// <summary>A configuration file: the curves, which fan runs which, the fans' names.</summary>
    public sealed record FanConfig(int Version, List<FanCurve> Curves, Dictionary<string, string> Assignments,
        Dictionary<string, string> Names, Dictionary<string, string> Chosen, List<string>? ControlOrder = null, List<string>? CurveOrder = null);

    public FanConfig Config() => new(1, Curves, Assignments, Names, Chosen, ControlOrder, CurveOrder);

    /// <summary>Writes the configuration to <paramref name="path"/> (Save / Save as).</summary>
    public void Export(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(Config(), new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>
    /// Replaces the curves, assignments and names with a configuration file's (checked like the live file), and makes it
    /// the file in use. Throws <see cref="InvalidDataException"/> for something that isn't a configuration.
    /// </summary>
    public void Import(string path)
    {
        FanConfig? config;
        try { config = JsonSerializer.Deserialize<FanConfig>(File.ReadAllText(path)); }
        catch (JsonException ex) { throw new InvalidDataException("Not an OpenHW fan configuration: " + ex.Message, ex); }
        if (config is null || config.Curves is null) throw new InvalidDataException("Not an OpenHW fan configuration.");
        Curves = config.Curves;
        Assignments = config.Assignments ?? [];
        Names = config.Names ?? [];
        Chosen = config.Chosen ?? [];
        ControlOrder = config.ControlOrder ?? ControlOrder;
        CurveOrder = config.CurveOrder ?? [];
        Normalise();
        ConfigFile = path;
    }

    /// <summary>An empty configuration (no curves, no assignments; names kept), saved in <paramref name="path"/>.</summary>
    public void StartNew(string path)
    {
        Curves = [];
        Assignments = [];
        Chosen = [];
        ConfigFile = path;
        Export(path);
    }

    /// <summary>True when the configuration in use differs from its file (unsaved — can't happen while auto-saving, but a
    /// file edited or removed outside OpenHW shows here).</summary>
    public bool DiffersFromFile()
    {
        if (ConfigFile is null) return false;
        try { return File.ReadAllText(ConfigFile) != JsonSerializer.Serialize(Config(), new JsonSerializerOptions { WriteIndented = true }); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return true; }
    }

    [System.Text.Json.Serialization.JsonIgnore] public string FilePath { get; private set; } = DefaultPath;

    public static FanCurveStore Load(string? path = null)
    {
        path ??= DefaultPath;
        FanCurveStore store;
        try { store = JsonSerializer.Deserialize<FanCurveStore>(File.ReadAllText(path)) ?? new(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { store = new(); }
        store.FilePath = path;
        store.Normalise();
        return store;
    }

    // A hand-edited or older file can hold anything: keep what the UI could have produced.
    private void Normalise()
    {
        Curves = Curves.Where(c => c is not null && !string.IsNullOrWhiteSpace(c.Id)).DistinctBy(c => c.Id).ToList();
        foreach (var c in Curves)
        {
            c.Name ??= "";
            c.Points ??= [];
            c.Children ??= [];
            c.Percent = Math.Clamp(c.Percent, 0, 100);
            c.MaxSpeedPercent = Math.Clamp(c.MaxSpeedPercent, 1, 100);
            c.Children = c.Children.Where(id => Curves.Any(x => x.Id == id && x.Kind != FanCurveKind.Mix)).Distinct().ToList();
        }
        Assignments = Assignments.Where(a => Curves.Any(c => c.Id == a.Value)).ToDictionary(a => a.Key, a => a.Value);
        Chosen = Chosen.Where(a => Curves.Any(c => c.Id == a.Value)).ToDictionary(a => a.Key, a => a.Value);
        Names = Names.Where(n => !string.IsNullOrWhiteSpace(n.Value)).ToDictionary(n => n.Key, n => n.Value);
        ControlOrder = (ControlOrder ?? []).Where(id => !string.IsNullOrWhiteSpace(id)).Distinct().ToList();
        CurveOrder = (CurveOrder ?? []).Where(id => Curves.Any(c => c.Id == id)).Distinct().ToList();
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
        if (ConfigFile is { } file)
            try { Export(file); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Console.Error.WriteLine($"openhw: saving {file}: " + ex.Message); }
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
        CurveOrder.Remove(id);
        foreach (var c in Curves) c.Children.Remove(id);
        foreach (var zone in Assignments.Where(a => a.Value == id).Select(a => a.Key).ToList()) Assignments.Remove(zone);
        foreach (var zone in Chosen.Where(a => a.Value == id).Select(a => a.Key).ToList()) Chosen.Remove(zone);
    }

    /// <summary>Fans in card order: listed ones first (in <see cref="ControlOrder"/>), the rest as they come.</summary>
    public List<string> Ordered(IEnumerable<string> ids) => OrderBy(ControlOrder, ids);

    /// <summary>Curves in card order (<see cref="CurveOrder"/> first); <paramref name="natural"/> gives the rest's order.</summary>
    public List<string> OrderedCurves(IEnumerable<string> natural) => OrderBy(CurveOrder, natural);

    /// <summary>
    /// Moves a fan's card: <paramref name="to"/> = a position in the current order (clamped). Returns false when it
    /// didn't move. Fans that aren't shown right now keep their places in the saved order.
    /// </summary>
    public bool Move(IReadOnlyList<string> shown, string id, int to)
    {
        var order = ControlOrder;
        var moved = MoveIn(ref order, shown, id, to);
        ControlOrder = order;
        return moved;
    }

    /// <summary>Moves a curve's card (as <see cref="Move"/>); <paramref name="shown"/> = the curve cards as shown.</summary>
    public bool MoveCurve(IReadOnlyList<string> shown, string id, int to)
    {
        var order = CurveOrder;
        var moved = MoveIn(ref order, shown, id, to);
        CurveOrder = order;
        return moved;
    }

    private static List<string> OrderBy(List<string> saved, IEnumerable<string> ids) =>
        ids.Select((id, natural) => (id, natural))
            .OrderBy(x => saved.IndexOf(x.id) is var i && i >= 0 ? i : int.MaxValue).ThenBy(x => x.natural)
            .Select(x => x.id).ToList();

    private static bool MoveIn(ref List<string> saved, IReadOnlyList<string> shown, string id, int to)
    {
        var order = OrderBy(saved, shown);
        var from = order.IndexOf(id);
        if (from < 0 || order.Count == 0) return false;
        to = Math.Clamp(to, 0, order.Count - 1);
        if (from == to) return false;
        // Next to the card it lands on, in the full saved order: cards not shown now keep their places.
        var anchor = order[to];
        var full = OrderBy(saved, saved.Concat(shown).Distinct());
        full.Remove(id);
        var at = full.IndexOf(anchor);
        full.Insert(to < from ? at : at + 1, id);
        saved = full;
        return true;
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
