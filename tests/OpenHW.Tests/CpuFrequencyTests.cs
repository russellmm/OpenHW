using OpenHW.Hardware;
using Xunit;

namespace OpenHW.Tests;

/// <summary>cpufreq ceilings against a fake sysfs tree shaped like amd-pstate's.</summary>
public sealed class CpuFrequencyTests : IDisposable
{
    private const long Base = 4_000_000, Boost = 5_487_604;
    private readonly string _root = Directory.CreateTempSubdirectory("openhw-cpufreq").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private const long Min = 1_223_141;

    private void Policy(int id, long max, long hardwareMax, string boost = "1")
    {
        var dir = Path.Combine(_root, "cpufreq", $"policy{id}");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "scaling_max_freq"), max + "\n");
        File.WriteAllText(Path.Combine(dir, "cpuinfo_max_freq"), hardwareMax + "\n");
        File.WriteAllText(Path.Combine(dir, "scaling_min_freq"), Min + "\n");
        File.WriteAllText(Path.Combine(dir, "boost"), boost + "\n");
    }

    private void Thread(int cpu, int core, int ccd)
    {
        var topology = Path.Combine(_root, $"cpu{cpu}", "topology");
        Directory.CreateDirectory(topology);
        File.WriteAllText(Path.Combine(topology, "core_id"), core + "\n");
        File.WriteAllText(Path.Combine(topology, "physical_package_id"), "0\n");
        File.WriteAllText(Path.Combine(topology, "die_id"), ccd + "\n");
    }

    private long MaxOf(int id) => long.Parse(File.ReadAllText(Path.Combine(_root, "cpufreq", $"policy{id}", "scaling_max_freq")));

    [Fact]
    public void BoostBackOnWithTheBaseClockClampIsCapped()
    {
        Policy(0, Base, Boost);
        Policy(1, Base, Boost);
        var state = new CpuFrequencyLimits(_root).Read();
        Assert.False(state.BoostOff);
        Assert.True(state.Capped);
        Assert.Equal(Base, state.LowestMaxKHz);
        Assert.Equal(Boost, state.HardwareMaxKHz);
    }

    [Fact]
    public void PowerSaverIsBoostOffNotCapped()
    {
        // Boost off: cpuinfo_max_freq drops to base too, so the ceiling is already the most the CPU can do.
        Policy(0, Base, Base, boost: "0");
        var state = new CpuFrequencyLimits(_root).Read();
        Assert.True(state.BoostOff);
        Assert.False(state.Capped);
    }

    [Fact]
    public void RestoreRaisesOnlyCappedPoliciesToTheHardwareMaximum()
    {
        Policy(0, Base, Boost);
        Policy(1, Boost, Boost);
        Policy(2, 3_000_000, Boost);
        Assert.Equal(2, new CpuFrequencyLimits(_root).RestoreHardwareMaximum());
        Assert.All(new[] { 0, 1, 2 }, id => Assert.Equal(Boost, MaxOf(id)));
        Assert.False(new CpuFrequencyLimits(_root).Read().Capped);
    }

    [Fact]
    public void HelperRestoreTakesNoArguments()
    {
        Policy(0, Base, Boost);
        var session = new HelperSession(new NoSmart(), new CpuFrequencyLimits(_root));
        Assert.StartsWith("err", session.HandleLine("cpufreqrestore 9999999"));
        Assert.Equal(Base, MaxOf(0));
        Assert.Equal("ok 1", session.HandleLine("cpufreqrestore"));
        Assert.Equal(Boost, MaxOf(0));
        Assert.Equal("ok 0", session.HandleLine("cpufreqrestore"));
    }

    [Fact]
    public void SetMaximumChecksEveryValueBeforeWritingAny()
    {
        Policy(0, Boost, Boost);
        Policy(1, Boost, Boost);
        var limits = new CpuFrequencyLimits(_root);
        Assert.Throws<ArgumentException>(() => limits.SetMaximum(new Dictionary<int, long?> { [0] = 3_000_000, [1] = 1_000_000 }));
        Assert.Throws<ArgumentException>(() => limits.SetMaximum(new Dictionary<int, long?> { [0] = 3_000_000, [1] = 6_000_000 }));
        Assert.Throws<ArgumentException>(() => limits.SetMaximum(new Dictionary<int, long?> { [7] = 3_000_000 }));
        Assert.Equal(Boost, MaxOf(0));
        Assert.Equal(Boost, MaxOf(1));
    }

    [Fact]
    public void SetMaximumCapsAndRemovesCaps()
    {
        Policy(0, Boost, Boost);
        Policy(1, Boost, Boost);
        var limits = new CpuFrequencyLimits(_root);
        Assert.Equal(2, limits.SetMaximum(new Dictionary<int, long?> { [0] = 3_000_000, [1] = 3_000_000 }));
        Assert.Equal(3_000_000, MaxOf(1));
        Assert.Equal(1, limits.SetMaximum(new Dictionary<int, long?> { [0] = null, [1] = 3_000_000 }));
        Assert.Equal(Boost, MaxOf(0));
    }

    [Fact]
    public void AFailedWritePutsEarlierPoliciesBack()
    {
        Policy(0, Boost, Boost);
        Policy(1, Boost, Boost);
        var limits = new CpuFrequencyLimits(_root, (path, khz) =>
        {
            if (path.Contains("policy1") && khz != Boost) throw new IOException("Device or resource busy");
            File.WriteAllText(path, khz + "\n");
        });
        var error = Assert.Throws<IOException>(() => limits.SetMaximum(new Dictionary<int, long?> { [0] = 3_000_000, [1] = 3_000_000 }));
        Assert.Contains("previous ceilings kept", error.Message);
        Assert.Equal(Boost, MaxOf(0));
        Assert.Equal(Boost, MaxOf(1));
    }

    [Fact]
    public void ACeilingTheKernelDoesNotKeepIsRolledBack()
    {
        Policy(0, Boost, Boost);
        // The driver accepts the write but reports another value, as when firmware clamps it.
        var limits = new CpuFrequencyLimits(_root, (path, khz) => File.WriteAllText(path, (khz == Boost ? khz : khz + 1000) + "\n"));
        Assert.Throws<IOException>(() => limits.SetMaximum(new Dictionary<int, long?> { [0] = 3_000_000 }));
        Assert.Equal(Boost, MaxOf(0));
    }

    [Theory]
    [InlineData("cpufreqmax all 99999999999")]
    [InlineData("cpufreqmax all 0")]
    [InlineData("cpufreqmax all -1")]
    [InlineData("cpufreqmax 0;1 3000000")]
    [InlineData("cpufreqmax ../policy0 3000000")]
    [InlineData("cpufreqmax 0 3000000 extra")]
    [InlineData("cpufreqmax 5 3000000")]
    [InlineData("cpufreqmax 0 900000")]
    [InlineData("cpufreqmax 0 6000000")]
    public void HelperRejectsBadCaps(string line)
    {
        Policy(0, Boost, Boost);
        Assert.StartsWith("err", new HelperSession(new NoSmart(), new CpuFrequencyLimits(_root)).HandleLine(line));
        Assert.Equal(Boost, MaxOf(0));
    }

    [Fact]
    public void HelperSetsAndRemovesCaps()
    {
        Policy(0, Boost, Boost);
        Policy(32, Boost, Boost);
        var session = new HelperSession(new NoSmart(), new CpuFrequencyLimits(_root));
        Assert.Equal("ok 2", session.HandleLine("cpufreqmax 0,32 3000000"));
        Assert.Equal(3_000_000, MaxOf(32));
        Assert.Equal("ok 2", session.HandleLine("cpufreqmax all max"));
        Assert.Equal(Boost, MaxOf(0));
    }

    [Fact]
    public void CoresGroupSiblingThreadsWithTheirCcd()
    {
        Thread(0, core: 0, ccd: 0);
        Thread(32, core: 0, ccd: 0);
        Thread(8, core: 8, ccd: 1);
        Thread(40, core: 8, ccd: 1);
        var cores = new CpuFrequencyLimits(_root).ReadCores();
        Assert.Equal(2, cores.Count);
        Assert.Equal([0, 32], cores[0].Threads);
        Assert.Equal((1, 1), (cores[1].Index, cores[1].Ccd));
        Assert.Equal([8, 40], cores[1].Threads);
    }

    [Fact]
    public void HelperWithoutCpufreqRefuses() =>
        Assert.StartsWith("err", new HelperSession(new NoSmart()).HandleLine("cpufreqrestore"));

    private sealed class NoSmart : INvmeSmartReader
    {
        public byte[] Read(string controller, string identity) => throw new IOException("no drives");
        public void Dispose() { }
    }
}
