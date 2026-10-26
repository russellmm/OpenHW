using OpenHW.Hardware;
using Xunit;

namespace OpenHW.Tests;

/// <summary>GPU readings travel from the helper's NVML engine to the app as base64 JSON; the app never opens a card.</summary>
public sealed class HelperGpuTests
{
    private static HardwareItem Item(string id, string name) => new()
    {
        Id = id, Name = name, Kind = HardwareKind.Temperature, Unit = "°C", Backend = "nvml",
        Group = "RTX PRO 6000 · E1:00.0", CanSet = false, Details = "nvml · temp",
    };

    private sealed class FakeEngine : ISensorEngine
    {
        public int Discoveries, Reads;
        public List<HardwareItem> Items = [Item("nvml:gpu0:temp:core", "GPU Temperature")];
        public string Name => "fake";
        public bool Available => true;
        public IReadOnlyList<HardwareItem> Discover() { Discoveries++; return Items; }
        public void ReadInto(IDictionary<string, double?> readings, IReadOnlySet<string>? requested)
        {
            Reads++;
            readings["nvml:gpu0:temp:core"] = 41.5;
            readings["nvml:gpu0:metric:power"] = double.NaN; // must survive the JSON trip
        }
        public void Dispose() { }
    }

    private sealed class NoSmart : INvmeSmartReader
    {
        public byte[] Read(string controller, string identity) => throw new IOException("no drives");
        public void Dispose() { }
    }

    [Fact]
    public void TheHelperServesRowsAndReadingsThatTheAppEngineUnpacks()
    {
        var fake = new FakeEngine();
        var session = new HelperSession(new NoSmart(), gpus: new SharedGpuEngine(fake));
        string Ask(string line)
        {
            var reply = session.HandleLine(line);
            Assert.StartsWith("ok ", reply);
            Assert.DoesNotContain('\n', reply);
            Assert.True(reply.All(c => c < 128)); // one ASCII line, though units are "°C"
            return reply[3..];
        }
        var engine = new HelperGpuEngine(Ask);
        var items = engine.Discover();
        Assert.Equal("GPU Temperature", Assert.Single(items).Name);
        Assert.Equal("°C", items[0].Unit);
        Assert.Equal("RTX PRO 6000 · E1:00.0", items[0].Group);
        var readings = new Dictionary<string, double?>();
        engine.ReadInto(readings, new HashSet<string> { "nvml:gpu0:temp:core" });
        Assert.Equal(41.5, readings["nvml:gpu0:temp:core"]);
        Assert.False(readings.ContainsKey("nvml:gpu0:metric:power")); // not requested
        Assert.Equal("err unknown command", session.HandleLine("gpuread 0"));
    }

    [Fact]
    public void TheHelperSharesOneNvmlScanBetweenClients()
    {
        var fake = new FakeEngine();
        var shared = new SharedGpuEngine(fake);
        shared.Read(); // discovers first: NVML handles come from discovery
        shared.Discover();
        shared.Read();
        Assert.Equal(1, fake.Discoveries);
        Assert.Equal(1, fake.Reads);
    }

    [Fact]
    public void WhileTheHelperIsAwayTheRowsStayWithoutReadings()
    {
        var up = true;
        var session = new HelperSession(new NoSmart(), gpus: new SharedGpuEngine(new FakeEngine()));
        var engine = new HelperGpuEngine(line => up ? session.HandleLine(line)[3..] : throw new IOException("helper restarting"));
        Assert.Single(engine.Discover());
        up = false;
        Assert.Single(engine.Discover()); // the card's rows go stale, not away
        var readings = new Dictionary<string, double?>();
        engine.ReadInto(readings, null);
        Assert.Empty(readings);
    }

    [Fact]
    public void PowerLimitsComeFromTheHelper()
    {
        var limits = new List<GpuPowerLimit> { new("GPU-1", "RTX PRO 6000", "00000000:E1:00.0", 300_000, 600_000, 150_000, 600_000, 41_000) };
        var reader = new HelperGpuPowerLimits(line => line == "gpulimits" ? GpuWire.Encode(limits) : throw new IOException());
        Assert.Equal(limits, reader.Read());
        Assert.Empty(new HelperGpuPowerLimits(_ => throw new IOException("no helper")).Read());
    }
}
