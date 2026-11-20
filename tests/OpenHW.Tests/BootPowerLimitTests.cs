using OpenHW.Hardware;
using Xunit;

namespace OpenHW.Tests;

/// <summary>Boot-time limits against a fake /etc shaped like the owner's: OpenHW's CPU value and nvidia-power-limit.service.</summary>
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

    private readonly string _sys = Directory.CreateTempSubdirectory("openhw-hsmp").FullName;
    private string CapFile => Path.Combine(_sys, "hwmon3", "power1_cap");

    // A fake amd_hsmp_hwmon at the 9975WX's firmware default (350 W).
    private CpuPowerLimits Cpu()
    {
        var dir = Path.Combine(_sys, "hwmon3");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "name"), "amd_hsmp_hwmon\n");
        if (!File.Exists(CapFile)) File.WriteAllText(CapFile, "350000000\n");
        File.WriteAllText(Path.Combine(dir, "power1_cap_max"), "2000000000\n");
        return new CpuPowerLimits(_sys, Path.Combine(_etc, "openhw", "cpu-ppt-mw"));
    }

    public BootPowerLimitTests()
    {
        Directory.CreateDirectory(Path.Combine(_etc, "openhw"));
        Directory.CreateDirectory(Path.Combine(Units, "nvidia-power-limit.service.d"));
        File.WriteAllText(Path.Combine(_etc, "openhw", "cpu-ppt-mw"), "250000\n");
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

    public void Dispose()
    {
        Directory.Delete(_etc, recursive: true);
        Directory.Delete(_sys, recursive: true);
    }

    [LinuxFact]
    public void ReadsTheBootServicesValues()
    {
        var boot = new BootPowerLimits(_etc);
        Assert.Equal(250_000, boot.CpuBootMW());
        var gpus = boot.GpuBoot(Trx50);
        Assert.Equal(new BootLimit(150_000, BootLimitSource.BootService), gpus[Ti]);
        Assert.Equal(new BootLimit(250_000, BootLimitSource.BootService), gpus[Pro2]);
    }

    [LinuxFact]
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

    [LinuxFact]
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

    [LinuxFact]
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

    [LinuxFact]
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

    [LinuxFact]
    public void WithoutTheBootServiceNothingIsWritten()
    {
        File.Delete(Path.Combine(Units, "nvidia-power-limit.service"));
        var boot = new BootPowerLimits(_etc);
        Assert.Throws<IOException>(() => boot.SetGpu(Ti, 160));
        Assert.False(File.Exists(DropIn));
        boot.SetCpu(200_000, 2_000_000); // the CPU's needs no boot service: openhw-helper applies it
        Assert.Equal(200_000, boot.CpuBootMW());
    }

    [LinuxTheory]
    [InlineData("cpupptboot 0")]
    [InlineData("cpupptboot 40000")] // below 50 W
    [InlineData("cpupptboot 2500000")] // above the SMU's maximum
    [InlineData("cpupptboot ../ppt_mw")]
    [InlineData("gpupowerboot GPU-1 200")]
    [InlineData("gpupowerboot GPU-12345678-1234-1234-1234-123456789abc 20000")]
    [InlineData("gpupowerboot GPU-12345678-1234-1234-1234-123456789abc 200")] // no such card
    public void HelperRejectsBadBootLimits(string line)
    {
        using var gpus = new GpuPowerLimits();
        var session = new HelperSession(new NoSmart(), gpuPower: gpus, cpuPower: Cpu(), boot: new BootPowerLimits(_etc));
        Assert.StartsWith("err", session.HandleLine(line));
        Assert.Equal(250_000, new BootPowerLimits(_etc).CpuBootMW());
        Assert.False(File.Exists(DropIn));
    }

    [LinuxFact]
    public void HelperSetsTheCpuBootLimit()
    {
        var session = new HelperSession(new NoSmart(), cpuPower: Cpu(), boot: new BootPowerLimits(_etc));
        Assert.Equal("ok 200000", session.HandleLine("cpupptboot 200000"));
        Assert.Equal("ok 350000", session.HandleLine("cpupptboot 350000")); // above hsmp-control's old 300 W ceiling
        Assert.Equal("ok none", session.HandleLine("cpupptboot none"));
        Assert.StartsWith("err", new HelperSession(new NoSmart(), boot: new BootPowerLimits(_etc)).HandleLine("cpupptboot 200000")); // no HSMP: no range to check
    }

    [LinuxFact]
    public void TheHelperAppliesTheCpuBootLimitOncePerBoot()
    {
        var boot = new BootPowerLimits(_etc);
        var cpu = Cpu();
        var marker = Path.Combine(_sys, "cpu-ppt-applied");
        Assert.Contains("250", boot.ApplyCpuOncePerBoot(cpu, marker));
        Assert.Equal("250000000", File.ReadAllText(CapFile).Trim());

        // A helper restart later this boot leaves a live limit the owner set alone.
        cpu.Set(300_000);
        Assert.Contains("already", boot.ApplyCpuOncePerBoot(cpu, marker));
        Assert.Equal("300000000", File.ReadAllText(CapFile).Trim());

        // Next boot (marker gone): the boot value again. None set: firmware's stays.
        File.Delete(marker);
        boot.ApplyCpuOncePerBoot(cpu, marker);
        Assert.Equal("250000000", File.ReadAllText(CapFile).Trim());
        File.Delete(marker);
        boot.SetCpu(null, 2_000_000);
        Assert.Contains("firmware", boot.ApplyCpuOncePerBoot(cpu, marker));
        Assert.False(File.Exists(marker));

        // No HSMP driver yet: it throws (the helper retries) and leaves no marker.
        boot.SetCpu(250_000, 2_000_000);
        Directory.Delete(Path.Combine(_sys, "hwmon3"), recursive: true);
        Assert.Throws<IOException>(() => boot.ApplyCpuOncePerBoot(cpu, marker));
        Assert.False(File.Exists(marker));
    }

    private sealed class NoSmart : INvmeSmartReader
    {
        public byte[] Read(string controller, string identity) => throw new IOException("no drives");
        public void Dispose() { }
    }
}
