namespace OpenHW.Hardware;

/// <summary>
/// What one Super I/O voltage input is on a given motherboard: its rail name, the divider the board puts in front
/// of the pin (the chip only measures up to ~2 V), and whether to show it at all. <see cref="Confirmed"/> is false
/// where the mapping is inferred but not yet proven (see docs/board-profiles.md).
/// </summary>
public sealed record RailMapping(string Name, double Scale = 1, bool Confirmed = true, bool Hidden = false);

/// <summary>A board's rail map for one Super I/O chip family, matched on the DMI board vendor and name.</summary>
public sealed record BoardProfile(string VendorPrefix, string Board, string ChipPrefix, IReadOnlyDictionary<string, RailMapping> Inputs)
{
    public bool Matches(string vendor, string board, string chip) =>
        vendor.StartsWith(VendorPrefix, StringComparison.OrdinalIgnoreCase) &&
        board.Equals(Board, StringComparison.OrdinalIgnoreCase) &&
        chip.StartsWith(ChipPrefix, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Known boards. Linux only reports Super I/O voltages as in0…inN, raw at the pin; HWiNFO names and scales them
/// from a per-board database. Entries here are derived the same way: matching HWiNFO's register-ordered list
/// against the raw readings on the same machine (method in docs/board-profiles.md).
/// </summary>
public static class BoardProfiles
{
    /// <summary>What LibSensorsBackend appends to voltages it has no label for.</summary>
    public const string UnmappedRailNote = " · unverified motherboard rail mapping";

    // ×1.11: the divider ASUS uses on the low-voltage VIN pins of this board (four rails agree within 0.1 %).
    private const double LowRailDivider = 1.11;

    public static readonly IReadOnlyList<BoardProfile> Known =
    [
        new("ASUSTeK", "Pro WS TRX50-SAGE WIFI A", "nct67", new Dictionary<string, RailMapping>
        {
            ["in0"] = new("Vcore"),
            ["in1"] = new("+5V", 5),
            ["in2"] = new("AVSB"),
            ["in3"] = new("3VCC"),
            ["in4"] = new("+12V", 12),
            ["in5"] = new("CPU VDDIO", LowRailDivider),
            ["in6"] = new("CPU Core1", LowRailDivider),
            ["in7"] = new("3VSB"),
            ["in8"] = new("VBAT"),
            ["in9"] = new("VTT"),
            ["in10"] = new("CHIPSET1 1.0V"),
            ["in11"] = new("VDD_11_S3 / MC", LowRailDivider),
            ["in12"] = new("VIN2"),
            ["in13"] = new("CPU VSOC", LowRailDivider, Confirmed: false),
            ["in14"] = new("CHIPSET1 1.8V", LowRailDivider),
            ["in15"] = new("VIN9", Confirmed: false),
            ["in16"] = new("VHIF"),
            ["in17"] = new("in17", Hidden: true), // not wired to a rail HWiNFO reports
        }),
    ];

    /// <summary>
    /// Renames and drops hwmon voltage items per the matching profile. Returns the items to keep plus, for each
    /// renamed id, the factor to multiply raw readings by. Items on other chips/boards pass through untouched.
    /// </summary>
    public static (IReadOnlyList<HardwareItem> Items, IReadOnlyDictionary<string, double> Scales) Apply(
        IReadOnlyList<HardwareItem> items, string vendor, string board)
    {
        var kept = new List<HardwareItem>(items.Count);
        var scales = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            // libsensors voltage ids: hwmon:{chip}:{hwmonIndex}:in:{n}
            var parts = item.Id.Split(':');
            var profile = item.Kind == HardwareKind.Voltage && parts.Length == 5 && parts[3] == "in"
                ? Known.FirstOrDefault(p => p.Matches(vendor, board, parts[1]))
                : null;
            if (profile is null || !profile.Inputs.TryGetValue("in" + parts[4], out var rail))
            {
                kept.Add(item);
                continue;
            }
            if (rail.Hidden) continue;
            if (rail.Scale != 1) scales[item.Id] = rail.Scale;
            kept.Add(new HardwareItem
            {
                Id = item.Id, Kind = item.Kind, Name = rail.Name, Backend = item.Backend, Group = item.Group,
                MinPercent = item.MinPercent, CanSet = item.CanSet, Unit = item.Unit, Source = item.Source,
                // The profile replaces libsensors' generic "unverified motherboard rail mapping" note.
                Details = item.Details.Replace(UnmappedRailNote, ""),
                OriginalName = item.Name, Scale = rail.Scale, MappingConfirmed = rail.Confirmed,
            });
        }
        return (kept, scales);
    }
}
