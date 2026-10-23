using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OpenHW.App.Sensors;
using OpenHW.App.SensorWindow;
using Xunit;

namespace OpenHW.App.Tests;

// Runs against this machine's real sensors (the window needs a live SensorHub); rows that need two samples —
// CPU usage — exist on any Linux box.
public sealed class DimmingTests
{
    [AvaloniaFact]
    public void ResumedRateRowsRenderDimmed()
    {
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Directory.CreateTempSubdirectory("openhw-view-").FullName);
        using var hub = new SensorHub();
        hub.Sample();
        Thread.Sleep(1100);
        hub.Sample();

        var window = new MainWindow(hub) { Width = 820, Height = 3000 };
        window.Show();
        window.SetAllExpanded(false);
        hub.Sample();
        window.RefreshValues();

        window.SetAllExpanded(true); // the app polls immediately after this
        hub.Sample();
        window.RefreshValues();
        Dispatcher.UIThread.RunJobs();

        var frame = window.CaptureRenderedFrame();
        frame?.Save(Path.Combine(Path.GetTempPath(), "openhw-dimming.png"));

        var values = window.GetVisualDescendants().OfType<TextBlock>()
            .Where(t => t.DataContext is SensorRow && t.HorizontalAlignment == HorizontalAlignment.Right && !string.IsNullOrEmpty(t.Text))
            .ToList();
        var paused = values.Where(t => hub.IsPaused(((SensorRow)t.DataContext!).Id)).ToList();
        var live = values.Except(paused).ToList();

        Assert.NotEmpty(paused);
        Assert.All(paused, t => Assert.True(t.Opacity < 0.6, $"{((SensorRow)t.DataContext!).Name}: opacity {t.Opacity}"));
        Assert.All(live, t => Assert.Equal(1, t.Opacity));
    }
}
