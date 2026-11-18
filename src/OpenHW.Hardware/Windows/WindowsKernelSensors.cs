using System.Runtime.Versioning;

namespace OpenHW.Hardware.Windows;

/// <summary>
/// One reading as a Linux hwmon chip would expose it: chip name ("k10temp"), hwmon index, feature ("temp3"),
/// label ("Tccd1"), libsensors feature type (0 voltage, 1 fan, 2 temperature, 3 power, 5 current) and the value.
/// </summary>
public sealed record KernelChannel(string Chip, int Index, string Feature, string Label, int Type, double? Value);

/// <summary>
/// The OpenHW Helper service's kernel-access readings on Windows, through PawnIO (<see cref="PawnIo"/>): what the
/// Linux kernel's drivers give OpenHW through hwmon, decoded the same way and reported as the same chips and
/// channels, so the app's <see cref="HwmonEngine"/> — board profiles, names, groups, ids — works as on Linux.
/// Read-only. Each chip is a <see cref="IKernelChip"/>; one that can't open (no PawnIO, other CPU) is left out.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsKernelSensors(IReadOnlyList<Func<IKernelChip?>>? chips = null) : IDisposable
{
    private readonly object _gate = new();
    private readonly IReadOnlyList<Func<IKernelChip?>> _factories = chips ?? [() => K10Temp.Open()];
    private List<IKernelChip>? _chips;
    private List<KernelChannel>? _last;
    private DateTime _lastAt;

    /// <summary>Every chip's channels with fresh values (at most once a second).</summary>
    public List<KernelChannel> Read()
    {
        lock (_gate)
        {
            if (_last is not null && DateTime.UtcNow - _lastAt < TimeSpan.FromMilliseconds(900)) return _last;
            _chips ??= _factories.Select(f => f()).OfType<IKernelChip>().ToList();
            var channels = new List<KernelChannel>();
            foreach (var chip in _chips)
            {
                try { channels.AddRange(chip.Read()); }
                catch (IOException) { } // a busy bus or a refused read: this chip's rows skip one poll
            }
            (_last, _lastAt) = (channels, DateTime.UtcNow);
            return channels;
        }
    }

    public void Dispose()
    {
        lock (_gate) foreach (var chip in _chips ?? []) chip.Dispose();
    }
}

public interface IKernelChip : IDisposable
{
    IReadOnlyList<KernelChannel> Read();
}

/// <summary>
/// AMD CPU temperatures, as Linux's k10temp: Tctl from SMN 0x59800 (bits 31:21 × 0.125 °C, minus 49 °C when the
/// range-select bit is set) and each CCD's die temperature from SMN 0x59800 + CCD offset + 4·n (bit 11 valid, bits
/// 10:0 × 0.125 − 49 °C). The CCD offset and count per family/model are k10temp's (2026 kernel). Reads go through
/// PawnIO's AMDFamily17 module holding the shared PCI mutex.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class K10Temp : IKernelChip
{
    private const uint ReportedTempCtrl = 0x59800;
    private readonly PawnIo _pawn;
    private readonly uint _ccdOffset;
    private readonly int[] _ccds;

    private K10Temp(PawnIo pawn, uint ccdOffset, int[] ccds) => (_pawn, _ccdOffset, _ccds) = (pawn, ccdOffset, ccds);

    public static K10Temp? Open()
    {
        var (family, model) = CpuId();
        var layout = Layout(family, model);
        if (family < 0x17 || PawnIo.Open("AMDFamily17", out _) is not { } pawn) return null;
        var chip = new K10Temp(pawn, layout?.Offset ?? 0, []);
        // CCDs that report a valid reading, as k10temp_get_ccd_support.
        var ccds = layout is { } l
            ? Enumerable.Range(0, l.Count).Where(i => chip.TryRead(ReportedTempCtrl + l.Offset + (uint)(4 * i)) is { } v && v != 0xFFFFFFFF && (v & 0x800) != 0).ToArray()
            : [];
        return new K10Temp(pawn, layout?.Offset ?? 0, ccds);
    }

    /// <summary>k10temp's CCD register offset and maximum CCD count for a CPU, or null when it has none.</summary>
    public static (uint Offset, int Count)? Layout(int family, int model) => (family, model) switch
    {
        (0x17, 0x01 or 0x08 or 0x11 or 0x18) => (0x154, 4),
        (0x17, 0x31 or 0x47 or 0x60 or 0x68 or 0x71) => (0x154, 8),
        (0x17, >= 0xa0 and <= 0xaf) => (0x300, 8),
        (0x19, 0x00 or 0x01 or 0x08 or 0x21 or (>= 0x50 and <= 0x5f)) => (0x154, 8),
        (0x19, >= 0x40 and <= 0x4f) => (0x300, 8),
        (0x19, >= 0x60 and <= 0x7f) => (0x308, 8),
        (0x19, (>= 0x10 and <= 0x1f) or (>= 0xa0 and <= 0xaf)) => (0x300, 12),
        (0x1a, >= 0x00 and <= 0x1f) => (0x1F0, 16), // Zen 5 Turin / Threadripper 9000
        (0x1a, >= 0x40 and <= 0x4f) => (0x308, 8),
        _ => null,
    };

    /// <summary>Tctl in °C from the reported-temperature register.</summary>
    public static double Tctl(uint register)
    {
        var temp = (register >> 21) * 0.125;
        return (register & 0x80000) != 0 || (register & 0x30000) == 0x30000 ? temp - 49 : temp;
    }

    /// <summary>A CCD's temperature in °C, or null when its valid bit is clear.</summary>
    public static double? Ccd(uint register) => (register & 0x800) != 0 ? (register & 0x7FF) * 0.125 - 49 : null;

    public IReadOnlyList<KernelChannel> Read()
    {
        var channels = new List<KernelChannel>();
        var tctl = Smn(ReportedTempCtrl);
        channels.Add(new("k10temp", 0, "temp1", "Tctl", 2, Tctl(tctl)));
        // Linux's channel numbers: temp1 Tctl, temp2 Tdie (only with a Tctl offset, none on these CPUs), temp3+ Tccd1…
        foreach (var i in _ccds)
            channels.Add(new("k10temp", 0, $"temp{3 + i}", $"Tccd{i + 1}", 2, Ccd(Smn(ReportedTempCtrl + _ccdOffset + (uint)(4 * i)))));
        return channels;
    }

    private uint Smn(uint address) => TryRead(address) ?? throw new IOException($"SMN 0x{address:X} unreadable");

    private uint? TryRead(uint address)
    {
        try { return HardwareMutex.Hold(HardwareMutex.Pci, () => (uint)_pawn.Execute("ioctl_read_smn", [address], 1)[0]); }
        catch (IOException) { return null; }
    }

    /// <summary>Family and model as Linux computes them (base + extended).</summary>
    public static (int Family, int Model) CpuId()
    {
        if (!System.Runtime.Intrinsics.X86.X86Base.IsSupported) return (0, 0);
        var eax = System.Runtime.Intrinsics.X86.X86Base.CpuId(1, 0).Eax;
        var family = (eax >> 8) & 0xF;
        var model = (eax >> 4) & 0xF;
        if (family == 0xF) family += (eax >> 20) & 0xFF;
        if (family >= 0x6) model |= ((eax >> 16) & 0xF) << 4;
        return (family, model);
    }

    public void Dispose() => _pawn.Dispose();
}
