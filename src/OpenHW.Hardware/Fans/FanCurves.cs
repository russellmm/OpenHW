using System.Text.Json.Serialization;

namespace OpenHW.Hardware;

/// <summary>What a curve is: a graph over one temperature sensor, a fixed duty, or a mix of other curves.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FanCurveKind { Graph, Flat, Mix }

/// <summary>How a mix combines its curves' duties.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FanMixFunction { Max, Min, Average }

/// <summary>A graph point: temperature (°C) → duty (%).</summary>
public readonly record struct FanGraphPoint(double TemperatureC, double Percent);

/// <summary>
/// A fan curve, as OpenFan defines them (ported from OpenFan.Core: GraphCurve, MixCurve, FanController.Evaluate):
/// <list type="bullet">
///   <item><b>Graph</b>: any number of points over a temperature — one sensor, or the <b>hottest</b> of several ("bulk
///   sensors", as the BMC follows the hottest of a fan's ticked sensors) — straight lines between them, flat before the
///   first and after the last; capped at <see cref="MaxSpeedPercent"/>; hysteresis in °C and seconds.</item>
///   <item><b>Flat</b>: one fixed duty.</item>
///   <item><b>Mix</b>: the max, min or average of other (non-mix) curves.</item>
/// </list>
/// <see cref="MinTempC"/>/<see cref="MaxTempC"/> are the graph editor's axis only.
/// </summary>
public sealed class FanCurve
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public FanCurveKind Kind { get; set; } = FanCurveKind.Graph;
    /// <summary>The graph's sensor (the first of <see cref="SensorIds"/> when there are several; kept for older files).</summary>
    public string? SensorId { get; set; }

    /// <summary>Several sensors: the graph follows the hottest. Empty = just <see cref="SensorId"/>.</summary>
    public List<string> SensorIds { get; set; } = [];

    /// <summary>The sensors a graph reads (the hottest counts).</summary>
    [JsonIgnore]
    public IReadOnlyList<string> Inputs => SensorIds.Count > 0 ? SensorIds : SensorId is { } s ? [s] : [];

    /// <summary>Sets the graph's sensors (one or several).</summary>
    public void SetInputs(IEnumerable<string> ids)
    {
        SensorIds = ids.Where(i => !string.IsNullOrWhiteSpace(i)).Distinct().ToList();
        SensorId = SensorIds.FirstOrDefault();
        if (SensorIds.Count <= 1) SensorIds = [];
    }
    public List<FanGraphPoint> Points { get; set; } = [];
    public double Percent { get; set; } = 50;
    public FanMixFunction Function { get; set; } = FanMixFunction.Max;
    public List<string> Children { get; set; } = [];
    public double HysteresisC { get; set; } = 2;
    public double HysteresisS { get; set; } = 1;
    public double MinTempC { get; set; } = 25;
    public double MaxTempC { get; set; } = 90;
    public int MaxSpeedPercent { get; set; } = 100;

    public FanCurve Clone() => new()
    {
        Id = Id, Name = Name, Kind = Kind, SensorId = SensorId, SensorIds = [.. SensorIds], Points = [.. Points], Percent = Percent, Function = Function,
        Children = [.. Children], HysteresisC = HysteresisC, HysteresisS = HysteresisS, MinTempC = MinTempC, MaxTempC = MaxTempC,
        MaxSpeedPercent = MaxSpeedPercent,
    };

    public static FanCurve NewGraph(string id, string name, string? sensorId) => new()
    {
        Id = id, Name = name, Kind = FanCurveKind.Graph, SensorId = sensorId,
        Points = [new(30, 30), new(50, 40), new(65, 70), new(80, 100)],
    };
}

public static class FanCurveMath
{
    /// <summary>A graph's duty at <paramref name="temperatureC"/> (OpenFan's GraphCurve.Evaluate).</summary>
    public static double Graph(IReadOnlyList<FanGraphPoint> points, double temperatureC, int maxSpeed = 100)
    {
        var hi = Math.Clamp(maxSpeed <= 0 ? 100 : maxSpeed, 0, 100);
        if (points.Count == 0) return 0;
        var p = points.OrderBy(x => x.TemperatureC).ToArray();
        double y;
        if (p.Length == 1 || temperatureC <= p[0].TemperatureC) y = p[0].Percent;
        else if (temperatureC >= p[^1].TemperatureC) y = p[^1].Percent;
        else
        {
            var i = 0;
            while (i < p.Length - 1 && p[i + 1].TemperatureC < temperatureC) i++;
            var span = p[i + 1].TemperatureC - p[i].TemperatureC;
            var t = span <= 0 ? 0 : (temperatureC - p[i].TemperatureC) / span;
            y = p[i].Percent + t * (p[i + 1].Percent - p[i].Percent);
        }
        return Math.Clamp(y, 0, hi);
    }

    /// <summary>
    /// A curve's duty from the current readings, or null when a sensor it needs has no reading (a mix skips children
    /// without a value; it has none only when none of them has one).
    /// </summary>
    public static double? Evaluate(FanCurve curve, IReadOnlyDictionary<string, FanCurve> library, Func<string, double?> reading) =>
        Evaluate(curve, library, reading, []);

    private static double? Evaluate(FanCurve curve, IReadOnlyDictionary<string, FanCurve> library, Func<string, double?> reading, HashSet<string> stack)
    {
        if (!stack.Add(curve.Id)) return null; // a cycle
        try
        {
            switch (curve.Kind)
            {
                case FanCurveKind.Flat:
                    return Math.Clamp(curve.Percent, 0, 100);
                case FanCurveKind.Graph:
                    return Hottest(curve, reading) is { } t ? Graph(curve.Points, t, curve.MaxSpeedPercent) : null;
                default:
                    var values = curve.Children.Select(c => library.TryGetValue(c, out var child) ? Evaluate(child, library, reading, stack) : null)
                        .OfType<double>().ToList();
                    if (values.Count == 0) return null;
                    return curve.Function switch { FanMixFunction.Min => values.Min(), FanMixFunction.Average => values.Average(), _ => values.Max() };
            }
        }
        finally { stack.Remove(curve.Id); }
    }

    /// <summary>A graph's input now: the hottest of its sensors that has a reading, or null when none has.</summary>
    public static double? Hottest(FanCurve curve, Func<string, double?> reading) =>
        curve.Inputs.Select(reading).OfType<double>().Cast<double?>().DefaultIfEmpty(null).Max();

    /// <summary>Every sensor a curve reads (through its mix children).</summary>
    public static IReadOnlyList<string> Sensors(FanCurve curve, IReadOnlyDictionary<string, FanCurve> library)
    {
        var found = new List<string>();
        void Walk(FanCurve c, HashSet<string> stack)
        {
            if (!stack.Add(c.Id)) return;
            if (c.Kind == FanCurveKind.Graph)
                foreach (var s in c.Inputs)
                    if (!found.Contains(s)) found.Add(s);
            if (c.Kind == FanCurveKind.Mix)
                foreach (var child in c.Children)
                    if (library.TryGetValue(child, out var cc)) Walk(cc, stack);
            stack.Remove(c.Id);
        }
        Walk(curve, []);
        return found;
    }

    /// <summary>The hysteresis that gates an OpenHW-driven fan: a graph's own; a mix's widest child's; none for flat.</summary>
    public static (double C, double S) Hysteresis(FanCurve curve, IReadOnlyDictionary<string, FanCurve> library) => curve.Kind switch
    {
        FanCurveKind.Graph => (curve.HysteresisC, curve.HysteresisS),
        FanCurveKind.Mix => curve.Children.Select(c => library.TryGetValue(c, out var cc) && cc.Kind == FanCurveKind.Graph ? (cc.HysteresisC, cc.HysteresisS) : (0, 0))
            .DefaultIfEmpty((0, 0)).Aggregate((a, b) => (Math.Max(a.Item1, b.Item1), Math.Max(a.Item2, b.Item2))),
        _ => (0, 0),
    };
}

/// <summary>
/// OpenFan's HysteresisGate: a new duty is passed on only when the temperature moved at least
/// <c>deadbandC</c> since the last one and <c>holdSeconds</c> have passed; the same duty always passes.
/// </summary>
public sealed class FanHysteresisGate(double deadbandC, double holdSeconds)
{
    private bool _has;
    private int _last;
    private double _lastTemp, _lastAt;

    public int? Decide(double percent, double nowSeconds, double temperatureC)
    {
        var p = Math.Clamp((int)Math.Round(percent, MidpointRounding.AwayFromZero), 0, 100);
        if (!_has) { Commit(p, temperatureC, nowSeconds); return p; }
        if (p == _last) return p;
        if (Math.Abs(temperatureC - _lastTemp) < deadbandC) return null;
        if (nowSeconds - _lastAt < holdSeconds) return null;
        Commit(p, temperatureC, nowSeconds);
        return p;
    }

    private void Commit(int p, double t, double now) { _has = true; _last = p; _lastTemp = t; _lastAt = now; }
}
