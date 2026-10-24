using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using OpenHW.App.Sensors;
using OpenHW.App.SensorWindow;
using Xunit;

namespace OpenHW.App.Tests;

public sealed class GraphTests
{
    [Fact]
    public void HistoryKeepsTheNewestPointsInOrderAndSkipsWhatWasntRead()
    {
        var history = new SensorHistory(TimeSpan.FromMinutes(10)); // a tiny ring: 30 min / 10 min + 2 = 5 points
        for (var i = 1; i <= 8; i++) history.Record([("a", i), ("b", double.NaN)]);
        Assert.Equal([4, 5, 6, 7, 8], history.Get("a").Select(p => p.Value));
        Assert.All(history.Get("a"), p => Assert.True(p.Age >= 0));
        Assert.Empty(history.Get("b"));      // not a number: nothing recorded
        Assert.Empty(history.Get("never"));  // never read
    }

    [AvaloniaFact]
    public void AGraphShowsTheSensorsReadings()
    {
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Directory.CreateTempSubdirectory("openhw-graph-").FullName);
        using var hub = new SensorHub();
        for (var i = 0; i < 4; i++) { hub.Sample(); Thread.Sleep(300); }
        var item = hub.Inventory.First(i => i.Id == hub.CpuTemperatureId());
        Assert.NotEmpty(hub.History.Get(item.Id));

        var window = new SensorGraphWindow(hub, item);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.CaptureRenderedFrame()?.Save(Path.Combine(Path.GetTempPath(), "openhw-graph.png"));
        Assert.StartsWith("OpenHW — CPU (Tctl/Tdie)", window.Title);
        Assert.Equal(item.Id, window.SensorId);
    }
}
