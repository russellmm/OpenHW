using System.Globalization;

namespace OpenHW.Hardware;

/// <summary>
/// A PMBus voltage regulator on a known board's SMBus: where it is (adapter name and address, found once with a
/// read-only scan — tools/smbus-probe), its IC_DEVICE_ID to confirm it before every read, and which PAGE carries
/// the board's rail. OpenHW never writes PAGE: the board firmware leaves it set, and the rail is reported only while
/// it still is.
/// </summary>
public sealed record BoardVr(string VendorPrefix, string Board, string Adapter, int Address, byte[] DeviceId, string Chip, int RailPage)
{
    public string Slug => Chip.Split(' ').Last().ToLowerInvariant();

    public bool Matches(string vendor, string board) =>
        vendor.StartsWith(VendorPrefix, StringComparison.OrdinalIgnoreCase) && board.Equals(Board, StringComparison.OrdinalIgnoreCase);
}

public static class BoardVrs
{
    public static readonly IReadOnlyList<BoardVr> Known =
    [
        // The memory controller's VDD_11_S3 / VDDIO_Mem regulator: 1.35 V on loop 2 (PAGE 1), 12 V in. Found
        // 2026-10-25 at port 0 address 0x20 (PMBus revision, IC_DEVICE_ID 01 95, VOUT 1.352 V = HWiNFO's).
        new("ASUSTeK", "Pro WS TRX50-SAGE WIFI A", "SMBus PIIX4 adapter port 0 at 0b00", 0x20, [0x01, 0x95], "Infineon XDPE19283B", 1),
    ];

    public static BoardVr? For(string vendor, string board) => Known.FirstOrDefault(v => v.Matches(vendor, board));

    public static BoardVr? ForThisBoard(string sysRoot = "/sys") => For(
        SysFs.ReadText(Path.Combine(sysRoot, "class", "dmi", "id", "board_vendor")),
        SysFs.ReadText(Path.Combine(sysRoot, "class", "dmi", "id", "board_name")));
}

/// <summary>Raw PMBus telemetry from one page of a board VR.</summary>
public sealed record PmbusSample(string Chip, byte Page, byte VoutMode, ushort Vin, ushort Iin, ushort Vout, ushort Iout,
    ushort Temp1, ushort Temp2, ushort Pout, ushort Pin, ushort Status);

/// <summary>Root side: this board's VR (if it has a known one), confirmed by IC_DEVICE_ID, read with PMBus reads only.</summary>
public sealed class BoardVrReader(ISmbusProvider buses, BoardVr? vr, string sysRoot = "/sys")
{
    public PmbusSample? Read()
    {
        if (vr is null) return null;
        var adapter = SmbusAdapters.List(sysRoot).FirstOrDefault(a => a.Name == vr.Adapter);
        if (adapter is null) return null;
        try
        {
            using var bus = buses.Open(adapter.Number);
            var a = vr.Address;
            if (!bus.ReadBlock(a, 0xAD).SequenceEqual(vr.DeviceId)) return null; // not the expected chip: read nothing else
            return new PmbusSample(vr.Slug, bus.ReadByte(a, 0x00), bus.ReadByte(a, 0x20),
                bus.ReadWord(a, 0x88), bus.ReadWord(a, 0x89), bus.ReadWord(a, 0x8B), bus.ReadWord(a, 0x8C),
                bus.ReadWord(a, 0x8D), bus.ReadWord(a, 0x8E), bus.ReadWord(a, 0x96), bus.ReadWord(a, 0x97), bus.ReadWord(a, 0x79));
        }
        catch (IOException) { return null; }
    }

    /// <summary>"xdpe19283b 01 18 d302 b380 015a cbb0 0026 0025 d2d0 d278 0002" (chip, PAGE, VOUT_MODE, then the words).</summary>
    public static string Format(PmbusSample s) =>
        $"{s.Chip} {s.Page:x2} {s.VoutMode:x2} {s.Vin:x4} {s.Iin:x4} {s.Vout:x4} {s.Iout:x4} {s.Temp1:x4} {s.Temp2:x4} {s.Pout:x4} {s.Pin:x4} {s.Status:x4}";

    public static PmbusSample? Parse(string reply)
    {
        var f = reply.Split(' ');
        if (f.Length != 12) return null;
        var n = new ushort[11];
        for (var i = 1; i < 12; i++)
            if (!ushort.TryParse(f[i], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out n[i - 1])) return null;
        return new PmbusSample(f[0], (byte)n[0], (byte)n[1], n[2], n[3], n[4], n[5], n[6], n[7], n[8], n[9], n[10]);
    }
}

/// <summary>PMBus number formats (PMBus 1.3 part II, §7).</summary>
public static class Pmbus
{
    /// <summary>LINEAR11: a signed 5-bit exponent over a signed 11-bit mantissa.</summary>
    public static double Linear11(ushort raw)
    {
        var exponent = (short)raw >> 11;
        var mantissa = (short)(raw << 5) >> 5;
        return mantissa * Math.Pow(2, exponent);
    }

    /// <summary>
    /// READ_VOUT in LINEAR16: an unsigned mantissa with VOUT_MODE's signed 5-bit exponent. Null for the VID and
    /// direct modes (VOUT_MODE bits 7:5 not 000), which need chip-specific tables.
    /// </summary>
    public static double? Linear16(ushort raw, byte voutMode)
    {
        if (voutMode >> 5 != 0) return null;
        var exponent = (sbyte)(voutMode << 3) >> 3;
        return raw * Math.Pow(2, exponent);
    }
}
