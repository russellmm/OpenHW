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
    private static List<TextBlock> Values(Window window) => window.GetVisualDescendants().OfType<TextBlock>()
        .Where(t => t.DataContext is SensorRow && t.HorizontalAlignment == HorizontalAlignment.Right && !string.IsNullOrEmpty(t.Text))
        .ToList();

    private static void Capture(Window window, string name)
    {
        Dispatcher.UIThread.RunJobs();
        window.CaptureRenderedFrame()?.Save(Path.Combine(Path.GetTempPath(), name));
    }

    [AvaloniaFact]
    public void ExpandedRowsStayGrayUntilTheyAreReadAgain()
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

        // Expanded, before the next poll: every row shown is frozen, so every value is gray.
        window.SetAllExpanded(true);
        Capture(window, "openhw-expanded-before-poll.png");
        Assert.All(Values(window), t => Assert.Same(Palette.Frozen, t.Foreground));

        // First poll: instant readings are live again; rate rows (CPU usage, C-states) still wait for a second sample.
        hub.Sample();
        window.RefreshValues();
        Capture(window, "openhw-expanded-first-poll.png");
        var values = Values(window);
        var gray = values.Where(t => ReferenceEquals(t.Foreground, Palette.Frozen)).ToList();
        Assert.NotEmpty(gray);
        Assert.All(gray, t => Assert.True(hub.IsPaused(((SensorRow)t.DataContext!).Id)));
        Assert.Contains(values, t => ReferenceEquals(t.Foreground, Palette.Text));

        // Second poll: everything live.
        Thread.Sleep(1100);
        hub.Sample();
        window.RefreshValues();
        Assert.All(Values(window), t => Assert.Same(Palette.Text, t.Foreground));
    }

    [AvaloniaFact]
    public void FooterShowsVersionAndRevision()
    {
        Assert.Matches(@"^OpenHW \d+\.\d+\.\d+ · [0-9a-f]{7}", MainWindow.VersionText());
    }
}
