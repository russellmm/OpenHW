using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OpenHW.App.Power;
using OpenHW.App.Sensors;
using OpenHW.Hardware;
using Xunit;

namespace OpenHW.App.Tests;

// Reads this machine's limits; nothing here writes them (no Apply is clicked).
public sealed class MenuAndPowerLimitsTests
{
    [AvaloniaFact]
    public void PowerLimitsListTheCpuAndEveryGpu()
    {
        using var gpus = new GpuPowerLimits();
        var cpu = new CpuPowerLimits();
        var window = new PowerLimitsWindow(gpus, cpu);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.CaptureRenderedFrame()?.Save(Path.Combine(Path.GetTempPath(), "openhw-power-limits.png"));

        var texts = window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? "").ToList();
        if (cpu.Read() is not null) Assert.Contains(texts, t => t.StartsWith("CPU socket power limit", StringComparison.Ordinal));
        Assert.Equal(gpus.Read().Count, texts.Count(t => t.StartsWith("GPU", StringComparison.Ordinal) && t.Contains(':')));
    }

    [AvaloniaFact]
    public void MainWindowHasTheMenuBar()
    {
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Directory.CreateTempSubdirectory("openhw-menu-").FullName);
        using var hub = new SensorHub();
        hub.Sample();
        var power = new PowerModeController(new CpuFrequencyLimits());
        var window = new MainWindow(hub, power, new WindowCommands(() => { }, () => { }, () => { }, () => { })) { Height = 600 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.CaptureRenderedFrame()?.Save(Path.Combine(Path.GetTempPath(), "openhw-menu.png"));

        var menu = window.GetVisualDescendants().OfType<Menu>().Single();
        Assert.Equal(["_File", "_View", "_Power", "_Help"], menu.Items.OfType<MenuItem>().Select(m => (string)m.Header!));
        Assert.Contains(window.KeyBindings, k => k.Gesture.ToString() == "Ctrl+L");
    }

    [AvaloniaFact]
    public void FooterIsHwinfosBarWithASettingsGear()
    {
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Directory.CreateTempSubdirectory("openhw-footer-").FullName);
        using var hub = new SensorHub();
        hub.Sample();
        var settings = false;
        var power = new PowerModeController(new CpuFrequencyLimits());
        var window = new MainWindow(hub, power, new WindowCommands(() => { }, () => { }, () => settings = true, () => { })) { Width = 1000, Height = 600 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var footer = window.GetVisualDescendants().OfType<Grid>().First(g => g.Height == 58);
        var buttons = footer.GetVisualDescendants().OfType<Button>().ToList();
        Assert.Equal(["Expand all", "Collapse all", "Reset Min/Max/Average", "Settings", "Close (OpenHW keeps running in the tray)"],
            buttons.Select(b => ToolTip.GetTip(b) as string));
        Assert.Empty(footer.GetVisualDescendants().OfType<ComboBox>()); // power mode moved to the Power menu only
        buttons[3].RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.True(settings);
        window.CaptureRenderedFrame()?.Save(Path.Combine(Path.GetTempPath(), "openhw-footer.png"));

        // Hover keeps the frame (Fluent's default would drop it) and lightens the face.
        var gear = buttons[3];
        var centre = Avalonia.VisualExtensions.TranslatePoint(gear, new Avalonia.Point(gear.Bounds.Width / 2, gear.Bounds.Height / 2), window)!.Value;
        window.MouseMove(centre);
        Dispatcher.UIThread.RunJobs();
        var presenter = gear.GetVisualDescendants().OfType<Avalonia.Controls.Presenters.ContentPresenter>().First(p => p.Name == "PART_ContentPresenter");
        Assert.Same(OpenHW.App.SensorWindow.FooterIcons.ToolbarEdge, presenter.BorderBrush);
        Assert.Same(OpenHW.App.SensorWindow.FooterIcons.ToolbarHover, presenter.Background);
        window.CaptureRenderedFrame()?.Save(Path.Combine(Path.GetTempPath(), "openhw-footer-hover.png"));
    }
}
