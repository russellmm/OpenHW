using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace OpenHW.Hardware;

/// <summary>One sensor frame from a WireView Pro II (or its Noctua Edition): the 12V-2x6 cable's six wires and more.</summary>
/// <param name="Temperatures">°C: connector in, connector out, external probe 1, external probe 2 (null = no probe).</param>
public sealed record WireViewSample(double?[] Temperatures, double SupplyVolts, int FanPercent,
    double[] PinVolts, double[] PinAmps, double[] PinWatts, double TotalWatts, double TotalAmps, double AverageVolts,
    int? PsuCapabilityWatts, int FaultStatus, int FaultLog)
{
    /// <summary>The frame's size on the wire (firmware 5: 4-byte packed struct).</summary>
    public const int Size = 100;

    /// <summary>Fault bits, as the firmware numbers them (status = now, log = since the faults were last cleared).</summary>
    public static readonly string[] FaultNames =
    [
        "chip over-temperature", "connector over-temperature", "over-current", "wire over-current", "over-power", "current imbalance",
    ];

    /// <summary>
    /// Decodes a frame; null when it's out of step. The serial link has no framing or checksum, so a frame read half a
    /// frame late is caught by what a real one always has: zero padding after the fan duty and before the fault masks,
    /// and a fan duty of at most 100 %.
    /// </summary>
    public static WireViewSample? Parse(ReadOnlySpan<byte> f)
    {
        if (f.Length != Size || f[10] > 100 || f[11] != 0 || f[95] != 0) return null;
        var temps = new double?[4];
        for (var i = 0; i < 4; i++)
        {
            var t = BinaryPrimitives.ReadInt16LittleEndian(f[(2 * i)..]) / 10.0;
            temps[i] = t <= -99 ? null : t; // −100.0 °C = no probe
        }
        var volts = new double[6];
        var amps = new double[6];
        var watts = new double[6];
        for (var i = 0; i < 6; i++)
        {
            var o = 12 + 12 * i;
            volts[i] = BinaryPrimitives.ReadInt16LittleEndian(f[o..]) / 1000.0;
            amps[i] = BinaryPrimitives.ReadUInt32LittleEndian(f[(o + 4)..]) / 1000.0;
            watts[i] = BinaryPrimitives.ReadUInt32LittleEndian(f[(o + 8)..]) / 1000.0;
        }
        return new WireViewSample(temps, BinaryPrimitives.ReadUInt16LittleEndian(f[8..]) / 1000.0, f[10], volts, amps, watts,
            BinaryPrimitives.ReadUInt32LittleEndian(f[84..]) / 1000.0, BinaryPrimitives.ReadUInt32LittleEndian(f[88..]) / 1000.0,
            BinaryPrimitives.ReadUInt16LittleEndian(f[92..]) / 1000.0,
            f[94] switch { 0 => 600, 1 => 450, 2 => 300, 3 => 150, _ => null },
            BinaryPrimitives.ReadUInt16LittleEndian(f[96..]), BinaryPrimitives.ReadUInt16LittleEndian(f[98..]));
    }

    public static IEnumerable<string> Faults(int mask) =>
        FaultNames.Where((_, i) => (mask & 1 << i) != 0).Concat(mask >> FaultNames.Length != 0 ? [$"fault bits 0x{mask:x4}"] : []);
}

/// <summary>One WireView as the helper reports it: identity, and its latest sample (null while it doesn't answer).</summary>
public sealed record WireViewDevice(string Serial, string Product, string Firmware, string Port, WireViewSample? Sample, string? Problem);

/// <summary>A command/response link to a WireView: one command byte out, a fixed number of bytes back.</summary>
public interface IWireViewLink : IDisposable
{
    byte[] Command(byte command, int replySize);
}

/// <summary>
/// The WireView's USB serial port (STM32 CDC-ACM, /dev/ttyACM*), raw 115200 8N1 through termios. Root-only on most
/// systems (group dialout) — the helper opens it.
/// </summary>
public sealed class LinuxSerialLink : IWireViewLink
{
    private readonly int _fd;

    public LinuxSerialLink(string path)
    {
        _fd = Native.open(path, 2 /* O_RDWR */ | 0x100 /* O_NOCTTY */ | 0x800 /* O_NONBLOCK */ | 0x80000 /* O_CLOEXEC */);
        if (_fd < 0) throw new IOException($"{path}: errno {Marshal.GetLastPInvokeError()}");
        var tio = new byte[256]; // struct termios is 60 bytes on glibc x86_64; libc fills and edits it, so its layout doesn't matter here
        if (Native.tcgetattr(_fd, tio) != 0) { Native.close(_fd); throw new IOException($"{path}: not a serial port"); }
        Native.cfmakeraw(tio);
        Native.cfsetspeed(tio, 0x1002 /* B115200 */);
        Native.tcsetattr(_fd, 0 /* TCSANOW */, tio);
    }

    public byte[] Command(byte command, int replySize)
    {
        Native.tcflush(_fd, 0 /* TCIFLUSH */);
        if (Native.write(_fd, [command], 1) != 1) throw new IOException($"WireView write: errno {Marshal.GetLastPInvokeError()}");
        var buf = new byte[replySize];
        var got = 0;
        var deadline = Environment.TickCount64 + 1000;
        while (got < replySize)
        {
            var left = (int)(deadline - Environment.TickCount64);
            if (left <= 0) break;
            var pfd = new Native.PollFd { fd = _fd, events = 1 };
            if (Native.poll(ref pfd, 1, left) <= 0) continue;
            var n = Native.read(_fd, ref buf[got], (nuint)(replySize - got));
            if (n > 0) got += (int)n;
            else if (n < 0 && Marshal.GetLastPInvokeError() is not (11 or 4)) throw new IOException($"WireView read: errno {Marshal.GetLastPInvokeError()}");
        }
        return got == replySize ? buf : buf[..got];
    }

    public void Dispose() => Native.close(_fd);

    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)] public struct PollFd { public int fd; public short events; public short revents; }
        [DllImport("libc", SetLastError = true)] public static extern int open(string path, int flags);
        [DllImport("libc", SetLastError = true)] public static extern int close(int fd);
        [DllImport("libc", SetLastError = true)] public static extern int tcgetattr(int fd, byte[] termios);
        [DllImport("libc", SetLastError = true)] public static extern int tcsetattr(int fd, int action, byte[] termios);
        [DllImport("libc")] public static extern void cfmakeraw(byte[] termios);
        [DllImport("libc")] public static extern int cfsetspeed(byte[] termios, uint speed);
        [DllImport("libc", SetLastError = true)] public static extern int tcflush(int fd, int queue);
        [DllImport("libc", SetLastError = true)] public static extern nint write(int fd, byte[] buf, nuint count);
        [DllImport("libc", SetLastError = true)] public static extern nint read(int fd, ref byte buf, nuint count);
        [DllImport("libc", SetLastError = true)] public static extern int poll(ref PollFd fds, nuint count, int timeout);
    }
}

/// <summary>
/// Helper side of Thermal Grizzly WireView Pro II power meters (USB 0483:5740, "STM32 Virtual ComPort"): finds them
/// in sysfs, checks each is a WireView (vendor data EF 05 / EF 06, build info "Thermal Grizzly WireView…"), and keeps a
/// fresh sample per device on its own thread while a client asked within the last minute. <b>Read-only</b>: only the
/// read commands (vendor data 0x01, sensor values 0x04, build info 0x0D) — never config, calibration, flash, screen,
/// fault clearing, reset or bootloader. Protocol facts from the community wireview-linux project; this code is OpenHW's.
/// </summary>
public sealed class WireViewReader : IDisposable
{
    public const byte ReadVendorData = 0x01, ReadSensorValues = 0x04, ReadBuildInfo = 0x0D;
    private const int StrLen = 32;
    private static readonly TimeSpan SweepEvery = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan IdleAfter = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan RescanEvery = TimeSpan.FromSeconds(10);

    private readonly Func<IReadOnlyList<(string Serial, string Port)>> _find;
    private readonly Func<string, IWireViewLink> _open;
    private readonly object _gate = new();
    private readonly Dictionary<string, (WireViewDevice Device, IWireViewLink? Link)> _devices = new(StringComparer.Ordinal);
    private DateTime _lastAsked = DateTime.MinValue, _lastScan = DateTime.MinValue;
    private Thread? _thread;
    private volatile bool _stopping;

    public WireViewReader(Func<IReadOnlyList<(string Serial, string Port)>>? find = null, Func<string, IWireViewLink>? open = null)
    {
        _find = find ?? (() => Find());
        _open = open ?? (path => new LinuxSerialLink(path));
    }

    /// <summary>STM32 virtual COM ports in sysfs (vendor 0483, product 5740) and their /dev/ttyACM node.</summary>
    public static IReadOnlyList<(string Serial, string Port)> Find(string sys = "/sys")
    {
        var found = new List<(string, string)>();
        var usb = Path.Combine(sys, "bus", "usb", "devices");
        if (!Directory.Exists(usb)) return found;
        foreach (var dev in Directory.GetDirectories(usb))
        {
            string Read(string f) { try { return File.ReadAllText(Path.Combine(dev, f)).Trim(); } catch (IOException) { return ""; } catch (UnauthorizedAccessException) { return ""; } }
            if (Read("idVendor") != "0483" || Read("idProduct") != "5740") continue;
            foreach (var iface in Directory.GetDirectories(dev))
            {
                var tty = Path.Combine(iface, "tty");
                if (!Directory.Exists(tty)) continue;
                foreach (var node in Directory.GetDirectories(tty))
                    found.Add((Read("serial") is { Length: > 0 } s ? s : Path.GetFileName(node), "/dev/" + Path.GetFileName(node)));
            }
        }
        return found.OrderBy(f => f.Item2, StringComparer.Ordinal).ToList();
    }

    /// <summary>Every WireView found and its latest sample; waits up to <paramref name="wait"/> for the first sweep.</summary>
    public List<WireViewDevice> Read(TimeSpan wait)
    {
        lock (_gate)
        {
            _lastAsked = DateTime.UtcNow;
            if (_thread is null && !_stopping)
            {
                _thread = new Thread(Loop) { IsBackground = true, Name = "wireview" };
                _thread.Start();
            }
            var until = DateTime.UtcNow + wait;
            while (_lastScan == DateTime.MinValue && DateTime.UtcNow < until) Monitor.Wait(_gate, until - DateTime.UtcNow);
            return _devices.Values.Select(d => d.Device).OrderBy(d => d.Port, StringComparer.Ordinal).ToList();
        }
    }

    private void Loop()
    {
        while (!_stopping)
        {
            bool idle;
            lock (_gate) idle = DateTime.UtcNow - _lastAsked > IdleAfter;
            if (!idle)
            {
                if (DateTime.UtcNow - _lastScan > RescanEvery) Scan();
                Sweep();
            }
            Thread.Sleep(SweepEvery);
        }
    }

    /// <summary>Adds newly plugged WireViews (identity checked once), forgets unplugged ones.</summary>
    private void Scan()
    {
        IReadOnlyList<(string Serial, string Port)> ports;
        try { ports = _find(); } catch (IOException) { ports = []; }
        foreach (var (serial, port) in ports)
        {
            bool known;
            // A port that failed to open or answer is tried again at the next scan; another STM32 device isn't.
            lock (_gate) known = _devices.TryGetValue(port, out var d) && (d.Link is not null || d.Device.Problem?.StartsWith("not a WireView", StringComparison.Ordinal) == true);
            if (known) continue;
            var device = Identify(serial, port, out var link);
            lock (_gate) _devices[port] = (device, link);
        }
        lock (_gate)
        {
            foreach (var gone in _devices.Keys.Where(p => ports.All(x => x.Port != p)).ToList())
            {
                _devices[gone].Link?.Dispose();
                _devices.Remove(gone);
            }
            _lastScan = DateTime.UtcNow;
            Monitor.PulseAll(_gate);
        }
    }

    private WireViewDevice Identify(string serial, string port, out IWireViewLink? link)
    {
        link = null;
        try
        {
            link = _open(port);
            var vd = link.Command(ReadVendorData, 3);
            if (vd.Length != 3 || vd[0] != 0xEF || vd[1] is not (0x05 or 0x06))
            {
                link.Dispose();
                link = null;
                return new WireViewDevice(serial, "", "", port, null, "not a WireView Pro II (another STM32 serial device)");
            }
            var build = link.Command(ReadBuildInfo, 3 + StrLen + StrLen + 1);
            string Str(int at) => build.Length >= at + StrLen ? System.Text.Encoding.ASCII.GetString(build, at, StrLen).Split('\0')[0].Trim() : "";
            var product = Str(3) is { Length: > 0 } p ? p : vd[1] == 0x06 ? "WireView Pro II Noctua Edition" : "WireView Pro II";
            var firmware = Str(3 + StrLen) is { Length: > 0 } b ? b : $"firmware {vd[2]}";
            return new WireViewDevice(serial, product, firmware, port, null, null);
        }
        catch (IOException ex)
        {
            link?.Dispose();
            link = null;
            return new WireViewDevice(serial, "", "", port, null, ex.Message);
        }
    }

    private void Sweep()
    {
        List<(string Port, WireViewDevice Device, IWireViewLink Link)> live;
        lock (_gate) live = _devices.Where(d => d.Value.Link is not null).Select(d => (d.Key, d.Value.Device, d.Value.Link!)).ToList();
        foreach (var (port, device, link) in live)
        {
            if (_stopping) return;
            WireViewDevice next;
            try
            {
                // An out-of-step frame is dropped; the next command's input flush puts the stream back in step.
                var sample = WireViewSample.Parse(link.Command(ReadSensorValues, WireViewSample.Size));
                next = sample is null ? device : device with { Sample = sample, Problem = null };
            }
            catch (IOException ex) { next = device with { Sample = null, Problem = ex.Message }; }
            lock (_gate) if (_devices.ContainsKey(port)) _devices[port] = (next, link);
        }
    }

    public void Dispose()
    {
        _stopping = true;
        _thread?.Join(TimeSpan.FromSeconds(3));
        lock (_gate) foreach (var d in _devices.Values) d.Link?.Dispose();
    }
}
