using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace OpenHW.Hardware.Windows;

/// <summary>
/// Effective clocks (HWiNFO's "Effective Clock"), in the OpenHW Helper service: each logical CPU's APERF counter
/// (MSR_APERF_RO 0xC00000E8, on PawnIO's signed AMDFamily17 read list), read on that CPU. APERF only advances while
/// the thread is awake, at its real clock, so its increase divided by the elapsed time is the clock averaged over
/// sleep: what the core actually ran at, unlike Windows' estimate. A core's value is its busier thread's. Reads only.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsEffectiveClocks(Func<PawnIo?> open) : IDisposable
{
    private const uint AperfReadOnly = 0xC00000E8;
    /// <summary>MSR_HARDWARE_PSTATE_STATUS: the core's current P-state (FID, VID) — on AMDFamily17's read list.</summary>
    private const uint HardwarePstateStatus = 0xC0010293;

    private readonly object _gate = new();
    private PawnIo? _pawn;
    private DateTime _retryAt;
    private IReadOnlyList<(int Core, IReadOnlyList<(ushort Group, int Number)> Threads)> _cores = [];
    private readonly Dictionary<(ushort, int), ulong> _last = [];
    private long _lastAt;

    public string? Error { get; private set; }

    /// <summary>
    /// Core number to MHz, plus "avg" (all threads), "&lt;core&gt;t&lt;n&gt;" (each thread, HWiNFO's "Core 0 T1 Effective Clock")
    /// and "ps&lt;core&gt;" (the core's raw MSR_HARDWARE_PSTATE_STATUS, decoded by the app); clocks are empty on the
    /// first read (the baseline); null if unavailable.
    /// </summary>
    public Dictionary<string, double>? Read()
    {
        lock (_gate)
        {
            if (_pawn is null)
            {
                if (DateTime.UtcNow < _retryAt) return null;
                _pawn = open();
                if (_pawn is null) { _retryAt = DateTime.UtcNow.AddMinutes(1); Error = "PawnIO's AMDFamily17 module isn't available."; return null; }
                _cores = WindowsSystemEngine.Topology().Select(c => (c.Core, (IReadOnlyList<(ushort, int)>)c.Threads
                    .Select(t => t.Split(','))
                    .Select(t => ((ushort)int.Parse(t[0], System.Globalization.CultureInfo.InvariantCulture), int.Parse(t[1], System.Globalization.CultureInfo.InvariantCulture)))
                    .ToList())).ToList();
            }
            var now = Stopwatch.GetTimestamp();
            var seconds = _lastAt == 0 ? 0 : (double)(now - _lastAt) / Stopwatch.Frequency;
            var result = new Dictionary<string, double>(StringComparer.Ordinal);
            var all = new List<double>();
            Thread.BeginThreadAffinity();
            GroupAffinity original = default;
            var pinned = false;
            try
            {
                foreach (var (core, threads) in _cores)
                {
                    var best = 0.0;
                    var thread = 0;
                    foreach (var cpu in threads)
                    {
                        var affinity = new GroupAffinity { Mask = (nuint)1 << cpu.Number, Group = cpu.Group };
                        if (!SetThreadGroupAffinity(GetCurrentThread(), ref affinity, out var previous)) continue;
                        if (!pinned) (original, pinned) = (previous, true);
                        var aperf = _pawn.Execute("ioctl_read_msr", [AperfReadOnly], 1)[0];
                        if (seconds > 0 && _last.TryGetValue(cpu, out var last) && aperf >= last)
                        {
                            var mhz = (aperf - last) / seconds / 1e6;
                            all.Add(mhz);
                            best = Math.Max(best, mhz);
                            result[string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{core}t{thread}")] = mhz;
                        }
                        _last[cpu] = aperf;
                        if (thread == 0) // the P-state is the core's: read once, on its first thread
                            result["ps" + core.ToString(System.Globalization.CultureInfo.InvariantCulture)] =
                                _pawn.Execute("ioctl_read_msr", [HardwarePstateStatus], 1)[0] & 0xFFFF_FFFFUL;
                        thread++;
                    }
                    if (seconds > 0) result[core.ToString(System.Globalization.CultureInfo.InvariantCulture)] = best;
                }
                Error = null;
            }
            catch (IOException ex)
            {
                Error = ex.Message;
                return null;
            }
            finally
            {
                if (pinned) SetThreadGroupAffinity(GetCurrentThread(), ref original, out _);
                Thread.EndThreadAffinity();
            }
            _lastAt = now;
            if (all.Count > 0) result["avg"] = all.Average();
            return result;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GroupAffinity { public nuint Mask; public ushort Group; public ushort Reserved0, Reserved1, Reserved2; }

    [DllImport("kernel32.dll")] private static extern nint GetCurrentThread();
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetThreadGroupAffinity(nint thread, ref GroupAffinity affinity, out GroupAffinity previous);

    public void Dispose()
    {
        lock (_gate) _pawn?.Dispose();
    }
}

/// <summary>
/// The app's side of <see cref="WindowsEffectiveClocks"/>: "Core Effective Clocks" (one row per core, each with its
/// threads under it, as HWiNFO's "Core 0 T0/T1 Effective Clock") and the average; and from each core's
/// MSR_HARDWARE_PSTATE_STATUS, HWiNFO's "Core n Ratio" and "Core n VID" (Zen 5 only: see <see cref="Ratio"/>, <see cref="Vid"/>).
/// </summary>
public sealed class WindowsEffectiveClockEngine(Func<string>? request = null, int? cpuFamily = null) : ISensorEngine
{
    private const string Parent = "openhw:cpu:effclock";
    private const string Ratios = "openhw:cpu:ratio", Vids = "openhw:cpu:vid";
    private readonly Func<string> _request = request ?? (() => HelperClient.Request("effclock", receiveTimeoutMs: 2500, maxReplyBytes: 32768));
    private readonly bool _zen5 = (cpuFamily ?? CpuFamily()) == 0x1A;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private Dictionary<string, double>? _last;
    private TimeSpan _lastAt = TimeSpan.FromDays(-1), _failedAt = TimeSpan.FromDays(-1);
    private List<string> _cores = [];
    private Dictionary<string, List<string>> _threads = [];
    private List<string> _pstates = [];

    public string Name => "effclock";
    public bool Available => _last is not null;

    public IReadOnlyDictionary<string, IReadOnlyList<string>> Children
    {
        get
        {
            var children = new Dictionary<string, IReadOnlyList<string>> { [Parent] = _cores.Select(c => $"{Parent}:{c}").ToList() };
            foreach (var (core, threads) in _threads)
                if (threads.Count > 0) children[$"{Parent}:{core}"] = threads.Select(t => $"{Parent}:{core}t{t}").ToList();
            if (_pstates.Count > 0)
            {
                children[Ratios] = _pstates.Select(c => $"{Ratios}:{c}").ToList();
                children[Vids] = _pstates.Select(c => $"{Vids}:{c}").ToList();
            }
            return children;
        }
    }

    /// <summary>Zen 5 (family 1Ah) MSR_HARDWARE_PSTATE_STATUS: CpuFid [11:0] in 5 MHz steps (ratio = MHz ÷ 100).</summary>
    public static double Ratio(ulong pstate) => (pstate & 0xFFF) * 5 / 100.0;

    /// <summary>Zen 5 CpuVid [21:14], an SVI3 code: 0.245 V + 5 mV per step (150 → 0.995 V, as HWiNFO showed).</summary>
    public static double Vid(ulong pstate) => 0.245 + 0.005 * ((pstate >> 14) & 0xFF);

    private static int Int(string s) => int.Parse(s, System.Globalization.CultureInfo.InvariantCulture);
    private static bool Digits(string s) => s.Length > 0 && s.All(char.IsAsciiDigit);

    public IReadOnlyList<HardwareItem> Discover()
    {
        if (Fetch() is not { } values) return [];
        _cores = values.Keys.Where(Digits).OrderBy(Int).ToList();
        _threads = _cores.ToDictionary(c => c, c => values.Keys
            .Where(k => k.StartsWith(c + "t", StringComparison.Ordinal) && Digits(k[(c.Length + 1)..]))
            .Select(k => k[(c.Length + 1)..]).OrderBy(Int).ToList());
        _pstates = _zen5 ? values.Keys.Where(k => k.StartsWith("ps", StringComparison.Ordinal) && Digits(k[2..])).Select(k => k[2..]).OrderBy(Int).ToList() : [];
        if (_cores.Count == 0) return [];
        var items = new List<HardwareItem> { Item(Parent, "Core Effective Clocks") };
        foreach (var c in _cores)
        {
            items.Add(Item($"{Parent}:{c}", $"Core {c} Effective Clock"));
            items.AddRange(_threads[c].Select(t => Item($"{Parent}:{c}t{t}", $"Core {c} T{t} Effective Clock")));
        }
        items.Add(Item($"{Parent}:avg", "Average Effective Clock"));
        if (_pstates.Count > 0)
        {
            items.Add(Pstate(Ratios, "Core Ratios", HardwareKind.Load, "x", "the highest core's"));
            items.AddRange(_pstates.Select(c => Pstate($"{Ratios}:{c}", $"Core {c} Ratio", HardwareKind.Load, "x", "CpuFid × 5 MHz ÷ 100")));
            items.Add(Pstate(Vids, "Core VIDs", HardwareKind.Voltage, "V", "the highest core's"));
            items.AddRange(_pstates.Select(c => Pstate($"{Vids}:{c}", $"Core {c} VID", HardwareKind.Voltage, "V", "CpuVid: 0.245 V + 5 mV per step")));
        }
        return items;
    }

    private static HardwareItem Item(string id, string name) => new()
    {
        Id = id, Name = name, Kind = HardwareKind.Frequency, Unit = "MHz", Group = SystemEngine.CpuGroup, Backend = "openhw",
        CanSet = false, Source = "APERF (MSR, OpenHW Helper)",
        Details = "APERF increase divided by elapsed time: the clock averaged over sleep, as HWiNFO's Effective Clock",
    };

    private static HardwareItem Pstate(string id, string name, HardwareKind kind, string unit, string how) => new()
    {
        Id = id, Name = name, Kind = kind, Unit = unit, Group = SystemEngine.CpuGroup, Backend = "openhw", CanSet = false,
        Source = "MSR_HARDWARE_PSTATE_STATUS (0xC0010293, OpenHW Helper)",
        Details = $"The core's current P-state as its firmware set it ({how}); Zen 5 layout",
    };

    public void ReadInto(IDictionary<string, double?> readings, IReadOnlySet<string>? requested)
    {
        if (requested is not null && !requested.Any(id => id.StartsWith(Parent, StringComparison.Ordinal) ||
            id.StartsWith(Ratios, StringComparison.Ordinal) || id.StartsWith(Vids, StringComparison.Ordinal))) return;
        if (Fetch() is not { } values) return;
        foreach (var c in _cores)
        {
            readings[$"{Parent}:{c}"] = values.TryGetValue(c, out var v) ? v : null;
            foreach (var t in _threads[c]) readings[$"{Parent}:{c}t{t}"] = values.TryGetValue($"{c}t{t}", out var tv) ? tv : null;
        }
        readings[$"{Parent}:avg"] = values.TryGetValue("avg", out var a) ? a : null;
        readings[Parent] = _cores.Count > 0 ? _cores.Max(c => values.GetValueOrDefault(c)) : null;
        if (_pstates.Count == 0) return;
        var ratios = new List<double>();
        var vids = new List<double>();
        foreach (var c in _pstates)
        {
            if (!values.TryGetValue("ps" + c, out var raw)) { readings[$"{Ratios}:{c}"] = readings[$"{Vids}:{c}"] = null; continue; }
            var (ratio, vid) = (Ratio((ulong)raw), Vid((ulong)raw));
            readings[$"{Ratios}:{c}"] = ratio;
            readings[$"{Vids}:{c}"] = vid;
            ratios.Add(ratio);
            vids.Add(vid);
        }
        readings[Ratios] = ratios.Count > 0 ? ratios.Max() : null;
        readings[Vids] = vids.Count > 0 ? vids.Max() : null;
    }

    /// <summary>CPUID leaf 1's family (base + extended), as AMD numbers them (1Ah = Zen 5).</summary>
    private static int CpuFamily()
    {
        if (!System.Runtime.Intrinsics.X86.X86Base.IsSupported) return 0;
        var eax = (uint)System.Runtime.Intrinsics.X86.X86Base.CpuId(1, 0).Eax;
        var family = (int)((eax >> 8) & 0xF);
        return family == 0xF ? family + (int)((eax >> 20) & 0xFF) : family;
    }

    private Dictionary<string, double>? Fetch()
    {
        if (_last is not null && _clock.Elapsed - _lastAt < TimeSpan.FromMilliseconds(900)) return _last;
        if (_last is null && _clock.Elapsed - _failedAt < TimeSpan.FromSeconds(30)) return null;
        try
        {
            var values = GpuWire.Decode<Dictionary<string, double>>(_request());
            if (values is { Count: > 0 }) (_last, _lastAt) = (values, _clock.Elapsed);
        }
        catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or System.Text.Json.JsonException or FormatException)
        {
            if (_last is null || _clock.Elapsed - _lastAt > TimeSpan.FromSeconds(30)) { _last = null; _failedAt = _clock.Elapsed; }
        }
        return _last;
    }

    public void Dispose() { }
}
