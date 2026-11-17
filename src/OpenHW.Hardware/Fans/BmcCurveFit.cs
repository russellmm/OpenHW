namespace OpenHW.Hardware;

/// <summary>
/// How a fan curve runs on the WRX90E: on the BMC (its sensor bitmask + 5 points, written once, no flash writes
/// afterwards) or, when it reads a sensor the BMC can't see, driven by OpenHW (every duty change is a flash write).
/// </summary>
/// <param name="OnBmc">True when every sensor the curve reads is a BMC temperature sensor.</param>
/// <param name="SourceMask">The BMC sensors to follow (bit = SDR number − 1); null for a curve that reads no sensor
/// (flat): the fan keeps whatever it follows.</param>
/// <param name="Points">The BMC's five points (point 5 at 100 °C), never slower than the curve asked for.</param>
/// <param name="MaxExtraPercent">The most the BMC version runs faster than the curve, and where.</param>
/// <param name="OtherSensors">Sensors the BMC can't see (why the curve can't run there).</param>
public sealed record BmcCurvePlan(bool OnBmc, long? SourceMask, IReadOnlyList<FanCurvePoint> Points, double MaxExtraPercent,
    double MaxExtraAtC, IReadOnlyList<string> OtherSensors);

/// <summary>
/// Turns any graph / flat / mix curve into what the BMC can run: it follows the <b>hottest</b> of the ticked sensors
/// through one curve of four free points plus a fifth at 100 °C (the web console's points A–D; point E's temperature is
/// fixed), straight lines between, flat before the first. The curve asked for, evaluated with every sensor at the same
/// temperature T, is an upper bound for any mix of rising curves (each child at the hottest sensor ≥ at its own), so
/// fitting the BMC curve on or above it never runs a fan slower than the curve asked. The fit picks the four
/// temperatures that waste the least extra speed. Pure; no BMC access.
/// </summary>
public static class BmcCurveFit
{
    public const int LastTemperature = 100;
    private const int MaxCandidates = 36;

    /// <param name="bmcSensorNumber">A sensor id → its BMC SDR number (1–18), or null when the BMC can't see it.</param>
    /// <param name="floor">The zone's lowest allowed duty (CPU 20 %, pump 50 %, else 1 % — the firmware refuses 0 %).</param>
    /// <param name="fourthAt70">Fit with point 4 fixed at 70 °C (if this firmware turns out to require it).</param>
    public static BmcCurvePlan Plan(FanCurve curve, IReadOnlyDictionary<string, FanCurve> library, Func<string, int?> bmcSensorNumber,
        int floor = 1, bool fourthAt70 = false)
    {
        var sensors = FanCurveMath.Sensors(curve, library);
        var other = sensors.Where(s => bmcSensorNumber(s) is null).ToList();
        long? mask = sensors.Count == 0 ? null : sensors.Select(bmcSensorNumber).OfType<int>().Aggregate(0L, (m, n) => m | 1L << (n - 1));
        var wanted = Requested(curve, library, Math.Max(1, floor));
        var points = Fit(wanted, fourthAt70);
        var (extra, at) = Extra(points, wanted);
        return new BmcCurvePlan(other.Count == 0, mask, points, extra, at, other);
    }

    /// <summary>True when every sensor the curve reads is one of the BMC's (cheap: no fit).</summary>
    public static bool RunsOnBmc(FanCurve curve, IReadOnlyDictionary<string, FanCurve> library, Func<string, int?> bmcSensorNumber) =>
        FanCurveMath.Sensors(curve, library).All(s => bmcSensorNumber(s) is not null);

    /// <summary>The duty asked for at 0, 0.5, 1 … 100 °C with every sensor at that temperature; rising, at least the floor.</summary>
    public static double[] Requested(FanCurve curve, IReadOnlyDictionary<string, FanCurve> library, int floor)
    {
        var g = new double[2 * LastTemperature + 1];
        var running = 0.0;
        for (var i = 0; i < g.Length; i++)
        {
            var t = i / 2.0;
            var v = FanCurveMath.Evaluate(curve, library, _ => t) ?? 100; // no value at all: ask for full speed
            running = Math.Max(running, v);
            g[i] = Math.Clamp(Math.Max(running, floor), 0, 100);
        }
        return g;
    }

    /// <summary>The BMC's duty at <paramref name="t"/> for a 5-point curve (flat before the first and after the last).</summary>
    public static double Duty(IReadOnlyList<FanCurvePoint> p, double t)
    {
        if (t <= p[0].TemperatureC) return p[0].DutyPercent;
        for (var i = 1; i < p.Count; i++)
            if (t <= p[i].TemperatureC)
            {
                var span = p[i].TemperatureC - p[i - 1].TemperatureC;
                return p[i - 1].DutyPercent + (t - p[i - 1].TemperatureC) / span * (p[i].DutyPercent - p[i - 1].DutyPercent);
            }
        return p[^1].DutyPercent;
    }

    private static (double Extra, double At) Extra(IReadOnlyList<FanCurvePoint> p, double[] g)
    {
        var (extra, at) = (0.0, 0.0);
        for (var i = 0; i < g.Length; i++)
        {
            var d = Duty(p, i / 2.0) - g[i];
            if (d > extra + 1e-9) (extra, at) = (d, i / 2.0);
        }
        return (extra, at);
    }

    /// <summary>Four temperatures (and point 5 at 100 °C) whose BMC curve stays on or above <paramref name="g"/> with the least excess.</summary>
    public static IReadOnlyList<FanCurvePoint> Fit(double[] g, bool fourthAt70 = false)
    {
        int Up(double v) => (int)Math.Clamp(Math.Ceiling(v - 1e-9), 1, 100);
        var flat = g.All(v => Math.Abs(v - g[0]) < 1e-9);
        if (flat)
        {
            var d = Up(g[0]);
            return [new(20, d), new(40, d), new(60, d), fourthAt70 ? new(70, d) : new(80, d), new(100, d)];
        }

        // Where the curve starts to rise and where it stops: knots outside that range only waste points.
        var rise = Array.FindLastIndex(g, v => Math.Abs(v - g[0]) < 1e-9) / 2;
        var top = (Array.FindIndex(g, v => Math.Abs(v - g[^1]) < 1e-9) + 1) / 2;
        var limit = fourthAt70 ? 69 : 99;
        var lo = Math.Clamp(rise, 0, limit);
        var hi = Math.Clamp(top, lo, limit);
        var step = Math.Max(1, (int)Math.Ceiling((hi - lo + 1) / (double)MaxCandidates));
        var candidates = new List<int>();
        for (var t = lo; t <= hi; t += step) candidates.Add(t);
        if (!candidates.Contains(hi)) candidates.Add(hi);
        // Spare knots so a short rise can still use all four points.
        for (var t = hi + 1; candidates.Count < (fourthAt70 ? 3 : 4) && t <= limit; t++) candidates.Add(t);
        for (var t = lo - 1; candidates.Count < (fourthAt70 ? 3 : 4) && t >= 0; t--) candidates.Insert(0, t);

        IReadOnlyList<FanCurvePoint>? best = null;
        var bestCost = double.MaxValue;
        void Try(int[] knots)
        {
            var temps = knots.Append(LastTemperature).ToArray();
            var duties = temps.Select(t => Up(g[2 * t])).ToArray();
            // Raise both ends of any segment that dips below the requested duty (only ever raises: stays feasible).
            for (var pass = 0; pass < 6; pass++)
            {
                var raised = false;
                for (var s = 1; s < temps.Length; s++)
                {
                    var deficit = 0.0;
                    for (var i = 2 * temps[s - 1]; i <= 2 * temps[s]; i++)
                    {
                        var line = duties[s - 1] + (i / 2.0 - temps[s - 1]) / (temps[s] - temps[s - 1]) * (duties[s] - duties[s - 1]);
                        deficit = Math.Max(deficit, g[i] - line);
                    }
                    if (deficit > 1e-9)
                    {
                        var add = (int)Math.Ceiling(deficit - 1e-9);
                        duties[s - 1] = Math.Min(100, duties[s - 1] + add);
                        duties[s] = Math.Min(100, duties[s] + add);
                        raised = true;
                    }
                }
                if (!raised) break;
            }
            var points = temps.Zip(duties, (t, d) => new FanCurvePoint(t, d)).ToList();
            var cost = 0.0;
            for (var i = 0; i < g.Length; i++)
            {
                var d = Duty(points, i / 2.0) - g[i];
                if (d < -1e-6) return; // still below somewhere: not acceptable
                cost += d;
            }
            if (cost < bestCost - 1e-9) (best, bestCost) = (points, cost);
        }

        var c = candidates.ToArray();
        if (fourthAt70)
        {
            for (var a = 0; a < c.Length; a++)
                for (var b = a + 1; b < c.Length; b++)
                    for (var d = b + 1; d < c.Length; d++)
                        if (c[d] < 70) Try([c[a], c[b], c[d], 70]);
        }
        else
        {
            for (var a = 0; a < c.Length; a++)
                for (var b = a + 1; b < c.Length; b++)
                    for (var d = b + 1; d < c.Length; d++)
                        for (var e = d + 1; e < c.Length; e++)
                            Try([c[a], c[b], c[d], c[e]]);
        }
        // Can't happen for a rising curve (full-speed ends are always feasible), but never return something below it.
        return best ?? [new(20, 100), new(40, 100), new(60, 100), new(fourthAt70 ? 70 : 80, 100), new(100, 100)];
    }
}
