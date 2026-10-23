using OpenHW.Hardware;
using Xunit;

namespace OpenHW.Tests;

/// <summary>cpufreq ceilings against a fake sysfs tree shaped like amd-pstate's.</summary>
public sealed class CpuFrequencyTests : IDisposable
{
    private const long Base = 4_000_000, Boost = 5_487_604;
    private readonly string _root = Directory.CreateTempSubdirectory("openhw-cpufreq").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private void Policy(int id, long max, long hardwareMax, string boost = "1")
    {
        var dir = Path.Combine(_root, "cpufreq", $"policy{id}");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "scaling_max_freq"), max + "\n");
        File.WriteAllText(Path.Combine(dir, "cpuinfo_max_freq"), hardwareMax + "\n");
        File.WriteAllText(Path.Combine(dir, "boost"), boost + "\n");
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
    public void HelperWithoutCpufreqRefuses() =>
        Assert.StartsWith("err", new HelperSession(new NoSmart()).HandleLine("cpufreqrestore"));

    private sealed class NoSmart : INvmeSmartReader
    {
        public byte[] Read(string controller, string identity) => throw new IOException("no drives");
        public void Dispose() { }
    }
}
