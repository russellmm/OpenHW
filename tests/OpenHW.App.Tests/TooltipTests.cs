using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OpenHW.App.Sensors;
using OpenHW.App.SensorWindow;
using OpenHW.Hardware;
using Xunit;

namespace OpenHW.App.Tests;

public sealed class TooltipTests
{
    private static HardwareItem Rail(string pin, string name, double scale, bool confirmed) => new()
    {
        Id = $"hwmon:nct6799:0:in:{pin[2..]}", Name = name, Kind = HardwareKind.Voltage, Backend = "hwmon",
        Group = "nct6799 (hwmon0)", Unit = "V", Details = $"libsensors · {pin}_input",
        OriginalName = pin, Scale = scale, MappingConfirmed = confirmed,
    };

    [Fact]
    public void RenamedRailShowsOriginalInputAndScale()
    {
        var tip = SensorPresentation.Tooltip(Rail("in1", "+5V", 5, confirmed: true));
        Assert.Contains("Board input: in1 on nct6799 (hwmon0), raw reading × 5", tip);
        Assert.DoesNotContain("Unconfirmed", tip);
        Assert.EndsWith("Sensor ID: hwmon:nct6799:0:in:1", tip);
    }

    [Fact]
    public void UnconfirmedMappingIsCalledOutFirst()
    {
        var lines = SensorPresentation.Tooltip(Rail("in6", "CPU Core1", 1.11, confirmed: false)).Split('\n');
        Assert.Equal("CPU Core1", lines[0]);
        Assert.StartsWith("Unconfirmed mapping:", lines[1]);
        Assert.Contains("Board input: in6 on nct6799 (hwmon0), raw reading × 1.11", lines);
    }

    [Fact]
    public void RawUnlabeledVoltageIsCalledOutAsUnmapped()
    {
        var raw = new HardwareItem
        {
            Id = "hwmon:it8689:3:in:2", Name = "in2", Kind = HardwareKind.Voltage, Backend = "hwmon",
            Group = "it8689 (hwmon3)", Unit = "V", Details = "libsensors · in2_input" + BoardProfiles.UnmappedRailNote,
        };
        var tip = SensorPresentation.Tooltip(raw);
        Assert.Contains("Unmapped input:", tip);
        Assert.DoesNotContain(BoardProfiles.UnmappedRailNote, tip);
    }

    [Fact]
    public void PresentationRenamesShowWhatTheHardwareReported()
    {
        var tctl = new HardwareItem
        {
            Id = "hwmon:k10temp:8:temp:1", Name = "Tctl", Kind = HardwareKind.Temperature, Backend = "hwmon",
            Group = "k10temp (hwmon8)", Unit = "°C", Details = "libsensors · temp1_input",
        };
        Assert.Contains("Reported as: Tctl", SensorPresentation.Tooltip(tctl));
    }

    // Every row in the real window carries a tooltip.
    [AvaloniaFact]
    public void EveryShownSensorHasATooltip()
    {
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Directory.CreateTempSubdirectory("openhw-view-").FullName);
        using var hub = new SensorHub();
        hub.Sample();
        var window = new MainWindow(hub) { Width = 820, Height = 3000 };
        window.Show();
        window.SetAllExpanded(true);
        Dispatcher.UIThread.RunJobs();

        var rows = window.GetVisualDescendants().OfType<Grid>().Where(g => g.DataContext is SensorRow && ToolTip.GetTip(g) is not null).ToList();
        Assert.NotEmpty(rows);
        Assert.All(rows, g => Assert.Contains("Sensor ID: ", (string)ToolTip.GetTip(g)!));
    }

    [Fact]
    public void UnmappedSuperIoFanIsCalledOut()
    {
        var fan = new HardwareItem
        {
            Id = "hwmon:nct6799:0:fan:3", Name = "fan3", Kind = HardwareKind.Tach, Backend = "hwmon",
            Group = "nct6799 (hwmon0)", Unit = "RPM", Details = "libsensors · fan3_input",
        };
        Assert.Contains("Unmapped input:", SensorPresentation.Tooltip(fan));
    }
}

// Every row this machine shows should say what it measures, not just fall back to "A temperature sensor."
public sealed class ExplanationCoverageTests
{
    [Xunit.Fact]
    public void EveryShownSensorHereHasASpecificExplanation()
    {
        using var hub = new Sensors.SensorHub();
        hub.Sample();
        var generic = new HashSet<string> { "", "A temperature sensor.", "A voltage measured by the board.", "A fan or pump speed.",
            "A power reading.", "A clock frequency.", "A utilisation reading." };
        var missing = hub.Inventory.Where(Sensors.SensorPresentation.IsShown)
            .Select(i => (Item: i, Name: Sensors.SensorPresentation.BuiltInName(i)))
            .Where(x => generic.Contains(Sensors.SensorExplanations.Explain(x.Item, x.Name)))
            .Select(x => x.Name).Distinct().Order().ToList();
        Xunit.Assert.True(missing.Count == 0, "No explanation for: " + string.Join(", ", missing));
    }
}
