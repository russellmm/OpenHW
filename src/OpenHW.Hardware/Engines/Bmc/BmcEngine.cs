using System.Globalization;

namespace OpenHW.Hardware;

/// <summary>
/// The board management controller's sensors (IPMI SDR threshold sensors: named voltage rails, DIMM, LAN and slot
/// temperatures, every fan header, PSU readings where the PSU reports them), through openhw-helper's <c>bmc</c>
/// command — the IPMI device is root-only. One group, in the BMC's own (SDR) order. Sensors the BMC says it can't
/// read right now (no PSU telemetry, empty slot, disabled header) are left out until they read.
/// </summary>
public sealed class BmcEngine(Func<int, string>? request = null) : ISensorEngine
{
    public const string GroupKey = "bmc";
    public const string IdPrefix = "bmc:";

    private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan ReplyFresh = TimeSpan.FromMilliseconds(900);

    private readonly Func<int, string> _request = request ?? (timeout =>
        HelperClient.Request("bmc", receiveTimeoutMs: timeout, maxReplyBytes: GpuWire.MaxReplyBytes));
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private BmcWireReply? _last;
    private TimeSpan _lastAt = TimeSpan.FromDays(-1), _failedAt = TimeSpan.FromDays(-1);
    private bool _everAnswered;

    public string Name => "bmc";
    public bool Available => _last is not null;

    /// <summary>True while the first request (the helper's SDR read) is still out.</summary>
    public bool Pending => _first is { IsCompleted: false };

    /// <summary>The controller ("AST2600") and firmware, for the group title; null until the helper has answered.</summary>
    public (string Controller, string Firmware)? Controller => _last is { } r ? (r.Controller, r.Firmware) : null;

    /// <summary>The latest reply (every SDR sensor with limits, read or not) — the hardware check uses it.</summary>
    public BmcWireReply? Last => _last;

    public IReadOnlyList<HardwareItem> Discover()
    {
        var reply = Fetch();
        if (reply is null) return [];
        var ids = SensorIds(reply);
        return reply.Sensors.Select((s, i) => (s, id: ids[i]))
            .Where(x => x.s.Value is not null)
            .Select(x => new HardwareItem
            {
                Id = x.id, Name = x.s.Name, Kind = x.s.Kind, Unit = x.s.Unit, Group = GroupKey, Backend = "openhw",
                CanSet = false, Source = $"{reply.Controller} BMC (IPMI, openhw-helper)",
                Details = Describe(x.s, reply),
            }).ToList();
    }

    public void ReadInto(IDictionary<string, double?> readings, IReadOnlySet<string>? requested)
    {
        if (requested is not null && !requested.Any(id => id.StartsWith(IdPrefix, StringComparison.Ordinal))) return;
        var reply = Fetch();
        if (reply is null) return;
        var ids = SensorIds(reply);
        for (var i = 0; i < reply.Sensors.Count; i++) readings[ids[i]] = reply.Sensors[i].Value;
    }

    /// <summary>Stable ids from the BMC's sensor names ("bmc:+VCORE_0"); a repeated name gets "#2", "#3"….</summary>
    public static IReadOnlyList<string> SensorIds(BmcWireReply reply)
    {
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        return reply.Sensors.Select(s =>
        {
            var n = seen[s.Name] = seen.GetValueOrDefault(s.Name) + 1;
            return IdPrefix + s.Name + (n > 1 ? "#" + n.ToString(CultureInfo.InvariantCulture) : "");
        }).ToList();
    }

    public static string Describe(BmcWireSensor s, BmcWireReply reply)
    {
        var text = $"IPMI sensor {s.Number:X2}h · {s.Entity}";
        if (s.Limits is { } l)
        {
            var parts = new List<string>();
            void Add(string label, double? v) { if (v is { } x) parts.Add($"{label} {x.ToString("0.###", CultureInfo.InvariantCulture)}"); }
            Add("lower critical", l.LowerCritical); Add("lower warning", l.LowerNonCritical);
            Add("upper warning", l.UpperNonCritical); Add("upper critical", l.UpperCritical);
            if (parts.Count > 0) text += $" · BMC limits ({s.Unit}): " + string.Join(", ", parts);
        }
        return text + $" · {reply.Controller} firmware {reply.Firmware}";
    }

    private BmcWireReply? Fetch()
    {
        if (_last is not null && _clock.Elapsed - _lastAt < ReplyFresh) return _last;
        if (_last is null && _clock.Elapsed - _failedAt < RetryAfterFailure) return null;
        if (!_everAnswered)
        {
            // The helper's first answer waits for its SDR read (many seconds over KCS): fetched off the sampling
            // thread so the other sensors keep updating; the BMC group appears at the next inventory refresh.
            _first ??= Task.Run(() => Request(25000));
            if (!_first.IsCompleted) return null;
            var first = _first.Result;
            _first = null;
            _everAnswered = first is not null;
            return Store(first);
        }
        return Store(Request(2500));
    }

    private Task<BmcWireReply?>? _first;

    private BmcWireReply? Request(int timeoutMs)
    {
        try { return GpuWire.Decode<BmcWireReply>(_request(timeoutMs)); }
        catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or System.Text.Json.JsonException or FormatException)
        {
            return null;
        }
    }

    private BmcWireReply? Store(BmcWireReply? reply)
    {
        _last = reply;
        if (reply is null) _failedAt = _clock.Elapsed; else _lastAt = _clock.Elapsed;
        return reply;
    }

    public void Dispose() { }
}
