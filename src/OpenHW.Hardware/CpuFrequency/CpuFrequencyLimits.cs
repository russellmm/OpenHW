using System.Globalization;

namespace OpenHW.Hardware;

/// <summary>One cpufreq policy's ceiling: <c>scaling_max_freq</c> against the hardware's <c>cpuinfo_max_freq</c>.</summary>
public sealed record CpuFrequencyPolicy(int Id, long MaxKHz, long HardwareMaxKHz, bool? Boost)
{
    public bool Capped => MaxKHz < HardwareMaxKHz;
}

/// <summary>All policies at one moment. Readable without root.</summary>
public sealed record CpuFrequencyState(IReadOnlyList<CpuFrequencyPolicy> Policies)
{
    /// <summary>Every policy reports boost off (power-profiles-daemon's power-saver does this on amd-pstate).</summary>
    public bool BoostOff => Policies.Count > 0 && Policies.All(p => p.Boost == false);

    /// <summary>Boost is on but some policy is held below the hardware maximum.</summary>
    public bool Capped => !BoostOff && Policies.Any(p => p.Capped);

    public long LowestMaxKHz => Policies.Count == 0 ? 0 : Policies.Min(p => p.MaxKHz);
    public long HardwareMaxKHz => Policies.Count == 0 ? 0 : Policies.Max(p => p.HardwareMaxKHz);
}

/// <summary>
/// cpufreq ceilings. With boost off (power-saver) the kernel holds every policy's <c>scaling_max_freq</c> at the base
/// clock. Turning boost back on normally restores the last requested ceiling, but on this machine (kernel 7.3,
/// amd-pstate-epp, power-profiles-daemon 0.30) a session that started in power-saver came back still at base clock
/// — the stored request was the base clock — and stayed there until something with root wrote the ceiling.
/// <see cref="RestoreHardwareMaximum"/> is that write. It never touches boost, governor, EPP or the minimum frequency.
/// </summary>
public sealed class CpuFrequencyLimits(string cpuRoot = "/sys/devices/system/cpu")
{
    private string PolicyDir(int id) => Path.Combine(cpuRoot, "cpufreq", $"policy{id}");

    public CpuFrequencyState Read()
    {
        var policies = new List<CpuFrequencyPolicy>();
        var root = Path.Combine(cpuRoot, "cpufreq");
        if (!Directory.Exists(root)) return new CpuFrequencyState(policies);
        foreach (var dir in Directory.EnumerateDirectories(root, "policy*"))
        {
            if (!int.TryParse(Path.GetFileName(dir)["policy".Length..], NumberStyles.None, CultureInfo.InvariantCulture, out var id)) continue;
            if (ReadKHz(Path.Combine(dir, "scaling_max_freq")) is not { } max || ReadKHz(Path.Combine(dir, "cpuinfo_max_freq")) is not { } hw) continue;
            var boost = SysFs.ReadText(Path.Combine(dir, "boost")) switch { "1" => true, "0" => false, _ => (bool?)null };
            policies.Add(new CpuFrequencyPolicy(id, max, hw, boost));
        }
        policies.Sort((a, b) => a.Id.CompareTo(b.Id));
        return new CpuFrequencyState(policies);
    }

    /// <summary>
    /// Root only. Raises every policy whose ceiling is below <c>cpuinfo_max_freq</c> to that maximum, waits for the
    /// kernel to report it, and returns how many policies changed. Throws <see cref="IOException"/> if a write fails
    /// or the kernel doesn't keep the value.
    /// </summary>
    public int RestoreHardwareMaximum()
    {
        var capped = Read().Policies.Where(p => p.Capped).ToArray();
        foreach (var p in capped)
            File.WriteAllText(Path.Combine(PolicyDir(p.Id), "scaling_max_freq"), p.HardwareMaxKHz.ToString(CultureInfo.InvariantCulture) + "\n");

        // Frequency QoS updates can land just after the sysfs write returns.
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (capped.Any(p => ReadKHz(Path.Combine(PolicyDir(p.Id), "scaling_max_freq")) != p.HardwareMaxKHz))
        {
            if (clock.ElapsedMilliseconds > 500) throw new IOException("the kernel did not keep the restored maximum");
            Thread.Sleep(5);
        }
        return capped.Length;
    }

    private static long? ReadKHz(string path) =>
        long.TryParse(SysFs.ReadText(path), NumberStyles.None, CultureInfo.InvariantCulture, out var v) && v > 0 ? v : null;
}
