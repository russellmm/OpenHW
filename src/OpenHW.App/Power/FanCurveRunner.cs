using System.Globalization;
using OpenHW.Hardware;

namespace OpenHW.App.Power;

/// <summary>What one fan is doing with its assigned curve, for the Fan Control page.</summary>
public sealed record FanRunState(string Mode, int? Duty, int WritesToday, string? Problem);

/// <summary>
/// Runs the assigned curves. A curve that reads only BMC sensors (or none) runs <b>on the BMC</b>: <see cref="ApplyBmc"/>
/// writes its sensors and five points once, and nothing is written afterwards. A curve that reads any other sensor is
/// <b>OpenHW-driven</b>: <see cref="Tick"/> (every poll) evaluates it and asks the helper's fan driver for that duty —
/// each change is a write to the BMC's flash, so duties move in 5 % steps, up at once, down only 5 % or more after
/// a minute, behind the curve's hysteresis, and every write is counted per day. If OpenHW stops, the driver's 15 s
/// lease gives the fan back to its BMC curve.
/// </summary>
public sealed class FanCurveRunner(FanClient client, FanCurveStore store, Func<DateTime>? now = null)
{
    public const int DrivenStep = 5;
    public static readonly TimeSpan DrivenDownHold = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan MissingReadingRelease = TimeSpan.FromSeconds(20);

    private sealed class Driven
    {
        public string CurveId = "";
        public FanHysteresisGate Gate = new(0, 0);
        public int? Duty;
        public DateTime LastChange;
        public DateTime? MissingSince;
        public string? Problem;
        public bool OwnTemp; // reading its card's own core temperature in place of a silent BMC slot sensor
    }

    private readonly object _gate = new();
    private readonly Func<DateTime> _now = now ?? (() => DateTime.Now);
    private readonly Dictionary<string, Driven> _driven = new(StringComparer.Ordinal);
    private IReadOnlyList<(int Number, string Name)>? _bmcSensors;
    private DateTime _bmcSensorsAt;

    public FanCurveStore Store => store;
    public FanClient Client => client;

    /// <summary>
    /// Where the runner reports what it does to fans and why it couldn't (a fan lost or taken, a refused duty, a curve
    /// without readings): the user journal through stderr. Only changes are reported, never every poll.
    /// </summary>
    public Action<string> Log { get; set; } = line => Console.Error.WriteLine("openhw: fans: " + line);

    /// <summary>The BMC temperature sensors a fan can follow (number 1–18 → name), cached for a minute.</summary>
    public IReadOnlyList<(int Number, string Name)> BmcSensors()
    {
        lock (_gate)
        {
            if (_bmcSensors is { Count: > 0 } cached && _now() - _bmcSensorsAt < TimeSpan.FromMinutes(1)) return cached;
            try { _bmcSensors = client.SourceSensors(); _bmcSensorsAt = _now(); }
            catch (Exception ex) when (Soft(ex)) { _bmcSensors ??= []; }
            return _bmcSensors;
        }
    }

    /// <summary>A sensor id → its BMC number when it's one of the BMC's own temperature sensors ("bmc:PCIE01 Temp").</summary>
    public int? BmcNumber(string sensorId)
    {
        if (!sensorId.StartsWith(BmcEngine.IdPrefix, StringComparison.Ordinal)) return null;
        var name = sensorId[BmcEngine.IdPrefix.Length..];
        foreach (var (number, n) in BmcSensors()) if (n == name) return number;
        return null;
    }

    public static int ZoneIndex(string zoneId) => zoneId[^1] - '0';

    /// <summary>An NVIDIA fan ("nvml:&lt;uuid&gt;:fan:&lt;n&gt;"): always driven by OpenHW, through NVML — nothing is stored, no flash.</summary>
    public static bool IsGpu(string zoneId) => zoneId.StartsWith("nvml:", StringComparison.Ordinal);

    private readonly Dictionary<string, BmcCurvePlan> _plans = new(StringComparer.Ordinal);

    /// <summary>The BMC version of a curve for a zone (fitting takes a moment: cached per curve content).</summary>
    public BmcCurvePlan Plan(string zoneId, FanCurve curve) =>
        IsGpu(zoneId) ? new BmcCurvePlan(false, null, [], 0, 0, []) : PlanCurve(curve, FanDriver.MinDuty(ZoneIndex(zoneId)));

    /// <summary>A curve's BMC version for a fan with this floor (cached per curve content; the mix cards' preview uses floor 1).</summary>
    public BmcCurvePlan PlanCurve(FanCurve curve, int floor = 1)
    {
        var library = store.Library;
        var key = $"{floor}|{store.FourthAt70}|" + string.Join("|", Tree(curve, library).Select(c => System.Text.Json.JsonSerializer.Serialize(c)));
        lock (_gate)
        {
            if (_plans.TryGetValue(key, out var cached) && cached.OtherSensors.All(s => BmcNumber(s) is null)) return cached;
            if (_plans.Count > 200) _plans.Clear();
            return _plans[key] = BmcCurveFit.Plan(curve, library, BmcNumber, floor, store.FourthAt70);
        }
    }

    private static IEnumerable<FanCurve> Tree(FanCurve curve, IReadOnlyDictionary<string, FanCurve> library) =>
        curve.Kind == FanCurveKind.Mix
            ? curve.Children.Where(library.ContainsKey).Select(id => library[id]).Prepend(curve)
            : [curve];

    /// <summary>True when the zone's assigned curve is OpenHW-driven (reads a sensor the BMC can't see).</summary>
    public bool IsDriven(string zoneId) => store.AssignedTo(zoneId) is { } c && (IsGpu(zoneId) || !BmcCurveFit.RunsOnBmc(c, store.Library, BmcNumber));

    /// <summary>Gives a fan a curve (saved; a BMC-run one still needs <see cref="ApplyBmc"/>).</summary>
    public void Assign(string zoneId, string curveId)
    {
        lock (_gate) store.Assignments[zoneId] = curveId;
        store.Save();
    }

    /// <summary>
    /// Writes a BMC-run curve to the BMC: the sensors (only if they differ, needs the BMC login) and the five points
    /// (only if they differ). If the firmware refuses a point 4 other than 70 °C, refits with point 4 at 70 and
    /// remembers that. Returns what was done.
    /// </summary>
    public string ApplyBmc(FanZoneStatus zone, long? currentMask)
    {
        var curve = store.AssignedTo(zone.Id) ?? throw new IOException("No curve assigned.");
        var plan = Plan(zone.Id, curve);
        if (!plan.OnBmc) throw new IOException("This curve reads sensors the BMC can't see: OpenHW drives it.");
        ReleaseDriven(zone.Id);
        var done = new List<string>();
        if (plan.SourceMask is { } mask && mask != currentMask)
        {
            client.SetSource(zone.Id, mask);
            done.Add("sensors set");
        }
        if (zone.Active is null || !plan.Points.SequenceEqual(zone.Active))
        {
            try { client.SetCurve(zone.Id, plan.Points); }
            catch (IOException ex) when (!store.FourthAt70 && plan.Points[3].TemperatureC != 70 && ex.Message.Contains("refused", StringComparison.Ordinal))
            {
                store.FourthAt70 = true;
                store.Save();
                plan = Plan(zone.Id, curve);
                client.SetCurve(zone.Id, plan.Points);
                done.Add("the BMC wants point 4 at 70 °C — refitted");
            }
            done.Add("curve written");
        }
        return done.Count == 0 ? "Already on the BMC; nothing written." : char.ToUpperInvariant(done[0][0]) + string.Join(", ", done)[1..] + ".";
    }

    /// <summary>The fan goes back to running whatever BMC curve it has (an OpenHW-driven fan is released).</summary>
    public void Unassign(string zoneId)
    {
        lock (_gate) store.Assignments.Remove(zoneId);
        store.Save();
        ReleaseDriven(zoneId);
    }

    public FanRunState State(string zoneId)
    {
        lock (_gate)
        {
            var writes = store.WritesOn(zoneId, _now());
            if (store.AssignedTo(zoneId) is not { } curve) return new("BMC curve", null, writes, null);
            if (_driven.TryGetValue(zoneId, out var d)) return new("OpenHW-driven", d.Duty, writes, d.Problem);
            return new(IsDriven(zoneId) ? "OpenHW-driven" : "BMC-run", null, writes, null);
        }
    }

    /// <summary>What every assigned curve gives right now (for the curve cards), null where a sensor has no reading.</summary>
    public double? Output(FanCurve curve, Func<string, double?> reading)
    {
        try { return FanCurveMath.Evaluate(curve, store.Library, reading); }
        catch (InvalidOperationException) { return null; }
    }

    /// <summary>
    /// The duty the BMC is running a zone at now: its active curve at the hottest of the sensors it follows (read by
    /// OpenHW from the BMC engine), or null when unknown.
    /// </summary>
    public double? BmcDuty(FanZoneStatus zone, long? mask, Func<string, double?> reading)
    {
        if (zone.Active is not { Count: 5 } active || mask is not { } m || m <= 0 || m >= 1L << BmcRedfish.SourceBits) return null;
        lock (_gate)
        {
            var temps = BmcSensors().Where(x => (m & 1L << (x.Number - 1)) != 0).Select(x => reading(BmcEngine.IdPrefix + x.Name)).OfType<double>().ToList();
            return temps.Count == 0 ? null : BmcCurveFit.Duty(active, temps.Max());
        }
    }

    /// <summary>
    /// One poll (timer thread): evaluates every OpenHW-driven fan and asks the driver for its duty under the write
    /// rules; renews the lease. <paramref name="reading"/> gives the current sensor values.
    /// </summary>
    public void Tick(Func<string, double?> reading)
    {
        lock (_gate)
        {
            var now = _now();
            IReadOnlyDictionary<string, FanCurve> library;
            List<KeyValuePair<string, string>> wanted;
            try
            {
                library = store.Library;
                wanted = store.Assignments.Where(a => library.ContainsKey(a.Value)).ToList();
            }
            catch (InvalidOperationException) { return; } // the curves are being edited right now: next poll
            bool Driven(KeyValuePair<string, string> a) => IsGpu(a.Key) || !BmcCurveFit.RunsOnBmc(library[a.Value], library, BmcNumber);
            foreach (var zone in _driven.Keys.Where(z => !wanted.Any(w => w.Key == z && Driven(w))).ToList())
                ReleaseDriven(zone);
            if (++_ticks % GpuHoldCheckEvery == 0) ForgetLostGpuFans();
            foreach (var (zoneId, curveId) in wanted)
            {
                var curve = library[curveId];
                if (!Driven(new(zoneId, curveId))) continue;
                if (!_driven.TryGetValue(zoneId, out var d) || d.CurveId != curveId)
                {
                    var (c, s) = FanCurveMath.Hysteresis(curve, library);
                    _driven[zoneId] = d = new Driven { CurveId = curveId, Gate = new FanHysteresisGate(c, s), Duty = d?.Duty, LastChange = d?.LastChange ?? default };
                }
                var read = reading;
                if (IsGpu(zoneId))
                {
                    var own = OwnTempReading(zoneId, curve, library, reading);
                    if (own is not null != d.OwnTemp)
                    {
                        d.OwnTemp = own is not null;
                        Log(d.OwnTemp ? $"{zoneId}: no BMC slot reading yet; following its own core temperature meanwhile"
                                      : $"{zoneId}: BMC slot reading back; following \"{curve.Name}\" again");
                    }
                    read = own ?? reading;
                }
                var value = FanCurveMath.Evaluate(curve, library, read);
                if (value is null)
                {
                    if (d.MissingSince is null)
                        Log($"{zoneId}: curve \"{curve.Name}\" has no reading from {string.Join(", ", FanCurveMath.Sensors(curve, library))}");
                    d.MissingSince ??= now;
                    d.Problem = "no reading from its sensors";
                    if (now - d.MissingSince >= MissingReadingRelease && d.Duty is not null) Release(zoneId, d, "no reading: back on its BMC curve");
                    continue;
                }
                if (d.MissingSince is { } since) Log($"{zoneId}: readings back after {(now - since).TotalSeconds:0} s");
                d.MissingSince = null;
                var tempNow = FanCurveMath.Sensors(curve, library).Select(read).OfType<double>().DefaultIfEmpty(0).Max();
                if (IsGpu(zoneId))
                {
                    // NVML: nothing stored, so the curve's own hysteresis is the only gate (as in OpenFan).
                    // A fan we don't hold (first tick, or the helper gave it back) takes the gate's last decision at once:
                    // waiting for the temperature to move past the deadband would leave it on automatic meanwhile.
                    var decided = d.Gate.Decide(value.Value, (now - DateTime.UnixEpoch).TotalSeconds, tempNow) ?? (d.Duty is null ? d.Gate.Last : null);
                    if (decided is not { } duty || duty == d.Duty) continue;
                    var taking = d.Duty is null;
                    try
                    {
                        var reply = client.GpuSet(zoneId, duty).Split(' ');
                        d.Duty = reply.Length > 1 && int.TryParse(reply[1], NumberStyles.None, CultureInfo.InvariantCulture, out var t) ? t : duty;
                        d.LastChange = now;
                        d.Problem = null;
                        if (taking) Log($"{zoneId}: set to {d.Duty} % (\"{curve.Name}\" at {tempNow.ToString("0.#", CultureInfo.InvariantCulture)} °C)");
                    }
                    catch (Exception ex) when (Soft(ex)) { Problem(zoneId, d, duty, ex); }
                    continue;
                }
                var floor = FanDriver.MinDuty(ZoneIndex(zoneId));
                var target = Math.Clamp((int)Math.Ceiling(value.Value / DrivenStep - 1e-9) * DrivenStep, Math.Max(floor, 1), 100);
                if (d.Gate.Decide(target, (now - DateTime.UnixEpoch).TotalSeconds, tempNow) is not { } gated) continue;
                if (d.Duty is { } current)
                {
                    if (gated == current) continue;
                    if (gated < current && (current - gated < DrivenStep || now - d.LastChange < DrivenDownHold)) continue;
                }
                try
                {
                    var reply = client.Set(zoneId, gated);
                    var parts = reply.Split(' ');
                    var set = parts.Length > 1 && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var t) ? t : gated;
                    if (set != d.Duty) { store.CountWrite(zoneId, now); store.Save(); }
                    if (d.Duty is null) Log($"{zoneId}: set to {set} % (\"{curve.Name}\")");
                    d.Duty = set;
                    d.LastChange = now;
                    d.Problem = null;
                }
                catch (Exception ex) when (Soft(ex)) { Problem(zoneId, d, gated, ex); }
            }
            if (_driven.Values.Any(x => x.Duty is not null))
                try { client.Beat(); } catch (Exception ex) when (Soft(ex)) { }
        }
    }

    /// <summary>
    /// The BMC's PCIe slot sensors are the GPUs' core temperatures (measured 2026-11-17), but the BMC answers only
    /// ~20 s after OpenHW starts (its SDR read) and slowly after a BMC reset. Meanwhile a GPU fan whose curve reads
    /// slot sensors would sit on automatic and then be released. So when every sensor of the curve that has no reading
    /// is a BMC slot sensor ("bmc:PCIE…"), those read the fan's own card's core temperature until the BMC answers.
    /// Returns null when no stand-in is needed (or none is possible).
    /// </summary>
    public static Func<string, double?>? OwnTempReading(string gpuFanId, FanCurve curve, IReadOnlyDictionary<string, FanCurve> library, Func<string, double?> reading)
    {
        var fan = gpuFanId.LastIndexOf(":fan:", StringComparison.Ordinal);
        if (fan < 0) return null;
        var missing = FanCurveMath.Sensors(curve, library).Where(id => reading(id) is null).ToList();
        if (missing.Count == 0 || !missing.All(IsBmcSlot)) return null;
        var core = gpuFanId[..fan] + ":temp:core";
        if (reading(core) is not { } own) return null;
        return id => reading(id) ?? (IsBmcSlot(id) ? own : null);
    }

    private static bool IsBmcSlot(string id) => id.StartsWith(BmcEngine.IdPrefix + "PCIE", StringComparison.Ordinal);

    /// <summary>How often (in ticks, one per poll) the helper is asked which GPU fans it still holds for us.</summary>
    public const int GpuHoldCheckEvery = 5;
    private long _ticks;

    /// <summary>
    /// The helper gives every GPU fan back to its card's own control when a card is lent (and when a lease runs out),
    /// without telling the client. A GPU fan we think we set but the helper no longer holds for us is set again.
    /// </summary>
    private void ForgetLostGpuFans()
    {
        var mine = _driven.Where(x => IsGpu(x.Key) && x.Value.Duty is not null).ToList();
        if (mine.Count == 0) return;
        HashSet<string> held;
        try
        {
            if (client.GpuStatus() is not { } status) return;
            held = status.Fans.Where(f => f.OwnedByYou).Select(f => f.Id).ToHashSet(StringComparer.Ordinal);
        }
        catch (Exception ex) when (Soft(ex)) { return; }
        foreach (var (id, d) in mine.Where(x => !held.Contains(x.Key)))
        {
            Log($"{id}: the helper no longer holds it for OpenHW (a lend, or a lapsed lease); setting it again");
            d.Duty = null;
        }
    }

    /// <summary>Records why a duty was refused; reported once per different reason.</summary>
    private void Problem(string zoneId, Driven d, int duty, Exception ex)
    {
        var why = ex.Message.Replace("openhw-helper: err ", "");
        if (why != d.Problem) Log($"{zoneId}: can't set {duty} %: {why}");
        d.Problem = why;
    }

    /// <summary>Gives every OpenHW-driven fan back to its BMC curve (OpenHW is exiting).</summary>
    public void ReleaseAll()
    {
        lock (_gate)
        {
            var held = _driven.Count(x => x.Value.Duty is not null);
            foreach (var zone in _driven.Keys.ToList()) ReleaseDriven(zone);
            if (held > 0) Log($"released {held} fan(s) (exiting)");
        }
    }

    private void ReleaseDriven(string zoneId)
    {
        lock (_gate)
        {
            if (_driven.TryGetValue(zoneId, out var d)) Release(zoneId, d, null);
            _driven.Remove(zoneId);
        }
    }

    private void Release(string zoneId, Driven d, string? why)
    {
        if (d.Duty is null) return;
        try { if (IsGpu(zoneId)) client.GpuRelease(zoneId); else client.Release(zoneId); } catch (Exception ex) when (Soft(ex)) { }
        d.Duty = null;
        d.Problem = why;
        if (why is not null) Log($"{zoneId}: released — {why}");
    }

    private static bool Soft(Exception ex) =>
        ex is IOException or System.Net.Sockets.SocketException or System.Text.Json.JsonException or FormatException;
}
