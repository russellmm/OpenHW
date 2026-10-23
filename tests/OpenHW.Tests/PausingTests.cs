using OpenHW.Hardware;
using Xunit;

namespace OpenHW.Tests;

public sealed class PausingTests
{
    [Fact]
    public void FrozenSensorsKeepTheirStatistics()
    {
        var stats = new SensorStatistics();
        stats.Record(new Dictionary<string, double?> { ["a"] = 10, ["b"] = 50 });
        stats.Record(new Dictionary<string, double?> { ["a"] = 20, ["b"] = 70 });
        var before = stats.Get("b");

        // "b" collapsed: not read, so absent from the sample — it must neither update nor disappear.
        stats.Record(new Dictionary<string, double?> { ["a"] = 30 }, frozen: new HashSet<string> { "b" });

        Assert.Equal(before, stats.Get("b"));
        Assert.Equal(30, stats.Get("a").Current);
        Assert.Equal(3, stats.Get("a").Samples);
    }

    [Fact]
    public void UnfrozenMissingSensorsAreDropped()
    {
        var stats = new SensorStatistics();
        stats.Record(new Dictionary<string, double?> { ["gone"] = 1 });
        stats.Record(new Dictionary<string, double?>());
        Assert.Equal(0, stats.Get("gone").Samples); // hot-unplugged sensors still disappear
    }

    [Fact]
    public void NetworkEngineSkipsUnrequestedInterfaces()
    {
        var root = Directory.CreateTempSubdirectory("openhw-net-").FullName;
        try
        {
            foreach (var iface in new[] { "enp1s0", "enp2s0" })
            {
                Directory.CreateDirectory(Path.Combine(root, iface, "device"));
                Directory.CreateDirectory(Path.Combine(root, iface, "statistics"));
                File.WriteAllText(Path.Combine(root, iface, "statistics", "rx_bytes"), "1000000");
                File.WriteAllText(Path.Combine(root, iface, "statistics", "tx_bytes"), "2000000");
            }
            using var engine = new NetworkEngine(root);
            var items = engine.Discover();
            var wanted = items.Where(i => i.Group == NetworkEngine.GroupKey("enp1s0")).Select(i => i.Id).ToHashSet();

            var readings = new Dictionary<string, double?>();
            engine.ReadInto(readings, wanted);

            Assert.Contains(readings.Keys, id => id.Contains("enp1s0"));
            Assert.DoesNotContain(readings.Keys, id => id.Contains("enp2s0"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void SystemEngineSkipsSectionsWithNothingRequested()
    {
        using var engine = new SystemEngine();
        var items = engine.Discover();
        var memoryOnly = items.Where(i => i.Id.StartsWith("openhw:mem:")).Select(i => i.Id).ToHashSet();

        var readings = new Dictionary<string, double?>();
        engine.ReadInto(readings, memoryOnly);

        Assert.Contains("openhw:mem:used", readings.Keys);
        Assert.DoesNotContain(readings.Keys, id => id.StartsWith("openhw:cpu:") || id.StartsWith("openhw:cstate:"));
    }
}
