using System.Globalization;
namespace OpenHW.Hardware;

/// <summary>One BMC sensor as the helper sends it to the window: identity, unit, limits and the latest value.</summary>
public sealed record BmcWireSensor(string Name, byte Number, string Entity, HardwareKind Kind, string Unit,
    BmcThresholds? Limits, double? Value);

/// <summary>The helper's <c>bmc</c> reply: the controller, then its threshold sensors in SDR order.</summary>
/// <summary>
/// A fan's lower alarm thresholds (RPM) as the helper's <c>bmcthresh</c> lists them: what the BMC has now, what it had
/// before OpenHW first changed them (null until then), the step the BMC stores them in, and whether it lets them be set.
/// </summary>
public sealed record BmcFanThresholds(string Name, byte Number, double? Reading, double? NonCritical, double? Critical,
    double? NonRecoverable, double Step, double Max, bool Settable, double[]? Original);

public sealed record BmcWireReply(string Controller, string Firmware, int ManufacturerId, int ProductId, List<BmcWireSensor> Sensors);

/// <summary>
/// Helper side of the board management controller (IPMI over <c>/dev/ipmi0</c>): reads the SDR repository once, then
/// keeps every threshold sensor's reading fresh on its own thread while a client asked within the last minute —
/// a full sweep over KCS takes longer than a request may wait, so requests are answered from the last sweep.
/// Read-only (<see cref="IpmiCommands"/>).
/// </summary>
public sealed class BmcReader : IDisposable
{
    private static readonly TimeSpan SweepEvery = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan IdleAfter = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan RecheckAbsentEvery = TimeSpan.FromSeconds(60);

    private readonly Func<IIpmiTransport?> _open;
    private readonly object _gate = new();
    private IIpmiTransport? _transport;
    private IpmiCommands.DeviceId? _device;
    private List<BmcSensor>? _sensors;
    private readonly Dictionary<BmcSensor, BmcThresholds?> _limits = [];
    private readonly Dictionary<BmcSensor, double?> _values = [];
    private DateTime _lastAsked = DateTime.MinValue, _lastAbsentCheck = DateTime.MinValue;
    private Thread? _thread;
    private volatile bool _stopping;
    private string? _error;
    private DateTime _errorAt;

    /// <summary>
    /// Windows: how long after a failed start (BMC or IPMI driver not ready yet — seen right after a crash reboot,
    /// 2026-11-18) the reader tries again. On Linux a failed start stays failed until the helper restarts, as before.
    /// </summary>
    public static TimeSpan WindowsRetryAfter { get; set; } = TimeSpan.FromSeconds(30); // tests shorten it
    private List<BmcDiscreteSensor> _discrete = [];
    private readonly Dictionary<BmcDiscreteSensor, int?> _states = [];
    private Dictionary<byte, string> _names = [];
    private BmcSelReply? _sel;
    private uint _selStamp;
    private readonly object _selGate = new();

    public const string DefaultOriginalsPath = "/var/lib/openhw/bmc-thresholds-original.json";
    private readonly string _originalsPath;
    private readonly Func<string> _identity;

    public BmcReader(Func<IIpmiTransport?>? open = null, string? originalsPath = null, Func<string>? identity = null)
    {
        _open = open ?? (() => LinuxIpmiTransport.Exists() ? new LinuxIpmiTransport() : null);
        _originalsPath = originalsPath ?? DefaultOriginalsPath;
        _identity = identity ?? FanDriver.MachineIdentity;
    }

    /// <summary>Where the helper reports threshold writes (its journal through stderr).</summary>
    public Action<string> Log { get; set; } = line => Console.Error.WriteLine("openhw-helper: bmc: " + line);

    // ── fan alarm thresholds ─────────────────────────────────────────────────────────────────────────────────────

    private sealed record OriginalsFile(string Identity, Dictionary<string, double?[]> Lower);

    private Dictionary<string, double?[]> LoadOriginals()
    {
        try
        {
            if (!File.Exists(_originalsPath)) return [];
            var saved = System.Text.Json.JsonSerializer.Deserialize<OriginalsFile>(File.ReadAllText(_originalsPath));
            // Another machine's (or a damaged) file is ignored: no "Restore original" until a new one is saved.
            return saved?.Lower is not null && saved.Identity == _identity() ? saved.Lower : [];
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException) { return []; }
    }

    private void SaveOriginals(Dictionary<string, double?[]> lower)
    {
        var temp = _originalsPath + ".tmp";
        Directory.CreateDirectory(Path.GetDirectoryName(_originalsPath)!);
        using (var stream = File.Create(temp))
        {
            System.Text.Json.JsonSerializer.Serialize(stream, new OriginalsFile(_identity(), lower));
            stream.Flush(flushToDisk: true);
        }
        File.Move(temp, _originalsPath, overwrite: true);
    }

    private static double Step(BmcSensor s) =>
        s.Convert(1) is { } one && s.Convert(0) is { } zero ? Math.Abs(one - zero) : 1;

    /// <summary>Every fan (RPM) sensor's lower thresholds; null while the BMC hasn't answered.</summary>
    public List<BmcFanThresholds>? FanThresholds(TimeSpan wait)
    {
        if (Read(wait) is null) return null;
        var originals = LoadOriginals();
        lock (_gate)
            return _sensors!.Where(s => s.Kind == HardwareKind.Tach).Select(s =>
            {
                var l = _limits.GetValueOrDefault(s);
                return new BmcFanThresholds(s.Name, s.Number, _values.GetValueOrDefault(s), l?.LowerNonCritical, l?.LowerCritical,
                    l?.LowerNonRecoverable, Step(s), s.Convert(255) ?? 0, (s.SettableThresholds & 0x07) == 0x07,
                    originals.TryGetValue(s.Name, out var o) && o.Length == 3 && o.All(x => x is not null) ? o.Select(x => x!.Value).ToArray() : null);
            }).ToList();
    }

    /// <summary>
    /// Sets a fan's lower thresholds (RPM): non-recoverable ≤ critical ≤ non-critical, each a value the BMC can store.
    /// The thresholds it had before are saved first (once per fan) for <see cref="RestoreFanThresholds"/>. Returns
    /// what the BMC reads back.
    /// </summary>
    public BmcThresholds SetFanThresholds(string name, double nonRecoverable, double critical, double nonCritical)
    {
        if (Read(TimeSpan.FromSeconds(20)) is null) throw new IOException(Error ?? "BMC not ready");
        BmcSensor sensor;
        IIpmiTransport transport;
        BmcThresholds? before;
        lock (_gate)
        {
            sensor = _sensors!.FirstOrDefault(s => s.Name == name && s.Kind == HardwareKind.Tach)
                ?? throw new ArgumentException($"no BMC fan sensor {name}");
            transport = _transport!;
            before = _limits.GetValueOrDefault(sensor);
        }
        if ((sensor.SettableThresholds & 0x07) != 0x07) throw new ArgumentException($"the BMC doesn't let {name}'s lower thresholds be set");
        if (!(nonRecoverable <= critical && critical <= nonCritical)) throw new ArgumentException("expected non-recoverable ≤ critical ≤ non-critical");
        byte Raw(double v) => IpmiCommands.Raw(sensor, v) ?? throw new ArgumentException(
            $"{v} RPM can't be stored for {name}: use a multiple of {Step(sensor)} RPM up to {sensor.Convert(255)}");
        var (lnr, lcr, lnc) = (Raw(nonRecoverable), Raw(critical), Raw(nonCritical));
        if (before is { LowerNonCritical: var a, LowerCritical: var b, LowerNonRecoverable: var c } && a == nonCritical && b == critical && c == nonRecoverable)
            return before; // nothing to write
        var originals = LoadOriginals();
        if (!originals.ContainsKey(name) && before is not null)
        {
            originals[name] = [before.LowerNonRecoverable, before.LowerCritical, before.LowerNonCritical];
            SaveOriginals(originals);
        }
        IpmiCommands.SetLowerThresholds(transport, sensor, lnc, lcr, lnr);
        var after = IpmiCommands.GetThresholds(transport, sensor) ?? throw new IOException($"{name}: thresholds written, but the BMC didn't read them back");
        lock (_gate) _limits[sensor] = after;
        Log($"{name} lower thresholds {Describe(before)} → {Describe(after)} RPM");
        if (after.LowerNonRecoverable != nonRecoverable || after.LowerCritical != critical || after.LowerNonCritical != nonCritical)
            throw new IOException($"{name}: the BMC reads back {Describe(after)} RPM instead of {nonRecoverable}/{critical}/{nonCritical}");
        return after;
    }

    /// <summary>Puts back the thresholds a fan had before OpenHW first changed them.</summary>
    public BmcThresholds RestoreFanThresholds(string name)
    {
        if (!LoadOriginals().TryGetValue(name, out var o) || o.Length != 3 || o.Any(x => x is null))
            throw new ArgumentException($"OpenHW hasn't changed {name}'s thresholds");
        return SetFanThresholds(name, o[0]!.Value, o[1]!.Value, o[2]!.Value);
    }

    private static string Describe(BmcThresholds? t) => t is null ? "?" :
        string.Join('/', new[] { t.LowerNonRecoverable, t.LowerCritical, t.LowerNonCritical }.Select(v => v?.ToString(CultureInfo.InvariantCulture) ?? "–"));

    /// <summary>The latest reply, waiting up to <paramref name="wait"/> for the first sweep; null when there is no BMC.</summary>
    public BmcWireReply? Read(TimeSpan wait)
    {
        lock (_gate)
        {
            _lastAsked = DateTime.UtcNow;
            if (OperatingSystem.IsWindows() && _error is not null && _sensors is null && DateTime.UtcNow - _errorAt > WindowsRetryAfter && _thread is { IsAlive: false })
            {
                (_error, _thread) = (null, null); // start over: open the transport, read the SDR again
                _transport?.Dispose();
                _transport = null;
            }
            if (_thread is null && !_stopping)
            {
                _thread = new Thread(Loop) { IsBackground = true, Name = "bmc" };
                _thread.Start();
            }
            var until = DateTime.UtcNow + wait;
            while (_sensors is null && _error is null && DateTime.UtcNow < until)
                Monitor.Wait(_gate, until - DateTime.UtcNow);
            if (_sensors is null || _device is null) return null;
            return new BmcWireReply(_controllerName ?? $"BMC (product 0x{_device.ProductId:x4})", _device.Firmware, _device.ManufacturerId, _device.ProductId,
                _sensors.Select(s => new BmcWireSensor(s.Name, s.Number, s.Entity, s.Kind, s.Unit,
                    _limits.GetValueOrDefault(s), _values.GetValueOrDefault(s))).ToList());
        }
    }

    public string? Error { get { lock (_gate) return _error; } }

    /// <summary>The discrete sensors' asserted states from the last sweep (waits for the first, like <see cref="Read"/>).</summary>
    public List<BmcStateWire>? States(TimeSpan wait)
    {
        if (Read(wait) is null) return null;
        lock (_gate)
            return _discrete.Select(d => _states.GetValueOrDefault(d) is { } bits
                    ? new BmcStateWire(d.Name, d.Number, EntityName(d.EntityId), IpmiEvents.SensorTypeName(d.SensorType),
                        IpmiEvents.States(d.SensorType, d.EventType, bits), bits, true)
                    : new BmcStateWire(d.Name, d.Number, EntityName(d.EntityId), IpmiEvents.SensorTypeName(d.SensorType), [], 0, false))
                .ToList();
    }

    /// <summary>
    /// The System Event Log: its info and the newest <paramref name="newest"/> entries. Read on request only (a full
    /// walk over KCS takes a few seconds), and again only when the BMC's "last add" or "last erase" time changes.
    /// </summary>
    public BmcSelReply? Sel(TimeSpan wait, int newest = 500)
    {
        if (Read(wait) is null) return null;
        IIpmiTransport? transport;
        Dictionary<byte, string> names;
        lock (_gate) { transport = _transport; names = _names; }
        if (transport is null) return null;
        lock (_selGate)
        {
            var info = IpmiEvents.GetSelInfo(transport) ?? throw new IOException("BMC did not answer Get SEL Info");
            var stamp = info.LastAdd ^ info.LastErase * 31 ^ (uint)info.Entries;
            if (_sel is null || stamp != _selStamp)
            {
                var records = info.Entries == 0 ? [] : IpmiEvents.ReadSel(transport);
                var entries = records.Select(r => IpmiEvents.Decode(r, n => names.GetValueOrDefault(n))).ToList();
                _sel = new BmcSelReply(info.Entries, info.FreeBytes, IpmiEvents.Utc(info.LastAdd), IpmiEvents.Utc(info.LastErase),
                    entries.Skip(Math.Max(0, entries.Count - newest)).ToList());
                _selStamp = stamp;
            }
            return _sel;
        }
    }

    private static string EntityName(byte id) => id switch
    {
        3 => "Processor", 7 => "System board", 8 => "Memory module", 10 => "Power supply", 11 => "Add-in card",
        29 => "Fan", 34 => "BIOS", 40 => "Battery", 49 => "Add-in card", _ => $"Entity {id}",
    };

    // The management controller locator record names the BMC chip on ASPEED boards ("AST2600").
    private string? _controllerName;

    private void Loop()
    {
        try
        {
            var transport = _open() ?? throw new IOException("no IPMI device (/dev/ipmi0)");
            var device = IpmiCommands.GetDeviceId(transport) ?? throw new IOException("BMC did not answer Get Device ID");
            var records = IpmiCommands.ReadSdr(transport);
            var sensors = records.Select(r => BmcSensor.Parse(r)).OfType<BmcSensor>().Where(s => s.Owner == 0x20).ToList();
            // Management controller locator (12h): its name is the BMC chip on ASPEED boards ("AST2600").
            var mc = records.FirstOrDefault(r => r.Length > 16 && r[3] == 0x12);
            string? mcName = null;
            if (mc is not null && (mc[15] & 0x1f) is var len and > 0 && mc.Length >= 16 + len)
                mcName = System.Text.Encoding.ASCII.GetString(mc, 16, len).TrimEnd('\0', ' ');
            var discrete = records.Select(r => BmcDiscreteSensor.Parse(r)).OfType<BmcDiscreteSensor>().ToList();
            var names = new Dictionary<byte, string>();
            foreach (var x in sensors) names.TryAdd(x.Number, x.Name);
            foreach (var x in discrete) names.TryAdd(x.Number, x.Name);
            var limits = sensors.ToDictionary(s => s, s => Try(() => IpmiCommands.GetThresholds(transport, s)));
            lock (_gate)
            {
                _transport = transport; _device = device; _controllerName = mcName; _discrete = discrete; _names = names;
                foreach (var (s, l) in limits) _limits[s] = l;
            }
            Sweep(transport, sensors, all: true);
            lock (_gate) { _sensors = sensors; Monitor.PulseAll(_gate); }

            while (!_stopping)
            {
                Thread.Sleep(SweepEvery);
                lock (_gate) if (DateTime.UtcNow - _lastAsked > IdleAfter) continue;
                var recheck = DateTime.UtcNow - _lastAbsentCheck > RecheckAbsentEvery;
                Sweep(transport, sensors, recheck);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            lock (_gate) { _error = ex.Message; _errorAt = DateTime.UtcNow; Monitor.PulseAll(_gate); }
        }
    }

    // Sensors that read "unavailable" (absent PSU, empty slot, disabled header) are retried once a minute, not every sweep.
    private void Sweep(IIpmiTransport transport, List<BmcSensor> sensors, bool all)
    {
        if (all) _lastAbsentCheck = DateTime.UtcNow;
        List<BmcDiscreteSensor> discrete;
        lock (_gate) discrete = _discrete;
        foreach (var d in discrete)
        {
            if (_stopping) return;
            var bits = Try(() => IpmiEvents.GetStates(transport, d));
            lock (_gate) _states[d] = bits;
        }
        foreach (var s in sensors)
        {
            if (_stopping) return;
            bool wasAbsent;
            lock (_gate) wasAbsent = _values.TryGetValue(s, out var v) && v is null;
            if (wasAbsent && !all) continue;
            var raw = Try(() => IpmiCommands.GetReading(transport, s));
            var value = raw is { } b ? s.Convert(b) : null;
            lock (_gate) _values[s] = value;
        }
    }

    private static T? Try<T>(Func<T?> read)
    {
        try { return read(); }
        catch (IOException) { return default; }
    }

    public void Dispose()
    {
        _stopping = true;
        _thread?.Join(TimeSpan.FromSeconds(5));
        _transport?.Dispose();
    }
}
