using OpenHW.Hardware;
using Xunit;

namespace OpenHW.Tests;

public sealed class GpuFanDriverTests
{
    private const string Uuid = "GPU-6d3eab54-984e-f1bc-68eb-ee4948e6c3b0";
    private static readonly string Fan0 = GpuFanDriver.FanId(Uuid, 0), Fan1 = GpuFanDriver.FanId(Uuid, 1);
    private const string A = "0123456789abcdef0123456789abcdef", B = "fedcba9876543210fedcba9876543210";

    private sealed class FakeNvml : INvmlFans
    {
        public readonly Dictionary<(string, int), int?> Manual = []; // null = automatic
        public bool Present = true;
        public int Refuse;
        public IReadOnlyList<GpuFanCard> Cards() => Present ? [new GpuFanCard(Uuid, "NVIDIA RTX PRO 6000", "00000000:C1:00.0", 2, 30, 100)] : [];
        public int SetManual(string uuid, int fan, int percent) { if (Refuse != 0) return Refuse; Manual[(uuid, fan)] = percent; return 0; }
        public int SetDefault(string uuid, int fan) { Manual[(uuid, fan)] = null; return 0; }
    }

    [Fact]
    public void AClientHoldsAFanUntilItReleasesOrStopsRenewing()
    {
        var nvml = new FakeNvml();
        var now = new DateTime(2026, 11, 17, 12, 0, 0, DateTimeKind.Utc);
        using var driver = new GpuFanDriver(nvml, () => now, () => null);
        Assert.Equal($"{Fan0} 30", driver.Set(A, Fan0, 10)); // clamped to the card's minimum
        Assert.Equal(30, nvml.Manual[(Uuid, 0)]);
        Assert.Equal($"{Fan0} 0", driver.Set(A, Fan0, 0)); // 0 = the card's own control
        Assert.Null(nvml.Manual[(Uuid, 0)]);
        driver.Set(A, Fan1, 60);
        Assert.Throws<ArgumentException>(() => driver.Set(B, Fan1, 70));
        var status = driver.Status(A);
        Assert.Equal(2, status.Fans.Count);
        Assert.True(status.Fans[1].OwnedByYou);
        Assert.Equal(60, status.Fans[1].Target);

        now = now.AddSeconds(10);
        Assert.Equal(2, driver.Beat(A));
        now = now.AddSeconds(14);
        driver.Tick();
        Assert.Equal(60, nvml.Manual[(Uuid, 1)]); // renewed 14 s ago: still held
        now = now.AddSeconds(2);
        driver.Tick();
        Assert.Null(nvml.Manual[(Uuid, 1)]); // lease gone: automatic again
        Assert.False(driver.Status(null).Fans[1].Owned);

        driver.Set(B, Fan1, 50);
        Assert.Equal(Fan1, driver.Release(B, "all"));
        Assert.Null(nvml.Manual[(Uuid, 1)]);
    }

    [Fact]
    public void RefusedWhileOpenFanRunsOrACardIsLentAndRestoredOnStop()
    {
        var nvml = new FakeNvml();
        using (var openfan = new GpuFanDriver(nvml, otherController: () => "OpenFan's helper is running"))
            Assert.Throws<ArgumentException>(() => openfan.Set(A, Fan0, 50));

        var driver = new GpuFanDriver(nvml, otherController: () => null);
        driver.Set(A, Fan0, 50);
        driver.Suspend(); // a lend: held fans back to automatic first
        Assert.Null(nvml.Manual[(Uuid, 0)]);
        Assert.Throws<ArgumentException>(() => driver.Set(A, Fan0, 50));
        driver.Resume();
        nvml.Refuse = 3;
        Assert.Throws<IOException>(() => driver.Set(A, Fan0, 50));
        nvml.Refuse = 0;
        driver.Set(A, Fan0, 70);
        driver.Dispose(); // the helper stops
        Assert.Null(nvml.Manual[(Uuid, 0)]);
        Assert.Throws<ArgumentException>(() => new GpuFanDriver(nvml, otherController: () => null).Set(A, "nvml:GPU-x:fan:0", 50));
    }

    [Fact]
    public void HelperCommands()
    {
        var nvml = new FakeNvml();
        using var driver = new GpuFanDriver(nvml, otherController: () => null);
        var session = new HelperSession(new NoSmart(), gpuFans: driver);
        Assert.Equal("ok 17", session.HandleLine("ping"));
        Assert.Equal($"ok {Fan0} 55", session.HandleLine($"gpufanset {A} {Fan0} 55"));
        Assert.StartsWith("err", session.HandleLine($"gpufanset {A} {Fan0} 101"), StringComparison.Ordinal);
        Assert.StartsWith("err", session.HandleLine($"gpufanset nottoken {Fan0} 50"), StringComparison.Ordinal);
        Assert.StartsWith("ok ", session.HandleLine($"gpufans {A}"), StringComparison.Ordinal);
        Assert.Equal($"ok {Fan0}", session.HandleLine($"gpufanrelease {A} {Fan0}"));
    }

    private sealed class NoSmart : INvmeSmartReader
    {
        public byte[] Read(string controller, string identity) => [];
        public void Dispose() { }
    }
}
