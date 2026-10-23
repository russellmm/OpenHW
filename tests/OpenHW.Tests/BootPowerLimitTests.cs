using OpenHW.Hardware;
using Xunit;

namespace OpenHW.Tests;

/// <summary>Boot-time limits against a fake /etc shaped like the owner's: hsmp-control and nvidia-power-limit.service.</summary>
public sealed class BootPowerLimitTests : IDisposable
{
    private readonly string _etc = Directory.CreateTempSubdirectory("openhw-etc").FullName;
    private string Units => Path.Combine(_etc, "systemd", "system");
    private string DropIn => Path.Combine(Units, "nvidia-power-limit.service.d", "openhw.conf");
    private const string Gpu0 = "00000000:01:00.0", Gpu1 = "00000000:11:00.0", Gpu2 = "00000000:E1:00.0";

    public BootPowerLimitTests()
    {
        Directory.CreateDirectory(Path.Combine(_etc, "hsmp-control"));
        Directory.CreateDirectory(Path.Combine(Units, "nvidia-power-limit.service.d"));
        File.WriteAllText(Path.Combine(_etc, "hsmp-control", "ppt_mw"), "250000\n");
        File.WriteAllText(Path.Combine(Units, "hsmp-control-apply.service"), "[Service]\nExecStart=/usr/local/bin/hsmp-control apply\n");
        File.WriteAllText(Path.Combine(Units, "nvidia-power-limit.service"), $"""
            [Service]
            Type=oneshot
            ExecStart=/usr/bin/nvidia-smi -pm 1
            ExecStart=/usr/bin/nvidia-smi -i {Gpu0} -pl 150
            ExecStart=/usr/bin/nvidia-smi -i {Gpu1} -pl 250
            ExecStart=/usr/bin/nvidia-smi -i {Gpu2} -pl 250
            RemainAfterExit=yes
            """);
    }

    public void Dispose() => Directory.Delete(_etc, recursive: true);

    [Fact]
    public void ReadsTheBootServicesValues()
    {
        var boot = new BootPowerLimits(_etc);
        Assert.Equal(250_000, boot.CpuBootMW());
        var gpus = boot.GpuBoot();
        Assert.Equal(new BootLimit(150_000, BootLimitSource.BootService), gpus[Gpu0]);
        Assert.Equal(new BootLimit(250_000, BootLimitSource.BootService), gpus[Gpu2]);
    }

    [Fact]
    public void CpuBootLimitStaysInHsmpControlsRange()
    {
        var boot = new BootPowerLimits(_etc);
        Assert.Throws<ArgumentException>(() => boot.SetCpu(350_000)); // hsmp-control would refuse it at boot
        Assert.Throws<ArgumentException>(() => boot.SetCpu(90_000));
        boot.SetCpu(280_000);
        Assert.Equal(280_000, boot.CpuBootMW());
        boot.SetCpu(null);
        Assert.Null(boot.CpuBootMW());
    }

    [Fact]
    public void AGpuBootLimitGoesInADropInThatKeepsTheOtherCards()
    {
        var boot = new BootPowerLimits(_etc);
        boot.SetGpu(Gpu1, 300);
        var dropIn = File.ReadAllText(DropIn);
        Assert.Contains("ExecStart=\n", dropIn); // replaces the unit's list rather than appending to it
        Assert.Contains("nvidia-smi -pm 1", dropIn);
        Assert.Contains($"-i {Gpu0} -pl 150", dropIn);
        Assert.Contains($"-i {Gpu1} -pl 300", dropIn);
        Assert.Contains($"-i {Gpu2} -pl 250", dropIn);
        Assert.Equal(new BootLimit(300_000, BootLimitSource.OpenHW), boot.GpuBoot()[Gpu1]);
        Assert.DoesNotContain("-pl 300", File.ReadAllText(Path.Combine(Units, "nvidia-power-limit.service"))); // unit untouched
    }

    [Fact]
    public void BackToTheUnitsValueRemovesTheDropIn()
    {
        var boot = new BootPowerLimits(_etc);
        boot.SetGpu(Gpu1, 300);
        boot.SetGpu(Gpu2, 200);
        boot.SetGpu(Gpu1, null);
        Assert.True(File.Exists(DropIn)); // Gpu2 still differs
        boot.SetGpu(Gpu2, null);
        Assert.False(File.Exists(DropIn));
        Assert.Equal(new BootLimit(250_000, BootLimitSource.BootService), boot.GpuBoot()[Gpu1]);
    }

    [Fact]
    public void WithoutTheBootServiceNothingIsWritten()
    {
        File.Delete(Path.Combine(Units, "nvidia-power-limit.service"));
        File.Delete(Path.Combine(Units, "hsmp-control-apply.service"));
        var boot = new BootPowerLimits(_etc);
        Assert.Throws<IOException>(() => boot.SetGpu(Gpu0, 160));
        Assert.Throws<IOException>(() => boot.SetCpu(200_000));
        Assert.False(File.Exists(DropIn));
        Assert.Equal(250_000, boot.CpuBootMW());
    }

    [Theory]
    [InlineData("cpupptboot 0")]
    [InlineData("cpupptboot 350000")]
    [InlineData("cpupptboot ../ppt_mw")]
    [InlineData("gpupowerboot GPU-1 200")]
    [InlineData("gpupowerboot GPU-12345678-1234-1234-1234-123456789abc 20000")]
    [InlineData("gpupowerboot GPU-12345678-1234-1234-1234-123456789abc 200")] // no such card
    public void HelperRejectsBadBootLimits(string line)
    {
        using var gpus = new GpuPowerLimits();
        var session = new HelperSession(new NoSmart(), gpuPower: gpus, boot: new BootPowerLimits(_etc));
        Assert.StartsWith("err", session.HandleLine(line));
        Assert.Equal(250_000, new BootPowerLimits(_etc).CpuBootMW());
        Assert.False(File.Exists(DropIn));
    }

    [Fact]
    public void HelperSetsTheCpuBootLimit()
    {
        var session = new HelperSession(new NoSmart(), boot: new BootPowerLimits(_etc));
        Assert.Equal("ok 200000", session.HandleLine("cpupptboot 200000"));
        Assert.Equal("ok none", session.HandleLine("cpupptboot none"));
    }

    private sealed class NoSmart : INvmeSmartReader
    {
        public byte[] Read(string controller, string identity) => throw new IOException("no drives");
        public void Dispose() { }
    }
}
