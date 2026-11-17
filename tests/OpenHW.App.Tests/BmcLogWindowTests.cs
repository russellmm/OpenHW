using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using OpenHW.App.HardwareCheck;
using OpenHW.Hardware;
using Xunit;

namespace OpenHW.App.Tests;

public sealed class BmcLogWindowTests
{
    private static string Fake(string command) => command switch
    {
        "bmcstates" => GpuWire.Encode(new List<BmcStateWire>
        {
            new("CPU_ECC", 0xf9, "Memory module", "Memory", ["presence detected"], 0x40, true),
            new("PSU1 Detect", 0xe3, "Power supply", "Power supply", ["failure detected"], 0x02, true),
            new("Watchdog2", 0xfe, "System board", "Watchdog 2", [], 0, true),
            new("TR1", 0x02, "System board", "Temperature", [], 0, false),
        }),
        "bmcsel" => GpuWire.Encode(new BmcSelReply(2, 0x4000, new DateTime(2026, 11, 17, 9, 21, 0, DateTimeKind.Utc), null,
        [
            new(1, new DateTime(2026, 11, 17, 9, 20, 0, DateTimeKind.Utc), "", "CPU_ECC", "Memory", "correctable ECC", false, "0100"),
            new(2, null, "+5 s after BMC start", "", "Event logging disabled", "log area reset/cleared", false, "0200"),
        ])),
        _ => throw new IOException("openhw-helper: err unknown command"),
    };

    private static List<string> Texts(Window w) =>
        w.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text ?? "").ToList();

    [AvaloniaFact]
    public void ShowsStatesAndTheLogNewestFirst()
    {
        var window = new BmcLogWindow(Fake);
        window.Show();
        for (var i = 0; i < 50 && Texts(window).Contains("Reading the BMC…"); i++) { Thread.Sleep(20); Dispatcher.UIThread.RunJobs(); }
        var texts = Texts(window);
        Assert.Contains("presence detected", texts);
        Assert.Contains("failure detected", texts);
        Assert.Contains("no reading", texts);
        Assert.Contains(texts, t => t.StartsWith("PSU states:", StringComparison.Ordinal));
        Assert.True(texts.IndexOf("log area reset/cleared") < texts.IndexOf("correctable ECC")); // newest first
        Assert.Contains("+5 s after BMC start", texts);
        Assert.True(BmcLogWindow.IsBad("failure detected"));
        Assert.False(BmcLogWindow.IsBad("presence detected"));
        Assert.False(BmcLogWindow.IsBad("correctable ECC — deasserted"));
        var captures = Environment.GetEnvironmentVariable("OPENHW_CAPTURES") ?? Path.GetTempPath();
        window.CaptureRenderedFrame()?.Save(Path.Combine(captures, "openhw-bmc-log.png"));
    }

    [AvaloniaFact]
    public void OldHelperSaysWhatToInstall()
    {
        var window = new BmcLogWindow(_ => throw new IOException("openhw-helper: err unknown command"));
        window.Show();
        for (var i = 0; i < 50 && Texts(window).Contains("Reading the BMC…"); i++) { Thread.Sleep(20); Dispatcher.UIThread.RunJobs(); }
        Assert.Contains(Texts(window), t => t.Contains("protocol 16", StringComparison.Ordinal));
    }
}
