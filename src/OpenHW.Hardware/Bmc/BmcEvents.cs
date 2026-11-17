using System.Globalization;
using System.Text;

namespace OpenHW.Hardware;

/// <summary>
/// A discrete (state) sensor from the SDR: a Full (01h) or Compact (02h) Sensor Record whose event/reading type isn't
/// "threshold" — CPU_ECC, PSU presence, watchdog, BIOS/hardware change and the like. It reads as a set of asserted
/// states (offsets 0–14), not a number.
/// </summary>
public sealed record BmcDiscreteSensor(byte Lun, byte Number, string Name, byte EntityId, byte SensorType, byte EventType)
{
    /// <summary>Parses one SDR record; null for threshold sensors and anything but a Full/Compact Sensor Record.</summary>
    public static BmcDiscreteSensor? Parse(ReadOnlySpan<byte> record)
    {
        if (record.Length < 32) return null;
        var type = record[3];
        if (type is not (0x01 or 0x02)) return null;
        var eventType = record[13];
        if (eventType == 0x01) return null; // threshold: BmcSensor's business
        var idAt = type == 0x01 ? 47 : 31;
        if (record.Length <= idAt) return null;
        var idLength = record[idAt] & 0x1f;
        var name = record.Length >= idAt + 1 + idLength && record[idAt] >> 6 == 3
            ? Encoding.ASCII.GetString(record.Slice(idAt + 1, idLength)).TrimEnd('\0', ' ')
            : "";
        if (name.Length == 0) name = $"Sensor {record[7]:X2}h";
        return new BmcDiscreteSensor((byte)(record[6] & 0x03), record[7], name, record[8], record[12], eventType);
    }
}

/// <summary>A discrete sensor's state for the window: its asserted states in words (empty = none asserted).</summary>
public sealed record BmcStateWire(string Name, byte Number, string Entity, string SensorType, List<string> States, int Bits, bool Readable);

/// <summary>One System Event Log entry, decoded.</summary>
public sealed record BmcSelEntry(int Id, DateTime? TimeUtc, string Time, string Sensor, string SensorType, string Event,
    bool Deasserted, string Raw);

/// <summary>The helper's <c>bmcsel</c> reply: SEL info and its newest entries (oldest first).</summary>
public sealed record BmcSelReply(int Entries, int FreeBytes, DateTime? LastAddUtc, DateTime? LastEraseUtc, List<BmcSelEntry> Newest);

/// <summary>
/// IPMI 2.0 event and state text (tables 42-2 generic, 42-3 sensor-specific) and SEL decoding. Read-only: Get SEL Info
/// and Get SEL Entry; nothing that clears, adds or arms anything.
/// </summary>
public static class IpmiEvents
{
    private const byte NetFnSensor = 0x04, NetFnStorage = 0x0a;

    /// <summary>A discrete sensor's asserted-state bits (offsets 0–14), or null when the BMC can't read it now.</summary>
    public static int? GetStates(IIpmiTransport t, BmcDiscreteSensor s)
    {
        var r = t.Send(NetFnSensor, 0x2d, s.Lun, [s.Number]);
        if (r.Length < 4 || r[0] != 0) return null;
        if ((r[2] & 0x20) != 0 || (r[2] & 0x40) == 0) return null; // reading unavailable / scanning disabled
        return (r[3] | (r.Length > 4 ? (r[4] & 0x7f) << 8 : 0));
    }

    public sealed record SelInfo(int Version, int Entries, int FreeBytes, uint LastAdd, uint LastErase);

    public static SelInfo? GetSelInfo(IIpmiTransport t)
    {
        var r = t.Send(NetFnStorage, 0x40, 0, []);
        if (r.Length < 14 || r[0] != 0) return null;
        return new SelInfo(r[1], r[2] | r[3] << 8, r[4] | r[5] << 8, BitConverter.ToUInt32(r, 6), BitConverter.ToUInt32(r, 10));
    }

    /// <summary>Every SEL record (16 bytes each), oldest first, walked by record id; at most <paramref name="max"/>.</summary>
    public static List<byte[]> ReadSel(IIpmiTransport t, int max = 4096)
    {
        var records = new List<byte[]>();
        ushort next = 0;
        while (next != 0xffff && records.Count < max)
        {
            var current = next;
            // Reading whole records needs no reservation (0000).
            var r = t.Send(NetFnStorage, 0x43, 0, [0, 0, (byte)current, (byte)(current >> 8), 0, 0xff]);
            if (r.Length > 0 && r[0] == 0xcb && records.Count == 0) break; // empty SEL: "requested record not present"
            if (r.Length < 19 || r[0] != 0) throw new IOException($"Get SEL Entry {current:x4}: completion 0x{(r.Length > 0 ? r[0] : 0xff):x2}");
            next = (ushort)(r[1] | r[2] << 8);
            records.Add(r[3..19]);
            if (next == current) break;
        }
        return records;
    }

    /// <summary>A SEL timestamp in words: seconds since 1970 (UTC), or since the BMC started when the clock wasn't set yet.</summary>
    public static (DateTime? Utc, string Text) Timestamp(uint seconds) => seconds switch
    {
        0xffffffff => (null, "unspecified"),
        <= 0x20000000 => (null, $"+{seconds} s after BMC start"),
        _ => (DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime,
              DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture)),
    };

    public static DateTime? Utc(uint seconds) => Timestamp(seconds).Utc;

    /// <summary>Decodes one 16-byte SEL record; <paramref name="sensorName"/> names a sensor number from the SDR.</summary>
    public static BmcSelEntry Decode(ReadOnlySpan<byte> rec, Func<byte, string?> sensorName)
    {
        var id = rec[0] | rec[1] << 8;
        var type = rec[2];
        var raw = Convert.ToHexString(rec);
        if (type == 0x02)
        {
            var (utc, text) = Timestamp(BitConverter.ToUInt32(rec[3..7]));
            var sensorType = rec[10];
            var number = rec[11];
            var deasserted = (rec[12] & 0x80) != 0;
            var eventType = (byte)(rec[12] & 0x7f);
            var offset = rec[13] & 0x0f;
            var what = StateText(sensorType, eventType, offset) ?? $"state {offset}";
            if ((rec[13] >> 6) == 0b01 && eventType == 0x01 && rec[14] != 0xff) what += $" (reading 0x{rec[14]:x2})";
            return new BmcSelEntry(id, utc, text, sensorName(number) ?? $"Sensor {number:X2}h", SensorTypeName(sensorType),
                what + (deasserted ? " — deasserted" : ""), deasserted, raw);
        }
        if (type is >= 0xc0 and <= 0xdf)
        {
            var (utc, text) = Timestamp(BitConverter.ToUInt32(rec[3..7]));
            return new BmcSelEntry(id, utc, text, "", "OEM", $"OEM record {type:X2}h", false, raw);
        }
        return new BmcSelEntry(id, null, "", "", "OEM", $"OEM record {type:X2}h (no timestamp)", false, raw);
    }

    /// <summary>The asserted states of a discrete sensor's bits, in words.</summary>
    public static List<string> States(byte sensorType, byte eventType, int bits)
    {
        var list = new List<string>();
        for (var offset = 0; offset < 15; offset++)
            if ((bits & 1 << offset) != 0) list.Add(StateText(sensorType, eventType, offset) ?? $"state {offset}");
        return list;
    }

    public static string? StateText(byte sensorType, byte eventType, int offset)
    {
        string[]? table = eventType switch
        {
            0x01 => ["lower non-critical going low", "lower non-critical going high", "lower critical going low", "lower critical going high",
                     "lower non-recoverable going low", "lower non-recoverable going high", "upper non-critical going low", "upper non-critical going high",
                     "upper critical going low", "upper critical going high", "upper non-recoverable going low", "upper non-recoverable going high"],
            0x02 => ["idle", "active", "busy"],
            0x03 => ["deasserted", "asserted"],
            0x04 => ["predictive failure deasserted", "predictive failure asserted"],
            0x05 => ["limit not exceeded", "limit exceeded"],
            0x06 => ["performance met", "performance lags"],
            0x07 => ["OK", "non-critical", "critical", "non-recoverable", "non-critical (from more severe)", "critical (from non-recoverable)",
                     "non-recoverable", "monitor", "informational"],
            0x08 => ["device absent", "device present"],
            0x09 => ["device disabled", "device enabled"],
            0x0a => ["running", "in test", "power off", "on line", "off line", "off duty", "degraded", "power save", "install error"],
            0x0b => ["fully redundant", "redundancy lost", "redundancy degraded", "non-redundant: sufficient resources",
                     "non-redundant: sufficient resources", "non-redundant: insufficient resources", "redundancy degraded", "redundancy degraded"],
            0x0c => ["D0", "D1", "D2", "D3"],
            0x6f => SensorSpecific(sensorType),
            _ => null,
        };
        return table is not null && offset < table.Length && table[offset].Length > 0 ? table[offset] : null;
    }

    private static string[]? SensorSpecific(byte sensorType) => sensorType switch
    {
        0x05 => ["chassis intrusion", "drive bay intrusion", "I/O card area intrusion", "processor area intrusion", "LAN leash lost",
                 "unauthorized dock", "fan area intrusion"],
        0x07 => ["IERR", "thermal trip", "FRB1/BIST failure", "FRB2/hang in POST", "FRB3/processor startup failure", "configuration error",
                 "uncorrectable CPU-complex error", "presence detected", "processor disabled", "terminator presence", "throttled",
                 "machine check exception", "correctable machine check error"],
        0x08 => ["presence detected", "failure detected", "predictive failure", "AC lost", "AC lost or out of range",
                 "AC out of range but present", "configuration error", "inactive"],
        0x09 => ["power off", "power cycle", "240 VA power down", "interlock power down", "AC lost", "soft power control failure",
                 "power unit failure", "predictive failure"],
        0x0c => ["correctable ECC", "uncorrectable ECC", "parity", "memory scrub failed", "memory device disabled",
                 "correctable ECC logging limit reached", "presence detected", "configuration error", "spare", "throttled",
                 "critical overtemperature"],
        0x0f => ["system firmware error", "system firmware hang", "system firmware progress"],
        0x10 => ["correctable memory error logging disabled", "event type logging disabled", "log area reset/cleared",
                 "all event logging disabled", "SEL full", "SEL almost full", "correctable machine check error logging disabled"],
        0x12 => ["system reconfigured", "OEM system boot event", "undetermined system hardware failure", "entry added to auxiliary log",
                 "PEF action", "timestamp clock sync"],
        0x13 => ["front panel NMI", "bus timeout", "I/O channel check NMI", "software NMI", "PCI PERR", "PCI SERR", "EISA fail-safe timeout",
                 "bus correctable error", "bus uncorrectable error", "fatal NMI", "bus fatal error", "bus degraded"],
        0x1d => ["boot: power up", "boot: hard reset", "boot: warm reset", "boot: user-requested PXE", "boot: automatic diagnostic",
                 "boot: OS hard reset", "boot: OS warm reset", "system restart"],
        0x1f => ["A: boot completed", "C: boot completed", "PXE boot completed", "diagnostic boot completed", "CD-ROM boot completed",
                 "ROM boot completed", "boot completed"],
        0x20 => ["critical stop during OS load", "run-time critical stop", "OS graceful stop", "OS graceful shutdown",
                 "soft shutdown by PEF", "agent not responding"],
        0x23 => ["timer expired", "hard reset", "power down", "power cycle", "", "", "", "", "timer interrupt"],
        0x2b => ["hardware change detected", "firmware or software change detected", "hardware incompatibility detected",
                 "firmware or software incompatibility detected", "invalid or unsupported hardware version",
                 "invalid or unsupported firmware or software version", "hardware change successful", "firmware or software change successful"],
        _ => null,
    };

    public static string SensorTypeName(byte type) => type switch
    {
        0x01 => "Temperature", 0x02 => "Voltage", 0x03 => "Current", 0x04 => "Fan", 0x05 => "Physical security",
        0x06 => "Platform security", 0x07 => "Processor", 0x08 => "Power supply", 0x09 => "Power unit", 0x0a => "Cooling device",
        0x0b => "Other units", 0x0c => "Memory", 0x0d => "Drive slot", 0x0e => "POST memory resize", 0x0f => "System firmware progress",
        0x10 => "Event logging disabled", 0x11 => "Watchdog 1", 0x12 => "System event", 0x13 => "Critical interrupt",
        0x14 => "Button/switch", 0x15 => "Module/board", 0x16 => "Microcontroller", 0x17 => "Add-in card", 0x18 => "Chassis",
        0x19 => "Chip set", 0x1a => "Other FRU", 0x1b => "Cable/interconnect", 0x1c => "Terminator", 0x1d => "System boot",
        0x1e => "Boot error", 0x1f => "OS boot", 0x20 => "OS stop/shutdown", 0x21 => "Slot/connector", 0x22 => "ACPI power state",
        0x23 => "Watchdog 2", 0x24 => "Platform alert", 0x25 => "Entity presence", 0x27 => "LAN", 0x28 => "Management subsystem health",
        0x29 => "Battery", 0x2a => "Session audit", 0x2b => "Version change", 0x2c => "FRU state",
        >= 0xc0 => $"OEM {type:X2}h",
        _ => $"Type {type:X2}h",
    };
}
