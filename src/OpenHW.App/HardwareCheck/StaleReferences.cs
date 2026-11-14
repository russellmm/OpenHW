using System.Text.RegularExpressions;
using OpenHW.Hardware;

namespace OpenHW.App.HardwareCheck;

/// <summary>A saved choice (tray icon, alert, logging, rename…) whose sensor is not present now.</summary>
/// <param name="Uses">Where it's used, in words: "Tray icon", "Alert below 2800", "Logged", "Renamed"….</param>
/// <param name="OldName">What the sensor was called, as well as OpenHW can tell.</param>
/// <param name="Suggestion">A present sensor that is very likely the same thing (same name family and kind), or null.</param>
public sealed record StaleReference(string Id, string OldName, HardwareKind? Kind, List<string> Uses, string? Suggestion);

/// <summary>Finds saved sensor ids that no longer match the hardware, and the present sensor each most likely became.</summary>
public static partial class StaleReferences
{
    public sealed record Saved(string Id, string Use);

    public static List<StaleReference> Find(IEnumerable<Saved> saved, IReadOnlyList<HardwareItem> inventory,
        HardwareSnapshot? before, HardwareSnapshot now, Func<HardwareItem, string> displayName,
        IReadOnlyDictionary<string, string> userNames, bool gpuLent = false)
    {
        var present = inventory.Select(i => i.Id).ToHashSet(StringComparer.Ordinal);
        var result = new List<StaleReference>();
        foreach (var group in saved.Where(s => !present.Contains(s.Id) && !s.Id.StartsWith("auto:", StringComparison.Ordinal))
                     .GroupBy(s => s.Id, StringComparer.Ordinal))
        {
            var id = group.Key;
            // A lent card's sensors are away, not gone.
            if (gpuLent && id.StartsWith("nvml:", StringComparison.Ordinal)) continue;
            var oldName = before?.SensorNames.GetValueOrDefault(id) ?? userNames.GetValueOrDefault(id) ?? ProfileName(id) ?? Tail(id);
            var kind = KindOf(id);
            result.Add(new StaleReference(id, oldName, kind, group.Select(g => g.Use).Distinct().ToList(),
                Suggest(id, oldName, kind, inventory, before, now, displayName)));
        }
        return result;
    }

    /// <summary>The sensor a stale id most likely became: a drive's temperature by drive identity, else a unique name match of the same kind.</summary>
    private static string? Suggest(string id, string oldName, HardwareKind? kind, IReadOnlyList<HardwareItem> inventory,
        HardwareSnapshot? before, HardwareSnapshot now, Func<HardwareItem, string> displayName)
    {
        // hwmon:nvme:N:…: the drive that had hwmonN before, wherever its temperature is now.
        if (HwmonId().Match(id) is { Success: true } m && m.Groups[1].Value == "nvme" && before is not null)
        {
            var drive = before.Drives.FirstOrDefault(d => d.Hwmon?.ToString(System.Globalization.CultureInfo.InvariantCulture) == m.Groups[2].Value);
            if (now.Drives.FirstOrDefault(d => d.Identity == drive?.Identity)?.Hwmon is { } hwmon)
            {
                var candidate = $"hwmon:nvme:{hwmon}:{m.Groups[3].Value}";
                if (inventory.Any(i => i.Id == candidate)) return candidate;
            }
            return null; // never guess between drives
        }
        var key = Normalise(oldName, kind);
        if (key.Length == 0) return null;
        var matches = inventory.Where(i => (kind is null || i.Kind == kind) && Normalise(displayName(i), i.Kind) == key)
            .Select(i => i.Id).ToList();
        if (matches.Count == 1) return matches[0];
        // Several (e.g. the Super I/O and the BMC both read the pump): prefer the BMC's named header.
        var bmc = matches.Where(m2 => m2.StartsWith(BmcEngine.IdPrefix, StringComparison.Ordinal)).ToList();
        return bmc.Count == 1 ? bmc[0] : null;
    }

    /// <summary>
    /// One spelling for the names boards, HWiNFO and BMCs use for the same header or rail: "Water Pump+" and
    /// "W_PUMP+" → "WPUMP", "Chassis 2" and "CHA_FAN2" → "CHAFAN2", the fan "CPU" and "CPU_FAN" → "CPU".
    /// </summary>
    public static string Normalise(string name, HardwareKind? kind)
    {
        var s = NonAlnum().Replace(name.ToUpperInvariant(), "");
        s = s.Replace("WATERPUMP", "WPUMP").Replace("CHASSIS", "CHAFAN").Replace("CHAFANFAN", "CHAFAN");
        if (kind == HardwareKind.Tach)
        {
            if (s.EndsWith("FAN", StringComparison.Ordinal) && s.Length > 3) s = s[..^3];
            s = s.Replace("CPUFAN", "CPU");
        }
        if (kind == HardwareKind.Temperature) s = s.Replace("TEMPERATURE", "TEMP");
        return s;
    }

    /// <summary>A board profile's name for a Super I/O input, from whichever known board uses that chip ("nct6799 fan6" → "Water Pump+").</summary>
    private static string? ProfileName(string id)
    {
        if (HwmonId().Match(id) is not { Success: true } m) return null;
        var input = m.Groups[3].Value.Replace(":", ""); // "fan:6" → "fan6"
        return BoardProfiles.Known.FirstOrDefault(p => m.Groups[1].Value.StartsWith(p.ChipPrefix, StringComparison.OrdinalIgnoreCase)
                                                       && p.Inputs.ContainsKey(input))?.Inputs[input].Name;
    }

    private static HardwareKind? KindOf(string id)
    {
        if (HwmonId().Match(id) is not { Success: true } m) return null;
        return m.Groups[3].Value.Split(':')[0] switch
        {
            "fan" => HardwareKind.Tach, "temp" => HardwareKind.Temperature, "in" => HardwareKind.Voltage,
            "power" => HardwareKind.Power, "curr" => HardwareKind.Current, _ => null,
        };
    }

    private static string Tail(string id) => id.Length > 48 ? "…" + id[^47..] : id;

    [GeneratedRegex(@"^hwmon:([^:]+):([0-9]+):(.+)$", RegexOptions.CultureInvariant)] private static partial Regex HwmonId();
    [GeneratedRegex(@"[^A-Z0-9]", RegexOptions.CultureInvariant)] private static partial Regex NonAlnum();
}
