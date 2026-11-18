using System.Globalization;

namespace OpenHW.Hardware;

/// <summary>
/// Thermal Grizzly WireView Pro II power meters through openhw-helper's <c>wireview</c> (the serial port is root-only):
/// one group per device — keyed by its USB serial, not by the GPU it sits on, so moving it to another card changes
/// nothing — with each 12V-2x6 wire's voltage, current and power, the totals, the connector temperatures, the PSU's
/// signalled limit, its fan and its fault bits.
/// On Windows the rows take HWiNFO's layout (owner, 2026-11-18): its names and order, with each wire's voltage,
/// current and power under a "GPU 12VHPWR Connector …" row (the six wires' average, as HWiNFO shows). The ids are the
/// same on both systems; Linux keeps its own names and flat order.
/// </summary>
public sealed class WireViewEngine(Func<int, string>? request = null, bool? hwinfoLayout = null) : ISensorEngine
{
    private readonly bool _hwinfo = hwinfoLayout ?? OperatingSystem.IsWindows();
    private Dictionary<string, IReadOnlyList<string>> _children = [];

    /// <summary>HWiNFO layout only: the three "GPU 12VHPWR Connector …" rows and their six pins each.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Children => _children;

    public const string IdPrefix = "wireview:";
    public const string GroupPrefix = "wireview:";

    private static readonly TimeSpan ReplyFresh = TimeSpan.FromMilliseconds(900);
    private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromSeconds(30);

    private readonly Func<int, string> _request = request ?? (timeout =>
        HelperClient.Request("wireview", receiveTimeoutMs: timeout, maxReplyBytes: GpuWire.MaxReplyBytes));
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private List<WireViewDevice>? _last;
    private TimeSpan _lastAt = TimeSpan.FromDays(-1), _failedAt = TimeSpan.FromDays(-1);

    public string Name => "wireview";
    public bool Available => _last is { Count: > 0 };

    private sealed record Row(string Key, string Name, HardwareKind Kind, string Unit, Func<WireViewSample, double?> Value);

    private static readonly (string Key, string Name, HardwareKind Kind, string Unit, Func<WireViewSample, double[]> Pins)[] PinSets =
    [
        ("v", "Voltage", HardwareKind.Voltage, "V", x => x.PinVolts),
        ("a", "Current", HardwareKind.Current, "A", x => x.PinAmps),
        ("w", "Power", HardwareKind.Power, "W", x => x.PinWatts),
    ];

    /// <summary>HWiNFO's WireView Pro II rows, in its order; OpenHW's extra rows (imbalance, PSU limit, supply) after.</summary>
    private static IEnumerable<Row> HwinfoRows(WireViewSample s)
    {
        yield return new Row("temp:0", "Temperature In", HardwareKind.Temperature, "°C", x => x.Temperatures[0]);
        yield return new Row("temp:1", "Temperature Out", HardwareKind.Temperature, "°C", x => x.Temperatures[1]);
        for (var i = 2; i < 4; i++)
        {
            var n = i;
            if (s.Temperatures[i] is not null)
                yield return new Row($"temp:{i}", $"External Temperature {i - 1}", HardwareKind.Temperature, "°C", x => x.Temperatures[n]);
        }
        foreach (var (key, name, kind, unit, pins) in PinSets)
        {
            yield return new Row($"pins:{key}", $"GPU 12VHPWR Connector {name}s", kind, unit, x => pins(x).Average());
            for (var i = 0; i < 6; i++)
            {
                var n = i;
                yield return new Row($"pin{i + 1}:{key}", $"Pin {i + 1} {name}", kind, unit, x => pins(x)[n]);
            }
            yield return key switch
            {
                "v" => new Row("avg:v", "Average Pin Voltage", HardwareKind.Voltage, "V", x => x.AverageVolts),
                "a" => new Row("total:a", "Total Current", HardwareKind.Current, "A", x => x.TotalAmps),
                _ => new Row("total:w", "Total Power", HardwareKind.Power, "W", x => x.TotalWatts),
            };
        }
        yield return new Row("fan", "Fan Duty", HardwareKind.Load, "%", x => x.FanPercent);
        yield return new Row("faults:now", "Faults (Status)", HardwareKind.Counter, "", x => BitCount(x.FaultStatus));
        yield return new Row("faults:log", "Faults (Log)", HardwareKind.Counter, "", x => BitCount(x.FaultLog));
        yield return new Row("imbalance", "Current Imbalance", HardwareKind.Load, "%", x => Imbalance(x.PinAmps));
        yield return new Row("psu:limit", "PSU Power Limit (Sense Pins)", HardwareKind.Power, "W", x => x.PsuCapabilityWatts);
        yield return new Row("vdd", "WireView Supply Voltage", HardwareKind.Voltage, "V", x => x.SupplyVolts);
    }

    private IEnumerable<Row> Rows(WireViewSample s) => _hwinfo ? HwinfoRows(s) : LinuxRows(s);

    private static IEnumerable<Row> LinuxRows(WireViewSample s)
    {
        string[] probes = ["Connector Temperature (In)", "Connector Temperature (Out)", "External Temperature 1", "External Temperature 2"];
        for (var i = 0; i < 4; i++)
        {
            var n = i;
            // External probes only when one is plugged in (the device reports −100 °C for none).
            if (i < 2 || s.Temperatures[i] is not null)
                yield return new Row($"temp:{i}", probes[i], HardwareKind.Temperature, "°C", x => x.Temperatures[n]);
        }
        for (var i = 0; i < 6; i++)
        {
            var n = i;
            yield return new Row($"pin{i + 1}:v", $"12V-2x6 Pin {i + 1} Voltage", HardwareKind.Voltage, "V", x => x.PinVolts[n]);
        }
        for (var i = 0; i < 6; i++)
        {
            var n = i;
            yield return new Row($"pin{i + 1}:a", $"12V-2x6 Pin {i + 1} Current", HardwareKind.Current, "A", x => x.PinAmps[n]);
        }
        for (var i = 0; i < 6; i++)
        {
            var n = i;
            yield return new Row($"pin{i + 1}:w", $"12V-2x6 Pin {i + 1} Power", HardwareKind.Power, "W", x => x.PinWatts[n]);
        }
        yield return new Row("total:w", "12V-2x6 Total Power", HardwareKind.Power, "W", x => x.TotalWatts);
        yield return new Row("total:a", "12V-2x6 Total Current", HardwareKind.Current, "A", x => x.TotalAmps);
        yield return new Row("avg:v", "12V-2x6 Average Voltage", HardwareKind.Voltage, "V", x => x.AverageVolts);
        yield return new Row("imbalance", "12V-2x6 Current Imbalance", HardwareKind.Load, "%", x => Imbalance(x.PinAmps));
        yield return new Row("psu:limit", "PSU Power Limit (Sense Pins)", HardwareKind.Power, "W", x => x.PsuCapabilityWatts);
        yield return new Row("faults:now", "Faults Now", HardwareKind.Counter, "", x => BitCount(x.FaultStatus));
        yield return new Row("faults:log", "Faults Logged", HardwareKind.Counter, "", x => BitCount(x.FaultLog));
        yield return new Row("vdd", "WireView Supply Voltage", HardwareKind.Voltage, "V", x => x.SupplyVolts);
        yield return new Row("fan", "WireView Fan", HardwareKind.Load, "%", x => x.FanPercent);
    }

    /// <summary>How far the busiest wire is above the six wires' average, in % of that average (null at near-zero current).</summary>
    public static double? Imbalance(double[] amps)
    {
        var avg = amps.Average();
        return avg < 0.5 ? null : Math.Round((amps.Max() - avg) / avg * 100, 1);
    }

    private static int BitCount(int mask) => System.Numerics.BitOperations.PopCount((uint)mask);

    private static string Key(WireViewDevice d) => d.Serial.Length > 0 ? d.Serial : d.Port.Replace("/dev/", "");

    public IReadOnlyList<HardwareItem> Discover()
    {
        var devices = Fetch();
        if (devices is null) return [];
        var items = new List<HardwareItem>();
        var children = new Dictionary<string, IReadOnlyList<string>>();
        foreach (var d in devices.Where(d => d.Sample is not null))
        {
            var key = Key(d);
            if (_hwinfo)
                foreach (var set in PinSets)
                    children[$"{IdPrefix}{key}:pins:{set.Key}"] = Enumerable.Range(1, 6).Select(i => $"{IdPrefix}{key}:pin{i}:{set.Key}").ToList();
            foreach (var r in Rows(d.Sample!))
                items.Add(new HardwareItem
                {
                    Id = $"{IdPrefix}{key}:{r.Key}", Name = r.Name, Kind = r.Kind, Unit = r.Unit, Group = GroupPrefix + key,
                    Backend = "openhw", CanSet = false, Source = d.Product,
                    Details = $"{d.Product} · {d.Firmware} · USB serial {d.Serial} · {d.Port} (openhw-helper)",
                });
        }
        _children = children;
        return items;
    }

    public void ReadInto(IDictionary<string, double?> readings, IReadOnlySet<string>? requested)
    {
        if (requested is not null && !requested.Any(id => id.StartsWith(IdPrefix, StringComparison.Ordinal))) return;
        var devices = Fetch();
        if (devices is null) return;
        foreach (var d in devices)
        {
            var key = Key(d);
            if (d.Sample is not { } s) continue;
            foreach (var r in Rows(s)) readings[$"{IdPrefix}{key}:{r.Key}"] = r.Value(s);
        }
    }

    /// <summary>Fault names for a device's current and logged fault bits (for tooltips and alerts).</summary>
    public IReadOnlyList<string> FaultsOf(string groupKey, bool logged)
    {
        var d = _last?.FirstOrDefault(x => GroupPrefix + Key(x) == groupKey);
        return d?.Sample is { } s ? WireViewSample.Faults(logged ? s.FaultLog : s.FaultStatus).ToList() : [];
    }

    private List<WireViewDevice>? Fetch()
    {
        if (_last is not null && _clock.Elapsed - _lastAt < ReplyFresh) return _last;
        if (_clock.Elapsed - _failedAt < RetryAfterFailure) return _last;
        try
        {
            _last = GpuWire.Decode<List<WireViewDevice>>(_request(6000)) ?? [];
            _lastAt = _clock.Elapsed;
        }
        catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or System.Text.Json.JsonException or FormatException)
        {
            _failedAt = _clock.Elapsed;
            _last = null;
        }
        return _last;
    }

    public void Dispose() { }

    public static string Describe(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
}
