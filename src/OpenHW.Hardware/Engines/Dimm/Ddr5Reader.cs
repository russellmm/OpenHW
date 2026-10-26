using System.Globalization;

namespace OpenHW.Hardware;

/// <summary>
/// One DDR5 DIMM's sideband devices, raw, as openhw-helper reads them: its two TS5111-class thermal sensors and its
/// JEDEC PMIC (status/meter registers and the six ADC rails). Null fields were absent or unreadable.
/// </summary>
public sealed record Ddr5Sample(int Bus, int Hub, ushort? Ts0, ushort? Ts1, Ddr5Pmic? Pmic)
{
    /// <summary>"0-50": the hub's I2C client name without the "00", as sysfs spells it ("0-0050").</summary>
    public string Client => $"{Bus}-{Hub:x2}";
}

/// <param name="Vendor">JEDEC id from R3C/R3D, equal to what the module's SPD declares.</param>
/// <param name="Registers">R08, R09, R0A, R0B (status), R0C, R0E, R0F (meters), R1A, R1B (meter mode), R33.</param>
/// <param name="Adc">R31 for SWA, SWB, SWC, VIN_BULK, VLDO_1.8V, VLDO_1.0V.</param>
public sealed record Ddr5Pmic(ushort Vendor, byte[] Registers, byte[] Adc);

/// <summary>
/// Root side of the DIMM sensors. DDR5 puts each module's sideband devices on the SPD bus at fixed offsets from its
/// hub (JESD300-5): hub 0x50+n, PMIC 0x48+n, TS0 0x10+n, TS1 0x30+n. Only hubs the kernel's spd5118 driver has bound
/// are used, so nothing is probed on buses that aren't DDR5 SPD buses.
/// Everything is read-only except the PMIC's ADC select (R30), and only on a PMIC whose vendor id matches the one the
/// module's own SPD declares: each rail is selected in turn, its R31 reading taken, then R30 is put back as it was.
/// </summary>
public sealed class Ddr5Reader(ISmbusProvider buses, string sysRoot = "/sys", TimeSpan? adcSettle = null, TimeSpan? cacheFor = null)
{
    // ADC_SELECT codes (R30[6:3]) in the order the reply lists them.
    private static readonly byte[] AdcChannels = [0b0000, 0b0010, 0b0011, 0b0101, 0b1000, 0b1001];
    private static readonly byte[] PmicRegisters = [0x08, 0x09, 0x0A, 0x0B, 0x0C, 0x0E, 0x0F, 0x1A, 0x1B, 0x33];
    private const byte AdcSelect = 0x30, AdcRead = 0x31, AdcEnable = 0x80;

    private readonly TimeSpan _settle = adcSettle ?? TimeSpan.FromMilliseconds(15); // R30[1:0] update period is at most 8 ms
    private readonly TimeSpan _cacheFor = cacheFor ?? TimeSpan.FromSeconds(1.5);
    private readonly object _gate = new();
    private (IReadOnlyList<Ddr5Sample> Samples, DateTime At)? _cache;

    /// <summary>Every bound hub's DIMM, read at most once per cache period however many clients ask.</summary>
    public IReadOnlyList<Ddr5Sample> Read()
    {
        lock (_gate)
        {
            if (_cache is { } hit && DateTime.UtcNow - hit.At < _cacheFor) return hit.Samples;
            var samples = new List<Ddr5Sample>();
            foreach (var group in Hubs().GroupBy(h => h.Bus))
            {
                using var bus = buses.Open(group.Key);
                foreach (var (busNumber, hub, spd) in group) samples.Add(ReadDimm(bus, busNumber, hub, spd));
            }
            _cache = (samples, DateTime.UtcNow);
            return samples;
        }
    }

    private Ddr5Sample ReadDimm(ISmbus bus, int busNumber, int hub, byte[] spd)
    {
        var n = hub & 7;
        return new Ddr5Sample(busNumber, hub, Thermal(bus, 0x10 | n), Thermal(bus, 0x30 | n), Pmic(bus, 0x48 | n, Ddr5Spd.PmicVendor(spd)));
    }

    // TS5110/TS5111 (and the hub itself) identify as MR0 = 0x51; the temperature is MR49/MR50.
    private static ushort? Thermal(ISmbus bus, int address)
    {
        try { return bus.ReadByte(address, 0x00) == 0x51 ? bus.ReadWord(address, 0x31) : null; }
        catch (IOException) { return null; }
    }

    private Ddr5Pmic? Pmic(ISmbus bus, int address, ushort? declared)
    {
        if (declared is null) return null; // the SPD lists no PMIC: nothing is written
        try
        {
            var vendor = (ushort)(bus.ReadByte(address, 0x3C) << 8 | bus.ReadByte(address, 0x3D));
            if (vendor != declared) return null; // not the device the module describes: leave it alone
            var registers = PmicRegisters.Select(r => bus.ReadByte(address, r)).ToArray();
            var original = bus.ReadByte(address, AdcSelect);
            var adc = new byte[AdcChannels.Length];
            try
            {
                for (var i = 0; i < AdcChannels.Length; i++)
                {
                    bus.WriteByte(address, AdcSelect, (byte)(AdcEnable | AdcChannels[i] << 3 | original & 0b11));
                    if (_settle > TimeSpan.Zero) Thread.Sleep(_settle);
                    adc[i] = bus.ReadByte(address, AdcRead);
                }
            }
            finally { bus.WriteByte(address, AdcSelect, original); }
            return new Ddr5Pmic(vendor, registers, adc);
        }
        catch (IOException) { return null; }
    }

    private IEnumerable<(int Bus, int Hub, byte[] Spd)> Hubs()
    {
        var driver = Path.Combine(sysRoot, "bus", "i2c", "drivers", "spd5118");
        if (!Directory.Exists(driver)) yield break;
        foreach (var entry in Directory.EnumerateFileSystemEntries(driver).Select(Path.GetFileName).Order(StringComparer.Ordinal))
        {
            if (Ddr5Spd.ParseClient(entry) is not { } client) continue;
            var spd = Ddr5Spd.ReadHeader(Path.Combine(sysRoot, "bus", "i2c", "devices", entry!));
            if (spd is not null) yield return (client.Bus, client.Hub, spd);
        }
    }

    /// <summary>The helper's reply: "0-50 02f0 02e0 8a8c 00,00,…,00 5a,5a,79,ac,78,43;…" ("-" for an absent part).</summary>
    public static string Format(IReadOnlyList<Ddr5Sample> samples) => string.Join(';', samples.Select(s =>
        $"{s.Client} {Hex(s.Ts0)} {Hex(s.Ts1)} " + (s.Pmic is { } p
            ? $"{p.Vendor:x4} {string.Join(',', p.Registers.Select(b => b.ToString("x2")))} {string.Join(',', p.Adc.Select(b => b.ToString("x2")))}"
            : "- - -")));

    private static string Hex(ushort? value) => value is { } v ? v.ToString("x4") : "-";

    public static IReadOnlyList<Ddr5Sample> Parse(string reply)
    {
        var samples = new List<Ddr5Sample>();
        foreach (var part in reply.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var f = part.Split(' ');
            if (f.Length != 6 || Ddr5Spd.ParseClient(f[0].Replace("-", "-00")) is not { } client) continue;
            Ddr5Pmic? pmic = null;
            if (f[3] != "-" && ushort.TryParse(f[3], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var vendor)
                && Bytes(f[4]) is { Length: 10 } registers && Bytes(f[5]) is { Length: 6 } adc)
                pmic = new Ddr5Pmic(vendor, registers, adc);
            samples.Add(new Ddr5Sample(client.Bus, client.Hub, Word(f[1]), Word(f[2]), pmic));
        }
        return samples;
    }

    private static ushort? Word(string text) =>
        ushort.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v) ? v : null;

    private static byte[]? Bytes(string text)
    {
        var parts = text.Split(',');
        var bytes = new byte[parts.Length];
        for (var i = 0; i < parts.Length; i++)
            if (!byte.TryParse(parts[i], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out bytes[i])) return null;
        return bytes;
    }
}

/// <summary>What a DDR5 module's SPD says about itself (JESD400-5), from the spd5118 driver's world-readable eeprom.</summary>
public static class Ddr5Spd
{
    public const int HeaderLength = 554; // through the module part number (521–550) and DRAM maker (552–553)

    /// <summary>"0-0050" → bus 0, hub 0x50; only SPD hub addresses 0x50–0x57.</summary>
    public static (int Bus, int Hub)? ParseClient(string? name)
    {
        var m = System.Text.RegularExpressions.Regex.Match(name ?? "", @"^(\d{1,4})-005([0-7])$");
        return m.Success ? (int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), 0x50 | m.Groups[2].Value[0] - '0') : null;
    }

    public static byte[]? ReadHeader(string clientDir)
    {
        try
        {
            using var file = File.OpenRead(Path.Combine(clientDir, "eeprom"));
            var buffer = new byte[HeaderLength];
            var read = 0;
            while (read < buffer.Length && file.Read(buffer, read, buffer.Length - read) is var n and > 0) read += n;
            return read == buffer.Length ? buffer : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>PMIC 0's JEDEC manufacturer id (bytes 198–199), when byte 200 marks it installed.</summary>
    public static ushort? PmicVendor(byte[] spd)
    {
        var vendor = (ushort)(spd[198] << 8 | spd[199]);
        return vendor != 0 && (spd[200] & 0x80) != 0 ? vendor : null;
    }

    public static string PartNumber(byte[] spd) =>
        System.Text.Encoding.ASCII.GetString(spd, 521, 30).Trim(' ', '\0');

    /// <summary>The module maker (bytes 512–513), for the common DDR5 module brands; "" otherwise.</summary>
    public static string Manufacturer(byte[] spd) => (spd[512] << 8 | spd[513]) switch
    {
        0x0198 => "Kingston",
        0x029E => "Corsair",
        0x04CD => "G.Skill",
        0x04EF => "TeamGroup",
        0x04CB => "ADATA",
        0x80CE => "Samsung",
        0x80AD => "SK hynix",
        0x802C => "Micron",
        0x859B => "Crucial",
        _ => "",
    };
}
