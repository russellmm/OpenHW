using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using OpenHW.App.Power;
using OpenHW.Hardware;
using Xunit;

namespace OpenHW.App.Tests;

public sealed class FanThresholdDialogTests
{
    private readonly List<string> _sent = [];
    private double[] _now = [0, 360, 360]; // lnr, lcr, lnc
    private double[]? _original;

    private string Fake(string line)
    {
        _sent.Add(line);
        var p = line.Split(' ');
        switch (p[0])
        {
            case "bmcthresh" when p.Length == 1:
                return GpuWire.Encode(new List<BmcFanThresholds>
                {
                    new("CHA_FAN2", 0x35, 374, _now[2], _now[1], _now[0], 120, 30600, true, _original),
                    new("W_PUMP+", 0x39, 3840, null, null, null, 60, 15300, false, null),
                });
            case "bmcthresh":
                _original ??= _now;
                _now = [double.Parse(p[2]), double.Parse(p[3]), double.Parse(p[4])];
                return GpuWire.Encode(new BmcThresholds(_now[2], _now[1], _now[0], null, null, null));
            case "bmcthreshoriginal":
                _now = _original!;
                return GpuWire.Encode(new BmcThresholds(_now[2], _now[1], _now[0], null, null, null));
            default: throw new IOException("openhw-helper: err unknown command");
        }
    }

    private static void Click(Window w, string text) =>
        w.GetLogicalDescendants().OfType<Button>().Single(b => b.Content as string == text)
            .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

    [AvaloniaFact]
    public void OffersStepsAppliesAndRestores()
    {
        var dialog = new FanThresholdDialog(new FanClient(Fake), "CHA_FAN2");
        dialog.Show();
        var boxes = dialog.GetLogicalDescendants().OfType<ComboBox>().ToList();
        Assert.Equal(3, boxes.Count);
        Assert.Equal(26, boxes[0].Items.Count); // 0 … 3,000 RPM in 120 RPM steps
        Assert.Equal(360.0, ((ComboBoxItem)boxes[0].SelectedItem!).Tag);
        Assert.False(dialog.GetLogicalDescendants().OfType<Button>().Single(b => b.Content as string == "Restore original").IsEnabled);

        boxes[0].SelectedIndex = 2; // slow 240
        boxes[1].SelectedIndex = 1; // failing 120
        boxes[2].SelectedIndex = 0; // stopped 0
        Click(dialog, "Apply");
        Assert.Contains("bmcthresh CHA_FAN2 0 120 240", _sent);
        boxes = dialog.GetLogicalDescendants().OfType<ComboBox>().ToList();
        Assert.Equal(26, boxes[0].Items.Count); // reloaded, not doubled
        Assert.Contains(dialog.GetLogicalDescendants().OfType<TextBlock>(), t => t.Text == "original 360 RPM");
        var captures = Environment.GetEnvironmentVariable("OPENHW_CAPTURES") ?? Path.GetTempPath();
        dialog.CaptureRenderedFrame()?.Save(Path.Combine(captures, "openhw-fan-thresholds.png"));

        boxes[1].SelectedIndex = 3; // failing 360 > slow 240: refused before anything is sent
        var sent = _sent.Count;
        Click(dialog, "Apply");
        Assert.Equal(sent, _sent.Count); // nothing sent

        Click(dialog, "Restore original");
        Assert.Contains("bmcthreshoriginal CHA_FAN2", _sent);
        Assert.Equal([0.0, 360, 360], _now);
    }

    [AvaloniaFact]
    public void NotSettableOrOldHelperSaysSo()
    {
        var pump = new FanThresholdDialog(new FanClient(Fake), "W_PUMP+");
        pump.Show();
        Assert.Contains(pump.GetLogicalDescendants().OfType<TextBlock>(), t => t.Text!.Contains("doesn't let", StringComparison.Ordinal));
        var old = new FanThresholdDialog(new FanClient(_ => throw new IOException("openhw-helper: err unknown command")), "CHA_FAN2");
        old.Show();
        Assert.Contains(old.GetLogicalDescendants().OfType<TextBlock>(), t => t.Text!.Contains("protocol 17", StringComparison.Ordinal));
    }
}
