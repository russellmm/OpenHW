using OpenHW.Hardware;
using Xunit;
using Xunit.Abstractions;

namespace OpenHW.Tests;

public sealed class FanCurveFitTests(ITestOutputHelper output)
{
    // The owner's OpenFan curves (openfan11_14.json), sensors re-pointed to the BMC's own.
    private static FanCurve Graph(string id, string sensor, params (double T, double P)[] points) => new()
    {
        Id = id, Name = id, Kind = FanCurveKind.Graph, SensorId = sensor, Points = points.Select(p => new FanGraphPoint(p.T, p.P)).ToList(),
    };

    private static readonly FanCurve Slot5 = Graph("slot5", "bmc:PCIE05 Temp", (25, 30), (40, 30), (50, 37), (57, 52), (62.5, 62), (70, 77), (80, 100));
    private static readonly FanCurve Slot1 = Graph("slot1", "bmc:PCIE01 Temp", (25, 30), (42.5, 30), (52, 41), (63, 49), (68.5, 74), (80, 100));
    private static readonly FanCurve Cpu = Graph("cpu", "bmc:CPU Package Temp", (25, 20), (40, 20), (50, 20), (60, 63), (75, 64), (80, 82), (85, 88));
    private static readonly FanCurve Floor35 = new() { Id = "f35", Name = "35%", Kind = FanCurveKind.Flat, Percent = 35 };
    private static readonly FanCurve Mix = new() { Id = "mix", Name = "mix", Kind = FanCurveKind.Mix, Children = ["slot5", "slot1", "cpu", "f35"] };
    private static readonly Dictionary<string, FanCurve> Library = new[] { Slot5, Slot1, Cpu, Floor35, Mix }.ToDictionary(c => c.Id);

    private static int? Bmc(string id) => id switch
    {
        "bmc:CPU Package Temp" => 1, "bmc:DIMMA1_Temp" => 4, "bmc:PCIE01 Temp" => 12, "bmc:PCIE04 Temp" => 15, "bmc:PCIE05 Temp" => 16, _ => null,
    };

    private void Check(BmcCurvePlan plan, FanCurve curve, int floor = 1)
    {
        output.WriteLine($"{curve.Name}: {string.Join("  ", plan.Points.Select(p => $"{p.TemperatureC}:{p.DutyPercent}"))}  mask {plan.SourceMask}  " +
                         $"extra ≤ {plan.MaxExtraPercent:0.#} % at {plan.MaxExtraAtC} °C");
        Assert.Equal(5, plan.Points.Count);
        Assert.Equal(100, plan.Points[4].TemperatureC);
        for (var i = 1; i < 5; i++) Assert.True(plan.Points[i].TemperatureC > plan.Points[i - 1].TemperatureC);
        Assert.All(plan.Points, p => Assert.InRange(p.DutyPercent, Math.Max(1, floor), 100));
        var wanted = BmcCurveFit.Requested(curve, Library, floor);
        for (var i = 0; i < wanted.Length; i++)
            Assert.True(BmcCurveFit.Duty(plan.Points, i / 2.0) >= wanted[i] - 1e-6, $"slower than asked at {i / 2.0} °C");
    }

    [Fact]
    public void AGraphOnABmcSensorRunsOnTheBmcNeverSlowerThanAsked()
    {
        var plan = BmcCurveFit.Plan(Slot5, Library, Bmc);
        Assert.True(plan.OnBmc);
        Assert.Equal(1L << 15, plan.SourceMask);
        Check(plan, Slot5);
        Assert.True(plan.Points[3].TemperatureC > 70, "the top of an 80 °C curve can sit above 70 °C now");
        Assert.True(plan.MaxExtraPercent < 8, "four free points follow this curve closely");
    }

    [Fact]
    public void AMaxMixFollowsTheHottestOfAllItsSensors()
    {
        var plan = BmcCurveFit.Plan(Mix, Library, Bmc);
        Assert.True(plan.OnBmc);
        Assert.Equal(1L | 1L << 11 | 1L << 15, plan.SourceMask);
        Check(plan, Mix);
        Assert.True(BmcCurveFit.Duty(plan.Points, 30) >= 35);
    }

    [Fact]
    public void ACurveOnASensorTheBmcCantSeeIsOpenHwDriven()
    {
        var nvml = Graph("gpu", "nvml:GPU-x:temp:core", (40, 30), (80, 100));
        var lib = new Dictionary<string, FanCurve>(Library) { ["gpu"] = nvml };
        var plan = BmcCurveFit.Plan(nvml, lib, Bmc);
        Assert.False(plan.OnBmc);
        Assert.Equal(["nvml:GPU-x:temp:core"], plan.OtherSensors);
    }

    [Fact]
    public void FlatCurvesAndFloors()
    {
        var plan = BmcCurveFit.Plan(Floor35, Library, Bmc);
        Assert.True(plan.OnBmc);
        Assert.Null(plan.SourceMask); // follows whatever it followed
        Assert.All(plan.Points, p => Assert.Equal(35, p.DutyPercent));
        var pump = BmcCurveFit.Plan(Cpu, Library, Bmc, floor: 50);
        Check(pump, Cpu, 50);
        Assert.All(pump.Points, p => Assert.True(p.DutyPercent >= 50));
    }

    [Fact]
    public void WithPointFourAt70TheFitStillNeverRunsSlower()
    {
        var plan = BmcCurveFit.Plan(Slot1, Library, Bmc, fourthAt70: true);
        Assert.Equal(70, plan.Points[3].TemperatureC);
        Check(plan, Slot1);
    }

    [Fact]
    public void EvaluateMatchesOpenFan()
    {
        Assert.Equal(30, FanCurveMath.Graph(Slot5.Points, 20));
        Assert.Equal(44.5, FanCurveMath.Graph(Slot5.Points, 53.5), 3);
        Assert.Equal(100, FanCurveMath.Graph(Slot5.Points, 95));
        Assert.Equal(90, FanCurveMath.Graph(Slot5.Points, 95, maxSpeed: 90));
        double? Read(string id) => id == "bmc:PCIE05 Temp" ? 70 : id == "bmc:CPU Package Temp" ? 60 : null;
        Assert.Equal(77, FanCurveMath.Evaluate(Mix, Library, Read)); // slot5 77, cpu 63, slot1 none, floor 35
        Assert.Null(FanCurveMath.Evaluate(Slot1, Library, Read));
        var gate = new FanHysteresisGate(2, 1);
        Assert.Equal(40, gate.Decide(40, 0, 50));
        Assert.Null(gate.Decide(45, 5, 51)); // under 2 °C
        Assert.Equal(45, gate.Decide(45, 6, 52.5));
    }
}
