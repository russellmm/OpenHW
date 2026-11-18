using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace OpenHW.Hardware.Windows;

/// <summary>
/// AMD RAPL energy on Windows, in the OpenHW Helper service: the counters Linux's powercap gives OpenHW
/// (<see cref="RaplReader"/>), read through PawnIO's signed AMDFamily17 module, which allows exactly these MSRs —
/// MSR_PWR_UNIT (0xC0010299, energy unit in bits 12:8), MSR_PKG_ENERGY_STAT (0xC001029B, the package) and
/// MSR_CORE_ENERGY_STAT (0xC001029A, one per physical core, read on that core). The 32-bit counters wrap every few
/// minutes under load, so each is followed between reads and added into a running total in µJ; the totals are reported
/// as Linux's domains ("package-0", "core" = all cores) in the helper's <c>rapl</c> format, so <see cref="RaplEngine"/>
/// works unchanged. Reads only.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsRapl(Func<PawnIo?> open) : IDisposable
{
    private const uint PwrUnit = 0xC0010299, CoreEnergy = 0xC001029A, PackageEnergy = 0xC001029B;

    private readonly object _gate = new();
    private PawnIo? _pawn;
    private DateTime _retryAt;
    private double _unitUJ;
    private IReadOnlyList<(ushort Group, int Number)> _cores = [];
    private readonly Dictionary<int, uint> _lastCore = [];
    private uint? _lastPackage;
    private double _packageUJ, _coresUJ;

    public string? Error { get; private set; }

    public IReadOnlyList<RaplCounter>? Read()
    {
        lock (_gate)
        {
            if (_pawn is null)
            {
                if (DateTime.UtcNow < _retryAt) return null;
                _pawn = open();
                if (_pawn is null) { _retryAt = DateTime.UtcNow.AddMinutes(1); Error = "PawnIO's AMDFamily17 module isn't available."; return null; }
                // The first core's MSR_PWR_UNIT: energy status unit = 1 / 2^ESU joules.
                _unitUJ = 1e6 / Math.Pow(2, (Msr(PwrUnit) >> 8) & 0x1F);
                _cores = WindowsSystemEngine.Topology()
                    .Select(c => c.Threads[0].Split(','))
                    .Select(t => ((ushort)int.Parse(t[0], System.Globalization.CultureInfo.InvariantCulture), int.Parse(t[1], System.Globalization.CultureInfo.InvariantCulture)))
                    .ToList();
            }
            try
            {
                _packageUJ += Delta(ref _lastPackage, (uint)Msr(PackageEnergy));
                Thread.BeginThreadAffinity();
                var original = Pin(_cores.Count > 0 ? _cores[0] : ((ushort)0, 0));
                try
                {
                    for (var i = 0; i < _cores.Count; i++)
                    {
                        Pin(_cores[i]);
                        var raw = (uint)Msr(CoreEnergy);
                        uint? last = _lastCore.TryGetValue(i, out var l) ? l : null;
                        _coresUJ += Delta(ref last, raw);
                        _lastCore[i] = raw;
                    }
                }
                finally
                {
                    SetThreadGroupAffinity(GetCurrentThread(), ref original, out _);
                    Thread.EndThreadAffinity();
                }
                Error = null;
                // Totals never wrap; the range only needs to be larger than any total.
                return [new RaplCounter("package-0", (ulong)_packageUJ, ulong.MaxValue), new RaplCounter("core", (ulong)_coresUJ, ulong.MaxValue)];
            }
            catch (IOException ex)
            {
                Error = ex.Message;
                return null;
            }
        }
    }

    // A counter's increase since the last read (mod 2^32), in µJ; the first read only sets the baseline.
    private double Delta(ref uint? last, uint raw)
    {
        var used = last is { } previous ? (double)unchecked(raw - previous) * _unitUJ : 0;
        last = raw;
        return used;
    }

    private ulong Msr(uint msr) => _pawn!.Execute("ioctl_read_msr", [msr], 1)[0];

    // MSRs are per CPU: the service thread is moved onto the core it reads, then back.
    [StructLayout(LayoutKind.Sequential)]
    private struct GroupAffinity { public nuint Mask; public ushort Group; public ushort Reserved0, Reserved1, Reserved2; }

    private static GroupAffinity Pin((ushort Group, int Number) cpu)
    {
        var affinity = new GroupAffinity { Mask = (nuint)1 << cpu.Number, Group = cpu.Group };
        if (!SetThreadGroupAffinity(GetCurrentThread(), ref affinity, out var previous)) throw new IOException($"can't run on CPU {cpu.Group},{cpu.Number}");
        return previous;
    }

    [DllImport("kernel32.dll")] private static extern nint GetCurrentThread();
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetThreadGroupAffinity(nint thread, ref GroupAffinity affinity, out GroupAffinity previous);

    public void Dispose()
    {
        lock (_gate) _pawn?.Dispose();
    }
}
