using System.Globalization;

namespace OpenHW.Hardware;

/// <summary>One cpufreq policy's ceiling: <c>scaling_max_freq</c> against the hardware's <c>cpuinfo_max_freq</c>.</summary>
public sealed record CpuFrequencyPolicy(int Id, long MaxKHz, long HardwareMaxKHz, bool? Boost, long MinKHz = 0, long? CurrentKHz = null)
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

    /// <summary>Highest per-policy minimum: no cap may go below it.</summary>
    public long FloorKHz => Policies.Count == 0 ? 0 : Policies.Max(p => p.MinKHz);

    public CpuFrequencyPolicy? Get(int id) => Policies.FirstOrDefault(p => p.Id == id);
}

/// <summary>A physical core: its SMT threads (one cpufreq policy each here) and the CCD it sits on.</summary>
public sealed record CpuCore(int Index, int Ccd, IReadOnlyList<int> Threads);

/// <summary>
/// cpufreq ceilings. With boost off (power-saver) the kernel holds every policy's <c>scaling_max_freq</c> at the base
/// clock. Turning boost back on normally restores the last requested ceiling, but a request stored while boost was off
/// (the base clock) survives that, and "performance" then silently runs at base clock until something with root writes
/// the ceiling. Writes here only ever set <c>scaling_max_freq</c>; never boost, governor, EPP or the minimum.
/// </summary>
public sealed class CpuFrequencyLimits(string cpuRoot = "/sys/devices/system/cpu", Action<string, long>? write = null)
{
    private readonly Action<string, long> _write = write ?? ((path, khz) =>
        File.WriteAllText(path, khz.ToString(CultureInfo.InvariantCulture) + "\n"));
    private readonly object _gate = new();

    private string MaxPath(int id) => Path.Combine(cpuRoot, "cpufreq", $"policy{id}", "scaling_max_freq");

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
            var min = ReadKHz(Path.Combine(dir, "scaling_min_freq")) ?? ReadKHz(Path.Combine(dir, "cpuinfo_min_freq")) ?? 0;
            policies.Add(new CpuFrequencyPolicy(id, max, hw, boost, min, ReadKHz(Path.Combine(dir, "scaling_cur_freq"))));
        }
        policies.Sort((a, b) => a.Id.CompareTo(b.Id));
        return new CpuFrequencyState(policies);
    }

    /// <summary>Physical cores in order, numbered densely like the sensor window (Core 0..N-1).</summary>
    public IReadOnlyList<CpuCore> ReadCores()
    {
        var threads = new List<(int CoreId, int Package, int Ccd, int Cpu)>();
        if (!Directory.Exists(cpuRoot)) return [];
        foreach (var dir in Directory.EnumerateDirectories(cpuRoot, "cpu*"))
        {
            if (!int.TryParse(Path.GetFileName(dir).AsSpan(3), NumberStyles.None, CultureInfo.InvariantCulture, out var cpu)) continue;
            if (!int.TryParse(SysFs.ReadText(Path.Combine(dir, "topology", "core_id")), out var coreId)) continue;
            int.TryParse(SysFs.ReadText(Path.Combine(dir, "topology", "physical_package_id")), out var package);
            // die_id is the CCD on Zen; the L3 id says the same where die_id is missing.
            if (!int.TryParse(SysFs.ReadText(Path.Combine(dir, "topology", "die_id")), out var ccd))
                int.TryParse(SysFs.ReadText(Path.Combine(dir, "cache", "index3", "id")), out ccd);
            threads.Add((coreId, package, ccd, cpu));
        }
        return threads.GroupBy(t => (t.Package, t.CoreId)).OrderBy(g => g.Key.Package).ThenBy(g => g.Key.CoreId)
            .Select((g, index) => new CpuCore(index, g.First().Ccd, g.Select(t => t.Cpu).Order().ToList()))
            .ToList();
    }

    /// <summary>
    /// Root only. Sets each listed policy's ceiling (null = its <c>cpuinfo_max_freq</c>), skipping ones already there.
    /// The whole request is checked first — every id must exist and every value lie within that policy's minimum and
    /// hardware maximum — then written, then read back. If a write fails or the kernel doesn't keep a value, every
    /// policy written so far goes back to its previous ceiling and <see cref="IOException"/> is thrown. Returns how
    /// many policies changed.
    /// </summary>
    public int SetMaximum(IReadOnlyDictionary<int, long?> targets)
    {
        lock (_gate)
        {
            var state = Read();
            var changes = new List<(int Id, long Before, long After)>();
            foreach (var (id, requested) in targets)
            {
                if (state.Get(id) is not { } policy) throw new ArgumentException($"policy{id} does not exist");
                var khz = requested ?? policy.HardwareMaxKHz;
                if (khz < policy.MinKHz || khz > policy.HardwareMaxKHz)
                    throw new ArgumentException($"policy{id}: {khz / 1000.0:0.###} MHz is outside {policy.MinKHz / 1000.0:0.###} to {policy.HardwareMaxKHz / 1000.0:0.###} MHz");
                if (khz != policy.MaxKHz) changes.Add((id, policy.MaxKHz, khz));
            }

            var written = new List<(int Id, long Before)>();
            try
            {
                foreach (var change in changes)
                {
                    written.Add((change.Id, change.Before)); // a driver can accept a write and then clamp it: roll it back too
                    _write(MaxPath(change.Id), change.After);
                }
                if (!WaitFor(changes.Select(c => (c.Id, c.After)))) throw new IOException("the kernel did not keep the requested ceiling");
                return changes.Count;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                var restored = true;
                foreach (var (id, before) in Enumerable.Reverse(written))
                {
                    try { _write(MaxPath(id), before); }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException) { restored = false; }
                }
                restored &= WaitFor(written);
                throw new IOException(ex.Message + (restored ? "; previous ceilings kept" : "; some ceilings could not be put back"), ex);
            }
        }
    }

    /// <summary>Root only. Raises every capped policy to its hardware maximum.</summary>
    public int RestoreHardwareMaximum() =>
        SetMaximum(Read().Policies.Where(p => p.Capped).ToDictionary(p => p.Id, _ => (long?)null));

    // Frequency QoS updates can land just after the sysfs write returns: check the whole batch within one deadline.
    private bool WaitFor(IEnumerable<(int Id, long KHz)> expected)
    {
        var list = expected.ToArray();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            if (list.All(e => ReadKHz(MaxPath(e.Id)) == e.KHz)) return true;
            if (clock.ElapsedMilliseconds > 500) return false;
            Thread.Sleep(5);
        }
    }

    private static long? ReadKHz(string path) =>
        long.TryParse(SysFs.ReadText(path), NumberStyles.None, CultureInfo.InvariantCulture, out var v) && v > 0 ? v : null;
}
