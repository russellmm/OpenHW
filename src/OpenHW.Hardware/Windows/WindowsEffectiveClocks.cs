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

    private readonly object _gate = new();
    private PawnIo? _pawn;
    private DateTime _retryAt;
    private IReadOnlyList<(int Core, IReadOnlyList<(ushort Group, int Number)> Threads)> _cores = [];
    private readonly Dictionary<(ushort, int), ulong> _last = [];
    private long _lastAt;

    public string? Error { get; private set; }

    /// <summary>Core number to MHz, plus "avg" (all threads); empty on the first read (the baseline); null if unavailable.</summary>
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
                        }
                        _last[cpu] = aperf;
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

/// <summary>The app's side of <see cref="WindowsEffectiveClocks"/>: "Core Effective Clocks" (one row per core) and the average.</summary>
public sealed class WindowsEffectiveClockEngine(Func<string>? request = null) : ISensorEngine
{
    private const string Parent = "openhw:cpu:effclock";
    private readonly Func<string> _request = request ?? (() => HelperClient.Request("effclock", receiveTimeoutMs: 2500, maxReplyBytes: 16384));
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private Dictionary<string, double>? _last;
    private TimeSpan _lastAt = TimeSpan.FromDays(-1), _failedAt = TimeSpan.FromDays(-1);
    private List<string> _cores = [];

    public string Name => "effclock";
    public bool Available => _last is not null;

    public IReadOnlyDictionary<string, IReadOnlyList<string>> Children => new Dictionary<string, IReadOnlyList<string>>
    {
        [Parent] = _cores.Select(c => $"{Parent}:{c}").ToList(),
    };

    public IReadOnlyList<HardwareItem> Discover()
    {
        if (Fetch() is not { } values) return [];
        _cores = values.Keys.Where(k => k != "avg").OrderBy(k => int.Parse(k, System.Globalization.CultureInfo.InvariantCulture)).ToList();
        if (_cores.Count == 0) return [];
        return
        [
            Item(Parent, "Core Effective Clocks"),
            .. _cores.Select(c => Item($"{Parent}:{c}", $"Core {c} Effective Clock")),
            Item($"{Parent}:avg", "Average Effective Clock"),
        ];
    }

    private static HardwareItem Item(string id, string name) => new()
    {
        Id = id, Name = name, Kind = HardwareKind.Frequency, Unit = "MHz", Group = SystemEngine.CpuGroup, Backend = "openhw",
        CanSet = false, Source = "APERF (MSR, OpenHW Helper)",
        Details = "APERF increase divided by elapsed time: the clock averaged over sleep, as HWiNFO's Effective Clock",
    };

    public void ReadInto(IDictionary<string, double?> readings, IReadOnlySet<string>? requested)
    {
        if (requested is not null && !requested.Any(id => id.StartsWith(Parent, StringComparison.Ordinal))) return;
        if (Fetch() is not { } values) return;
        foreach (var c in _cores) readings[$"{Parent}:{c}"] = values.TryGetValue(c, out var v) ? v : null;
        readings[$"{Parent}:avg"] = values.TryGetValue("avg", out var a) ? a : null;
        readings[Parent] = _cores.Count > 0 ? _cores.Max(c => values.GetValueOrDefault(c)) : null;
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
