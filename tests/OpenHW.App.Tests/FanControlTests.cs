using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using OpenHW.App.Power;
using OpenHW.App.Sensors;
using OpenHW.Hardware;
using Xunit;

namespace OpenHW.App.Tests;

public sealed class FanControlTests
{
    private static FanDriverStatus Status(string? other = null) => new(true, null, other, new DateTime(2026, 11, 16, 20, 0, 0, DateTimeKind.Utc),
        Enumerable.Range(0, AsusBmcFans.ZoneCount).Select(i => new FanZoneStatus($"bmc:{i}", AsusBmcFans.Sensors[i], AsusBmcFans.Labels[i],
            "bmc:" + AsusBmcFans.Sensors[i], i == 1, false, i == 1 ? 70 : null, i == 1 ? 60 : null,
            [new(20, i == 1 ? 70 : 20), new(45, i == 1 ? 70 : 40), new(65, 70), new(70, 100), new(100, 100)], null, i == 1 ? 2 : 0, null)).ToList());

    // The helper as the window sees it: fans, sources (CHA_FAN1 on DIMMA1 + DIMMB1), login, BMC sensors.
    internal static string Helper(string line) => line.Split(' ')[0] switch
    {
        "fans" => GpuWire.Encode(Status()),
        "fansources" => GpuWire.Encode(new Dictionary<int, long> { [0] = 1, [1] = 24, [2] = 1, [3] = 1, [4] = 1, [5] = 1, [6] = 1 }),
        "bmclogin" => "openhw",
        "gpufans" => GpuWire.Encode(new GpuFanDriverStatus(null, [
            new("nvml:GPU-6d3eab54-984e-f1bc-68eb-ee4948e6c3b0:fan:0", "NVIDIA RTX PRO 6000 Blackwell Workstation Edition", "00000000:C1:00.0", 0, 30, 100, false, false, null),
            new("nvml:GPU-6d3eab54-984e-f1bc-68eb-ee4948e6c3b0:fan:1", "NVIDIA RTX PRO 6000 Blackwell Workstation Edition", "00000000:C1:00.0", 1, 30, 100, false, false, null)])),
        "bmc" => GpuWire.Encode(new BmcWireReply("AST2600", "2.01", 2623, 0x1193,
            new[] { (1, "CPU Package Temp"), (2, "TR1 Temperature"), (3, "LAN Temp"), (4, "DIMMA1_Temp"), (5, "DIMMB1_Temp"), (12, "PCIE01 Temp") }
                .Select(x => new BmcWireSensor(x.Item2, (byte)x.Item1, "Board", HardwareKind.Temperature, "°C", null, 40)).ToList())),
        _ => throw new IOException("openhw-helper: err unknown command"),
    };

    [AvaloniaFact]
    public void OneScreenShowsEveryBmcAndGpuFan()
    {
        using var hub = new SensorHub();
        var window = new FanControlWindow(hub, runner: new FanCurveRunner(new FanClient(Helper), FanCurveStore.Load(Path.Combine(Path.GetTempPath(), $"openhw-none-{Guid.NewGuid():N}.json"))));
        window.Show();
        var texts = window.Texts();
        Assert.Contains("CPU_FAN / CPU_OPT", texts);
        Assert.Contains("W_PUMP+", texts);
        Assert.Contains("RTX PRO 6000 C1:00.0 Fan 1", texts);
        Assert.Contains("The card's own control", texts);
        Assert.Contains("70 % (held)", texts);
        Assert.Contains(texts, t => t.StartsWith("BMC curve, follows max of DIMMA1_Temp, DIMMB1_Temp", StringComparison.Ordinal));
        Assert.Contains(texts, t => t.Contains("BMC login: openhw", StringComparison.Ordinal));
        Assert.Contains("No curves yet: + Flat, + Graph or + Mix.", texts);
        var live = window.LiveSensorIds();
        Assert.Contains("bmc:CHA_FAN1", live); // a BMC fan's RPM
        Assert.Contains("nvml:GPU-6d3eab54-984e-f1bc-68eb-ee4948e6c3b0:tach:1", live); // a GPU fan's RPM
        Assert.Contains("bmc:PCIE01 Temp", live);
        window.CaptureRenderedFrame()?.Save(Path.Combine(Environment.GetEnvironmentVariable("OPENHW_CAPTURES") ?? Path.GetTempPath(), "openhw-fan-control.png"));
        window.Close();
    }

    [AvaloniaFact]
    public void SaysWhenOpenFanHoldsTheFansOrTheHelperIsTooOld()
    {
        using var hub = new SensorHub();
        var store = FanCurveStore.Load(Path.Combine(Path.GetTempPath(), $"openhw-none-{Guid.NewGuid():N}.json"));
        var held = new FanControlWindow(hub, runner: new FanCurveRunner(new FanClient(l => l.StartsWith("fans", StringComparison.Ordinal) && l.Length == 4
            ? GpuWire.Encode(Status("OpenFan is controlling BMC fans right now.")) : throw new IOException("openhw-helper: err unknown command")), store));
        Assert.Contains(held.Texts(), t => t.Contains("OpenFan is controlling", StringComparison.Ordinal));
        var old = new FanControlWindow(hub, runner: new FanCurveRunner(new FanClient(_ => throw new IOException("openhw-helper: err unknown command")), store));
        Assert.Contains(old.Texts(), t => t.Contains("too old for fan control", StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public void TheCurveDialogSendsOnlyWhatChanged()
    {
        var sent = new List<string>();
        var client = new FanClient(l => { sent.Add(l); return l.StartsWith("fancurve", StringComparison.Ordinal) || l.StartsWith("fansource", StringComparison.Ordinal) ? "done" : Helper(l); });
        var zone = Status().Zones[2];
        var sensors = client.SourceSensors();
        var dialog = new FanCurveDialog(client, zone, 1, sensors, canSetSource: true);
        dialog.Show();
        Assert.Equal(1, dialog.SelectedSource());
        Assert.Equal(zone.Active, dialog.SelectedCurve());
        var boxes = dialog.GetLogicalDescendants().OfType<CheckBox>().ToList();
        boxes.Single(b => (b.Content as TextBlock)?.Text == "DIMMA1_Temp").IsChecked = true;
        boxes.Single(b => (b.Content as TextBlock)?.Text == "CPU Package Temp").IsChecked = false;
        Assert.Equal(8, dialog.SelectedSource());
        dialog.GetLogicalDescendants().OfType<Button>().Single(b => b.Content as string == "Apply")
            .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.Contains("fansource bmc:2 8", sent);
        Assert.DoesNotContain(sent, l => l.StartsWith("fancurve", StringComparison.Ordinal)); // the curve didn't change
        Assert.Contains(dialog.GetLogicalDescendants().OfType<TextBlock>(), t => t.Text == "Sensors set.");
        dialog.CaptureRenderedFrame()?.Save(Path.Combine(Environment.GetEnvironmentVariable("OPENHW_CAPTURES") ?? Path.GetTempPath(), "openhw-fan-curve.png"));
        dialog.Close();
    }
}
