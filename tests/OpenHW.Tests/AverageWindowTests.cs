using OpenHW.Hardware;
using Xunit;

namespace OpenHW.Tests;

/// <summary>Sensor Settings → Averaging Interval: "Average last N samples", 0 = the whole run.</summary>
public sealed class AverageWindowTests
{
    private static void Record(SensorStatistics stats, double value) => stats.Record(new Dictionary<string, double?> { ["a"] = value });

    [Fact]
    public void ZeroAveragesEverySampleSinceReset()
    {
        var stats = new SensorStatistics();
        foreach (var v in new[] { 10.0, 20, 30, 40 }) Record(stats, v);
        Assert.Equal(25, stats.Get("a").Average);
    }

    [Fact]
    public void NAveragesTheLastNSamplesOnly()
    {
        var stats = new SensorStatistics { AverageSamples = 2 };
        Record(stats, 10);
        Assert.Equal(10, stats.Get("a").Average);
        Record(stats, 20);
        Record(stats, 40);
        Assert.Equal(30, stats.Get("a").Average);            // (20 + 40) / 2
        Assert.Equal((10.0, 40.0), (stats.Get("a").Minimum!.Value, stats.Get("a").Maximum!.Value)); // min/max still the whole run
    }

    [Fact]
    public void ChangingNStartsTheWindowAfresh()
    {
        var stats = new SensorStatistics { AverageSamples = 3 };
        foreach (var v in new[] { 10.0, 20, 30 }) Record(stats, v);
        stats.AverageSamples = 2;
        Record(stats, 100);
        Assert.Equal(100, stats.Get("a").Average);
        Record(stats, 50);
        Assert.Equal(75, stats.Get("a").Average);
    }
}
