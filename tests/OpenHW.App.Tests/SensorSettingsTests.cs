using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OpenHW.App.Alerts;
using OpenHW.App.Sensors;
using OpenHW.App.Settings;
using OpenHW.App.SensorWindow;
using Xunit;

namespace OpenHW.App.Tests;

/// <summary>
/// The footer gear's HWiNFO-style Sensor Settings and Main Settings dialogs: their tabs, the items connected to
/// OpenHW's settings — each change showing both here and in the older window that sets the same thing — and the
/// scaffolding that still saves nothing.
/// </summary>
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

    /// <summary>The app's tray calls, done on the settings directly (App saves and re-registers the icons).</summary>
    private static SensorSettingsActions Actions(AppSettings s, SensorHub hub)
    {
        string? Resolve(TraySensor t) => t.SensorId == AppSettings.CpuTemperature ? hub.CpuTemperatureId() : t.SensorId;
        return new SensorSettingsActions(
            () => { },
            id =>
            {
                var inTray = s.TrayIcons.Any(t => Resolve(t) == id);
                return new TrayState(inTray, !inTray, null);
            },
            id => { s.TrayIcons.Add(new TraySensor { SensorId = id }); s.Save(); },
            id => { s.TrayIcons.RemoveAll(t => Resolve(t) == id); s.Save(); },
            () => { s.TrayIcons.Clear(); s.Save(); },
            (id, hex) => { foreach (var t in s.TrayIcons.Where(t => Resolve(t) == id)) t.Colour = hex; s.Save(); });
    }

    private sealed record Setup(SensorHub Hub, AppSettings Settings, AlertStore Alerts, SensorSettingsWindow Window) : IDisposable
    {
        public void Dispose()
        {
            Window.Close();
            Hub.Dispose();
        }
    }

    private static Setup Open()
    {
        var hub = new SensorHub();
        hub.Sample();
        var settings = AppSettings.Load();
        var alerts = AlertStore.Load();
        var window = new SensorSettingsWindow(hub, settings, alerts, Actions(settings, hub));
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return new Setup(hub, settings, alerts, window);
    }

    private static ClassicEntryList.Entry FirstSensor(ClassicEntryList list, Func<ClassicEntryList.Entry, bool>? where = null) =>
        list.Entries.First(e => !e.IsGroup && !e.IsSpacer && (where?.Invoke(e) ?? true));

    [AvaloniaFact]
    public void HasHwinfosTabsWithTheMissingOnesGrayed()
    {
        Isolate();
        using var s = Open();
        var window = s.Window;
        Assert.Equal(SensorSettingsWindow.Tabs, window.TabTitles);
        foreach (var missing in new[] { "OSD", "OSD (RTSS)", "Logitech LCD", "HWiNFO Gadget" }) Assert.False(window.SelectTab(missing));
        foreach (var tab in new[] { "General", "Layout", "Customize", "Tray Icon", "Alerts" })
        {
            Assert.True(window.SelectTab(tab));
            Dispatcher.UIThread.RunJobs();
            Capture(window, $"sensor-settings-{tab.Replace(' ', '-').ToLowerInvariant()}");
        }
    }

    [AvaloniaFact]
    public void EntryListsShowTheRealSensorsAndSelectionEnablesTheControls()
    {
        Isolate();
        using var s = Open();
        var window = s.Window;
        Assert.StartsWith("[", window.LayoutList.Entries[0].Label);
        Assert.All(window.CustomizeList.Entries.Where(e => e.IsGroup), e => Assert.Equal("-", e.Value));
        Assert.Contains(window.TrayList.Entries, e => e.Value == "Yes"); // the default CPU temperature tile

        window.SelectTab("Alerts");
        Dispatcher.UIThread.RunJobs();
        var page = window.SelectedPage!;
        Assert.All(page.NeedSelection, c => Assert.False(c.IsEnabled));
        window.AlertList.Selected = FirstSensor(window.AlertList);
        Assert.All(page.NeedSelection, c => Assert.True(c.IsEnabled));
        window.AlertList.Selected = window.AlertList.Entries.First(e => e.IsGroup);
        Assert.All(page.NeedSelection, c => Assert.False(c.IsEnabled));
    }

    [AvaloniaFact]
    public void PollingPeriodIsFileSettingsPollInterval()
    {
        Isolate();
        using var s = Open();
        var settingsWindow = new SettingsWindow(s.Settings, () => { }, () => { });
        settingsWindow.Show();
        Dispatcher.UIThread.RunJobs();
        var poll = settingsWindow.GetVisualDescendants().OfType<ComboBox>().First();
        Assert.Equal("2000", s.Window.PollField.Text);

        s.Window.PollField.Text = "4400"; // OpenHW's nearest interval
        s.Window.SetPollNow();
        Assert.Equal(5, s.Settings.PollSeconds);
        Assert.Equal("5000", s.Window.PollField.Text);
        Assert.Equal(5.0, (poll.SelectedItem as ComboBoxItem)?.Tag);

        poll.SelectedItem = poll.Items.OfType<ComboBoxItem>().First(i => (double)i.Tag! == 3);
        Assert.Equal(3, s.Settings.PollSeconds);
        Assert.Equal("3000", s.Window.PollField.Text);
        Assert.Equal(3, AppSettings.Load().PollSeconds); // saved
        settingsWindow.Close();
    }

    [AvaloniaFact]
    public void LayoutTabIsViewLayoutSettings()
    {
        Isolate();
        using var s = Open();
        var custom = SensorCustomizations.Current;
        var layout = new LayoutWindow(s.Hub);
        var window = s.Window;
        window.SelectTab("Layout");
        var sensor = FirstSensor(window.LayoutList);
        window.LayoutList.Selected = sensor;

        window.MonitoringBox.IsChecked = false;
        Assert.True(custom.IsDisabled(sensor.Key));
        Assert.Equal("No", layout.ListEntries.First(e => e.Key == sensor.Key).Monitoring);
        custom.SetDisabled(sensor.Key, false); // the row menu / Layout Settings
        Assert.True(window.MonitoringBox.IsChecked);
        Assert.Equal("Yes", window.LayoutList.Entries.First(e => e.Key == sensor.Key).Value);

        window.ShowBox.IsChecked = false;
        Assert.True(custom.IsHidden(sensor.Key));
        Assert.Contains(layout.HiddenEntries, e => e.Key == sensor.Key);
        Assert.Equal(sensor.Key, window.HiddenList.Selected?.Key); // follows it into Hidden items
        window.ShowBox.IsChecked = true;
        Assert.False(custom.IsHidden(sensor.Key));

        // Moving the group's second sensor up puts it first, in both windows.
        var group = window.LayoutList.Entries.Where(e => !e.IsGroup && !e.IsSpacer && e.GroupKey == sensor.GroupKey).ToList();
        window.LayoutList.Selected = group[1];
        window.MoveSelected(-1);
        Assert.Equal(group[1].Key, window.LayoutList.Entries.First(e => !e.IsGroup && e.GroupKey == sensor.GroupKey).Key);
        Assert.Equal(group[1].Key, layout.ListEntries.First(e => !e.IsGroup && e.GroupKey == sensor.GroupKey).Key);
        custom.RestoreOriginalOrder();
        Assert.Equal(group[0].Key, window.LayoutList.Entries.First(e => !e.IsGroup && e.GroupKey == sensor.GroupKey).Key);
        layout.Close();
    }

    [AvaloniaFact]
    public void CustomizeTabIsRenameCustomizeValuesAndLogging()
    {
        Isolate();
        using var s = Open();
        var custom = SensorCustomizations.Current;
        var window = s.Window;
        window.SelectTab("Customize");
        var item = s.Hub.Inventory.First(i => SensorPresentation.CanCustomize(i) && window.CustomizeList.Entries.Any(e => e.Key == i.Id));
        window.CustomizeList.Selected = window.CustomizeList.Entries.First(e => e.Key == item.Id);
        Assert.Equal(SensorPresentation.BuiltInName(item), window.CurrentLabel.Text);

        window.CurrentLabel.Text = "Renamed here";
        window.RenameLabelNow();
        Assert.Equal("Renamed here", custom.NameOf(item.Id));
        custom.Rename(item.Id, "Renamed in the row menu");
        Assert.Equal("Renamed in the row menu", window.CurrentLabel.Text);
        Assert.Equal("Renamed in the row menu", window.CustomizeList.Entries.First(e => e.Key == item.Id).Label);

        window.MultiplyField.Text = "1,8"; // either decimal separator, as Customize values…
        window.SetValuesNow();
        Assert.Equal(1.8, custom.ValuesOf(item.Id)?.Multiply);
        custom.Customize(item.Id, null);
        Assert.Equal("1", window.MultiplyField.Text);

        window.LoggedBox.IsChecked = true;
        Assert.True(custom.IsLogged(item.Id));
        Assert.Equal("Yes", window.CustomizeList.Entries.First(e => e.Key == item.Id).Value);
        custom.SetLogged(item.Id, false); // the row menu's Disable Logging
        Assert.False(window.LoggedBox.IsChecked);
    }

    [AvaloniaFact]
    public void TrayIconTabIsAddAndRemoveFromTray()
    {
        Isolate();
        using var s = Open();
        var window = s.Window;
        window.SelectTab("Tray Icon");
        var sensor = FirstSensor(window.TrayList, e => e.Value == "No");
        window.TrayList.Selected = sensor;
        Assert.False(window.InTrayBox.IsChecked);

        window.InTrayBox.IsChecked = true;
        Assert.Contains(s.Settings.TrayIcons, t => t.SensorId == sensor.Key);
        Assert.Equal("Yes", window.TrayList.Entries.First(e => e.Key == sensor.Key).Value);

        s.Settings.TrayIcons.RemoveAll(t => t.SensorId == sensor.Key); // e.g. the icon's own Remove
        s.Settings.Save();
        Assert.False(window.InTrayBox.IsChecked);
        Assert.Equal("No", window.TrayList.Entries.First(e => e.Key == sensor.Key).Value);

        // No icon limit, so "Add All" would put every sensor on the panel: owner said not to offer it.
        var addAll = window.SelectedPage!.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "Add All Items To Tray");
        Assert.False(addAll.IsEnabled);
        Assert.Contains("Not offered", ToolTip.GetTip(addAll) as string);
    }

    [AvaloniaFact]
    public void AlertsTabIsOpenHwsAlertRules()
    {
        Isolate();
        using var s = Open();
        var window = s.Window;
        window.SelectTab("Alerts");
        var sensor = FirstSensor(window.AlertList);
        window.AlertList.Selected = sensor;

        window.AboveField.Text = "80";
        window.AboveBox.IsChecked = true;
        var rule = Assert.Single(s.Alerts.For(sensor.Key));
        Assert.Equal((AlertCondition.Above, 80.0, true), (rule.Condition, rule.Threshold, rule.Enabled));
        Assert.Equal("Yes", window.AlertList.Entries.First(e => e.Key == sensor.Key).Value);

        s.Alerts.Update(() => rule.Threshold = 90); // File → Alerts…
        Assert.Equal("90", window.AboveField.Text);
        window.EnableAlertingBox.IsChecked = false;
        Assert.False(rule.Enabled);

        s.Alerts.Remove(rule);
        Assert.False(window.AboveBox.IsChecked);
        Assert.Equal("No", window.AlertList.Entries.First(e => e.Key == sensor.Key).Value);
    }

    [AvaloniaFact]
    public void MainSettingsStartupItemsAreFileSettingsStartAtLogin()
    {
        Isolate();
        var settings = AppSettings.Load();
        var settingsWindow = new SettingsWindow(settings, () => { }, () => { });
        settingsWindow.Show();
        var main = new MainSettingsWindow(settings, () => { }, () => "11");
        main.Show();
        Dispatcher.UIThread.RunJobs();
        var startAtLogin = settingsWindow.GetVisualDescendants().OfType<CheckBox>().First(c => c.Content as string == "Start OpenHW when I log in");

        main.AutoStartBox.IsChecked = true;
        Assert.True(settings.StartAtLogin);
        Assert.True(File.Exists(AppSettings.AutostartPath));
        Assert.True(startAtLogin.IsChecked);
        Assert.True(main.StartMinimizedBox.IsEnabled);

        startAtLogin.IsChecked = false;
        Assert.False(main.AutoStartBox.IsChecked);
        Assert.False(main.StartMinimizedBox.IsEnabled); // only meaningful at login, as in File → Settings
        main.Close();
        settingsWindow.Close();

        Assert.Equal("Running (protocol 11)", MainSettingsWindow.HelperStatus(() => "11"));
        Assert.Equal("Not running", MainSettingsWindow.HelperStatus(() => throw new System.Net.Sockets.SocketException()));
    }

    [AvaloniaFact]
    public void DisplaySwitchesChangeTheSensorWindow()
    {
        Isolate();
        using var s = Open();
        var main = new MainWindow(s.Hub, null, new WindowCommands(() => { }, () => { }, () => { }, () => { })) { Width = 1000, Height = 600 };
        main.Show();
        Dispatcher.UIThread.RunJobs();
        var dialog = s.Window;

        dialog.GeneralCheck("Row Shading").IsChecked = false;
        dialog.GeneralCheck("Highlight changing values").IsChecked = false;
        dialog.GeneralCheck("Show ToolTips").IsChecked = false;
        dialog.GeneralCheck("Always On Top").IsChecked = true;
        dialog.GeneralCheck("Minimum").IsChecked = false;
        Assert.Equal((false, false, false, true, false),
            (s.Settings.RowShading, s.Settings.HighlightChanges, s.Settings.ShowToolTips, s.Settings.AlwaysOnTop, s.Settings.ShowMinimum));
        main.ApplyDisplay(s.Settings); // what App.ApplySettings does after every save
        Dispatcher.UIThread.RunJobs();
        Assert.False(main.RowShading);
        Assert.False(main.HighlightChanges);
        Assert.True(main.Topmost);
        Assert.Equal(0, main.ColumnLayout.C2.Value);
        Assert.False(ToolTip.GetServiceEnabled(main.GetVisualDescendants().OfType<ListBox>().First()));
        main.CaptureRenderedFrame()?.Save(System.IO.Path.Combine(
            Environment.GetEnvironmentVariable("OPENHW_CAPTURES") ?? System.IO.Path.GetTempPath(), "openhw-display-switches.png"));

        s.Settings.RowShading = true; // as File → Settings would, through Save
        s.Settings.ShowMinimum = true;
        s.Settings.Save();
        Assert.True(dialog.GeneralCheck("Row Shading").IsChecked);
        Assert.True(dialog.GeneralCheck("Minimum").IsChecked);
        main.ApplyDisplay(s.Settings);
        Assert.True(main.RowShading);
        Assert.Equal(main.ColumnLayout.Widths[2], main.ColumnLayout.C2.Value);
        main.Close();
    }

    [AvaloniaFact]
    public void AverageLastSamplesIsSavedAndUsed()
    {
        Isolate();
        using var s = Open();
        s.Window.AverageField.Text = "30";
        s.Window.SetAverageNow();
        Assert.Equal(30, s.Settings.AverageSamples);
        Assert.Equal(30, AppSettings.Load().AverageSamples);
        s.Hub.SetAverageSamples(s.Settings.AverageSamples);
        Assert.Equal(30, s.Hub.Statistics.AverageSamples);
        s.Window.AverageField.Text = "-4"; // not a count: back to the saved value
        s.Window.SetAverageNow();
        Assert.Equal("30", s.Window.AverageField.Text);
    }

    [AvaloniaFact]
    public void ChangeItemColorDecidesWhetherAFiringAlertTurnsTheCellRed()
    {
        Isolate();
        using var s = Open();
        var window = s.Window;
        window.SelectTab("Alerts");
        var sensor = FirstSensor(window.AlertList);
        window.AlertList.Selected = sensor;
        window.AboveField.Text = "-1000";
        window.AboveBox.IsChecked = true;
        var rule = Assert.Single(s.Alerts.For(sensor.Key));
        Assert.True(rule.ChangeColor);
        Assert.True(window.ChangeColorBox.IsChecked);

        var monitor = new AlertMonitor(s.Alerts);
        monitor.Evaluate(0, _ => 1, _ => ("x", ""));
        Assert.Contains(sensor.Key, monitor.AlertingSensors);
        window.ChangeColorBox.IsChecked = false;
        Assert.False(rule.ChangeColor);
        Assert.DoesNotContain(sensor.Key, monitor.AlertingSensors); // still firing, but no red cell
    }

    [AvaloniaFact]
    public void ReaderSwitchesTakeTheirSensorsOutAndBack()
    {
        Isolate();
        using var s = Open();
        var main = new MainSettingsWindow(s.Settings, () => s.Hub.SetReaders(s.Settings.ReadNvidia, s.Settings.ReadSmbus, s.Settings.ReadSmart), () => "11");
        main.Show();
        Dispatcher.UIThread.RunJobs();
        CheckBox Box(string tab, string text)
        {
            main.SelectTab(tab);
            Dispatcher.UIThread.RunJobs();
            return main.SelectedPage!.GetVisualDescendants().OfType<CheckBox>().First(c => c.Content as string == text);
        }
        static bool Smart(string id) => id.StartsWith("nvme-health:", StringComparison.Ordinal);
        var hadSmart = s.Hub.Inventory.Any(i => Smart(i.Id));

        Box("Safety", "S.M.A.R.T. Support").IsChecked = false;
        Box("Safety", "Enable NVML").IsChecked = false;
        Box("SMBus / I2C", "SMBus Support").IsChecked = false;
        Assert.Equal((false, false, false), (s.Settings.ReadSmart, s.Settings.ReadNvidia, s.Settings.ReadSmbus));
        s.Hub.Sample();
        Assert.DoesNotContain(s.Hub.Inventory, i => Smart(i.Id) || i.Id.StartsWith("nvml:", StringComparison.Ordinal)
                                                   || i.Id.StartsWith("ddr5:", StringComparison.Ordinal) || i.Id.StartsWith("vr:", StringComparison.Ordinal));

        Box("Safety", "S.M.A.R.T. Support").IsChecked = true;
        s.Hub.Sample();
        Assert.Equal(hadSmart, s.Hub.Inventory.Any(i => Smart(i.Id))); // back (on a machine whose helper reads SMART)

        Box("General / User Interface", "Validate Window Positions").IsChecked = false;
        Assert.False(s.Settings.ValidateWindowPositions);
        main.Close();
    }

    [AvaloniaFact]
    public void ScaffoldingControlsSaveNothing()
    {
        var dir = Isolate();
        using var s = Open();
        // General's other check boxes and radios aren't connected yet.
        string[] connected = ["Row Shading", "Highlight changing values", "Show ToolTips", "Always On Top", "Current", "Minimum", "Maximum", "Average"];
        foreach (var check in s.Window.SelectedPage!.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.ToggleButton>()
                     .Where(c => !connected.Contains(c.Content as string)))
            check.IsChecked = !check.IsChecked;
        s.Window.Ok.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        Assert.False(s.Window.IsVisible);
        Assert.False(File.Exists(System.IO.Path.Combine(dir, "openhw", "settings.json")));
    }

    [AvaloniaFact]
    public void MainSettingsButtonOpensTheSettingsDialog()
    {
        Isolate();
        using var s = Open();
        var opened = false;
        s.Window.ShowMainSettings = () => { opened = true; return Task.CompletedTask; };
        var button = s.Window.SelectedPage!.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "Main Settings");
        button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.True(opened);

        var main = new MainSettingsWindow(s.Settings, () => { }, () => "11");
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
