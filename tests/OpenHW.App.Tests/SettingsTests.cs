using System.Text.Json;
using OpenHW.App.Settings;
using Xunit;

namespace OpenHW.App.Tests;

public sealed class SettingsTests
{
    private static string Isolate()
    {
        var dir = Directory.CreateTempSubdirectory("openhw-settings-").FullName;
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", dir);
        return dir;
    }

    [Fact]
    public void DefaultsMatchOpenHwBeforeSettings()
    {
        Isolate();
        var settings = AppSettings.Load();
        Assert.Equal(2, settings.PollSeconds);
        var icon = Assert.Single(settings.TrayIcons);
        Assert.Equal((AppSettings.CpuTemperature, "#C0392B"), (icon.SensorId, icon.Colour));
        Assert.False(settings.StartAtLogin);
    }

    [Fact]
    public void AHandEditedFileIsClampedToWhatTheUiOffers()
    {
        var dir = Isolate();
        Directory.CreateDirectory(Path.Combine(dir, "openhw"));
        File.WriteAllText(Path.Combine(dir, "openhw", "settings.json"), JsonSerializer.Serialize(new
        {
            PollSeconds = 0.01,
            TrayIcons = Enumerable.Range(0, 20).Select(i => new { SensorId = $"hwmon:x:{i}", Colour = i == 0 ? "#123456" : "blue" }),
        }));
        var settings = AppSettings.Load();
        Assert.Equal(2, settings.PollSeconds);
        Assert.Equal(AppSettings.MaxTrayIcons, settings.TrayIcons.Count);
        Assert.Equal("#123456", settings.TrayIcons[0].Colour); // custom colors are kept…
        Assert.All(settings.TrayIcons.Skip(1), t => Assert.Equal(AppSettings.Colours[0].Hex, t.Colour)); // …anything else isn't
    }

    [Fact]
    public void StartAtLoginWritesAndRemovesTheAutostartEntry()
    {
        Isolate();
        var settings = AppSettings.Load();
        settings.StartAtLogin = true;
        settings.StartInTray = true;
        settings.Save();
        var entry = File.ReadAllText(AppSettings.AutostartPath);
        Assert.Contains("--tray", entry);
        Assert.Contains("Type=Application", entry);
        Assert.True(AppSettings.Load().StartAtLogin);

        settings.StartAtLogin = false;
        settings.Save();
        Assert.False(File.Exists(AppSettings.AutostartPath));
    }

    [Theory]
    [InlineData(null, "--")]
    [InlineData(44.6, "45")]
    [InlineData(250.0, "250")]
    [InlineData(5324.0, "5.3k")]
    [InlineData(12500.0, "13k")]
    [InlineData(1.25, "1.25")]
    [InlineData(0.5, "0.5")]
    public void TrayTextFitsATile(double? value, string text) => Assert.Equal(text, App.TrayText(value));

    [Theory]
    [InlineData("°C", "°")]
    [InlineData("°F", "°")] // a unit set in Customize values
    [InlineData("W", "W")]
    [InlineData("V", "V")]
    [InlineData("A", "A")]
    [InlineData("%", "%")]
    [InlineData("MHz", "")] // long, and "4.5k" + "MHz" would be wrong
    [InlineData("RPM", "")]
    [InlineData("MB", "")]
    [InlineData("MB/s", "")]
    [InlineData("", "")]
    public void TrayTilesShowShortUnitsOnly(string unit, string shown) => Assert.Equal(shown, App.TrayUnit(unit));

    [Fact]
    public void ASecondLaunchAsksTheFirstToShow()
    {
        Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", Directory.CreateTempSubdirectory("openhw-run-").FullName);
        using var first = SingleInstance.Claim();
        Assert.NotNull(first);
        using var shown = new ManualResetEventSlim();
        first!.ShowRequested += shown.Set;

        Assert.Null(SingleInstance.Claim());
        Assert.True(shown.Wait(TimeSpan.FromSeconds(5)));
    }
}

public sealed class SettingsWindowTests
{
    [Avalonia.Headless.XUnit.AvaloniaFact]
    public void ShowsTrayIconsAndOffersEverySensor()
    {
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Directory.CreateTempSubdirectory("openhw-settings-ui-").FullName);
        using var hub = new Sensors.SensorHub();
        hub.Sample();
        var settings = AppSettings.Load();
        settings.TrayIcons.Add(new TraySensor { SensorId = "hsmp:active-limit", Colour = "#1E90FF" });
        settings.AddCustomColour("#1E90FF");
        settings.AddCustomColour("#FF6F61");
        var window = new TrayIconsWindow(settings, hub, () => { }, new FakeTrayHover());
        window.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window).OfType<Avalonia.Controls.TextBox>()
            .First(t => (t.Watermark as string ?? "").StartsWith("Search", StringComparison.Ordinal)).Text = "gpu power";
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(window)?.Save(Path.Combine(Path.GetTempPath(), "openhw-settings.png"));

        var texts = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window).OfType<Avalonia.Controls.TextBlock>().Select(t => t.Text ?? "").ToList();
        Assert.Contains(texts, t => t.StartsWith("CPU temperature", StringComparison.Ordinal)); // the first tray row
        Assert.Contains(texts, t => t.StartsWith("Active Frequency Limit  ·  CPU", StringComparison.Ordinal));
    }
}

public sealed class SensorSearchTests
{
    [Theory]
    [InlineData("GPU [#1]: NVIDIA RTX PRO 6000 › GPU Power", "gpu power", true)]
    [InlineData("GPU [#1]: NVIDIA RTX PRO 6000 › GPU Power", "power 6000", true)]   // any order
    [InlineData("GPU [#1]: NVIDIA RTX PRO 6000 › GPU Power", "  GPU   POWER ", true)] // spaces and case don't matter
    [InlineData("GPU [#1]: NVIDIA RTX PRO 6000 › GPU Power", "gpu temperature", false)]
    [InlineData("CPU [#0]: AMD Ryzen Threadripper 9970X: Enhanced › CCD2 (Tdie)", "ccd2 tdie", true)]
    [InlineData("anything", "", true)]
    public void EveryWordMustMatch(string label, string query, bool expected) =>
        Assert.Equal(expected, TrayIconsWindow.Matches(label, query));

    [Avalonia.Headless.XUnit.AvaloniaFact]
    public void TypingNarrowsTheList()
    {
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Directory.CreateTempSubdirectory("openhw-search-").FullName);
        using var hub = new Sensors.SensorHub();
        hub.Sample();
        var window = new TrayIconsWindow(AppSettings.Load(), hub, () => { }, new FakeTrayHover());
        window.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        var search = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window).OfType<Avalonia.Controls.TextBox>().First(t => (t.Watermark as string ?? "").StartsWith("Search", StringComparison.Ordinal));
        var list = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window).OfType<Avalonia.Controls.ListBox>().Single();
        var all = list.ItemCount;

        search.Text = "fclk";
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        var fclk = Assert.Single(list.Items.OfType<Avalonia.Controls.ListBoxItem>());
        Assert.EndsWith("Infinity Fabric Clock (FCLK)", (string)fclk.Content!);
        Assert.Same(fclk, list.SelectedItem); // Enter would add it

        search.Text = "";
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.Equal(all, list.ItemCount);
        Assert.True(all > 100);
    }
}

public sealed class ColorTests
{
    [Theory]
    [InlineData("#C0392B", true)]
    [InlineData("#a1b2c3", true)]
    [InlineData("#12345", false)]
    [InlineData("C0392B", false)]
    [InlineData("#GGGGGG", false)]
    [InlineData(null, false)]
    public void OnlyRrGgBbIsAColor(string? hex, bool expected) => Assert.Equal(expected, AppSettings.IsColor(hex));

    [Fact]
    public void PresetsHaveNamesCustomColorsDont()
    {
        Assert.Equal("Teal", AppSettings.PresetName("#16a085"));
        Assert.Null(AppSettings.PresetName("#123456"));
    }

    [Avalonia.Headless.XUnit.AvaloniaFact]
    public void ACustomColorShowsAsItsOwnSwatchWithCustomAfterIt()
    {
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Directory.CreateTempSubdirectory("openhw-colors-").FullName);
        using var hub = new Sensors.SensorHub();
        hub.Sample();
        var settings = AppSettings.Load();
        settings.TrayIcons[0].Colour = "#123456";
        var window = new TrayIconsWindow(settings, hub, () => { }, new FakeTrayHover());
        window.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        var box = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window).OfType<Avalonia.Controls.ComboBox>()
            .First(c => c.Items.OfType<Avalonia.Controls.ComboBoxItem>().Any(i => i.Tag is "custom"));
        var tags = box.Items.OfType<Avalonia.Controls.ComboBoxItem>().Select(i => (string)i.Tag!).ToList();
        Assert.Equal(AppSettings.Colours.Length + 2, tags.Count);
        Assert.Equal(["#123456", "custom"], tags.TakeLast(2));
        Assert.Equal("#123456", (string)((Avalonia.Controls.ComboBoxItem)box.SelectedItem!).Tag!);
    }
}

public sealed class TrayDuplicateTests
{
    [Avalonia.Headless.XUnit.AvaloniaFact]
    public void SensorsInTheTrayAreGrayedAndCantBeAddedTwice()
    {
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Directory.CreateTempSubdirectory("openhw-dupes-").FullName);
        using var hub = new Sensors.SensorHub();
        hub.Sample();
        var settings = AppSettings.Load(); // default: the automatic CPU temperature
        settings.TrayIcons.Add(new TraySensor { SensorId = "hsmp:fclk", Colour = "#2E86DE" });
        var window = new TrayIconsWindow(settings, hub, () => { }, new FakeTrayHover());
        window.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        var search = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window).OfType<Avalonia.Controls.TextBox>().First(t => (t.Watermark as string ?? "").StartsWith("Search", StringComparison.Ordinal));
        var list = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window).OfType<Avalonia.Controls.ListBox>().Single();
        var add = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window).OfType<Avalonia.Controls.Button>().Single(b => (b.Content as string) == "Add to tray");

        search.Text = "fclk";
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        var fclk = Assert.Single(list.Items.OfType<Avalonia.Controls.ListBoxItem>());
        Assert.False(fclk.IsEnabled);
        Assert.EndsWith("(in tray)", (string)fclk.Content!);
        Assert.Null(list.SelectedItem);
        add.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        Assert.Equal(2, settings.TrayIcons.Count);

        // The automatic CPU temperature is the same sensor as the Tctl row it resolves to.
        search.Text = "Tctl";
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.All(list.Items.OfType<Avalonia.Controls.ListBoxItem>().Where(i => (string)i.Tag! == hub.CpuTemperatureId()), i => Assert.False(i.IsEnabled));

        // Something not in the tray is still offered and selected.
        search.Text = "mclk";
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.True(Assert.Single(list.Items.OfType<Avalonia.Controls.ListBoxItem>()).IsEnabled);
        Assert.NotNull(list.SelectedItem);
    }

    [Xunit.Fact]
    public void ASettingsFileWithDuplicatesKeepsOneOfEach()
    {
        var dir = Directory.CreateTempSubdirectory("openhw-dupefile-").FullName;
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", dir);
        Directory.CreateDirectory(Path.Combine(dir, "openhw"));
        File.WriteAllText(Path.Combine(dir, "openhw", "settings.json"),
            """{"TrayIcons":[{"SensorId":"hsmp:fclk","Colour":"#2E86DE"},{"SensorId":"hsmp:fclk","Colour":"#27AE60"},{"SensorId":"hsmp:mclk","Colour":"#C0392B"}]}""");
        var icons = AppSettings.Load().TrayIcons;
        Assert.Equal(["hsmp:fclk", "hsmp:mclk"], icons.Select(t => t.SensorId));
        Assert.Equal("#2E86DE", icons[0].Colour); // the first one wins
    }
}

public sealed class ColorDialogRenderTests
{
    [Avalonia.Headless.XUnit.AvaloniaFact]
    public void RendersThePicker()
    {
        var dialog = new ColorDialog("#16A085");
        dialog.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(dialog)?.Save(Path.Combine(Path.GetTempPath(), "openhw-color-dialog.png"));
        Assert.Single(Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(dialog).OfType<Avalonia.Controls.ColorView>());
    }
}

public sealed class CustomColorTests
{
    [Fact]
    public void TheCustomListKeepsNewColorsOnly()
    {
        var s = new AppSettings();
        s.AddCustomColour("#1e90ff");
        s.AddCustomColour("#1E90FF");   // same color
        s.AddCustomColour("#16A085");   // the Teal preset
        s.AddCustomColour("blue");      // not a color
        Assert.Equal(["#1E90FF"], s.CustomColours);
        for (var i = 0; i < 20; i++) s.AddCustomColour($"#0000{i:X2}");
        Assert.Equal(AppSettings.MaxCustomColours, s.CustomColours.Count);
        Assert.DoesNotContain("#1E90FF", s.CustomColours); // the oldest went first
        Assert.Equal(AppSettings.Colours.Length + AppSettings.MaxCustomColours, s.AllColours().Count);
    }

    [Theory]
    [InlineData("#1e90ff", "#1E90FF")]
    [InlineData("1E90FF", "#1E90FF")]
    [InlineData("  #abcdef ", "#ABCDEF")]
    [InlineData("#12345", null)]
    [InlineData("#zzzzzz", null)]
    [InlineData("", null)]
    public void HexIsReadWithOrWithoutTheHash(string text, string? expected) => Assert.Equal(expected, ColorDialog.Parse(text));

    [Avalonia.Headless.XUnit.AvaloniaFact]
    public void TypingAHexCodeAddsItToTheListAndEveryIconsChoices()
    {
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Directory.CreateTempSubdirectory("openhw-custom-").FullName);
        using var hub = new Sensors.SensorHub();
        hub.Sample();
        var settings = AppSettings.Load();
        var window = new TrayIconsWindow(settings, hub, () => { }, new FakeTrayHover());
        window.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        var hex = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window).OfType<Avalonia.Controls.TextBox>().Single(t => t.Watermark as string == "#RRGGBB");
        var add = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window).OfType<Avalonia.Controls.Button>().Single(b => b.Content as string == "Add color");

        hex.Text = "1e90f";
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.False(add.IsEnabled);
        hex.Text = "1e90ff";
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.True(add.IsEnabled);
        add.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.Equal(["#1E90FF"], settings.CustomColours);
        Assert.Equal(["#1E90FF"], AppSettings.Load().CustomColours); // saved
        var texts = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window).OfType<Avalonia.Controls.TextBlock>().Select(t => t.Text).ToList();
        Assert.Contains("#1E90FF", texts); // its chip
        var box = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window).OfType<Avalonia.Controls.ComboBox>().First(c => c.Items.OfType<Avalonia.Controls.ComboBoxItem>().Any(i => i.Tag is "custom"));
        Assert.Contains(box.Items.OfType<Avalonia.Controls.ComboBoxItem>(), i => i.Tag is "#1E90FF");
    }

    [Avalonia.Headless.XUnit.AvaloniaFact]
    public void TheDialogsHexFieldMovesTheWheel()
    {
        var dialog = new ColorDialog("#C0392B");
        dialog.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        var hex = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(dialog).OfType<Avalonia.Controls.TextBox>().First();
        var view = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(dialog).OfType<Avalonia.Controls.ColorView>().Single();
        Assert.Equal("#C0392B", hex.Text);
        hex.Text = "#1E90FF";
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.Equal(Avalonia.Media.Color.Parse("#1E90FF"), view.Color);
        view.Color = Avalonia.Media.Color.Parse("#27AE60");
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.Equal("#27AE60", hex.Text);
        Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(dialog)?.Save(Path.Combine(Path.GetTempPath(), "openhw-color-dialog.png"));
    }
}

public sealed class GeneralSettingsTests
{
    [Avalonia.Headless.XUnit.AvaloniaFact]
    public void SettingsHoldsGeneralOptionsAndPointsToTrayIcons()
    {
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Directory.CreateTempSubdirectory("openhw-general-").FullName);
        var opened = false;
        var window = new SettingsWindow(AppSettings.Load(), () => { }, () => opened = true);
        window.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        var all = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window).ToList();
        Assert.Contains(all.OfType<Avalonia.Controls.CheckBox>(), c => c.Content as string == "Start OpenHW when I log in");
        Assert.DoesNotContain(all.OfType<Avalonia.Controls.TextBox>(), t => (t.Watermark as string ?? "").StartsWith("Search", StringComparison.Ordinal)); // the tray parts moved out
        all.OfType<Avalonia.Controls.Button>().Single(b => b.Content as string == "Tray Icons…")
            .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        Assert.True(opened);
        Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(window)?.Save(Path.Combine(Path.GetTempPath(), "openhw-general-settings.png"));
    }
}

public sealed class WindowScaleTests
{
    [Fact]
    public void AFixedScaleOverridesTheDesktopAndAutomaticUndoesOnlyThat()
    {
        try
        {
            WindowScale.Apply(150);
            Assert.Equal("1.5", Environment.GetEnvironmentVariable("AVALONIA_GLOBAL_SCALE_FACTOR"));
            Assert.NotNull(Environment.GetEnvironmentVariable("AVALONIA_SCREEN_SCALE_FACTORS")); // stops Xft.dpi multiplying it
            Assert.Equal(150, WindowScale.Fixed);

            WindowScale.Apply(0); // a restart back to Automatic inherits the variables above
            Assert.Null(Environment.GetEnvironmentVariable("AVALONIA_GLOBAL_SCALE_FACTOR"));
            Assert.Null(Environment.GetEnvironmentVariable("AVALONIA_SCREEN_SCALE_FACTORS"));
            Assert.Null(WindowScale.Fixed);

            Environment.SetEnvironmentVariable("AVALONIA_GLOBAL_SCALE_FACTOR", "2"); // the user's own
            WindowScale.Apply(0);
            Assert.Equal("2", Environment.GetEnvironmentVariable("AVALONIA_GLOBAL_SCALE_FACTOR"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("AVALONIA_GLOBAL_SCALE_FACTOR", null);
            Environment.SetEnvironmentVariable("AVALONIA_SCREEN_SCALE_FACTORS", null);
        }
    }

    [Avalonia.Headless.XUnit.AvaloniaFact]
    public void PickingAScaleSavesItAndOffersARestart()
    {
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Directory.CreateTempSubdirectory("openhw-scale-").FullName);
        File.WriteAllText(Path.Combine(Directory.CreateDirectory(Path.Combine(Environment.GetEnvironmentVariable("XDG_CONFIG_HOME")!, "openhw")).FullName, "settings.json"),
            """{ "WindowScale": 130 }""");
        var settings = AppSettings.Load();
        Assert.Equal(0, settings.WindowScale); // not a choice → Automatic
        var restarted = false;
        var window = new SettingsWindow(settings, () => { }, () => { }, () => restarted = true);
        window.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        var all = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window).ToList();
        var scale = all.OfType<Avalonia.Controls.ComboBox>().Single(c => c.Items.OfType<Avalonia.Controls.ComboBoxItem>().Any(i => i.Tag is int));
        Assert.StartsWith("Automatic (now 100 %)", (string)((Avalonia.Controls.ComboBoxItem)scale.SelectedItem!).Content!);
        var restart = all.OfType<Avalonia.Controls.Button>().Single(b => b.Content as string == "Restart OpenHW now");
        Assert.False(restart.IsVisible);

        scale.SelectedItem = scale.Items.OfType<Avalonia.Controls.ComboBoxItem>().Single(i => i.Tag is 175);
        Assert.Equal(175, AppSettings.Load().WindowScale);
        Assert.True(restart.IsVisible);
        Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(window)?.Save(Path.Combine(Path.GetTempPath(), "openhw-window-scale.png"));
        restart.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        Assert.True(restarted);

        scale.SelectedItem = scale.Items.OfType<Avalonia.Controls.ComboBoxItem>().Single(i => i.Tag is 0);
        Assert.False(restart.IsVisible); // back to what this run uses
    }
}

public sealed class ForeignAutostartTests
{
    // RTXinVM's reroute, as its setup.py writes it on the owner's machine.
    private const string Rtxgpu = """
        [Desktop Entry]
        Type=Application
        Name=OpenHW
        Comment=Hardware monitor
        Exec=/home/russellm/.local/bin/rtxgpu --app openhw
        Icon=openhw
        Terminal=false
        X-GNOME-Autostart-enabled=true

        """;

    private static void Isolate(string? entry)
    {
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Directory.CreateTempSubdirectory("openhw-autostart-").FullName);
        if (entry is null) return;
        Directory.CreateDirectory(Path.GetDirectoryName(AppSettings.AutostartPath)!);
        File.WriteAllText(AppSettings.AutostartPath, entry);
    }

    [Fact]
    public void AnotherLaunchersEntryIsKeptOnEverySave()
    {
        Isolate(Rtxgpu);
        Assert.Equal("/home/russellm/.local/bin/rtxgpu --app openhw", AppSettings.ForeignAutostart());
        var settings = AppSettings.Load();
        Assert.True(settings.StartAtLogin);
        var before = File.GetLastWriteTimeUtc(AppSettings.AutostartPath);
        settings.PollSeconds = 5;
        settings.StartInTray = false;
        settings.Save();
        Assert.Equal(Rtxgpu, File.ReadAllText(AppSettings.AutostartPath)); // not even rewritten
        Assert.Equal(before, File.GetLastWriteTimeUtc(AppSettings.AutostartPath));
    }

    [Fact]
    public void TurningLoginStartOffAndOnKeepsTheOtherLaunchersCommand()
    {
        Isolate(Rtxgpu);
        var settings = AppSettings.Load();
        settings.StartAtLogin = false;
        settings.Save();
        var off = File.ReadAllText(AppSettings.AutostartPath);
        Assert.Contains("Exec=/home/russellm/.local/bin/rtxgpu --app openhw", off);
        Assert.Contains("Hidden=true", off);
        Assert.False(AppSettings.Load().StartAtLogin);

        settings.StartAtLogin = true;
        settings.Save();
        var on = File.ReadAllText(AppSettings.AutostartPath);
        Assert.Contains("Exec=/home/russellm/.local/bin/rtxgpu --app openhw", on);
        Assert.DoesNotContain("Hidden=true", on);
        Assert.Contains("X-GNOME-Autostart-enabled=true", on);
        Assert.True(AppSettings.Load().StartAtLogin);
    }

    [Fact]
    public void OpenHwsOwnEntryIsStillItsToRewrite()
    {
        Isolate("[Desktop Entry]\nExec=\"/usr/bin/openhw\" --tray\n");
        Assert.Null(AppSettings.ForeignAutostart());
        var settings = AppSettings.Load();
        settings.StartInTray = false;
        settings.Save();
        Assert.DoesNotContain("--tray", File.ReadAllText(AppSettings.AutostartPath));
    }

    [Avalonia.Headless.XUnit.AvaloniaFact]
    public void SettingsSaysWhoStartsOpenHwAtLogin()
    {
        Isolate(Rtxgpu);
        var window = new SettingsWindow(AppSettings.Load(), () => { }, () => { });
        window.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        var all = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window).ToList();
        Assert.Contains(all.OfType<Avalonia.Controls.TextBlock>(), t => t.IsVisible && (t.Text ?? "").Contains("rtxgpu --app openhw"));
        Assert.False(all.OfType<Avalonia.Controls.CheckBox>().Single(c => (c.Content as string ?? "").StartsWith("…with only")).IsEnabled);
        Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(window)?.Save(Path.Combine(Path.GetTempPath(), "openhw-login-entry.png"));
    }
}

public sealed class RestartTests
{
    [Fact]
    public void AUserServiceIsRestartedByAWaiterOutsideTheUnit()
    {
        const string Rtxgpu = "0::/user.slice/user-1000.slice/user@1000.service/rtxgpu.slice/rtxgpu-openhw.service\n";
        var args = Program.SystemdRestart(Rtxgpu, ["/usr/lib/openhw/app/openhw"], _ => "RTXinVM app openhw");
        Assert.NotNull(args);
        Assert.Equal(["--user", "--quiet", "--collect", "--slice=rtxgpu.slice", "--", "/bin/sh", "-c"], args.Take(7));
        Assert.Equal(["sh", "rtxgpu-openhw.service", "rtxgpu.slice", "RTXinVM app openhw", "/usr/lib/openhw/app/openhw"], args.Skip(8));
        // For the live check in the handoff: the same argv for a throwaway unit running `sleep`.
        var probe = Program.SystemdRestart("0::/user.slice/user-1000.slice/user@1000.service/ohwtest.slice/ohw-restart-test.service\n",
            ["/usr/bin/sleep", "300"], _ => "OpenHW restart test");
        File.WriteAllLines(Path.Combine(Path.GetTempPath(), "openhw-restart-argv.txt"), probe!.Select(a => a.Replace("\n", "\\n")));
    }

    [Theory]
    [InlineData("0::/user.slice/user-1000.slice/user@1000.service/app.slice/app-gnome-openhw-12244.scope\n")] // desktop launch
    [InlineData("0::/user.slice/user-1000.slice/user@1000.service/app.slice/app-com.anthropic.Claude-23722.scope\n")]
    [InlineData("0::/system.slice/openhw.service\n")] // not a user service
    public void OtherLaunchesStartTheNewProcessDirectly(string cgroup) =>
        Assert.Null(Program.SystemdRestart(cgroup, ["/usr/bin/openhw"], _ => ""));
}

public sealed class ExitTests
{
    // App.CloseAvaloniaDBus reaches Avalonia's own session connection by reflection; an Avalonia upgrade that renames
    // it would bring back the crash on exit (tools/tray-exit-test reproduces it), so the field must still be there.
    [Fact]
    public void AvaloniasDBusConnectionFieldIsWhereTheExitFixLooksForIt()
    {
        var helper = Type.GetType("Avalonia.FreeDesktop.DBusHelper, Avalonia.FreeDesktop");
        Assert.NotNull(helper);
        var field = helper.GetField("s_defaultConntection", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(field);
        Assert.Equal("Tmds.DBus.Protocol.Connection", field.FieldType.FullName);
        App.CloseAvaloniaDBus(); // harmless when no connection was made (tests have none)
    }
}
