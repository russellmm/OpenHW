using System.Text;

namespace OpenHW.Hardware;

/// <summary>A BMC fan zone's curve: five temperature (°C) → duty (%) points, as the firmware stores them.</summary>
public sealed record FanCurvePoint(int TemperatureC, int DutyPercent);

/// <summary>
/// The ASUS WRX90E-SAGE SE BMC's fan zones, through ASUS's OEM IPMI commands (ASUS E26328 IPMI manual, July 2025,
/// appendix A-5–A-7). Ported from OpenFan's AsusBmcController (owner, 2026-11-16: OpenFan is ours to reuse), on
/// OpenHW's own IPMI transport. Firmware state per zone is 11 bytes: mode (04 = manual curve), then five
/// temperature/duty pairs; the last temperature is 100 °C (the BMC web console edits points A–D, point E stays at 100 °C).
/// <list type="bullet">
///   <item><c>30 13 01 00</c> zone count (8), <c>30 13 03 00</c> zone order (ff 00…07),
///   <c>30 13 00 N</c> 54-byte table (32-byte name, …, active and default curves) — identity checks only.</item>
///   <item><c>30 0f N</c> reads the active state; <c>30 0e 04 N</c> + 10 bytes writes the zone's manual curve.</item>
/// </list>
/// Writes are refused unless the board, the BMC's identity and firmware, and every zone's table match what was
/// validated on the owner's machine; global fan modes and temperature sources are never touched. The firmware
/// rejects any 0 % point (completion 0xD9), so a duty is 1–100 %.
/// </summary>
public sealed class AsusBmcFans
{
    public const string Board = "Pro WS WRX90E-SAGE SE";
    public const string Vendor = "ASUSTeK COMPUTER INC.";
    private const byte NetFnOem = 0x30;

    /// <summary>Zone labels as the firmware's table names them, and the BMC fan sensor that measures each.</summary>
    public static readonly string[] Labels = ["Zone1(CPU_FAN & CPU_OPT)", "Zone2(CHA_FAN1)", "Zone3(CHA_FAN2)",
        "Zone4(CHA_FAN3)", "Zone5(CHA_FAN4)", "Zone6(CHA_FAN5)", "Zone7(W_PUMP+)"];
    public static readonly string[] Sensors = ["CPU_FAN", "CHA_FAN1", "CHA_FAN2", "CHA_FAN3", "CHA_FAN4", "CHA_FAN5", "W_PUMP+"];

    public static int ZoneCount => Labels.Length;

    private readonly IIpmiTransport _ipmi;

    private AsusBmcFans(IIpmiTransport ipmi) => _ipmi = ipmi;

    /// <summary>
    /// The driver if this is the validated board and BMC; otherwise null with the reason. Read-only: identity, zone
    /// count/order, each zone's table and active state.
    /// </summary>
    public static AsusBmcFans? Open(IIpmiTransport ipmi, string boardVendor, string boardName, out string? reason)
    {
        reason = Validate(ipmi, boardVendor, boardName);
        return reason is null ? new AsusBmcFans(ipmi) : null;
    }

    private static string? Validate(IIpmiTransport ipmi, string vendor, string board)
    {
        if (vendor != Vendor || board != Board) return $"BMC fan control is validated only on the ASUS {Board}.";
        var id = IpmiCommands.GetDeviceId(ipmi);
        if (id is null) return "The BMC didn't answer Get Device ID.";
        if (id.ManufacturerId != 2623 || id.ProductId != 0x1193 || id.Firmware != "2.01" ||
            id.Aux is not [0x22, 0, 0, 0])
            return $"Unvalidated BMC identity or firmware ({id.ManufacturerId}/0x{id.ProductId:x4}, firmware {id.Firmware}); fan writes disabled.";
        if (!Oem(ipmi, 0x13, [0x01, 0x00]).AsSpan().SequenceEqual(new byte[] { 8 }) ||
            !Oem(ipmi, 0x13, [0x03, 0x00]).AsSpan().SequenceEqual(new byte[] { 0xff, 0, 1, 2, 3, 4, 5, 6, 7 }))
            return "Unrecognized BMC fan zone count or order.";
        for (var i = 0; i < ZoneCount; i++)
        {
            var table = Oem(ipmi, 0x13, [0x00, (byte)i]);
            var active = Oem(ipmi, 0x0f, [(byte)i]);
            // The table holds a copy of the active curve: its temperatures must match. Duties aren't compared — a
            // takeover changes only those, and after a crash the journal (restored next) must still be reachable.
            if (table.Length != 54 || Encoding.ASCII.GetString(table, 0, 32).TrimEnd('\0') != Labels[i] ||
                !ValidState(active) || Enumerable.Range(0, 5).Any(p => active[1 + p * 2] != table[34 + p * 2]))
                return $"Unrecognized BMC fan table for zone {i}.";
        }
        return null;
    }

    /// <summary>An OEM command's data bytes (completion code removed); empty when it failed.</summary>
    private static byte[] Oem(IIpmiTransport ipmi, byte command, byte[] data)
    {
        var r = ipmi.Send(NetFnOem, command, 0, data);
        return r.Length >= 1 && r[0] == 0 ? r[1..] : [];
    }

    /// <summary>A manual-mode state whose temperatures rise, duties are 0–100 and the last point is at 100 °C.</summary>
    public static bool ValidState(byte[] state) => state.Length == 11 && state[0] == 4 &&
        Enumerable.Range(0, 5).All(i => state[1 + i * 2] <= 100 && state[2 + i * 2] <= 100) &&
        Enumerable.Range(1, 4).All(i => state[1 + i * 2] > state[1 + (i - 1) * 2]) &&
        state[9] == 100;

    public static IReadOnlyList<FanCurvePoint> Curve(byte[] state) =>
        Enumerable.Range(0, 5).Select(i => new FanCurvePoint(state[1 + i * 2], state[2 + i * 2])).ToList();

    /// <summary>The same temperatures with every duty set to <paramref name="percent"/>: a fixed duty, as a curve.</summary>
    public static byte[] Flat(byte[] state, int percent)
    {
        var flat = (byte[])state.Clone();
        for (var i = 0; i < 5; i++) flat[2 + i * 2] = (byte)percent;
        return flat;
    }

    public byte[] Read(int zone)
    {
        var state = Oem(_ipmi, 0x0f, [(byte)zone]);
        if (!ValidState(state)) throw new IOException($"Can't read a valid curve for {Sensors[zone]}.");
        return state;
    }

    /// <summary>Writes the zone's curve and reads it back; throws if the firmware refuses or the readback differs.</summary>
    public void Write(int zone, byte[] state)
    {
        if (!ValidState(state)) throw new InvalidDataException("Invalid fan curve.");
        var r = _ipmi.Send(NetFnOem, 0x0e, 0, [0x04, (byte)zone, .. state.AsSpan(1)]);
        if (r.Length >= 1 && r[0] == 0xd9)
            throw new IOException($"The BMC firmware refused that curve for {Sensors[zone]} (0xD9 — it rejects 0 % points).");
        if (r.Length < 1 || r[0] != 0)
            throw new IOException($"The BMC refused the curve for {Sensors[zone]} (completion 0x{(r.Length > 0 ? r[0] : 0xff):x2}).");
        if (!Read(zone).AsSpan().SequenceEqual(state))
            throw new IOException($"The curve for {Sensors[zone]} didn't read back as written.");
    }
}
