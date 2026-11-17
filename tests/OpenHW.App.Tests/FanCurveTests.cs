using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using OpenHW.App.Power;
using OpenHW.App.Sensors;
using OpenHW.Hardware;
using Xunit;

namespace OpenHW.App.Tests;

public sealed class FanCurveTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("openhw-fancurves-").FullName;
    private readonly List<string> _sent = [];
    private DateTime _now = new(2026, 11, 17, 10, 0, 0);
    private bool _refuseFourth;

    public void Dispose() => Directory.Delete(_dir, true);

    private static readonly FanCurve Slot5 = new()
    {
        Id = "slot5", Name = "GPU Slot 5", Kind = FanCurveKind.Graph, SensorId = "bmc:PCIE01 Temp",
        Points = [new(40, 30), new(50, 37), new(57, 52), new(62.5, 62), new(70, 77), new(80, 100)],
    };

    private static readonly FanCurve Nvml = new()
    {
        Id = "nvml", Name = "5060 core", Kind = FanCurveKind.Graph, SensorId = "nvml:GPU-x:temp:core", HysteresisC = 2, HysteresisS = 1,
        Points = [new(40, 30), new(80, 100)],
    };

    private FanCurveStore Store()
    {
        var store = FanCurveStore.Load(Path.Combine(_dir, "fan-curves.json"));
        store.Curves.AddRange([Slot5.Clone(), Nvml.Clone()]);
        return store;
    }

    private string Helper(string line)
    {
        _sent.Add(line);
        var p = line.Split(' ');
        return p[0] switch
        {
            "fanset" => $"{p[2]} {p[3]}",
            "gpufanset" => $"{p[2]} {p[3]}",
            "gpufanrelease" => p[2],
            "fanbeat" => "1",
            "fanrelease" => p[2],
            "fancurve" when _refuseFourth && !p[2].Split(',')[3].StartsWith("70:", StringComparison.Ordinal) =>
                throw new IOException("openhw-helper: err The BMC refused the curve for CHA_FAN2 (completion 0xcc)."),
            "fancurve" or "fansource" => "done",
            _ => FanControlTests.Helper(line),
        };
    }

    private FanCurveRunner Runner(FanCurveStore store) => new(new FanClient(Helper), store, () => _now);

    [Fact]
    public void ABmcCurveIsWrittenOnceWithItsSensors()
    {
        var store = Store();
        var runner = Runner(store);
        Assert.Equal(12, runner.BmcNumber("bmc:PCIE01 Temp"));
        Assert.Null(runner.BmcNumber("nvml:GPU-x:temp:core"));
        store.Assignments["bmc:2"] = "slot5";
        var zone = new FanZoneStatus("bmc:2", "CHA_FAN2", "Zone3(CHA_FAN2)", "bmc:CHA_FAN2", false, false, null, null,
            [new(20, 20), new(45, 20), new(65, 20), new(70, 100), new(100, 100)], null, 0, null);
        Assert.Equal("Sensors set, curve written.", runner.ApplyBmc(zone, 1));
        Assert.Contains($"fansource bmc:2 {1L << 11}", _sent);
        var curve = _sent.Single(l => l.StartsWith("fancurve", StringComparison.Ordinal));
        Assert.EndsWith(",100:100", curve, StringComparison.Ordinal);
        runner.Tick(_ => 60);
        Assert.DoesNotContain(_sent, l => l.StartsWith("fanset", StringComparison.Ordinal)); // the BMC runs it: no writes
    }

    [Fact]
    public void IfTheBmcRefusesAFreePointFourItRefitsAt70AndRemembers()
    {
        _refuseFourth = true;
        var store = Store();
        var runner = Runner(store);
        store.Curves.Add(new FanCurve { Id = "lin", Name = "lin", SensorId = "bmc:PCIE01 Temp", Points = [new(40, 30), new(85, 100)] });
        store.Assignments["bmc:2"] = "lin";
        Assert.NotEqual(70, runner.Plan("bmc:2", store.Find("lin")!).Points[3].TemperatureC);
        var zone = new FanZoneStatus("bmc:2", "CHA_FAN2", "", "", false, false, null, null, null, null, 0, null);
        Assert.Contains("refitted", runner.ApplyBmc(zone, 1 << 11), StringComparison.Ordinal);
        Assert.Equal("70", _sent.Last(l => l.StartsWith("fancurve bmc:2", StringComparison.Ordinal)).Split(' ')[2].Split(',')[3].Split(':')[0]);
        Assert.True(FanCurveStore.Load(store.FilePath).FourthAt70);
    }

    [Fact]
    public void ADrivenFanMovesInStepsUpAtOnceDownAfterAMinuteAndCountsWrites()
    {
        var store = Store();
        var runner = Runner(store);
        store.Assignments["bmc:3"] = "nvml";
        var temp = 50.0;
        double? Read(string id) => id == "nvml:GPU-x:temp:core" ? temp : null;
        runner.Tick(Read); // 50 °C → 47.5 % → 50 %
        Assert.Contains("fanset", _sent.Last(l => l.StartsWith("fanset", StringComparison.Ordinal)), StringComparison.Ordinal);
        Assert.EndsWith(" bmc:3 50", _sent.Last(l => l.StartsWith("fanset", StringComparison.Ordinal)), StringComparison.Ordinal);
        Assert.Equal(1, store.WritesOn("bmc:3", _now));

        _now = _now.AddSeconds(2);
        temp = 51; // 49 % → still 50: nothing written
        runner.Tick(Read);
        Assert.Single(_sent, l => l.StartsWith("fanset", StringComparison.Ordinal));

        _now = _now.AddSeconds(2);
        temp = 60; // 65 %: faster goes out at once
        runner.Tick(Read);
        Assert.EndsWith(" 65", _sent.Last(l => l.StartsWith("fanset", StringComparison.Ordinal)), StringComparison.Ordinal);

        _now = _now.AddSeconds(10);
        temp = 50; // slower: held for a minute
        runner.Tick(Read);
        Assert.Equal(2, _sent.Count(l => l.StartsWith("fanset", StringComparison.Ordinal)));
        _now = _now.AddSeconds(61);
        runner.Tick(Read);
        Assert.EndsWith(" 50", _sent.Last(l => l.StartsWith("fanset", StringComparison.Ordinal)), StringComparison.Ordinal);
        Assert.Equal(3, store.WritesOn("bmc:3", _now));
        Assert.Contains("fanbeat", string.Join('\n', _sent), StringComparison.Ordinal);
        Assert.Equal("OpenHW-driven", runner.State("bmc:3").Mode);
        Assert.Equal(50, runner.State("bmc:3").Duty);
    }

    [Fact]
    public void ADrivenFanWithoutReadingsGoesBackToItsBmcCurve()
    {
        var store = Store();
        var runner = Runner(store);
        store.Assignments["bmc:3"] = "nvml";
        runner.Tick(_ => 60);
        _now = _now.AddSeconds(5);
        runner.Tick(_ => null);
        Assert.DoesNotContain(_sent, l => l.StartsWith("fanrelease", StringComparison.Ordinal));
        _now = _now.AddSeconds(21);
        runner.Tick(_ => null);
        Assert.Contains(_sent, l => l.StartsWith("fanrelease", StringComparison.Ordinal) && l.EndsWith(" bmc:3", StringComparison.Ordinal));
        Assert.Contains("BMC curve", runner.State("bmc:3").Problem, StringComparison.Ordinal);
        runner.Unassign("bmc:3");
        Assert.Equal("BMC curve", runner.State("bmc:3").Mode);
    }

    [Fact]
    public void TheStoreKeepsCurvesAndDropsBrokenReferences()
    {
        var store = Store();
        store.Curves.Add(new FanCurve { Id = "mix", Name = "mix", Kind = FanCurveKind.Mix, Children = ["slot5", "nvml", "gone"] });
        store.Assignments["bmc:1"] = "mix";
        store.Assignments["bmc:2"] = "gone";
        store.Save();
        var loaded = FanCurveStore.Load(store.FilePath);
        Assert.Equal(["slot5", "nvml"], loaded.Find("mix")!.Children);
        Assert.Equal("mix", loaded.AssignedTo("bmc:1")!.Id);
        Assert.Null(loaded.AssignedTo("bmc:2"));
        loaded.Remove("slot5");
        Assert.Equal(["nvml"], loaded.Find("mix")!.Children);
    }

    [AvaloniaFact]
    public void CurvesAndFansOnOneScreen()
    {
        using var hub = new SensorHub();
        var store = Store();
        store.Curves.Add(new FanCurve { Id = "f35", Name = "35%", Kind = FanCurveKind.Flat, Percent = 35 });
        store.Curves.Add(new FanCurve { Id = "mix", Name = "mix", Kind = FanCurveKind.Mix, Children = ["slot5", "f35"] });
        store.Assignments["bmc:2"] = "mix";
        store.Names["bmc:2"] = "AIO Top";
        store.Assignments["bmc:3"] = "nvml";
        store.Assignments["nvml:GPU-6d3eab54-984e-f1bc-68eb-ee4948e6c3b0:fan:0"] = "slot5";
        var runner = Runner(store);
        var window = new FanControlWindow(hub, runner: runner);
        window.Show();
        var texts = window.Texts();
        Assert.Contains("Not on the BMC yet:", texts);
        var aio = window.GetLogicalDescendants().OfType<TextBox>().Single(t => t.Text == "AIO Top");
        Assert.StartsWith("CHA_FAN2\nbmc:2", ToolTip.GetTip(aio) as string, StringComparison.Ordinal);
        Assert.Contains(window.GetLogicalDescendants().OfType<TextBox>(), t => t.Text == "CHA_FAN3"); // unnamed: the original
        Assert.Contains("⚠ OpenHW-driven · 0 flash writes today", texts);
        Assert.Contains("OpenHW via NVIDIA driver (no flash)", texts);
        Assert.Contains("FLAT", texts);
        Assert.Contains("GRAPH", texts);
        Assert.Contains("MIX", texts);
        Assert.Contains("35 %", texts); // the flat card's output
        Assert.Contains(texts, t => t.StartsWith("BMC sensors — runs on the BMC: one write to put it there, then none\nFans: AIO Top", StringComparison.Ordinal));
        Assert.Contains(texts, t => t.StartsWith("⚠ Not a BMC sensor", StringComparison.Ordinal));
        Assert.Contains(window.GetLogicalDescendants().OfType<Button>(), b => b.Content as string == "Apply");
        var captures = Environment.GetEnvironmentVariable("OPENHW_CAPTURES") ?? Path.GetTempPath();
        window.CaptureRenderedFrame()?.Save(Path.Combine(captures, "openhw-fan-control-curves.png"));

        var flatMenu = window.NewMenu(FanCurveKind.Flat);
        Assert.Equal("New flat", ((MenuItem)flatMenu.Items[0]!).Header);
        Assert.Equal("Duplicate \"35%\"", ((TextBlock)((MenuItem)flatMenu.Items[2]!).Header!).Text);
        var plus = window.GetLogicalDescendants().OfType<Button>().Single(b => b.Content as string == "+ Mix");
        plus.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); // opens its menu (and doesn't throw)
        var mixMenu = window.NewMenu(FanCurveKind.Mix);
        mixMenu.ShowAt(plus);
        Assert.True(mixMenu.IsOpen); // a menu built first does open
        mixMenu.Hide();
        var copy = window.Duplicate(store.Find("f35")!);
        Assert.Equal("35% copy", copy.Name);
        Assert.Equal(35, copy.Percent);
        Assert.NotEqual("f35", copy.Id);
        Assert.Equal("35% copy 2", window.Duplicate(store.Find("f35")!).Name);
        var mixCopy = window.Duplicate(store.Find("mix")!);
        Assert.Equal(["slot5", "f35"], mixCopy.Children);
        Assert.Equal("mix", store.AssignedTo("bmc:2")!.Id); // fans keep the original

        // The GPU fan runs its curve through NVML at once: no budget, no 5 % steps.
        runner.Tick(id => id == "bmc:PCIE01 Temp" ? 53.5 : 60);
        Assert.Contains(_sent, l => l.StartsWith("gpufanset ", StringComparison.Ordinal) && l.EndsWith(":fan:0 45", StringComparison.Ordinal));

        FanCurve? saved = null;
        var editor = new GraphEditorWindow(new FanCurveSensors(hub, runner), store.Find("slot5")!, c => saved = c);
        editor.Show();
        Assert.True(editor.Plan!.OnBmc);
        Assert.Contains(editor.GetLogicalDescendants().OfType<TextBlock>(), t => t.Text!.StartsWith("Runs on the BMC", StringComparison.Ordinal));
        editor.CaptureRenderedFrame()?.Save(Path.Combine(captures, "openhw-graph-editor.png"));
        editor.GetLogicalDescendants().OfType<Button>().Single(b => b.Content as string == "Ok")
            .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(6, saved!.Points.Count);
    }
}
