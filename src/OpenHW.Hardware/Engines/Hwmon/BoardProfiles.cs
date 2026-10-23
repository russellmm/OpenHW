namespace OpenHW.Hardware;

/// <summary>
/// What one Super I/O input (voltage inN, temperature tempN, fan fanN) is on a given motherboard: its name, for
/// voltages the divider the board puts in front of the pin (the chip only measures up to ~2 V), and whether to show
/// it at all. <see cref="Confirmed"/> is false where the mapping is inferred but not yet proven, and
/// <see cref="Note"/> is added to the row's tooltip (see docs/board-profiles.md).
/// </summary>
public sealed record RailMapping(string Name, double Scale = 1, bool Confirmed = true, bool Hidden = false, string Note = "");

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

            // Temperatures: matched on behaviour across the same idle → load → idle run in both OSes
            // (docs/board-profiles.md). Inputs HWiNFO doesn't show read open-pin values and are hidden.
            ["temp1"] = new("Motherboard"),                                     // SYSTIN, flat
            ["temp2"] = new("CPUTIN", Note: "Socket thermistor; lags the CPU by minutes. Not in HWiNFO's list on this board"),
            ["temp3"] = new("Temp2"),                                           // AUXTIN0: drops under load in both OSes
            ["temp4"] = new("AUXTIN1", Hidden: true),
            ["temp5"] = new("AUXTIN2", Hidden: true),
            ["temp6"] = new("AUXTIN3", Hidden: true),
            ["temp7"] = new("Temp9"),                                           // AUXTIN4, flat 28 °C in both
            ["temp8"] = new("CPU", Confirmed: false,
                Note: "PECI/TSI Agent 0 Calibration; tracks load about 11 °C below Tctl, as HWiNFO's \"CPU\" does"),
            ["temp9"] = new("CPU (Weighted Value)", Confirmed: false,
                Note: "Virtual_TEMP: a heavily averaged CPU temperature for the board's fan control — rises ~2 °C over " +
                      "90 s of full load while the CPU rises 22 °C. For the CPU itself use CPU (Tctl/Tdie) or TSI0"),
            ["temp10"] = new("Temp14"),                                         // AUXTIN5: open pin, ~97 °C in both
            ["temp11"] = new("PCH_CHIP_CPU_MAX_TEMP", Hidden: true),
            ["temp12"] = new("PCH_CHIP_TEMP", Hidden: true),
            ["temp13"] = new("TSI0 (CPU)"),                                     // equals k10temp Tctl

            // Fans in HWiNFO's (register) order. CPU and Water Pump+ aren't under software control and match within
            // 2 %; the chassis fans differ in speed between OSes (different fan curves) but keep the same order.
            ["fan1"] = new("Chassis 1"),
            ["fan2"] = new("CPU"),
            ["fan3"] = new("Chassis 2"),
            ["fan4"] = new("Chassis 3"),
            ["fan5"] = new("Chassis 4"),
            ["fan6"] = new("Water Pump+"),
        }),
    ];

    private static readonly Dictionary<string, HardwareKind> InputKinds = new()
    {
        ["in"] = HardwareKind.Voltage, ["temp"] = HardwareKind.Temperature, ["fan"] = HardwareKind.Tach,
    };

    /// <summary>
    /// Renames and drops hwmon voltage, temperature and fan items per the matching profile. Returns the items to keep
    /// plus, for each renamed voltage, the factor to multiply raw readings by. Items on other chips/boards, and an
    /// input's alarm/limit rows, pass through untouched.
    /// </summary>
    public static (IReadOnlyList<HardwareItem> Items, IReadOnlyDictionary<string, double> Scales) Apply(
        IReadOnlyList<HardwareItem> items, string vendor, string board)
    {
        var kept = new List<HardwareItem>(items.Count);
        var scales = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            // libsensors input ids: hwmon:{chip}:{hwmonIndex}:{in|temp|fan}:{n}
            var parts = item.Id.Split(':');
            var profile = parts.Length == 5 && InputKinds.TryGetValue(parts[3], out var kind) && item.Kind == kind
                ? Known.FirstOrDefault(p => p.Matches(vendor, board, parts[1]))
                : null;
            if (profile is null || !profile.Inputs.TryGetValue(parts[3] + parts[4], out var rail))
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
                Details = item.Details.Replace(UnmappedRailNote, "") + (rail.Note.Length > 0 ? " · " + rail.Note : ""),
                OriginalName = item.Name, Scale = rail.Scale, MappingConfirmed = rail.Confirmed,
            });
        }
        return (kept, scales);
    }
}
