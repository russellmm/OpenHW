using Avalonia;
using Avalonia.Headless;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OpenHW.App.Sensors;
using OpenHW.App.SensorWindow;
using Xunit;

namespace OpenHW.App.Tests;

public sealed class LayoutTests
{
    [AvaloniaFact]
    public void AColumnWidthChangesEveryRowAndIsRemembered()
    {
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Directory.CreateTempSubdirectory("openhw-columns-").FullName);
        using var hub = new SensorHub();
        hub.Sample();
        var window = new MainWindow(hub) { Height = 800 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        window.ColumnLayout.Set(1, 150);
        window.ColumnLayout.Set(0, 10); // clamped to the minimum
        window.SaveColumns();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        var grids = window.GetVisualDescendants().OfType<Grid>().Where(g => g.ColumnDefinitions.Count == 6).ToList();
        Assert.True(grids.Count > 5); // the header and the rows on screen
        Assert.All(grids, g => Assert.Equal(150, g.ColumnDefinitions[1].ActualWidth, 1));
        Assert.All(grids, g => Assert.Equal(ColumnLayout.Minimums[0], g.ColumnDefinitions[0].ActualWidth, 1));
        Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(window)?.Save(Path.Combine(Path.GetTempPath(), "openhw-narrow-columns.png"));

        var reopened = new MainWindow(hub);
        Assert.Equal([ColumnLayout.Minimums[0], 150, 96, 96, 96], reopened.ColumnLayout.Widths);
        reopened.ColumnLayout.Reset(1);
        Assert.Equal(96, reopened.ColumnLayout.Widths[1]);
    }

    [AvaloniaFact]
    public void TheWindowComesBackAtItsSavedSize()
    {
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Directory.CreateTempSubdirectory("openhw-geometry-").FullName);
        using var hub = new SensorHub();
        hub.Sample();
        var window = new MainWindow(hub) { Width = 900, Height = 700 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.SaveWindowState();
        var reopened = new MainWindow(hub);
        Assert.Equal((900, 700), (reopened.Width, reopened.Height));
    }

    [Fact]
    public void BadSavedWidthsFallBackToDefaults()
    {
        Assert.Equal(ColumnLayout.Defaults, new ColumnLayout([1, 2, 3]).Widths);
        Assert.Equal(ColumnLayout.Defaults, new ColumnLayout([100, 96, 96, 96, 96]).Widths); // Sensor below its minimum
        Assert.Equal(ColumnLayout.Defaults, new ColumnLayout(null).Widths);
    }
}

public sealed class ColumnDragTests
{
    [AvaloniaFact]
    public void DraggingAHeaderEdgeResizesTheColumn()
    {
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Directory.CreateTempSubdirectory("openhw-drag-").FullName);
        using var hub = new SensorHub();
        hub.Sample();
        var window = new MainWindow(hub) { Width = 820, Height = 600 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        // The resize handles are the header's west-east cursor areas, one per column; [1] is Current's right edge.
        var handles = window.GetVisualDescendants().OfType<Border>()
            .Where(b => b.Cursor?.ToString()?.Contains("SizeWestEast") == true || ToolTip.GetTip(b) as string == "Drag to resize; double-click for the default width")
            .ToList();
        Assert.Equal(5, handles.Count);
        var edge = handles[1].TranslatePoint(new Avalonia.Point(4, 10), window)!.Value;

        window.MouseMove(edge);
        window.MouseDown(edge, Avalonia.Input.MouseButton.Left);
        window.MouseMove(edge + new Avalonia.Point(30, 0));
        window.MouseMove(edge + new Avalonia.Point(60, 0));
        window.MouseUp(edge + new Avalonia.Point(60, 0), Avalonia.Input.MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        Assert.Equal(156, window.ColumnLayout.Widths[1], 1); // 96 + 60
        var grids = window.GetVisualDescendants().OfType<Grid>().Where(g => g.ColumnDefinitions.Count == 6).ToList();
        Assert.All(grids, g => Assert.Equal(156, g.ColumnDefinitions[1].ActualWidth, 1));
        Assert.Equal(156, new MainWindow(hub).ColumnLayout.Widths[1], 1); // saved on release

        // Double-click the edge: back to the default.
        var moved = handles[1].TranslatePoint(new Avalonia.Point(4, 10), window)!.Value;
        window.MouseDown(moved, Avalonia.Input.MouseButton.Left);
        window.MouseUp(moved, Avalonia.Input.MouseButton.Left);
        window.MouseDown(moved, Avalonia.Input.MouseButton.Left);
        window.MouseUp(moved, Avalonia.Input.MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(96, window.ColumnLayout.Widths[1], 1);
    }
}
