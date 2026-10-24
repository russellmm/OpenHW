using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OpenHW.App.Sensors;
using OpenHW.App.SensorWindow;
using Xunit;

namespace OpenHW.App.Tests;

public sealed class RowMenuTests
{
    [AvaloniaFact]
    public void SensorRowsOfferHwinfosMenuWithAddToTrayWorking()
    {
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Directory.CreateTempSubdirectory("openhw-rowmenu-").FullName);
        using var hub = new SensorHub();
        hub.Sample();
        var tray = new HashSet<string>();
        var commands = new WindowCommands(() => { }, () => { }, () => { }, () => { })
        {
            ShowTrayIcons = () => { },
            ShowLayout = _ => { },
            TrayStateOf = id => new TrayState(tray.Contains(id), !tray.Contains(id), null),
            AddToTray = id => tray.Add(id),
            RemoveFromTray = id => tray.Remove(id),
        };
        var window = new MainWindow(hub, null, commands) { Height = 800 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var grid = window.GetVisualDescendants().OfType<Grid>().First(g => g.DataContext is SensorRow && g.ContextMenu is not null);
        var row = (SensorRow)grid.DataContext!;
        RightClick(grid);

        var items = grid.ContextMenu!.Items.OfType<MenuItem>().ToList();
        Assert.Equal(["Show Graph", "Add to tray", "Remove from tray", "Disable monitoring", "Enable monitoring", "Rename", "Hide",
                      "Enable Logging", "Disable Logging", "Layout settings…", "Customize values…", "Tray icon settings…",
                      "OSD settings…", "OSD (RTSS) settings…", "Alert settings…", "LG LCD settings…"],
                     items.Select(i => (string)i.Header!));
        var enabled = items.Where(i => i.IsEnabled).Select(i => (string)i.Header!).ToList();
        Assert.Equal(["Add to tray", "Disable monitoring", "Rename", "Hide", "Enable Logging", "Layout settings…", "Customize values…",
                      "Tray icon settings…"], enabled);
        Assert.All(items.Where(i => !i.IsEnabled && (string)i.Header! is not ("Remove from tray" or "Enable monitoring" or "Disable Logging")), i =>
        {
            Assert.Equal("To be implemented", ToolTip.GetTip(i));
            Assert.True(ToolTip.GetShowOnDisabled(i));
        });

        items.Single(i => (string)i.Header! == "Add to tray").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
        Assert.Contains(row.Id, tray);

        grid.ContextMenu!.Close();
        RightClick(grid); // reopening reflects the new state
        var reopened = grid.ContextMenu.Items.OfType<MenuItem>().ToList();
        Assert.False(reopened.Single(i => (string)i.Header! == "Add to tray").IsEnabled);
        Assert.True(reopened.Single(i => (string)i.Header! == "Remove from tray").IsEnabled);
        window.CaptureRenderedFrame()?.Save(Path.Combine(Path.GetTempPath(), "openhw-row-menu.png"));
    }

    // The path a right-click (or the Menu key) takes: a context request, which is what raises ContextMenu.Opening.
    private static void RightClick(Control control)
    {
        control.RaiseEvent(new ContextRequestedEventArgs { RoutedEvent = Control.ContextRequestedEvent, Source = control });
        Dispatcher.UIThread.RunJobs();
    }
}
