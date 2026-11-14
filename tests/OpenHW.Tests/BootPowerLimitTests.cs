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
    private const string Ti = "GPU-551a52fc-c5ce-c335-e0d7-81fc00948a65", Pro1 = "GPU-d0f36b3b-abad-1a42-0e65-e78da2139da3", Pro2 = "GPU-6d3eab54-984e-f1bc-68eb-ee4948e6c3b0";

    private static GpuPowerLimit Card(string uuid, string bus) => new(uuid, "card", bus, 0, 0, 0, 600_000, null);

    // The TRX50 layout the unit was written for, and the WRX90E's after the board swap (same cards, other slots).
    private static readonly GpuPowerLimit[] Trx50 = [Card(Ti, Gpu0), Card(Pro1, Gpu1), Card(Pro2, Gpu2)];
    private static readonly GpuPowerLimit[] Wrx90 = [Card(Ti, "00000000:11:00.0"), Card(Pro2, "00000000:C1:00.0"), Card(Pro1, "00000000:F1:00.0")];

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
        var gpus = boot.GpuBoot(Trx50);
        Assert.Equal(new BootLimit(150_000, BootLimitSource.BootService), gpus[Ti]);
        Assert.Equal(new BootLimit(250_000, BootLimitSource.BootService), gpus[Pro2]);
    }

    [Fact]
    public void AGpuBootLimitNamesTheCardNotItsSlot()
    {
        var boot = new BootPowerLimits(_etc);
        boot.SetGpu(Pro1, 300);
        var dropIn = File.ReadAllText(DropIn);
        Assert.Contains("ExecStart=\n", dropIn); // replaces the unit's list rather than appending to it
        Assert.Contains("ExecStart=-/usr/bin/nvidia-smi -pm 1", dropIn);
        Assert.Contains($"ExecStart=-/usr/bin/nvidia-smi -i {Gpu0} -pl 150", dropIn); // the unit's lines, each allowed to fail
        Assert.Contains($"ExecStart=-/usr/bin/nvidia-smi -i {Pro1} -pl 300", dropIn);
        Assert.True(dropIn.IndexOf($"-i {Gpu1} -pl 250", StringComparison.Ordinal) < dropIn.IndexOf($"-i {Pro1}", StringComparison.Ordinal)); // OpenHW's line wins
        Assert.Equal(new BootLimit(300_000, BootLimitSource.OpenHW), boot.GpuBoot(Trx50)[Pro1]);
        Assert.DoesNotContain("-pl 300", File.ReadAllText(Path.Combine(Units, "nvidia-power-limit.service"))); // unit untouched
        Assert.Throws<ArgumentException>(() => boot.SetGpu(Gpu1, 200)); // never by slot again
    }

    [Fact]
    public void AfterABoardSwapOpenHWsLimitsFollowTheCardsAndTheUnitsGoBySlot()
    {
        var boot = new BootPowerLimits(_etc);
        boot.SetGpu(Ti, 150);
        boot.SetGpu(Pro1, 300);
        var gpus = boot.GpuBoot(Wrx90);
        Assert.Equal(new BootLimit(150_000, BootLimitSource.OpenHW), gpus[Ti]);   // now at 11:00.0, still 150 W
        Assert.Equal(new BootLimit(300_000, BootLimitSource.OpenHW), gpus[Pro1]); // now at F1:00.0
        Assert.False(gpus.ContainsKey(Pro2)); // C1:00.0: no line for it (the unit's E1 line has nothing in its slot)
        Assert.Equal(3, boot.SlotLines().Count);
    }

    [Fact]
    public void AnOldSlotDropInIsReplacedAtTheNextWrite()
    {
        File.WriteAllText(DropIn, $"[Service]\nExecStart=\nExecStart=/usr/bin/nvidia-smi -i {Gpu1} -pl 300\n");
        var boot = new BootPowerLimits(_etc);
        Assert.Contains(boot.SlotLines(), l => l.OpenHW && l.Bus == Gpu1);
        Assert.Equal(new BootLimit(300_000, BootLimitSource.OpenHW), boot.GpuBoot(Wrx90)[Ti]); // the 5060 Ti now sits at 11:00.0
        boot.SetGpu(Pro2, 280);
        Assert.DoesNotContain(boot.SlotLines(), l => l.OpenHW);
        Assert.Equal(280, boot.CardLines()[Pro2]);
    }

    [Fact]
    public void BackToTheUnitsValueRemovesTheDropIn()
    {
        var boot = new BootPowerLimits(_etc);
        boot.SetGpu(Pro1, 300);
        boot.SetGpu(Pro2, 200);
        boot.SetGpu(Pro1, null);
        Assert.True(File.Exists(DropIn)); // Pro2 still set
        boot.SetGpu(Pro2, null);
        Assert.False(File.Exists(DropIn));
        Assert.Equal(new BootLimit(250_000, BootLimitSource.BootService), boot.GpuBoot(Trx50)[Pro1]);
    }

    [Fact]
    public void WithoutTheBootServiceNothingIsWritten()
    {
        File.Delete(Path.Combine(Units, "nvidia-power-limit.service"));
        File.Delete(Path.Combine(Units, "hsmp-control-apply.service"));
        var boot = new BootPowerLimits(_etc);
        Assert.Throws<IOException>(() => boot.SetGpu(Ti, 160));
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
