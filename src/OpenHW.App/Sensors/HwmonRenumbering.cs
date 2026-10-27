using System.Text.RegularExpressions;
using OpenHW.Hardware;

namespace OpenHW.App.Sensors;

/// <summary>
/// hwmon ids and group keys carry the kernel's hwmon number ("hwmon:amd_hsmp_hwmon:9:power:1", "asusec (hwmon12)"),
/// which can change at any boot as drivers register in a different order. This maps a saved id or key that is no
/// longer present to the current one with the same chip and channel — only when exactly one matches, so chips that
/// appear more than once (nvme, spd5118) are never guessed at. Anything else comes back unchanged.
/// </summary>
public sealed partial class HwmonRenumbering
{
    private readonly HashSet<string> _ids;
    private readonly HashSet<string> _groups;
    private readonly Dictionary<string, string?> _byChannel = new(StringComparer.Ordinal); // "chip\nrest" → id, null if several
    private readonly Dictionary<string, string?> _byChip = new(StringComparer.Ordinal);    // chip → group key, null if several

    public HwmonRenumbering(IEnumerable<HardwareItem> inventory)
    {
        var items = inventory.ToList();
        _ids = items.Select(i => i.Id).ToHashSet(StringComparer.Ordinal);
        _groups = items.Select(i => i.Group).ToHashSet(StringComparer.Ordinal);
        foreach (var id in _ids)
            if (IdPattern().Match(id) is { Success: true } m)
                Add(_byChannel, m.Groups[1].Value + "\n" + m.Groups[3].Value, id);
        foreach (var group in _groups)
            if (GroupPattern().Match(group) is { Success: true } m)
                Add(_byChip, m.Groups[1].Value, group);
    }

    private static void Add(Dictionary<string, string?> map, string key, string value) =>
        map[key] = map.TryGetValue(key, out var existing) && existing != value ? null : value;

    /// <summary>The current id for a saved sensor id.</summary>
    public string Id(string id) =>
        _ids.Contains(id) || IdPattern().Match(id) is not { Success: true } m ||
        _byChannel.GetValueOrDefault(m.Groups[1].Value + "\n" + m.Groups[3].Value) is not { } current ? id : current;

    /// <summary>The current key for a saved group key (group order, collapsed groups).</summary>
    public string Group(string key) =>
        _groups.Contains(key) || GroupPattern().Match(key) is not { Success: true } m ||
        _byChip.GetValueOrDefault(m.Groups[1].Value) is not { } current ? key : current;

    /// <summary>A set with every member mapped; true when anything changed.</summary>
    public static bool Remap(HashSet<string> set, Func<string, string> map)
    {
        var mapped = set.Select(map).ToHashSet(set.Comparer);
        if (mapped.SetEquals(set)) return false;
        set.Clear();
        set.UnionWith(mapped);
        return true;
    }

    /// <summary>A list with every member mapped (first of any duplicates kept); true when anything changed.</summary>
    public static bool Remap(List<string> list, Func<string, string> map)
    {
        var mapped = list.Select(map).Distinct(StringComparer.Ordinal).ToList();
        if (mapped.SequenceEqual(list, StringComparer.Ordinal)) return false;
        list.Clear();
        list.AddRange(mapped);
        return true;
    }

    /// <summary>
    /// A dictionary with every key mapped; true when anything changed. An entry already under the current key wins
    /// over one moved onto it.
    /// </summary>
    public static bool Remap<T>(Dictionary<string, T> dictionary, Func<string, string> map)
    {
        var moves = dictionary.Keys.Select(k => (Old: k, New: map(k))).Where(m => m.Old != m.New).ToList();
        foreach (var (old, @new) in moves)
        {
            if (!dictionary.ContainsKey(@new)) dictionary[@new] = dictionary[old];
            dictionary.Remove(old);
        }
        return moves.Count > 0;
    }

    // hwmon:{chip}:{hwmonIndex}:{kind}:{n}[…]; stable identities (DIMM hubs' "device-…") don't match.
    [GeneratedRegex(@"^hwmon:([^:]+):([0-9]+):(.+)$", RegexOptions.CultureInvariant)]
    private static partial Regex IdPattern();

    [GeneratedRegex(@"^(.+) \(hwmon([0-9]+)\)$", RegexOptions.CultureInvariant)]
    private static partial Regex GroupPattern();
}
