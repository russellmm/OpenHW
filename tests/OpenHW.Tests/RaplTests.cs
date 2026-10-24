using OpenHW.Hardware;
using Xunit;

namespace OpenHW.Tests;

public sealed class RaplTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("openhw-powercap").FullName;

    public RaplTests()
    {
        Domain("intel-rapl:0", "package-0", 1_000_000, 65_532_610_987);
        Domain("intel-rapl:0:0", "core", 400_000, 65_532_610_987);
        Domain("intel-rapl:1", "psys", 5, 10); // not a domain OpenHW reads
    }

    private void Domain(string dir, string name, ulong energy, ulong range)
    {
        var d = Path.Combine(_root, dir);
        Directory.CreateDirectory(d);
        File.WriteAllText(Path.Combine(d, "name"), name + "\n");
        File.WriteAllText(Path.Combine(d, "energy_uj"), energy + "\n");
        File.WriteAllText(Path.Combine(d, "max_energy_range_uj"), range + "\n");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void ReadsPackageAndCoreOnly()
    {
        var counters = new RaplReader(_root).Read();
        Assert.Equal(["package-0", "core"], counters.Select(c => c.Domain));
        Assert.Equal(counters, RaplReader.Parse(RaplReader.Format(counters)));
    }

    [Fact]
    public void HelperServesTheCountersAndTakesNoArguments()
    {
        var session = new HelperSession(new NoSmart(), rapl: new RaplReader(_root));
        Assert.Equal("ok package-0 1000000 65532610987;core 400000 65532610987", session.HandleLine("rapl"));
        Assert.StartsWith("err", session.HandleLine("rapl /sys/class/powercap"));
        Assert.StartsWith("err", new HelperSession(new NoSmart()).HandleLine("rapl"));
    }

    [Fact]
    public void PowerIsEnergyOverTimeIncludingAWrap()
    {
        var replies = new Queue<string>(["package-0 900000000 1000000000;core 100 1000000000",
                                         "package-0 900000000 1000000000;core 100 1000000000",
                                         "package-0 950000000 1000000000;core 100 1000000000",
                                         "package-0 10000000 1000000000;core 100 1000000000"]);
        var engine = new RaplEngine(() => replies.Dequeue());
        Assert.Equal(["rapl:package", "rapl:cores"], engine.Discover().Select(i => i.Id)); // first reply

        var r = new Dictionary<string, double?>();
        engine.ReadInto(r, null);
        Assert.Null(r["rapl:package"]); // no interval yet
        Thread.Sleep(200);
        engine.ReadInto(r, null);
        Assert.InRange(r["rapl:package"]!.Value, 50 / 0.5, 50 / 0.15); // 50 J over ~0.2 s
        Thread.Sleep(200);
        engine.ReadInto(r, null);
        Assert.InRange(r["rapl:package"]!.Value, 60 / 0.5, 60 / 0.15); // wrapped: 50 J to the top + 10 J after
    }

    [Fact]
    public void NoHelperMeansNoRows()
    {
        var engine = new RaplEngine(() => throw new IOException("no helper"));
        Assert.Empty(engine.Discover());
        Assert.False(engine.Available);
    }

    private sealed class NoSmart : INvmeSmartReader
    {
        public byte[] Read(string controller, string identity) => throw new IOException("no drives");
        public void Dispose() { }
    }
}
