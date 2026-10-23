using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OpenHW.App.Power;
using OpenHW.Hardware;
using Xunit;

namespace OpenHW.App.Tests;

// Reads this machine's cpufreq state; nothing here writes it (no Apply is clicked).
public sealed class CpuFrequencyWindowTests
{
    [AvaloniaFact]
    public void ListsEveryCoreWithItsCurrentCap()
    {
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Directory.CreateTempSubdirectory("openhw-caps-").FullName);
        var limits = new CpuFrequencyLimits();
        var power = new PowerModeController(limits);
        power.Refresh();
        var window = new CpuFrequencyWindow(power) { Height = 1400 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.CaptureRenderedFrame()?.Save(Path.Combine(Path.GetTempPath(), "openhw-cpu-frequency.png"));

        var texts = window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? "").ToList();
        Assert.All(power.Cores, core => Assert.Contains(texts, t => t.StartsWith($"Core {core.Index}  ·  CCD{core.Ccd}", StringComparison.Ordinal)));
        var boxes = window.GetVisualDescendants().OfType<TextBox>().Where(b => b.Watermark as string == "MHz").ToList(); // not the all-cores spinner
        Assert.Equal(power.Cores.Count, boxes.Count);
        Assert.All(boxes, b => Assert.False(string.IsNullOrEmpty(b.Text)));
    }
}
