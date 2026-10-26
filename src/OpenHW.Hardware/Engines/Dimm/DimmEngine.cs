namespace OpenHW.Hardware;

/// <summary>
/// HWiNFO's per-DIMM rows beyond the hub temperature: TS0/TS1, the PMIC's rails (SWA VDD, SWB VDDQ, SWC VPP, the 1.8 V
/// and 1.0 V LDOs, VIN), its power or current meters and alarm flags — read through openhw-helper's <c>ddr5</c>
/// command (<see cref="Ddr5Reader"/>). Without the helper (or an older one) there are no rows; it asks again at most
/// once a minute. Each DIMM is its own group, shared with the spd5118 hub temperature from hwmon.
/// </summary>
public sealed class DimmEngine(Func<string>? request = null, string sysRoot = "/sys") : ISensorEngine
{
    private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromMinutes(1);

    private readonly Func<string> _request = request ?? (() => HelperClient.Request("ddr5", receiveTimeoutMs: 5000));
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private IReadOnlyList<Ddr5Sample> _samples = [];
    private readonly Dictionary<string, string> _identities = []; // "0-50" → hub identity
    private TimeSpan? _failedAt;

    public string Name => "ddr5";
    public bool Available => _samples.Count > 0;

    /// <summary>The group a DIMM's rows share: the hub identity <see cref="DramSensorIdentity"/> gives its hwmon row.</summary>
    public static string GroupKey(string identity) => "dimm:" + identity;

    public IReadOnlyList<HardwareItem> Discover()
    {
        if (_samples.Count == 0 && (_failedAt is null || _clock.Elapsed - _failedAt > RetryAfterFailure)) Request();
        var items = new List<HardwareItem>();
        foreach (var sample in _samples)
        {
            if (Identity(sample) is not { } identity) continue;
            foreach (var row in Rows(sample))
                items.Add(new HardwareItem
                {
                    Id = $"ddr5:{identity}:{row.Key}", Name = row.Name, Kind = row.Kind, Unit = row.Unit,
                    Group = GroupKey(identity), Backend = "openhw", Source = "DDR5 sideband (openhw-helper)", CanSet = false,
                    Details = row.Details + $" · I2C {sample.Bus}, {row.Device}",
                });
        }
        return items;
    }

    public void ReadInto(IDictionary<string, double?> readings, IReadOnlySet<string>? requested)
    {
        if (_samples.Count == 0) return;
        if (requested is not null && !requested.Any(id => id.StartsWith("ddr5:", StringComparison.Ordinal))) return;
        if (!Request()) return;
        foreach (var sample in _samples)
        {
            if (Identity(sample) is not { } identity) continue;
            foreach (var row in Rows(sample)) readings[$"ddr5:{identity}:{row.Key}"] = row.Value;
        }
    }

    private bool Request()
    {
        try
        {
            _samples = Ddr5Reader.Parse(_request());
            _failedAt = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException)
        {
            _failedAt = _clock.Elapsed;
            return false;
        }
    }

    private string? Identity(Ddr5Sample sample)
    {
        if (_identities.TryGetValue(sample.Client, out var known)) return known;
        var identity = DramSensorIdentity.Locate($"{sample.Bus}-00{sample.Hub:x2}", sysRoot)?.Identity;
        if (identity is not null) _identities[sample.Client] = identity;
        return identity;
    }

    private sealed record Row(string Key, string Name, HardwareKind Kind, string Unit, double? Value, string Device, string Details);

    /// <summary>The rows one DIMM has, with their values decoded (JEDEC PMIC5000/5100 register map; RTQ5132 datasheet).</summary>
    private static IEnumerable<Row> Rows(Ddr5Sample s)
    {
        var n = s.Hub & 7;
        if (s.Ts0 is { } ts0) yield return new("ts0", "TS0 Temperature", HardwareKind.Temperature, "°C", Ddr5Decode.Temperature(ts0), $"TS0 0x{0x10 | n:x2}", "DIMM thermal sensor 0, MR49/MR50");
        if (s.Ts1 is { } ts1) yield return new("ts1", "TS1 Temperature", HardwareKind.Temperature, "°C", Ddr5Decode.Temperature(ts1), $"TS1 0x{0x30 | n:x2}", "DIMM thermal sensor 1, MR49/MR50");
        if (s.Pmic is not { } p) yield break;
        var pmic = $"PMIC 0x{0x48 | n:x2} (vendor {p.Vendor:x4})";
        string Adc(string rail) => $"PMIC ADC (R30 select {rail}, R31)";
        yield return new("vdd", "VDD (SWA) Voltage", HardwareKind.Voltage, "V", Ddr5Decode.Rail(p.Adc[0]), pmic, Adc("SWA"));
        yield return new("vddq", "VDDQ (SWB) Voltage", HardwareKind.Voltage, "V", Ddr5Decode.Rail(p.Adc[1]), pmic, Adc("SWB"));
        yield return new("vpp", "VPP (SWC) Voltage", HardwareKind.Voltage, "V", Ddr5Decode.Rail(p.Adc[2]), pmic, Adc("SWC"));
        yield return new("ldo18", "1.8V VOUT Voltage", HardwareKind.Voltage, "V", Ddr5Decode.Rail(p.Adc[4]), pmic, Adc("VLDO_1.8V"));
        yield return new("ldo10", "1.0V VOUT Voltage", HardwareKind.Voltage, "V", Ddr5Decode.Rail(p.Adc[5]), pmic, Adc("VLDO_1.0V"));
        yield return new("vin", "VIN Voltage", HardwareKind.Voltage, "V", Ddr5Decode.Input(p.Adc[3]), pmic, Adc("VIN_BULK"));
        var (r0c, r0e, r0f, r1a, r1b) = (p.Registers[4], p.Registers[5], p.Registers[6], p.Registers[7], p.Registers[8]);
        var power = (r1b & 0x40) != 0;
        if (power && (r1a & 0x02) != 0)
            yield return new("ptotal", "Total Power", HardwareKind.Power, "W", Ddr5Decode.Meter(r0c), pmic, "PMIC R0C (sum of SWA, SWB, SWC output power)");
        else
        {
            var (kind, unit, what) = power ? (HardwareKind.Power, "W", "Power") : (HardwareKind.Current, "A", "Current");
            yield return new("mswa", $"SWA {what}", kind, unit, Ddr5Decode.Meter(r0c), pmic, $"PMIC R0C (SWA output {what.ToLowerInvariant()})");
            yield return new("mswb", $"SWB {what}", kind, unit, Ddr5Decode.Meter((byte)(r0e & 0x3F)), pmic, $"PMIC R0E (SWB output {what.ToLowerInvariant()})");
            yield return new("mswc", $"SWC {what}", kind, unit, Ddr5Decode.Meter((byte)(r0f & 0x3F)), pmic, $"PMIC R0F (SWC output {what.ToLowerInvariant()})");
        }
        var (r08, r09, r0a, r0b) = (p.Registers[0], p.Registers[1], p.Registers[2], p.Registers[3]);
        yield return new("hot", "PMIC High Temperature", HardwareKind.Status, "flag", Flag((r09 & 0x80) != 0), pmic, "PMIC R09[7] (high temperature warning)");
        yield return new("ov", "PMIC Over Voltage", HardwareKind.Status, "flag", Flag((r08 & 0x01) != 0 || (r0a & 0xB0) != 0), pmic, "PMIC R08[0], R0A[7,5,4] (VIN and switcher overvoltage)");
        yield return new("uv", "PMIC Under Voltage", HardwareKind.Status, "flag", Flag((r0b & 0x0B) != 0), pmic, "PMIC R0B[3,1,0] (switcher undervoltage lockout)");
    }

    private static double Flag(bool on) => on ? 1 : 0;

    /// <summary>"DDR5 DIMM [#0]: Kingston KF556R28-32 (AMDI0010:01 / 0x50)" for a DIMM group, numbered in hub order.</summary>
    public static string Title(string groupKey, string sysRoot = "/sys")
    {
        var identity = groupKey.StartsWith("dimm:", StringComparison.Ordinal) ? groupKey[5..] : groupKey;
        var modules = Modules(sysRoot);
        var index = modules.FindIndex(m => m.Identity == identity);
        if (index < 0) return "DDR5 DIMM";
        var m = modules[index];
        var name = string.Join(' ', new[] { m.Maker, m.Part }.Where(t => t.Length > 0));
        return $"DDR5 DIMM [#{index}]: {(name.Length > 0 ? name + " " : "")}({m.Who})";
    }

    private static readonly Dictionary<string, List<(string Identity, string Who, string Maker, string Part)>> ModuleCache = [];

    // SPD contents don't change while the system runs; read once per sysfs root.
    private static List<(string Identity, string Who, string Maker, string Part)> Modules(string sysRoot)
    {
        lock (ModuleCache)
        {
            if (ModuleCache.TryGetValue(sysRoot, out var cached)) return cached;
            var driver = Path.Combine(sysRoot, "bus", "i2c", "drivers", "spd5118");
            var list = new List<(string, string, string, string)>();
            if (Directory.Exists(driver))
                foreach (var client in Directory.EnumerateFileSystemEntries(driver).Select(Path.GetFileName).Where(c => Ddr5Spd.ParseClient(c) is not null))
                {
                    if (DramSensorIdentity.Locate(client!, sysRoot) is not { } hub) continue;
                    var spd = Ddr5Spd.ReadHeader(Path.Combine(sysRoot, "bus", "i2c", "devices", client!));
                    list.Add((hub.Identity, hub.Who, spd is null ? "" : Ddr5Spd.Manufacturer(spd), spd is null ? "" : Ddr5Spd.PartNumber(spd)));
                }
            list.Sort((a, b) => string.CompareOrdinal(a.Item1, b.Item1));
            return ModuleCache[sysRoot] = list;
        }
    }

    public void Dispose() { }
}

/// <summary>DDR5 sideband value formats.</summary>
public static class Ddr5Decode
{
    /// <summary>TS5111/SPD5118 MR49–MR50: bits 12:2 two's complement in 0.25 °C steps.</summary>
    public static double Temperature(ushort raw) => ((short)(raw << 3) >> 5) * 0.25;

    /// <summary>R31 for SWA–SWC and the LDOs: 15 mV per count; 0 is undefined.</summary>
    public static double? Rail(byte raw) => raw == 0 ? null : raw * 0.015;

    /// <summary>R31 for VIN_BULK: 70 mV per count; 0 is undefined.</summary>
    public static double? Input(byte raw) => raw == 0 ? null : raw * 0.070;

    /// <summary>R0C/R0E/R0F: 125 mA or 125 mW per count; 0 reads "undefined" — below one step, shown as 0.</summary>
    public static double Meter(byte raw) => raw * 0.125;
}
