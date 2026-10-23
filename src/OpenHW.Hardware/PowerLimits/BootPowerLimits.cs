using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace OpenHW.Hardware;

/// <summary>Where a boot-time power limit comes from.</summary>
public enum BootLimitSource { None, BootService, OpenHW }

/// <summary>A limit applied at every boot, in milliwatts, and who set it.</summary>
public sealed record BootLimit(long MilliWatts, BootLimitSource Source);

/// <summary>
/// Boot-time power limits, kept in the owner's existing boot services rather than a new boot path:
/// <list type="bullet">
///   <item>CPU: <c>/etc/hsmp-control/ppt_mw</c>, which hsmp-control-apply.service re-asserts at boot. hsmp-control
///   refuses anything outside 100–300 W, so OpenHW does too — a value it would reject would silently not apply.</item>
///   <item>GPUs: nvidia-power-limit.service runs <c>nvidia-smi -i &lt;bus&gt; -pl &lt;W&gt;</c> per card. OpenHW never
///   edits that unit; it writes its own drop-in (<c>openhw.conf</c>) that repeats the unit's lines with the cards
///   the owner changed. Deleting the drop-in returns every card to the unit's own values.</item>
/// </list>
/// Reads need no root; writes are helper-only. systemd reads units fresh at boot, so no daemon-reload is needed.
/// </summary>
public sealed partial class BootPowerLimits(string etc = "/etc")
{
    public const long CpuMinMW = 100_000, CpuMaxMW = 300_000; // hsmp-control's own accepted range
    public const string GpuUnit = "nvidia-power-limit.service";
    public const string CpuUnit = "hsmp-control-apply.service";

    private string PptFile => Path.Combine(etc, "hsmp-control", "ppt_mw");
    private string UnitFile => Path.Combine(etc, "systemd", "system", GpuUnit);
    private string DropIn => Path.Combine(etc, "systemd", "system", GpuUnit + ".d", "openhw.conf");

    public bool CpuServiceInstalled => File.Exists(Path.Combine(etc, "systemd", "system", CpuUnit));
    public bool GpuServiceInstalled => File.Exists(UnitFile);

    /// <summary>The CPU limit hsmp-control will apply at boot, or null (firmware default).</summary>
    public long? CpuBootMW() =>
        long.TryParse(SysFs.ReadText(PptFile), NumberStyles.None, CultureInfo.InvariantCulture, out var mw) ? mw : null;

    /// <summary>Each card's boot limit by PCI bus id (as nvidia-smi writes it), and whether OpenHW's drop-in sets it.</summary>
    public IReadOnlyDictionary<string, BootLimit> GpuBoot()
    {
        var limits = new Dictionary<string, BootLimit>(StringComparer.OrdinalIgnoreCase);
        foreach (var (bus, watts) in PowerLines(UnitFile)) limits[bus] = new BootLimit(watts * 1000, BootLimitSource.BootService);
        var unit = limits.ToDictionary(l => l.Key, l => l.Value.MilliWatts, StringComparer.OrdinalIgnoreCase);
        foreach (var (bus, watts) in PowerLines(DropIn))
            limits[bus] = new BootLimit(watts * 1000, unit.TryGetValue(bus, out var u) && u == watts * 1000 ? BootLimitSource.BootService : BootLimitSource.OpenHW);
        return limits;
    }

    /// <summary>Root only. Sets (or with null removes) the CPU boot limit; hsmp-control applies it at the next boot.</summary>
    public void SetCpu(long? milliwatts)
    {
        if (!CpuServiceInstalled) throw new IOException($"{CpuUnit} isn't installed, so a boot limit would never be applied");
        if (milliwatts is null)
        {
            File.Delete(PptFile);
            return;
        }
        if (milliwatts < CpuMinMW || milliwatts > CpuMaxMW)
            throw new ArgumentException($"{milliwatts / 1000.0:0.#} W is outside the {CpuMinMW / 1000} to {CpuMaxMW / 1000} W hsmp-control applies at boot");
        WriteAtomically(PptFile, milliwatts.Value.ToString(CultureInfo.InvariantCulture) + "\n",
            UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        if (CpuBootMW() != milliwatts) throw new IOException("the boot limit did not read back");
    }

    /// <summary>
    /// Root only. Sets the boot limit for the card on <paramref name="bus"/> to whole <paramref name="watts"/>, or with
    /// null puts it back to the unit's own value. The drop-in is rewritten from the unit's lines each time, and
    /// removed when nothing differs from the unit.
    /// </summary>
    public void SetGpu(string bus, long? watts)
    {
        if (!GpuServiceInstalled) throw new IOException($"{GpuUnit} isn't installed, so a boot limit would never be applied");
        var unitLines = PowerLines(UnitFile).ToDictionary(l => l.Bus, l => l.Watts, StringComparer.OrdinalIgnoreCase);
        var wanted = PowerLines(DropIn).ToDictionary(l => l.Bus, l => l.Watts, StringComparer.OrdinalIgnoreCase);
        foreach (var (b, w) in unitLines) wanted.TryAdd(b, w);
        if (watts is { } value) wanted[bus] = value;
        else if (unitLines.TryGetValue(bus, out var original)) wanted[bus] = original;
        else wanted.Remove(bus);

        if (wanted.Count == unitLines.Count && wanted.All(w => unitLines.TryGetValue(w.Key, out var u) && u == w.Value))
        {
            if (File.Exists(DropIn)) File.Delete(DropIn); // nothing differs from the unit any more
            return;
        }
        var text = new StringBuilder()
            .AppendLine("# Written by OpenHW (Power Limits → keep after reboot). It replaces the unit's ExecStart lines with")
            .AppendLine($"# the same commands and OpenHW's per-card limits. Delete this file to return to {GpuUnit}'s own values.")
            .AppendLine("[Service]")
            .AppendLine("ExecStart=");
        if (UnitText().Contains("-pm 1", StringComparison.Ordinal)) text.AppendLine("ExecStart=/usr/bin/nvidia-smi -pm 1");
        foreach (var (b, w) in wanted.OrderBy(w => w.Key, StringComparer.OrdinalIgnoreCase))
            text.AppendLine(CultureInfo.InvariantCulture, $"ExecStart=/usr/bin/nvidia-smi -i {b} -pl {w}");
        WriteAtomically(DropIn, text.ToString(),
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        if (watts is { } check && PowerLines(DropIn).FirstOrDefault(l => l.Bus.Equals(bus, StringComparison.OrdinalIgnoreCase)).Watts != check)
            throw new IOException("the boot limit did not read back");
    }

    private string UnitText() => File.Exists(UnitFile) ? File.ReadAllText(UnitFile) : "";

    private static IEnumerable<(string Bus, long Watts)> PowerLines(string path)
    {
        if (!File.Exists(path)) yield break;
        foreach (Match m in PowerLine().Matches(File.ReadAllText(path)))
            yield return (m.Groups[1].Value, long.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture));
    }

    // Temp file + rename in the same directory: a boot service never reads a half-written file.
    private static void WriteAtomically(string path, string text, UnixFileMode mode)
    {
        var temp = path + ".openhw-tmp";
        File.WriteAllText(temp, text);
        File.SetUnixFileMode(temp, mode);
        File.Move(temp, path, overwrite: true);
    }

    [GeneratedRegex(@"^ExecStart=\S*nvidia-smi\s+-i\s+([0-9A-Fa-f:.]+)\s+-pl\s+([0-9]+)\s*$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex PowerLine();

    /// <summary>NVML's bus id ("00000000:11:00.0") in the form nvidia-power-limit.service uses.</summary>
    public static string BusOf(GpuPowerLimit gpu) => gpu.BusId;
}
