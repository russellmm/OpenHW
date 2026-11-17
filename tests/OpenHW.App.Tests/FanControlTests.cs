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

    [AvaloniaFact]
    public void ShowsEveryZoneWhoControlsItAndItsCurve()
    {
        using var hub = new SensorHub();
        var window = new FanControlWindow(hub, new FanClient(_ => GpuWire.Encode(Status())));
        window.Show();
        var texts = window.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
        Assert.Contains("CPU_FAN + CPU_OPT", texts);
        Assert.Contains("W_PUMP+", texts);
        Assert.Contains("OpenHW: 70 % (→ 60 % when allowed)", texts);
        Assert.Contains("20°→70%  45°→70%  65°→70%  70°→100%  100°→100%", texts);
        window.CaptureRenderedFrame()?.Save(Path.Combine(Environment.GetEnvironmentVariable("OPENHW_CAPTURES") ?? Path.GetTempPath(), "openhw-fan-control.png"));
        window.Close();
    }

    [AvaloniaFact]
    public void SaysWhenOpenFanHoldsTheFansOrTheHelperIsTooOld()
    {
        using var hub = new SensorHub();
        var held = new FanControlWindow(hub, new FanClient(_ => GpuWire.Encode(Status("OpenFan is controlling BMC fans right now."))));
        Assert.Contains(held.GetLogicalDescendants().OfType<TextBlock>(), t => t.Text!.StartsWith("OpenFan is controlling", StringComparison.Ordinal));
        var old = new FanControlWindow(hub, new FanClient(_ => throw new IOException("openhw-helper: err unknown command")));
        Assert.Contains(old.GetLogicalDescendants().OfType<TextBlock>(), t => t.Text!.Contains("too old for fan control", StringComparison.Ordinal));
    }
}
