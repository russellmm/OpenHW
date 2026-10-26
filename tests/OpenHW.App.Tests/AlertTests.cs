using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using OpenHW.App.Alerts;
using OpenHW.App.Sensors;
using Xunit;

namespace OpenHW.App.Tests;

public sealed class AlertTests
{
    private static AlertStore Store()
    {
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Directory.CreateTempSubdirectory("openhw-alerts-").FullName);
        return AlertStore.Load();
    }

    private static readonly Func<string, (string, string)> Describe = id => (id == "pump" ? "Water Pump+" : "CPU (Tctl/Tdie)", id == "pump" ? "RPM" : "°C");

    [Fact]
    public void FiresAfterTheDurationOncePerEpisodeAndSaysWhenItClears()
    {
        var store = Store();
        store.Add(new AlertRule { SensorId = "cpu", Condition = AlertCondition.Above, Threshold = 85, ForSeconds = 10 });
        var monitor = new AlertMonitor(store);
        double temp = 80;
        IReadOnlyList<AlertEvent> At(double t) => monitor.Evaluate(t, _ => temp, Describe);

        Assert.Empty(At(0));
        temp = 90;
        Assert.Empty(At(2));                // over, but not for 10 s yet
        Assert.Empty(At(8));
        var fired = Assert.Single(At(12));  // held 10 s
        Assert.True(fired.Firing);
        Assert.Equal("above 85.0 °C for 10 s", fired.Description);
        Assert.Equal("90.0 °C", fired.Value);
        Assert.Contains("cpu", monitor.AlertingSensors);
        Assert.Empty(At(14));               // still over: no repeat
        temp = 70;
        var cleared = Assert.Single(At(16));
        Assert.False(cleared.Firing);
        Assert.Empty(monitor.AlertingSensors);
        temp = 90;
        Assert.Empty(At(18));               // a new episode waits its 10 s again
        Assert.Single(At(28));
    }

    [Fact]
    public void ASpikeShorterThanTheDurationDoesntFire()
    {
        var store = Store();
        store.Add(new AlertRule { SensorId = "cpu", Threshold = 85, ForSeconds = 10 });
        var monitor = new AlertMonitor(store);
        Assert.Empty(monitor.Evaluate(0, _ => 95, Describe));
        Assert.Empty(monitor.Evaluate(5, _ => 60, Describe));
        Assert.Empty(monitor.Evaluate(11, _ => 95, Describe)); // the clock restarted at the dip
    }

    [Fact]
    public void BelowYesNoMissingReadingsAndSwitchingOff()
    {
        var store = Store();
        var pump = new AlertRule { SensorId = "pump", Condition = AlertCondition.Below, Threshold = 1000 };
        store.Add(pump);
        store.Add(new AlertRule { SensorId = "prochot", Condition = AlertCondition.Above, Threshold = 0.5 });
        var monitor = new AlertMonitor(store);
        double? rpm = 3500;
        Assert.Empty(monitor.Evaluate(0, id => id == "pump" ? rpm : 0, Describe));
        rpm = null;                                                       // not read this poll: nothing changes
        Assert.Empty(monitor.Evaluate(1, id => id == "pump" ? rpm : 0, Describe));
        rpm = 0;
        Assert.Equal("below 1,000 RPM", Assert.Single(monitor.Evaluate(2, id => id == "pump" ? rpm : 0, Describe)).Description);
        Assert.Equal("becomes Yes", store.Snapshot()[1].Describe("flag"));
        Assert.Single(monitor.Evaluate(3, id => id == "pump" ? rpm : 1, Describe)); // PROCHOT went Yes

        store.Update(() => pump.Enabled = false);                          // off: cleared quietly
        Assert.Empty(monitor.Evaluate(4, id => id == "pump" ? rpm : 1, Describe));
        Assert.DoesNotContain("pump", monitor.AlertingSensors);
        Assert.DoesNotContain("pump", monitor.WatchedSensors);
    }

    [Fact]
    public void RulesAreSaved()
    {
        var store = Store();
        store.Add(new AlertRule { SensorId = "pump", Condition = AlertCondition.Below, Threshold = 1000, ForSeconds = 30 });
        var loaded = Assert.Single(AlertStore.Load().Rules);
        Assert.Equal((AlertCondition.Below, 1000.0, 30), (loaded.Condition, loaded.Threshold, loaded.ForSeconds));
        Assert.Contains("\"Below\"", File.ReadAllText(store.Path)); // readable in the file
    }

    [AvaloniaFact]
    public void TheDialogAddsARuleForItsSensor()
    {
        var store = Store();
        using var hub = new SensorHub();
        hub.Sample();
        var item = hub.Inventory.First(i => i.Id == hub.CpuTemperatureId());
        var dialog = new AlertDialog(new AlertMonitor(store), hub, item);
        dialog.Show();
        Dispatcher.UIThread.RunJobs();
        var all = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(dialog).ToList();
        all.OfType<Avalonia.Controls.TextBox>().First(t => t.TemplatedParent is null).Text = "90"; // not a ComboBox's own (11.3 templates one in)
        all.OfType<Avalonia.Controls.Button>().Single(b => b.Content as string == "Add")
            .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        var rule = Assert.Single(store.Rules);
        Assert.Equal((item.Id, AlertCondition.Above, 90.0, 10), (rule.SensorId, rule.Condition, rule.Threshold, rule.ForSeconds));
        Dispatcher.UIThread.RunJobs();
        Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(dialog)?.Save(Path.Combine(Path.GetTempPath(), "openhw-alert-dialog.png"));
    }
}
