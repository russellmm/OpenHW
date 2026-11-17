using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia;
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
    public void AGpuFanTheHelperGaveBackIsSetAgainWithoutATemperatureChange()
    {
        // A lend hands every GPU fan back to its card (helper Suspend) without telling the app; afterwards the curve's
        // duty hasn't changed, so the runner used to skip it and the fan stayed on automatic (seen 2026-11-17).
        const string Fan = "nvml:GPU-x:fan:0";
        var held = true;
        var store = Store();
        store.Assignments[Fan] = "nvml";
        var runner = new FanCurveRunner(new FanClient(line => line.StartsWith("gpufans ", StringComparison.Ordinal)
            ? GpuWire.Encode(new GpuFanDriverStatus(null, [new(Fan, "RTX", "00000000:C1:00.0", 0, 30, 100, held, held, held ? 65 : null)]))
            : Helper(line)), store, () => _now);
        var log = new List<string>();
        runner.Log = log.Add;
        int Sets() => _sent.Count(l => l.StartsWith($"gpufanset ", StringComparison.Ordinal) && l.Contains(Fan, StringComparison.Ordinal));

        runner.Tick(_ => 60);
        Assert.Equal(1, Sets());
        for (var i = 0; i < FanCurveRunner.GpuHoldCheckEvery; i++) { _now += TimeSpan.FromSeconds(2); runner.Tick(_ => 60); }
        Assert.Equal(1, Sets());                                 // still held: nothing sent again

        held = false;                                            // the lend took it back
        for (var i = 0; i < FanCurveRunner.GpuHoldCheckEvery; i++) { _now += TimeSpan.FromSeconds(2); runner.Tick(_ => 60.5); }
        Assert.Equal(2, Sets());                                 // same duty, set again within one check
        Assert.EndsWith($"{Fan} 65", _sent.Last(l => l.StartsWith("gpufanset ", StringComparison.Ordinal)), StringComparison.Ordinal);

        // The journal says what happened, once each: taken at start, lost, taken again.
        Assert.Equal([$"{Fan}: set to 65 % (\"5060 core\" at 60 °C)",
                      $"{Fan}: the helper no longer holds it for OpenHW (a lend, or a lapsed lease); setting it again",
                      $"{Fan}: set to 65 % (\"5060 core\" at 60.5 °C)"], log);

        // A refused duty is reported once per reason, and a curve without readings once until they're back.
        var refuse = true;
        var lent = new FanCurveRunner(new FanClient(line => line.StartsWith("gpufanset ", StringComparison.Ordinal) && refuse
            ? throw new IOException("openhw-helper: err a card is being lent to a VM") : Helper(line)), store, () => _now);
        var lines = new List<string>();
        lent.Log = lines.Add;
        for (var i = 0; i < 3; i++) { _now += TimeSpan.FromSeconds(2); lent.Tick(_ => 60); }
        Assert.Equal([$"{Fan}: can't set 65 %: a card is being lent to a VM"], lines);
        refuse = false;
        for (var i = 0; i < 3; i++) { _now += TimeSpan.FromSeconds(2); lent.Tick(_ => null); }
        _now += TimeSpan.FromSeconds(2);
        lent.Tick(_ => 60);
        Assert.Equal([$"{Fan}: can't set 65 %: a card is being lent to a VM",
                      $"{Fan}: curve \"5060 core\" has no reading from nvml:GPU-x:temp:core",
                      $"{Fan}: readings back after 6 s",
                      $"{Fan}: set to 65 % (\"5060 core\" at 60 °C)"], lines);
    }

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

    [Fact]
    public void ConfigurationsSaveAndLoadAndChangesFollowTheFileInUse()
    {
        var store = Store();
        store.Assignments["bmc:1"] = "slot5";
        store.Names["bmc:1"] = "Memory";
        var file = Path.Combine(_dir, "profiles", "mine.json");
        store.Export(file);
        store.ConfigFile = file;
        store.Curves.Single(c => c.Id == "slot5").Name = "Renamed";
        store.Save(); // the file in use follows
        Assert.False(store.DiffersFromFile());

        var other = FanCurveStore.Load(Path.Combine(_dir, "other.json"));
        other.Import(file);
        Assert.Equal(file, other.ConfigFile);
        Assert.Equal("Renamed", other.Find("slot5")!.Name);
        Assert.Equal("slot5", other.AssignedTo("bmc:1")!.Id);
        Assert.Equal("Memory", other.Names["bmc:1"]);

        other.StartNew(Path.Combine(_dir, "profiles", "empty.json"));
        Assert.Empty(other.Curves);
        Assert.Empty(other.Assignments);
        Assert.Equal("Memory", other.Names["bmc:1"]); // names belong to the fans: kept

        File.WriteAllText(Path.Combine(_dir, "junk.json"), "{ not json");
        Assert.Throws<InvalidDataException>(() => other.Import(Path.Combine(_dir, "junk.json")));
    }

    [Fact]
    public void CardsMoveAndKeepTheirOrder()
    {
        var store = Store();
        string[] shown = ["bmc:0", "bmc:1", "bmc:2", "gpu:a"];
        Assert.True(store.Move(shown, "gpu:a", 0));
        Assert.Equal(["gpu:a", "bmc:0", "bmc:1", "bmc:2"], store.Ordered(shown));
        Assert.True(store.Move(shown, "bmc:0", int.MaxValue));
        Assert.Equal(["gpu:a", "bmc:1", "bmc:2", "bmc:0"], store.Ordered(shown));
        Assert.False(store.Move(shown, "gpu:a", -1)); // already first
        Assert.True(store.Move(shown, "bmc:1", 0));
        Assert.Equal(["bmc:1", "gpu:a", "bmc:2", "bmc:0"], store.Ordered(shown));
        // a fan that isn't there now (a lent card) keeps its place for when it's back
        Assert.Equal(["bmc:1", "bmc:2", "bmc:0"], store.Ordered(["bmc:0", "bmc:1", "bmc:2"]));
        Assert.True(store.Move(["bmc:0", "bmc:1", "bmc:2"], "bmc:0", 0));
        Assert.Equal(["bmc:0", "bmc:1", "gpu:a", "bmc:2"], store.Ordered(shown));
        store.Save();
        Assert.Equal(store.ControlOrder, FanCurveStore.Load(store.FilePath).ControlOrder);
    }

    [Fact]
    public void MixCardsWarnWhenTheBmcCantKeepTheMix()
    {
        var store = Store();
        var a = new FanCurve { Id = "a", Name = "a", SensorId = "bmc:PCIE01 Temp", Points = [new(40, 30), new(80, 100)] };
        var b = new FanCurve { Id = "b", Name = "b", SensorId = "bmc:CPU Package Temp", Points = [new(40, 30), new(80, 100)] };
        var c = new FanCurve { Id = "c", Name = "c", SensorId = "bmc:CPU Package Temp", Points = [new(50, 20), new(70, 100)] };
        var floor = new FanCurve { Id = "f", Name = "f", Kind = FanCurveKind.Flat, Percent = 30 };
        store.Curves.AddRange([a, b, c, floor]);
        var runner = Runner(store);
        string? Warn(FanCurve mix)
        {
            store.Curves.RemoveAll(x => x.Id == mix.Id);
            store.Curves.Add(mix);
            return FanControlWindow.MixWarning(mix, store.Library, runner.PlanCurve(mix));
        }
        Assert.Null(Warn(new FanCurve { Id = "m", Kind = FanCurveKind.Mix, Children = ["a", "f"] })); // one sensor + a floor: exact
        Assert.Null(Warn(new FanCurve { Id = "m", Kind = FanCurveKind.Mix, Children = ["a", "b"] })); // same curve on two sensors = bulk: exact
        Assert.StartsWith("⚠ Different curves", Warn(new FanCurve { Id = "m", Kind = FanCurveKind.Mix, Children = ["a", "c"] }), StringComparison.Ordinal);
        Assert.StartsWith("⚠ The BMC follows only the hottest sensor, so min", Warn(new FanCurve { Id = "m", Kind = FanCurveKind.Mix, Function = FanMixFunction.Min, Children = ["a", "b"] }), StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void TheSensorPickerTicksSeveralAndWholeGroups()
    {
        FanSensorChoice[] choices = [new("bmc:CPU Package Temp", "BMC · CPU Package Temp", true), new("bmc:DIMMA1_Temp", "BMC · DIMMA1_Temp", true),
            new("bmc:DIMMB1_Temp", "BMC · DIMMB1_Temp", true), new("bmc:PCIE01 Temp", "BMC · PCIE01 Temp", true), new("nvml:x", "GPU Temperature · GPU", false)];
        IReadOnlyList<string>? got = null;
        var picker = new SensorPicker(choices, ["bmc:DIMMA1_Temp"], _ => 40, ids => got = ids);
        Assert.Equal("DIMMA1_Temp", SensorPicker.Summary(picker.Selected, choices));
        picker.Toggle(c => c.Bmc && c.Label.Contains("DIMM", StringComparison.Ordinal));
        Assert.Equal(["bmc:DIMMA1_Temp", "bmc:DIMMB1_Temp"], got);
        Assert.Equal("Hottest of 2: DIMMA1_Temp, DIMMB1_Temp", SensorPicker.Summary(got!, choices));
        picker.Toggle(c => c.Bmc && c.Label.Contains("DIMM", StringComparison.Ordinal)); // all ticked → untick, but keep one
        Assert.Equal(["bmc:DIMMA1_Temp"], got);
        picker.Select(["bmc:CPU Package Temp", "nvml:x"]);
        Assert.Equal(["bmc:CPU Package Temp", "nvml:x"], got);
    }

    [AvaloniaFact]
    public void CardsReorderByDragAndDrop()
    {
        using var hub = new SensorHub();
        var store = Store();
        store.Curves.Add(new FanCurve { Id = "f35", Name = "35%", Kind = FanCurveKind.Flat, Percent = 35 });
        var window = new FanControlWindow(hub, runner: Runner(store)) { Width = 1620, Height = 1400 };
        window.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        var before = window.CardOrder().ToList();
        var cards = window.GetLogicalDescendants().OfType<Border>().Where(b => b.Parent is WrapPanel).ToList();
        Point At(Border card, double x, double y) => card.TranslatePoint(new Point(x, y), window)!.Value;

        // Drag the first fan card past the right half of the third: it lands third.
        var first = cards[0];
        var third = cards[2];
        window.MouseDown(At(first, 5, 5), MouseButton.Left);
        window.MouseMove(At(first, 40, 5));
        window.MouseMove(At(third, third.Bounds.Width - 20, 30));
        window.MouseUp(At(third, third.Bounds.Width - 20, 30), MouseButton.Left);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.Equal([before[1], before[2], before[0], .. before.Skip(3)], window.CardOrder());

        // Curve cards: the flat card (first by kind) dragged onto the left half of the last one.
        var curvesBefore = window.CurvesInOrder().Select(c => c.Id).ToList();
        var curveCards = window.GetLogicalDescendants().OfType<Border>().Where(b => b.Parent is WrapPanel).Skip(window.CardOrder().Count).ToList();
        var last = curveCards[^1];
        window.MouseDown(At(curveCards[0], 5, 5), MouseButton.Left);
        window.MouseMove(At(curveCards[0], 40, 5));
        window.MouseMove(At(last, 20, 30));
        window.MouseUp(At(last, 20, 30), MouseButton.Left);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        var curvesAfter = window.CurvesInOrder().Select(c => c.Id).ToList();
        Assert.Equal(curvesBefore[0], curvesAfter[^2]); // just before the last
        Assert.Equal(curvesAfter, FanCurveStore.Load(store.FilePath).OrderedCurves(curvesBefore));

        // A click (no movement) and a press on a dropdown don't move anything.
        var order = window.CardOrder().ToList();
        var now = window.GetLogicalDescendants().OfType<Border>().First(b => b.Parent is WrapPanel);
        window.MouseDown(At(now, 5, 5), MouseButton.Left);
        window.MouseUp(At(now, 5, 5), MouseButton.Left);
        var combo = now.GetLogicalDescendants().OfType<ComboBox>().First();
        var onCombo = combo.TranslatePoint(new Point(10, 10), window)!.Value;
        window.MouseDown(onCombo, MouseButton.Left);
        window.MouseMove(onCombo + new Point(300, 0));
        window.MouseUp(onCombo + new Point(300, 0), MouseButton.Left);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.Equal(order, window.CardOrder());
    }

    [AvaloniaFact]
    public void LoadingAConfigurationReplacesTheSetupAndOffersApplyAll()
    {
        using var hub = new SensorHub();
        var source = Store();
        source.Assignments["bmc:2"] = "slot5";
        source.Assignments["bmc:3"] = "slot5";
        var file = Path.Combine(_dir, "two.json");
        source.Export(file);
        var window = new FanControlWindow(hub, runner: Runner(FanCurveStore.Load(Path.Combine(_dir, "live.json"))));
        window.Show();
        window.LoadConfig(file);
        Assert.Equal("Fan Control — two.json", window.Title);
        Assert.Contains(window.Texts(), t => t == "GPU Slot 5");
        Assert.Contains(window.GetLogicalDescendants().OfType<Button>(), b => b.Content as string == "Apply all to the BMC (2)" && b.IsVisible);
        Assert.DoesNotContain(_sent, l => l.StartsWith("fancurve", StringComparison.Ordinal)); // nothing written until Apply
    }

    [AvaloniaFact]
    public void CurvesAndFansOnOneScreen()
    {
        using var hub = new SensorHub();
        var store = Store();
        store.Curves.Add(new FanCurve { Id = "f35", Name = "35%", Kind = FanCurveKind.Flat, Percent = 35 });
        store.Curves.Add(new FanCurve { Id = "mix", Name = "mix", Kind = FanCurveKind.Mix, Children = ["slot5", "f35"] });
        var bulk = new FanCurve { Id = "bulk", Name = "All DIMMs", Points = [new(40, 30), new(55, 60), new(70, 100)] };
        bulk.SetInputs(["bmc:DIMMA1_Temp", "bmc:DIMMB1_Temp"]);
        store.Curves.Add(bulk);
        store.Curves.Add(new FanCurve { Id = "cpu", Name = "CPU", SensorId = "bmc:CPU Package Temp", Points = [new(50, 20), new(70, 100)] });
        store.Curves.Add(new FanCurve { Id = "mix2", Name = "CPU or GPU", Kind = FanCurveKind.Mix, Children = ["slot5", "cpu"] });
        store.Assignments["bmc:2"] = "mix";
        store.Names["bmc:2"] = "AIO Top";
        store.Assignments["bmc:3"] = "nvml";
        store.Assignments["nvml:GPU-6d3eab54-984e-f1bc-68eb-ee4948e6c3b0:fan:0"] = "slot5";
        var runner = Runner(store);
        store.ControlOrder = ["nvml:GPU-6d3eab54-984e-f1bc-68eb-ee4948e6c3b0:fan:0"];
        var window = new FanControlWindow(hub, runner: runner) { Height = 1450 };
        window.Show();
        Assert.Equal("nvml:GPU-6d3eab54-984e-f1bc-68eb-ee4948e6c3b0:fan:0", window.CardOrder()[0]);
        Assert.Equal("bmc:0", window.CardOrder()[1]);
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
        Assert.Contains("Hottest of 2: DIMMA1_Temp, DIMMB1_Temp", texts);
        Assert.Contains(texts, t => t.StartsWith("⚠ Different curves on different sensors", StringComparison.Ordinal));
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

    [Fact]
    public void GpuFanFollowsItsOwnCoreTemperatureUntilTheBmcAnswers()
    {
        const string Fan = "nvml:GPU-c1:fan:0", Core = "nvml:GPU-c1:temp:core";
        var slot = new FanCurve { Id = "s", Name = "s", SensorId = "bmc:PCIE05 Temp", Points = [new(40, 30), new(80, 100)] };
        var cpu = new FanCurve { Id = "c", Name = "c", SensorId = "bmc:CPU Package Temp", Points = [new(40, 30), new(80, 100)] };
        var both = new FanCurve { Id = "m", Name = "m", Kind = FanCurveKind.Mix, Children = ["s", "c"] };
        var library = new Dictionary<string, FanCurve> { ["s"] = slot, ["c"] = cpu, ["m"] = both };

        // BMC silent, card readable: the slot sensor reads the card's own core temperature.
        var own = FanCurveRunner.OwnTempReading(Fan, slot, library, id => id == Core ? 61 : null);
        Assert.NotNull(own);
        Assert.Equal(61, own!("bmc:PCIE05 Temp"));
        Assert.Null(own("bmc:CPU Package Temp"));

        // The BMC answers: no stand-in.
        Assert.Null(FanCurveRunner.OwnTempReading(Fan, slot, library, id => id == Core ? 61 : 45));
        // A missing sensor that isn't a slot sensor (CPU Package): no stand-in, the curve waits as before.
        Assert.Null(FanCurveRunner.OwnTempReading(Fan, both, library, id => id == Core ? 61 : null));
        // Card not readable (lent): nothing to stand in with.
        Assert.Null(FanCurveRunner.OwnTempReading(Fan, slot, library, _ => null));
        // Not a GPU fan.
        Assert.Null(FanCurveRunner.OwnTempReading("bmc:1", slot, library, id => id == Core ? 61 : null));
    }
}
