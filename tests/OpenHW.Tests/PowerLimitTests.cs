using OpenHW.Hardware;
using Xunit;

namespace OpenHW.Tests;

/// <summary>CPU socket limit against a fake amd_hsmp_hwmon tree; GPU commands only as far as validation goes.</summary>
public sealed class PowerLimitTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("openhw-hsmp").FullName;
    private string Hwmon => Path.Combine(_root, "hwmon9");
    private string Boot => Path.Combine(_root, "ppt_mw");

    public PowerLimitTests()
    {
        Directory.CreateDirectory(Hwmon);
        Directory.CreateDirectory(Path.Combine(_root, "hwmon0"));
        File.WriteAllText(Path.Combine(_root, "hwmon0", "name"), "nct6799\n");
        File.WriteAllText(Path.Combine(Hwmon, "name"), "amd_hsmp_hwmon\n");
        File.WriteAllText(Path.Combine(Hwmon, "power1_cap"), "250000000\n");
        File.WriteAllText(Path.Combine(Hwmon, "power1_cap_max"), "2000000000\n");
        File.WriteAllText(Path.Combine(Hwmon, "power1_input"), "91375000\n");
        File.WriteAllText(Boot, "250000\n");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private long CapUW => long.Parse(File.ReadAllText(Path.Combine(Hwmon, "power1_cap")));

    [Fact]
    public void ReadsLimitDrawAndBootValue()
    {
        var limit = new CpuPowerLimits(_root, Boot).Read()!;
        Assert.Equal((250_000, 2_000_000, 91_375L, 250_000L), (limit.LimitMW, limit.MaxMW, limit.DrawMW!.Value, limit.BootMW!.Value));
    }

    [Fact]
    public void SetsAndRestoresTheBootValue()
    {
        var cpu = new CpuPowerLimits(_root, Boot);
        Assert.Equal(350_000, cpu.Set(350_000));
        Assert.Equal(350_000_000, CapUW);
        Assert.Equal(250_000, cpu.Set(null));
        Assert.Equal(250_000_000, CapUW);
    }

    [Fact]
    public void RefusesValuesOutsideTheRange()
    {
        var cpu = new CpuPowerLimits(_root, Boot);
        Assert.Throws<ArgumentException>(() => cpu.Set(10_000));
        Assert.Throws<ArgumentException>(() => cpu.Set(2_500_000));
        Assert.Equal(250_000_000, CapUW);
    }

    [Fact]
    public void AClampedValueIsAnError()
    {
        // Firmware accepts the write but keeps its own value.
        var cpu = new CpuPowerLimits(_root, Boot, (path, _) => File.WriteAllText(path, "300000000\n"));
        Assert.Throws<IOException>(() => cpu.Set(400_000));
    }

    [Theory]
    [InlineData("cpuppt 0")]
    [InlineData("cpuppt -5")]
    [InlineData("cpuppt 99999999")]
    [InlineData("cpuppt 250000 now")]
    [InlineData("cpuppt ../../power1_cap")]
    [InlineData("gpupower GPU-1 150000")]
    [InlineData("gpupower /dev/nvidia0 150000")]
    [InlineData("gpupower GPU-12345678-1234-1234-1234-123456789abc 15x")]
    [InlineData("gpupower GPU-12345678-1234-1234-1234-123456789abc")]
    public void HelperRejectsMalformedLimits(string line)
    {
        var session = new HelperSession(new NoSmart(), null, new GpuPowerLimits(), new CpuPowerLimits(_root, Boot));
        Assert.StartsWith("err", session.HandleLine(line));
        Assert.Equal(250_000_000, CapUW);
    }

    [Fact]
    public void HelperSetsTheCpuLimit()
    {
        var session = new HelperSession(new NoSmart(), cpuPower: new CpuPowerLimits(_root, Boot));
        Assert.Equal("ok 300000", session.HandleLine("cpuppt 300000"));
        Assert.Equal("ok 250000", session.HandleLine("cpuppt boot"));
    }

    [Fact]
    public void UnknownGpuIsRefused()
    {
        using var gpus = new GpuPowerLimits();
        var session = new HelperSession(new NoSmart(), gpuPower: gpus);
        Assert.StartsWith("err", session.HandleLine("gpupower GPU-00000000-0000-0000-0000-000000000000 150000"));
    }

    private sealed class NoSmart : INvmeSmartReader
    {
        public byte[] Read(string controller, string identity) => throw new IOException("no drives");
        public void Dispose() { }
    }
}
