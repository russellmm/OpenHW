using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OpenHW.App.Settings;
using OpenHW.App.Tray;
using Xunit;

namespace OpenHW.App.Tests;

/// <summary>GNOME's extension state, in memory: the UI tests never touch the real session's extensions.</summary>
public sealed class FakeTrayHover(TrayHoverStatus? status = null) : ITrayHover
{
    public TrayHoverStatus Status { get; set; } = status ?? new(TrayHoverState.Off);
    public List<bool> Sets { get; } = [];

    public Task<TrayHoverStatus> GetAsync() => Task.FromResult(Status);

    public Task<TrayHoverStatus> SetAsync(bool on)
    {
        Sets.Add(on);
        Status = Status.State == TrayHoverState.NeedsLogin
            ? Status with { OnAtLogin = on }
            : new(on ? TrayHoverState.On : TrayHoverState.Off);
        return Task.FromResult(Status);
    }
}

public sealed class TrayHoverTests
{
    [Theory]
    [InlineData("['ubuntu-dock@ubuntu.com', 'openhw-tray-hover@russellmm.github.io']\n", new[] { "ubuntu-dock@ubuntu.com", "openhw-tray-hover@russellmm.github.io" })]
    [InlineData("@as []\n", new string[0])]
    [InlineData("['one@x']", new[] { "one@x" })]
    public void ReadsGsettingsLists(string text, string[] expected) =>
        Assert.Equal(expected, GnomeTrayHover.ParseList(text));

    private static (TrayIconsWindow Window, CheckBox Box, TextBlock Note) Open(FakeTrayHover hover)
    {
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Directory.CreateTempSubdirectory("openhw-hover-").FullName);
        var hub = new Sensors.SensorHub();
        var window = new TrayIconsWindow(AppSettings.Load(), hub, () => { }, hover);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var box = window.GetVisualDescendants().OfType<CheckBox>().Single(c => (c.Content as string ?? "").Contains("pointer is over"));
        var texts = window.GetVisualDescendants().OfType<TextBlock>().ToList();
        var note = texts[texts.FindIndex(t => t.Text == "Hover") + 2]; // heading, the box's own text, then the note
        return (window, box, note);
    }

    [Avalonia.Headless.XUnit.AvaloniaFact]
    public void ShowsGnomesStateAndTurnsItOnAndOff()
    {
        var hover = new FakeTrayHover(new(TrayHoverState.On));
        var (window, box, note) = Open(hover);
        Assert.True(box.IsChecked);
        Assert.True(box.IsEnabled);
        Assert.Empty(hover.Sets); // showing the state is not a click
        Assert.StartsWith("On:", note.Text);

        box.IsChecked = false;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal([false], hover.Sets);
        Assert.StartsWith("Off:", note.Text);

        box.IsChecked = true;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal([false, true], hover.Sets);
        Assert.True(box.IsEnabled);

        // Changed in GNOME's Extensions app while the window was hidden: reopening shows it.
        window.Hide();
        hover.Status = new(TrayHoverState.Off);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        Assert.False(box.IsChecked);
        Assert.Equal([false, true], hover.Sets);
        window.Close();
    }

    [Avalonia.Headless.XUnit.AvaloniaFact]
    public void BeforeTheNextLoginTheChoiceIsKeptForThen()
    {
        var hover = new FakeTrayHover(new(TrayHoverState.NeedsLogin, OnAtLogin: false));
        var (window, box, note) = Open(hover);
        Assert.False(box.IsChecked);
        Assert.Contains("Log out and back in once", note.Text);
        Assert.EndsWith("off then.", note.Text);

        box.IsChecked = true;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal([true], hover.Sets);
        Assert.EndsWith("on then.", note.Text);
        window.Close();
    }

    [Avalonia.Headless.XUnit.AvaloniaTheory]
    [InlineData(TrayHoverState.NotInstalled, "Needs OpenHW's GNOME extension")]
    [InlineData(TrayHoverState.NoShell, "Only needed on GNOME")]
    public void CantBeTickedWithoutTheExtension(TrayHoverState state, string text)
    {
        var (window, box, note) = Open(new FakeTrayHover(new(state)));
        Assert.False(box.IsChecked);
        Assert.False(box.IsEnabled);
        Assert.StartsWith(text, note.Text);
        window.Close();
    }

    [Avalonia.Headless.XUnit.AvaloniaFact]
    public void ShowsGnomesErrorWhenItCouldntRunIt()
    {
        var (window, box, note) = Open(new FakeTrayHover(new(TrayHoverState.Failed, "SyntaxError: oops")));
        Assert.True(box.IsChecked);
        Assert.EndsWith("SyntaxError: oops", note.Text);
        window.Close();
    }
}

public sealed class TrayUnitsSettingTests
{
    [Avalonia.Headless.XUnit.AvaloniaFact]
    public void TheUnitsBoxIsSavedAndAppliedAtOnce()
    {
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Directory.CreateTempSubdirectory("openhw-units-").FullName);
        var settings = AppSettings.Load();
        Assert.True(settings.TrayUnits); // on unless turned off
        var applied = 0;
        var window = new TrayIconsWindow(settings, new Sensors.SensorHub(), () => applied++, new FakeTrayHover());
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var box = window.GetVisualDescendants().OfType<CheckBox>().Single(c => (c.Content as string ?? "").StartsWith("Show units", StringComparison.Ordinal));
        Assert.True(box.IsChecked);

        box.IsChecked = false;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(1, applied);
        Assert.False(AppSettings.Load().TrayUnits);
        window.Close();
    }
}
