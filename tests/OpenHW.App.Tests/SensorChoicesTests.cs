using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OpenHW.App.Sensors;
using OpenHW.App.SensorWindow;
using Xunit;

namespace OpenHW.App.Tests;

// Hide, Rename and Disable monitoring from the sensor menu, against this machine's sensors.
public sealed class SensorChoicesTests
{
    private static (SensorHub Hub, MainWindow Window, SensorCustomizations Custom) Open()
    {
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Directory.CreateTempSubdirectory("openhw-choices-").FullName);
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

    [AvaloniaFact]
    public void HiddenRowsLeaveAndComeBackWithShowHidden()
    {
        var (hub, window, custom) = Open();
        using var _ = hub;
        var target = Rows(window).First(r => r.Depth == 0);
        custom.SetHidden(target.Id, true);
        Dispatcher.UIThread.RunJobs();
        Assert.DoesNotContain(Rows(window), r => r.Id == target.Id);
        Assert.True(hub.IsPaused(target.Id)); // not shown, so not read

        custom.UnhideAll();
        Dispatcher.UIThread.RunJobs();
        Assert.Contains(Rows(window), r => r.Id == target.Id);
    }

    [AvaloniaFact]
    public void ARenameShowsEverywhereAndKeepsTheExplanation()
    {
        var (hub, window, custom) = Open();
        using var _ = hub;
        var target = Rows(window).First(r => r.Name == "CPU (Tctl/Tdie)");
        custom.Rename(target.Id, "Threadripper");
        Dispatcher.UIThread.RunJobs();
        var renamed = Rows(window).Single(r => r.Id == target.Id);
        Assert.Equal("Threadripper", renamed.Name);
        Assert.Contains("Renamed from: CPU (Tctl/Tdie)", renamed.Tooltip);
        Assert.Contains("control temperature", renamed.Tooltip); // explanation still matched on the built-in name
        Assert.Equal("Threadripper", SensorPresentation.DisplayName(target.Item)); // tray, Settings and logs use this
        Assert.Equal("Threadripper", SensorCustomizations.Current.NameOf(target.Id));

        custom.Rename(target.Id, "  ");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("CPU (Tctl/Tdie)", Rows(window).Single(r => r.Id == target.Id).Name);
    }

    [AvaloniaFact]
    public void DisabledSensorsShowNoValuesAndAreNotRead()
    {
        var (hub, window, custom) = Open();
        using var _ = hub;
        var target = Rows(window).First(r => r.Depth == 0 && r.Current.Length > 0);
        custom.SetDisabled(target.Id, true);
        hub.Sample();
        window.RefreshValues();
        Dispatcher.UIThread.RunJobs();
        var row = Rows(window).Single(r => r.Id == target.Id);
        Assert.True(row.Disabled);
        Assert.EndsWith("(monitoring disabled)", row.Label);
        Assert.Equal("", row.Current);
        Assert.True(hub.IsPaused(target.Id));

        custom.SetDisabled(target.Id, false);
        Dispatcher.UIThread.RunJobs();
        Assert.False(Rows(window).Single(r => r.Id == target.Id).Disabled);
    }
}
