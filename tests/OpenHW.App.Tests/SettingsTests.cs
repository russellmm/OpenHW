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
            TrayIcons = Enumerable.Range(0, 20).Select(i => new { SensorId = $"hwmon:x:{i}", Colour = "#123456" }),
        }));
        var settings = AppSettings.Load();
        Assert.Equal(2, settings.PollSeconds);
        Assert.Equal(AppSettings.MaxTrayIcons, settings.TrayIcons.Count);
        Assert.All(settings.TrayIcons, t => Assert.Equal(AppSettings.Colours[0].Hex, t.Colour));
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
        settings.TrayIcons.Add(new TraySensor { SensorId = "hsmp:active-limit", Colour = "#2E86DE" });
        var window = new SettingsWindow(settings, hub, () => { });
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
        Assert.Equal(expected, SettingsWindow.Matches(label, query));

    [Avalonia.Headless.XUnit.AvaloniaFact]
    public void TypingNarrowsTheList()
    {
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Directory.CreateTempSubdirectory("openhw-search-").FullName);
        using var hub = new Sensors.SensorHub();
        hub.Sample();
        var window = new SettingsWindow(AppSettings.Load(), hub, () => { });
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
