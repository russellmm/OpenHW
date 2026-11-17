using System.Runtime.InteropServices;
using System.Text;

namespace OpenHW.Hardware;

/// <summary>One IPMI request/response with the BMC. The response's first byte is the completion code.</summary>
public interface IIpmiTransport : IDisposable
{
    byte[] Send(byte netFn, byte command, byte lun, ReadOnlySpan<byte> data);
}

/// <summary>
/// The in-band system interface (KCS/BT/SSIF) through the kernel's ipmi_devintf: <c>/dev/ipmi0</c>, root-only. Every
/// request goes to the BMC itself (system interface address, BMC channel); nothing is bridged to other controllers.
/// </summary>
public sealed class LinuxIpmiTransport : IIpmiTransport
{
    public const string DefaultDevice = "/dev/ipmi0";

    // linux/ipmi.h on x86_64: struct ipmi_req is 40 bytes, struct ipmi_recv 48.
    private const uint SendCommand = 0x8028690d;    // _IOR('i', 13, struct ipmi_req)
    private const uint ReceiveTrunc = 0xc030690b;   // _IOWR('i', 11, struct ipmi_recv)
    private const int SystemInterfaceAddrType = 0x0c;
    private const short BmcChannel = 0x0f;
    private const int ResponseRecvType = 1;

    private readonly int _fd;
    private readonly int _timeoutMs;
    private long _msgId;
    private readonly object _gate = new();

    public LinuxIpmiTransport(string device = DefaultDevice, int timeoutMs = 3000)
    {
        _fd = Native.open(device, 2 /* O_RDWR */ | 0x80000 /* O_CLOEXEC */);
        if (_fd < 0) throw new IOException($"{device}: errno {Marshal.GetLastPInvokeError()}");
        _timeoutMs = timeoutMs;
    }

    public static bool Exists(string device = DefaultDevice) => File.Exists(device);

    public unsafe byte[] Send(byte netFn, byte command, byte lun, ReadOnlySpan<byte> data)
    {
        lock (_gate)
        {
            var addr = stackalloc byte[8]; // struct ipmi_system_interface_addr { int addr_type; short channel; u8 lun; }
            new Span<byte>(addr, 8).Clear();
            *(int*)addr = SystemInterfaceAddrType;
            *(short*)(addr + 4) = BmcChannel;
            addr[6] = lun;

            var payload = data.ToArray();
            var id = ++_msgId;
            fixed (byte* p = payload)
            {
                var req = stackalloc byte[40];
                new Span<byte>(req, 40).Clear();
                *(nint*)req = (nint)addr;
                *(uint*)(req + 8) = 8;
                *(long*)(req + 16) = id;
                req[24] = netFn;
                req[25] = command;
                *(ushort*)(req + 26) = (ushort)payload.Length;
                *(nint*)(req + 32) = (nint)p;
                if (Native.ioctl(_fd, SendCommand, req) < 0)
                    throw new IOException($"IPMI send 0x{netFn:x2}/0x{command:x2}: errno {Marshal.GetLastPInvokeError()}");
            }

            var deadline = Environment.TickCount64 + _timeoutMs;
            var buffer = new byte[256];
            // Allocated once: the loop can go round many times (events, stale replies, EAGAIN).
            var raddr = stackalloc byte[32];
            var recv = stackalloc byte[48];
            while (true)
            {
                var left = (int)(deadline - Environment.TickCount64);
                if (left <= 0) throw new IOException($"IPMI 0x{netFn:x2}/0x{command:x2}: no response");
                var pfd = new Native.PollFd { fd = _fd, events = 1 /* POLLIN */ };
                if (Native.poll(ref pfd, 1, left) <= 0) continue;

                fixed (byte* b = buffer)
                {
                    new Span<byte>(recv, 48).Clear();
                    *(nint*)(recv + 8) = (nint)raddr;
                    *(uint*)(recv + 16) = 32;
                    *(ushort*)(recv + 34) = (ushort)buffer.Length;
                    *(nint*)(recv + 40) = (nint)b;
                    if (Native.ioctl(_fd, ReceiveTrunc, recv) < 0)
                    {
                        var errno = Marshal.GetLastPInvokeError();
                        if (errno is 11 /* EAGAIN */ or 4 /* EINTR */) continue;
                        throw new IOException($"IPMI receive: errno {errno}");
                    }
                    if (*(int*)recv != ResponseRecvType || *(long*)(recv + 24) != id) continue; // an event or a stale reply
                    var length = *(ushort*)(recv + 34);
                    return buffer.AsSpan(0, Math.Min(length, buffer.Length)).ToArray();
                }
            }
        }
    }

    public void Dispose() => Native.close(_fd);

    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct PollFd { public int fd; public short events; public short revents; }

        [DllImport("libc", SetLastError = true)] public static extern int open(string path, int flags);
        [DllImport("libc", SetLastError = true)] public static extern int close(int fd);
        [DllImport("libc", SetLastError = true)] public static extern unsafe int ioctl(int fd, nuint request, void* arg);
        [DllImport("libc", SetLastError = true)] public static extern int poll(ref PollFd fds, nuint count, int timeout);
    }
}

/// <summary>Which threshold limits a sensor reports, in IPMI's own order (lower non-critical … upper non-recoverable).</summary>
public sealed record BmcThresholds(double? LowerNonCritical, double? LowerCritical, double? LowerNonRecoverable,
    double? UpperNonCritical, double? UpperCritical, double? UpperNonRecoverable);

/// <summary>
/// A threshold-based analog sensor from a Full Sensor Record (SDR type 01h): what it is, and how to turn its raw byte
/// into a value — y = L[(M·x + B·10^Bexp)·10^Rexp] (IPMI 2.0 §36.3).
/// </summary>
public sealed record BmcSensor(
    int RecordId, byte Owner, byte Lun, byte Number, string Name, byte EntityId, byte EntityInstance,
    byte SensorType, byte BaseUnit, byte ModifierUnit, int Format, int Linearization, int M, int B, int BExp, int RExp,
    byte ReadableThresholds, byte SettableThresholds = 0)
{
    public string Unit => BaseUnit switch
    {
        1 => "°C", 2 => "°F", 4 => "V", 5 => "A", 6 => "W", 7 => "J", 18 => "RPM", 19 => "Hz", 20 => "%",
        _ => "",
    };

    public HardwareKind Kind => BaseUnit switch
    {
        1 or 2 => HardwareKind.Temperature,
        4 => HardwareKind.Voltage,
        5 => HardwareKind.Current,
        6 => HardwareKind.Power,
        7 => HardwareKind.Energy,
        18 => HardwareKind.Tach,
        19 => HardwareKind.Frequency,
        20 => HardwareKind.Load,
        _ => HardwareKind.Data,
    };

    /// <summary>IPMI entity id, in words (where the sensor sits).</summary>
    public string Entity => EntityId switch
    {
        3 => "Processor", 7 => "System board", 8 => "Memory module", 10 => "Power supply", 11 => "Add-in card",
        29 => "Fan", 40 => "Battery", _ => $"Entity {EntityId}",
    };

    public double? Convert(byte raw)
    {
        double x = Format switch
        {
            0 => raw,
            1 => (raw & 0x80) != 0 ? -(~raw & 0x7f) : raw, // one's complement
            2 => (sbyte)raw,
            _ => double.NaN,
        };
        if (double.IsNaN(x)) return null;
        var y = (M * x + B * Math.Pow(10, BExp)) * Math.Pow(10, RExp);
        y = Linearization switch
        {
            0 => y,
            1 => Math.Log(y),
            2 => Math.Log10(y),
            3 => Math.Log2(y),
            4 => Math.Exp(y),
            5 => Math.Pow(10, y),
            6 => Math.Pow(2, y),
            7 => y == 0 ? double.NaN : 1 / y,
            8 => y * y,
            9 => y * y * y,
            10 => Math.Sqrt(y),
            11 => Math.Cbrt(y),
            _ => double.NaN, // 70h–7Fh: OEM non-linear, needs per-reading factors (not supported)
        };
        return double.IsFinite(y) ? Math.Round(y, 6) : null;
    }

    /// <summary>Parses one SDR record (header included); null for anything but a threshold analog Full Sensor Record.</summary>
    public static BmcSensor? Parse(ReadOnlySpan<byte> record)
    {
        if (record.Length < 48 || record[3] != 0x01) return null;
        if (record[13] != 0x01) return null;                 // event/reading type: threshold
        var format = record[20] >> 6;
        if (format == 3) return null;                         // no analog reading
        var linearization = record[23] & 0x7f;
        var m = TenBit(record[24], record[25]);
        var b = TenBit(record[26], record[27]);
        var rexp = FourBit(record[29] >> 4);
        var bexp = FourBit(record[29] & 0x0f);
        var idLength = record[47] & 0x1f;
        var idType = record[47] >> 6;
        var name = record.Length >= 48 + idLength && idType == 3
            ? Encoding.ASCII.GetString(record.Slice(48, idLength)).TrimEnd('\0', ' ')
            : $"Sensor {record[7]:X2}h";
        if (name.Length == 0) name = $"Sensor {record[7]:X2}h";
        return new BmcSensor(record[0] | record[1] << 8, record[5], (byte)(record[6] & 0x03), record[7], name,
            record[8], record[9], record[12], record[21], record[22], format, linearization, m, b, bexp, rexp,
            (byte)(record[18] & 0x3f), (byte)(record[19] & 0x3f));
    }

    private static int TenBit(byte ls, byte ms)
    {
        var v = ls | (ms & 0xc0) << 2;
        return (v & 0x200) != 0 ? v - 0x400 : v;
    }

    private static int FourBit(int v) => (v & 0x8) != 0 ? v - 0x10 : v;
}

/// <summary>
/// The read-only IPMI commands OpenHW sends: Get Device ID, the SDR repository (info, reserve, get), and per sensor
/// Get Sensor Reading and Get Sensor Thresholds. Nothing that sets, clears, arms or controls anything.
/// </summary>
public static class IpmiCommands
{
    private const byte NetFnApp = 0x06, NetFnStorage = 0x0a, NetFnSensor = 0x04;

    public sealed record DeviceId(byte Id, string Firmware, int ManufacturerId, int ProductId, byte[]? Aux = null);

    public static DeviceId? GetDeviceId(IIpmiTransport t)
    {
        var r = t.Send(NetFnApp, 0x01, 0, []);
        if (r.Length < 12 || r[0] != 0) return null;
        var fw = $"{r[3] & 0x7f}.{(r[4] >> 4) * 10 + (r[4] & 0x0f):00}";
        return new DeviceId(r[1], fw, r[7] | r[8] << 8 | r[9] << 16, r[10] | r[11] << 8, r.Length >= 16 ? r[12..16] : null);
    }

    /// <summary>Every record in the repository, walked by record id (partial reads with a reservation, 16 bytes at a time).</summary>
    public static List<byte[]> ReadSdr(IIpmiTransport t, int maxRecords = 1024)
    {
        var records = new List<byte[]>();
        var reservation = Reserve(t);
        ushort next = 0;
        while (next != 0xffff && records.Count < maxRecords)
        {
            var current = next;
            byte[]? header = null;
            for (var attempt = 0; attempt < 3 && header is null; attempt++)
            {
                var r = GetSdr(t, reservation, current, 0, 5);
                if (r.Length >= 8 && r[0] == 0) { header = r; break; }
                if (r.Length > 0 && r[0] == 0xc5) { reservation = Reserve(t); continue; } // reservation lost
                throw new IOException($"Get SDR {current:x4}: completion 0x{(r.Length > 0 ? r[0] : 0xff):x2}");
            }
            if (header is null) throw new IOException($"Get SDR {current:x4}: reservation keeps being cancelled");
            next = (ushort)(header[1] | header[2] << 8);
            var length = header[7];
            var record = new byte[5 + length];
            header.AsSpan(3, 5).CopyTo(record);
            var offset = 5;
            while (offset < record.Length)
            {
                var chunk = (byte)Math.Min(16, record.Length - offset);
                var r = GetSdr(t, reservation, current, (byte)offset, chunk);
                if (r.Length > 0 && r[0] == 0xc5) { reservation = Reserve(t); continue; }
                if (r.Length < 3 + chunk || r[0] != 0) throw new IOException($"Get SDR {current:x4}+{offset}: completion 0x{(r.Length > 0 ? r[0] : 0xff):x2}");
                r.AsSpan(3, chunk).CopyTo(record.AsSpan(offset));
                offset += chunk;
            }
            records.Add(record);
            if (next == current) break; // a BMC that never advances
        }
        return records;
    }

    private static ushort Reserve(IIpmiTransport t)
    {
        var r = t.Send(NetFnStorage, 0x22, 0, []);
        return r.Length >= 3 && r[0] == 0 ? (ushort)(r[1] | r[2] << 8) : (ushort)0;
    }

    private static byte[] GetSdr(IIpmiTransport t, ushort reservation, ushort record, byte offset, byte count) =>
        t.Send(NetFnStorage, 0x23, 0, [(byte)reservation, (byte)(reservation >> 8), (byte)record, (byte)(record >> 8), offset, count]);

    /// <summary>The raw reading, or null when the BMC says it's unavailable or not scanning (absent PSU, empty slot, disabled header).</summary>
    public static byte? GetReading(IIpmiTransport t, BmcSensor s)
    {
        var r = t.Send(NetFnSensor, 0x2d, s.Lun, [s.Number]);
        if (r.Length < 3 || r[0] != 0) return null;
        var flags = r[2];
        if ((flags & 0x20) != 0 || (flags & 0x40) == 0) return null;
        return r[1];
    }

    /// <summary>
    /// The raw byte that converts to exactly <paramref name="value"/> (any linearization: searched), or null when the
    /// sensor can't store that value (e.g. not a multiple of a fan's 120 RPM step).
    /// </summary>
    public static byte? Raw(BmcSensor s, double value)
    {
        for (var b = 0; b < 256; b++)
            if (s.Convert((byte)b) is { } v && Math.Abs(v - value) < 1e-6) return (byte)b;
        return null;
    }

    /// <summary>
    /// Set Sensor Thresholds (04h/26h) for the lower three only — non-critical, critical, non-recoverable — the one
    /// write OpenHW makes here. Bits the SDR doesn't mark settable are left out of the mask.
    /// </summary>
    public static void SetLowerThresholds(IIpmiTransport t, BmcSensor s, byte lnc, byte lcr, byte lnr)
    {
        var mask = (byte)(s.SettableThresholds & 0x07);
        if (mask == 0) throw new IOException($"{s.Name}: the BMC doesn't let its lower thresholds be set");
        var r = t.Send(NetFnSensor, 0x26, s.Lun, [s.Number, mask, lnc, lcr, lnr, 0, 0, 0]);
        if (r.Length < 1 || r[0] != 0)
            throw new IOException($"{s.Name}: the BMC refused the thresholds (completion 0x{(r.Length > 0 ? r[0] : 0xff):x2})");
    }

    public static BmcThresholds? GetThresholds(IIpmiTransport t, BmcSensor s)
    {
        if (s.ReadableThresholds == 0) return null;
        var r = t.Send(NetFnSensor, 0x27, s.Lun, [s.Number]);
        if (r.Length < 8 || r[0] != 0) return null;
        var mask = r[1];
        double? At(int bit) => (mask & 1 << bit) != 0 ? s.Convert(r[2 + bit]) : null;
        return new BmcThresholds(At(0), At(1), At(2), At(3), At(4), At(5));
    }
}
