using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OpenHW.App.Sensors;
using OpenHW.App.Settings;
using OpenHW.App.SensorWindow;
using Xunit;

namespace OpenHW.App.Tests;

// Layout settings (order, show/hide, monitoring), Customize values, and pausing reads while the window is hidden,
// against this machine's sensors.
public sealed class LayoutAndValuesTests
{
    private static (SensorHub Hub, MainWindow Window, SensorCustomizations Custom) Open()
    {
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Directory.CreateTempSubdirectory("openhw-layout-").FullName);
        var hub = new SensorHub();
        hub.Sample();
        var window = new MainWindow(hub) { Height = 3000 };
        window.Show();
        window.SetAllExpanded(true);
        Dispatcher.UIThread.RunJobs();
        return (hub, window, SensorCustomizations.Current);
    }

    private static List<SensorRow> Rows(MainWindow window) =>
        window.GetVisualDescendants().OfType<ListBox>().Single().Items.OfType<SensorRow>().ToList();

    private static List<string> GroupTitles(MainWindow window) =>
        window.GetVisualDescendants().OfType<ListBox>().Single().Items.OfType<GroupRow>().Select(g => g.Title).ToList();

    [Fact]
    public void ArrangePlacesNamedItemsAndLeavesTheRest()
    {
        string[] items = ["a", "b", "c", "d", "e"];
        Assert.Equal(["a", "b", "c", "d", "e"], SensorCustomizations.Arrange(items, x => x, null));
        // Named items swap among the slots they hold (b and d); the others stay put.
        Assert.Equal(["a", "d", "c", "b", "e"], SensorCustomizations.Arrange(items, x => x, ["d", "b"]));
        // Stale ids are ignored; a new item ("e" missing from the saved order) keeps its built-in place.
        Assert.Equal(["c", "b", "a", "d", "e"], SensorCustomizations.Arrange(items, x => x, ["c", "gone", "b", "a", "d"]));
    }

    [Fact]
    public void NumbersTakeEitherDecimalSeparator()
    {
        Assert.Equal(1.8, CustomizeDialog.ParseNumber("1.8"));
        Assert.Equal(1.8, CustomizeDialog.ParseNumber(" 1,8 "));
        Assert.Equal(-32, CustomizeDialog.ParseNumber("-32"));
        Assert.Null(CustomizeDialog.ParseNumber(""));
        Assert.Null(CustomizeDialog.ParseNumber("abc"));
        Assert.Null(CustomizeDialog.ParseNumber("1e999")); // not finite
    }

    [AvaloniaFact]
    public void CustomizedValuesShowEverywhereAndReset()
    {
        var (hub, window, custom) = Open();
        using var _ = hub;
        var row = Rows(window).First(r => r.Name == "CPU (Tctl/Tdie)");
        var raw = hub.Get(row.Id).Current!.Value;
        custom.Customize(row.Id, new ValueCustomization { Multiply = 1.8, Offset = 32, Unit = "°F", Decimals = 2 });
        window.RefreshValues();
        Dispatcher.UIThread.RunJobs();

        var shown = Rows(window).Single(r => r.Id == row.Id);
        Assert.EndsWith(" °F", shown.Current);
        Assert.Equal(SensorPresentation.Format(raw * 1.8 + 32, "°F", 2), shown.Current);
        Assert.Equal("°F", SensorPresentation.DisplayUnit(row.Item)); // logs, graphs and alerts use these
        Assert.Equal(raw * 1.8 + 32, SensorPresentation.Scale(row.Item, raw), 6);
        Assert.Equal(raw, SensorPresentation.BuiltInScale(row.Item, raw));

        custom.Customize(row.Id, new ValueCustomization()); // all defaults = no customization
        window.RefreshValues();
        Dispatcher.UIThread.RunJobs();
        Assert.EndsWith(" °C", Rows(window).Single(r => r.Id == row.Id).Current);
        Assert.Null(custom.ValuesOf(row.Id));
    }

    [AvaloniaFact]
    public void CustomizeDialogValidatesPreviewsAndReturnsTheValues()
    {
        var (hub, window, _) = Open();
        using var __ = hub;
        var row = Rows(window).First(r => r.Name == "CPU (Tctl/Tdie)");
        var dialog = new CustomizeDialog(row.Item, 50);
        dialog.Show();
        Dispatcher.UIThread.RunJobs();
        var boxes = dialog.GetVisualDescendants().OfType<TextBox>().ToList(); // label, unit, multiply, offset
        var (name, unit, multiply, offset) = (boxes[0], boxes[1], boxes[2], boxes[3]);
        var ok = dialog.GetVisualDescendants().OfType<Button>().Single(b => (string)b.Content! == "OK");
        string Preview() => dialog.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? "").Single(t => t.StartsWith("Now:") || t.Contains("needs") || t.Contains("can't"));

        Assert.Equal("CPU (Tctl/Tdie)", name.Text);
        Assert.Equal("°C", unit.Text);
        Assert.Equal("Now: 50.0 °C → 50.0 °C", Preview());

        multiply.Text = "abc";
        Dispatcher.UIThread.RunJobs();
        Assert.False(ok.IsEnabled);
        Assert.Contains("needs a number", Preview());
        multiply.Text = "0";
        Dispatcher.UIThread.RunJobs();
        Assert.Contains("can't be 0", Preview());

        multiply.Text = "1,8";
        offset.Text = "32";
        unit.Text = "°F";
        name.Text = "CPU °F";
        Dispatcher.UIThread.RunJobs();
        Assert.True(ok.IsEnabled);
        Assert.Equal("Now: 50.0 °C → 122.0 °F", Preview());
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        dialog.CaptureRenderedFrame()?.Save(Path.Combine(Path.GetTempPath(), "openhw-customize.png"));
        ok.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("CPU °F", dialog.Outcome!.Name);
        Assert.Equal(new ValueCustomization { Multiply = 1.8, Offset = 32, Unit = "°F" }, dialog.Outcome.Values);
    }

    [AvaloniaFact]
    public void AnEmptiedUnitShowsNoneAndYesNoSensorsCantBeCustomized()
    {
        var (hub, window, custom) = Open();
        using var _ = hub;
        var row = Rows(window).First(r => r.Name == "CPU (Tctl/Tdie)");
        custom.Customize(row.Id, new ValueCustomization { Unit = "" });
        window.RefreshValues();
        Dispatcher.UIThread.RunJobs();
        Assert.DoesNotContain("°", Rows(window).Single(r => r.Id == row.Id).Current);

        var flag = hub.Inventory.FirstOrDefault(i => SensorPresentation.BuiltInUnit(i) == "flag");
        if (flag is null) return; // no Yes/No sensor on this machine
        Assert.False(SensorPresentation.CanCustomize(flag));
        custom.Customize(flag.Id, new ValueCustomization { Multiply = 5, Unit = "x" }); // a hand-edited file
        Assert.Equal("flag", SensorPresentation.DisplayUnit(flag));
        Assert.Equal(1, SensorPresentation.Scale(flag, 1));
    }

    [AvaloniaFact]
    public void LayoutWindowMovesSensorsAndGroupsAndRestores()
    {
        var (hub, window, custom) = Open();
        using var _ = hub;
        var layout = new LayoutWindow(hub);
        layout.ShowFor(null);
        Dispatcher.UIThread.RunJobs();

        // A sensor moved up swaps with the one above it, in the main window too.
        var entries = layout.ListEntries;
        var first = entries.First(e => !e.IsGroup);
        var second = entries.First(e => !e.IsGroup && e.GroupKey == first.GroupKey && e.Key != first.Key);
        layout.Select(layout.ListEntries.Single(e => e.Key == second.Key && !e.IsGroup));
        layout.MoveSelected(-1);
        Dispatcher.UIThread.RunJobs();
        var ids = Rows(window).Where(r => r.Depth == 0).Select(r => r.Id).ToList();
        Assert.True(ids.IndexOf(second.Key) < ids.IndexOf(first.Key));

        // The last group moved to the top becomes the first group.
        var titlesBefore = GroupTitles(window);
        var lastGroup = layout.ListEntries.Last(e => e.IsGroup);
        layout.Select(lastGroup);
        layout.MoveSelected(int.MinValue);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(titlesBefore[^1], GroupTitles(window)[0]);
        Assert.Equal(lastGroup.Key, layout.ListEntries.First(e => e.IsGroup).Key); // the selection followed

        // Saved: a fresh load (as at the next start) has the same order.
        var saved = File.ReadAllText(Path.Combine(Environment.GetEnvironmentVariable("XDG_CONFIG_HOME")!, "openhw", "sensors.json"));
        Assert.Contains(lastGroup.Key, saved);

        custom.RestoreOriginalOrder();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(titlesBefore, GroupTitles(window));
        Assert.Equal(first.Key, Rows(window).First(r => r.Depth == 0).Id);
        layout.Close();
    }

    [AvaloniaFact]
    public void LayoutWindowHidesIntoTheHiddenListAndTurnsMonitoringOff()
    {
        var (hub, window, custom) = Open();
        using var _ = hub;
        var layout = new LayoutWindow(hub);
        var target = Rows(window).First(r => r.Depth == 0);
        layout.ShowFor(target.Id); // a row's "Layout settings…" opens with that sensor selected
        Dispatcher.UIThread.RunJobs();
        var boxes = layout.GetVisualDescendants().OfType<CheckBox>().ToList();
        var show = boxes.Single(b => (string)b.Content! == "Show");
        var monitoring = boxes.Single(b => (string)b.Content! == "Monitoring");
        Assert.True(show.IsChecked);
        layout.CaptureRenderedFrame()?.Save(Path.Combine(Path.GetTempPath(), "openhw-layout.png"));

        monitoring.IsChecked = false;
        Dispatcher.UIThread.RunJobs();
        Assert.True(custom.IsDisabled(target.Id));
        Assert.Equal("No", layout.ListEntries.Single(e => e.Key == target.Id).Monitoring);

        show.IsChecked = false;
        Dispatcher.UIThread.RunJobs();
        Assert.True(custom.IsHidden(target.Id));
        Assert.DoesNotContain(layout.ListEntries, e => e.Key == target.Id);
        Assert.Contains(layout.HiddenEntries, e => e.Key == target.Id);
        Assert.DoesNotContain(Rows(window), r => r.Id == target.Id);

        // Selected in the hidden list, Show brings it back (order buttons only work in the main list).
        layout.Select(layout.HiddenEntries.Single(e => e.Key == target.Id));
        Dispatcher.UIThread.RunJobs();
        show.IsChecked = true;
        Dispatcher.UIThread.RunJobs();
        Assert.False(custom.IsHidden(target.Id));
        Assert.Contains(Rows(window), r => r.Id == target.Id);
        layout.Close();
    }

    [AvaloniaFact]
    public void PausingWhileHiddenKeepsOnlyPinnedSensorsLive()
    {
        var (hub, window, _) = Open();
        using var __ = hub;
        var rows = Rows(window).Where(r => !r.Disabled).ToList();
        var pinned = rows[0].Id;
        var other = rows[1].Id;
        hub.SetPinned([pinned]);

        window.Hide(); // setting off: an expanded group keeps reading while hidden, as HWiNFO does
        Assert.False(hub.IsPaused(other));

        window.PauseWhenHidden = true;
        Assert.True(hub.IsPaused(other));
        Assert.False(hub.IsPaused(pinned)); // tray, alert, logged or graphed

        window.Show();
        Dispatcher.UIThread.RunJobs();
        hub.Sample();
        Assert.False(hub.IsPaused(other)); // read again once shown; the layout (expanded groups) is unchanged
        Assert.Equal(rows.Count, Rows(window).Count(r => !r.Disabled));

        window.WindowState = WindowState.Minimized; // minimized counts as hidden
        Assert.True(hub.IsPaused(other));
        window.WindowState = WindowState.Normal;
        window.PauseWhenHidden = false;
    }

    [Fact]
    public void NewSettingsAreSavedAndLoaded()
    {
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Directory.CreateTempSubdirectory("openhw-newsettings-").FullName);
        var settings = AppSettings.Load();
        Assert.False(settings.PauseWhenHidden);
        Assert.False(settings.SoftwareRendering);
        settings.PauseWhenHidden = true;
        settings.SoftwareRendering = true;
        settings.Save();
        var again = AppSettings.Load();
        Assert.True(again.PauseWhenHidden);
        Assert.True(again.SoftwareRendering);
    }
}
