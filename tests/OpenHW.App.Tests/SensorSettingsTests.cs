using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OpenHW.App.Alerts;
using OpenHW.App.Sensors;
using OpenHW.App.Settings;
using Xunit;

namespace OpenHW.App.Tests;

/// <summary>The footer gear's HWiNFO-style Sensor Settings and Main Settings dialogs (scaffolding: nothing saved).</summary>
public sealed class SensorSettingsTests
{
    private static string Isolate()
    {
        var dir = Directory.CreateTempSubdirectory("openhw-sensor-settings-").FullName;
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", dir);
        return dir;
    }

    // Renders of every tab, for comparing with reference/settings_menu/ (OPENHW_CAPTURES picks the folder).
    private static void Capture(Window window, string name) =>
        window.CaptureRenderedFrame()?.Save(System.IO.Path.Combine(
            Environment.GetEnvironmentVariable("OPENHW_CAPTURES") ?? System.IO.Path.GetTempPath(), $"openhw-{name}.png"));

    private static (SensorHub Hub, SensorSettingsWindow Window) Open()
    {
        var hub = new SensorHub();
        hub.Sample();
        var window = new SensorSettingsWindow(hub, AppSettings.Load(), AlertStore.Load());
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (hub, window);
    }

    [AvaloniaFact]
    public void HasHwinfosTabsWithTheMissingOnesGrayed()
    {
        Isolate();
        var (hub, window) = Open();
        using var _ = hub;
        Assert.Equal(SensorSettingsWindow.Tabs, window.TabTitles);
        Assert.Equal("General", window.TabTitles[0]);
        foreach (var missing in new[] { "OSD", "OSD (RTSS)", "Logitech LCD", "HWiNFO Gadget" }) Assert.False(window.SelectTab(missing));
        foreach (var tab in new[] { "General", "Layout", "Customize", "Tray Icon", "Alerts" })
        {
            Assert.True(window.SelectTab(tab));
            Dispatcher.UIThread.RunJobs();
            Capture(window, $"sensor-settings-{tab.Replace(' ', '-').ToLowerInvariant()}");
        }
        window.Close();
    }

    [AvaloniaFact]
    public void EntryListsShowTheRealSensorsAndSelectionEnablesTheControls()
    {
        Isolate();
        var (hub, window) = Open();
        using var _ = hub;
        var entries = window.LayoutList.Entries;
        Assert.NotEmpty(entries);
        Assert.StartsWith("[", entries[0].Label);
        Assert.All(window.CustomizeList.Entries.Where(e => e.IsGroup), e => Assert.Equal("-", e.Value));
        Assert.Contains(window.TrayList.Entries, e => e.Value == "Yes"); // the default CPU temperature tile

        window.SelectTab("Alerts");
        Dispatcher.UIThread.RunJobs();
        var page = window.SelectedPage!;
        Assert.All(page.NeedSelection, c => Assert.False(c.IsEnabled));
        window.AlertList.Selected = window.AlertList.Entries.First(e => !e.IsGroup && !e.IsSpacer);
        Assert.All(page.NeedSelection, c => Assert.True(c.IsEnabled));
        window.AlertList.Selected = window.AlertList.Entries.First(e => e.IsGroup);
        Assert.All(page.NeedSelection, c => Assert.False(c.IsEnabled));
        window.Close();
    }

    [AvaloniaFact]
    public void TogglingAndOkSaveNothing()
    {
        var dir = Isolate();
        var (hub, window) = Open();
        using var _ = hub;
        foreach (var check in window.SelectedPage!.GetVisualDescendants().OfType<CheckBox>()) check.IsChecked = !check.IsChecked;
        window.Ok.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        Assert.False(window.IsVisible);
        Assert.False(File.Exists(System.IO.Path.Combine(dir, "openhw", "settings.json")));
    }

    [AvaloniaFact]
    public void MainSettingsButtonOpensTheSettingsDialog()
    {
        Isolate();
        var (hub, window) = Open();
        using var _ = hub;
        var opened = false;
        window.ShowMainSettings = () => { opened = true; return Task.CompletedTask; };
        var button = window.SelectedPage!.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "Main Settings");
        button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.True(opened);
        window.Close();

        var main = new MainSettingsWindow();
        main.Show();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(MainSettingsWindow.Tabs, main.TabTitles);
        Assert.False(main.SelectTab("License Management"));
        foreach (var tab in new[] { "General / User Interface", "Safety", "SMBus / I2C", "Driver Management" })
        {
            Assert.True(main.SelectTab(tab));
            Dispatcher.UIThread.RunJobs();
            Capture(main, $"main-settings-{tab.Split(' ')[0].ToLowerInvariant()}");
        }
        // SMBus: 8 × 16 address boxes, the first row (0x00–0x0F) excluded; 8 GPU buses.
        main.SelectTab("SMBus / I2C");
        Dispatcher.UIThread.RunJobs();
        var cells = main.SelectedPage!.GetVisualDescendants().OfType<CheckBox>().Where(c => c.Content as string == "").ToList();
        Assert.Equal(128 + 8, cells.Count);
        Assert.Equal(16, cells.Count(c => c.IsChecked == true));
        main.Close();
    }
}
