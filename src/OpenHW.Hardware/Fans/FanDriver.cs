using System.Text.Json;
using System.Text.RegularExpressions;

namespace OpenHW.Hardware;

/// <summary>One fan zone as clients see it.</summary>
/// <param name="Target">The fixed duty OpenHW holds the zone at, or null while the firmware's own curve runs.</param>
/// <param name="Pending">A lower duty asked for that waits for the flash limits (see <see cref="FanDriver"/>).</param>
/// <param name="Writes">Curve writes to the BMC since the helper started (takeovers, changes, restores).</param>
/// <param name="SavedOriginal">The zone's curve as it was before OpenHW first configured it (kept for "Restore original").</param>
/// <param name="MinDuty">The lowest duty a configured curve may use on this zone (pump and CPU fan have floors).</param>
public sealed record FanZoneStatus(string Id, string Name, string Label, string RpmSensorId, bool Owned, bool OwnedByYou,
    int? Target, int? Pending, IReadOnlyList<FanCurvePoint>? Active, IReadOnlyList<FanCurvePoint>? Original,
    int Writes, DateTime? LastWriteUtc, IReadOnlyList<FanCurvePoint>? SavedOriginal = null, int MinDuty = 1);

public sealed record FanDriverStatus(bool Available, string? Reason, string? OtherController, DateTime SinceUtc,
    IReadOnlyList<FanZoneStatus> Zones);

/// <summary>
/// openhw-helper's fan driver: the one program that writes fan settings, for OpenHW and, while it still runs, OpenFan.
/// Follows OpenFan's proven model — a client (identified by a random token) takes a zone over by asking for a duty,
/// renews its lease with every request or a heartbeat, and the zone's original firmware curve comes back when the
/// client releases it, its lease runs out (15 s), the helper stops, or — after a crash — the helper starts again
/// (the original is journaled before the first write). Adds limits to spare the BMC's flash: a faster duty is written
/// at once, a slower one only after <see cref="DownHold"/> since the last write and when it's at least
/// <see cref="DownStep"/> points lower; meanwhile it waits as Pending. Restores are never delayed.
/// </summary>
public sealed partial class FanDriver : IDisposable
{
    public static readonly TimeSpan LeaseTime = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan DownHold = TimeSpan.FromSeconds(30);
    public const int DownStep = 3;
    public const string DefaultJournal = "/var/lib/openhw/fan-restore.json";
    public const string OpenFanJournal = "/var/lib/openfan/bmc-restore.json";

    /// <summary>
    /// Floors for configured curves: a pump or CPU fan set too low costs the most. CPU_FAN (zone 0) ≥ 20 %, W_PUMP+
    /// (zone 6) ≥ 50 %; the firmware itself refuses 0 % everywhere.
    /// </summary>
    public static int MinDuty(int zone) => zone switch { 0 => 20, 6 => 50, _ => 1 };
    private static readonly TimeSpan ActiveCacheTime = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RetryOpenAfter = TimeSpan.FromMinutes(1);

    private sealed class Zone
    {
        public string? Owner;
        public DateTime Lease;
        public int? Target, Pending;
        public byte[]? Active;
        public DateTime ActiveAt;
        public int Writes;
        public DateTime? LastWrite;
    }

    private readonly object _gate = new();
    private readonly Func<(AsusBmcFans? Fans, string? Reason)> _open;
    private readonly string _journalPath;
    private readonly string _identity;
    private readonly Func<DateTime> _now;
    private readonly Func<string?> _otherController;
    private readonly Dictionary<int, byte[]> _original = [];
    private readonly string _savedOriginalsPath;
    private readonly Dictionary<int, byte[]> _savedOriginals = [];
    private readonly Zone[] _zones = Enumerable.Range(0, AsusBmcFans.ZoneCount).Select(_ => new Zone()).ToArray();
    private readonly DateTime _since;
    private readonly Timer? _watchdog;
    private AsusBmcFans? _fans;
    private string? _reason = "Not opened yet.";
    private DateTime _lastOpenAttempt = DateTime.MinValue;
    private bool _disposed;

    public FanDriver(Func<(AsusBmcFans? Fans, string? Reason)> open, string journalPath, string identity,
        Func<DateTime>? now = null, Func<string?>? otherController = null, bool startWatchdog = false, string? savedOriginalsPath = null)
    {
        _open = open;
        _journalPath = journalPath;
        _savedOriginalsPath = savedOriginalsPath ?? Path.Combine(Path.GetDirectoryName(journalPath)!, "fan-original.json");
        _identity = identity;
        _now = now ?? (() => DateTime.UtcNow);
        _otherController = otherController ?? (() => null);
        _since = _now();
        lock (_gate) EnsureOpen();
        if (startWatchdog) _watchdog = new Timer(_ => Tick(), null, 1000, 1000);
    }

    /// <summary>The live driver: the WRX90E's BMC over /dev/ipmi0, journal in /var/lib/openhw.</summary>
    public static FanDriver Live() => new(() =>
        {
            if (!LinuxIpmiTransport.Exists()) return (null, "No IPMI device (/dev/ipmi0).");
            var ipmi = new LinuxIpmiTransport();
            var fans = AsusBmcFans.Open(ipmi, SysFs.ReadText("/sys/class/dmi/id/board_vendor"),
                SysFs.ReadText("/sys/class/dmi/id/board_name"), out var reason);
            if (fans is null) ipmi.Dispose();
            return (fans, reason);
        }, DefaultJournal, MachineIdentity(), otherController: OpenFanController, startWatchdog: true);

    public static string MachineIdentity()
    {
        var uuid = SysFs.ReadText("/sys/class/dmi/id/product_uuid");
        return uuid.Length > 0 ? uuid : "unavailable";
    }

    /// <summary>OpenFan's own BMC fan driver holds zones while its restore journal exists.</summary>
    public static string? OpenFanController() =>
        File.Exists(OpenFanJournal) ? "OpenFan is controlling BMC fans right now (its restore journal exists)." : null;

    public static string ZoneId(int zone) => "bmc:" + zone;

    // ── client commands ──────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Takes the zone over (if free) and asks for a fixed duty, 1–100 %. Returns what happened.</summary>
    public string Set(string token, string zoneId, int percent)
    {
        CheckToken(token);
        if (percent is < 1 or > 100) throw new ArgumentException("duty must be 1–100 % (the BMC refuses 0 %)");
        lock (_gate)
        {
            var (index, zone) = Find(zoneId);
            var fans = Fans();
            var now = _now();
            if (zone.Owner is { } owner && owner != token) throw new ArgumentException($"{zoneId} is controlled by another client");
            if (zone.Owner is null && _otherController() is { } other) throw new ArgumentException(other);

            zone.Owner = token;
            zone.Lease = now;
            RenewAll(token, now);
            if (zone.Target is null)
            {
                // Takeover: journal the firmware's own curve before anything is written.
                var current = fans.Read(index);
                zone.Active = current;
                zone.ActiveAt = now;
                if (!_original.ContainsKey(index))
                {
                    _original[index] = current;
                    try { SaveJournal(); }
                    catch { _original.Remove(index); zone.Owner = null; throw; }
                }
                Apply(fans, index, zone, percent, now);
                return $"{zoneId} {zone.Target}";
            }
            if (percent >= zone.Target)
            {
                zone.Pending = null;
                if (percent > zone.Target) Apply(fans, index, zone, percent, now);
                return $"{zoneId} {zone.Target}";
            }
            // Slower: only after the hold time, and only in steps worth a flash write.
            if (zone.Target - percent < DownStep) { zone.Pending = null; return $"{zoneId} {zone.Target} (within {DownStep} %)"; }
            if (zone.LastWrite is { } last && now - last < DownHold) { zone.Pending = percent; return $"{zoneId} {zone.Target} pending {percent}"; }
            zone.Pending = null;
            Apply(fans, index, zone, percent, now);
            return $"{zoneId} {zone.Target}";
        }
    }

    /// <summary>Gives a zone (or every zone the client holds: "all") back to its firmware curve.</summary>
    public string Release(string token, string zoneId)
    {
        CheckToken(token);
        lock (_gate)
        {
            var indexes = zoneId == "all"
                ? Enumerable.Range(0, _zones.Length).Where(i => _zones[i].Owner == token).ToList()
                : [Find(zoneId).Index];
            foreach (var i in indexes)
            {
                if (_zones[i].Owner is { } owner && owner != token) throw new ArgumentException($"{ZoneId(i)} is controlled by another client");
                Restore(i);
            }
            return indexes.Count == 0 ? "nothing held" : string.Join(' ', indexes.Select(ZoneId));
        }
    }

    /// <summary>Keeps the client's zones (renews the lease); returns how many it holds.</summary>
    public int Beat(string token)
    {
        CheckToken(token);
        lock (_gate)
        {
            RenewAll(token, _now());
            return _zones.Count(z => z.Owner == token);
        }
    }

    public FanDriverStatus Status(string? token)
    {
        lock (_gate)
        {
            var now = _now();
            if (token is not null) RenewAll(token, now);
            var fans = _disposed ? null : TryFans();
            var zones = Enumerable.Range(0, _zones.Length).Select(i =>
            {
                var z = _zones[i];
                if (fans is not null && (z.Active is null || now - z.ActiveAt > ActiveCacheTime))
                {
                    try { z.Active = fans.Read(i); z.ActiveAt = now; }
                    catch (IOException) { z.Active = null; }
                }
                return new FanZoneStatus(ZoneId(i), AsusBmcFans.Sensors[i], AsusBmcFans.Labels[i], BmcEngine.IdPrefix + AsusBmcFans.Sensors[i],
                    z.Owner is not null, token is not null && z.Owner == token, z.Target, z.Pending,
                    z.Active is { } a ? AsusBmcFans.Curve(a) : null,
                    _original.TryGetValue(i, out var o) ? AsusBmcFans.Curve(o) : null, z.Writes, z.LastWrite,
                    _savedOriginals.TryGetValue(i, out var so) ? AsusBmcFans.Curve(so) : null, MinDuty(i));
            }).ToList();
            return new FanDriverStatus(fans is not null, fans is null ? _reason : null, _otherController(), _since, zones);
        }
    }

    // ── configured curves (stage 2): written once, kept by the BMC, run by the BMC ───────────────────────────────

    /// <summary>
    /// Writes a zone's own curve permanently (the BMC runs it from then on, with or without OpenHW). Five points; the
    /// last at 100 °C (the web console edits points A–D only); temperatures rise; duties between the zone's floor and 100 %.
    /// The zone's curve before OpenHW's first change is saved once (fan-original.json) for <see cref="RestoreOriginal"/>.
    /// Refused while a client holds the zone (a takeover) or OpenFan holds BMC fans. One write, read back; none if equal.
    /// </summary>
    public string SetCurve(string zoneId, IReadOnlyList<FanCurvePoint> points)
    {
        lock (_gate)
        {
            var (index, zone) = Find(zoneId);
            if (points.Count != 5) throw new ArgumentException("a curve has five points");
            if (points[4].TemperatureC != 100)
                throw new ArgumentException("the last point is at 100 °C");
            for (var i = 1; i < 5; i++)
                if (points[i].TemperatureC <= points[i - 1].TemperatureC) throw new ArgumentException("temperatures must rise from point to point");
            if (points[0].TemperatureC < 0) throw new ArgumentException("temperatures must be 0–100 °C");
            var floor = MinDuty(index);
            if (points.Any(p => p.DutyPercent < floor || p.DutyPercent > 100))
                throw new ArgumentException($"duties on {AsusBmcFans.Sensors[index]} must be {floor}–100 %");
            if (zone.Owner is not null) throw new ArgumentException($"{zoneId} is taken over by a client right now");
            if (_otherController() is { } other) throw new ArgumentException(other);
            var fans = Fans();
            var current = fans.Read(index);
            if (!_savedOriginals.ContainsKey(index))
            {
                _savedOriginals[index] = current;
                try { SaveOriginals(); }
                catch { _savedOriginals.Remove(index); throw; }
            }
            byte[] state = [0x04, .. points.SelectMany(p => new[] { (byte)p.TemperatureC, (byte)p.DutyPercent })];
            return WriteConfigured(fans, index, zone, current, state);
        }
    }

    /// <summary>Puts back the curve the zone had before OpenHW first configured it.</summary>
    public string RestoreOriginal(string zoneId)
    {
        lock (_gate)
        {
            var (index, zone) = Find(zoneId);
            if (zone.Owner is not null) throw new ArgumentException($"{zoneId} is taken over by a client right now");
            if (!_savedOriginals.TryGetValue(index, out var original)) throw new ArgumentException($"no saved original curve for {zoneId}");
            var fans = Fans();
            return WriteConfigured(fans, index, zone, fans.Read(index), original);
        }
    }

    private string WriteConfigured(AsusBmcFans fans, int index, Zone zone, byte[] current, byte[] state)
    {
        var now = _now();
        if (!current.AsSpan().SequenceEqual(state))
        {
            fans.Write(index, state);
            zone.Writes++;
            zone.LastWrite = now;
        }
        zone.Active = state;
        zone.ActiveAt = now;
        return $"{ZoneId(index)} {string.Join(',', AsusBmcFans.Curve(state).Select(p => $"{p.TemperatureC}:{p.DutyPercent}"))}";
    }

    private void SaveOriginals()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_savedOriginalsPath)!);
        var temp = _savedOriginalsPath + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            JsonSerializer.Serialize(stream, new Journal(_identity, _savedOriginals));
            stream.Flush(flushToDisk: true);
        }
        File.Move(temp, _savedOriginalsPath, overwrite: true);
    }

    private void LoadOriginals()
    {
        if (!File.Exists(_savedOriginalsPath)) return;
        var saved = JsonSerializer.Deserialize<Journal>(File.ReadAllText(_savedOriginalsPath));
        // Another machine's (or a damaged) file is ignored, not trusted: no "Restore original" until a new one is saved.
        if (saved?.Curves is null || saved.Identity != _identity) return;
        foreach (var (zone, state) in saved.Curves)
            if (zone >= 0 && zone < _zones.Length && state is not null && AsusBmcFans.ValidState(state)) _savedOriginals[zone] = state;
    }

    /// <summary>Parses "20:20,45:40,65:70,70:100,100:100" (the helper's wire form).</summary>
    public static IReadOnlyList<FanCurvePoint> ParseCurve(string text)
    {
        var points = new List<FanCurvePoint>();
        foreach (var part in text.Split(','))
        {
            var pair = part.Split(':');
            if (pair.Length != 2 || !int.TryParse(pair[0], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var t) ||
                !int.TryParse(pair[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var d) || t > 100 || d > 100)
                throw new ArgumentException("expected five temperature:duty pairs");
            points.Add(new FanCurvePoint(t, d));
        }
        return points;
    }

    // ── watchdog ─────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Once a second: expired leases restore their zones; due pending (slower) duties are written.</summary>
    public void Tick()
    {
        lock (_gate)
        {
            if (_disposed) return;
            var now = _now();
            for (var i = 0; i < _zones.Length; i++)
            {
                var z = _zones[i];
                if (z.Owner is not null && now - z.Lease > LeaseTime)
                {
                    Console.Error.WriteLine($"openhw-helper: fans: {ZoneId(i)} lease expired; restoring its curve");
                    Restore(i);
                }
                else if (z.Pending is { } pending && z.Target is { } target && z.LastWrite is { } last && now - last >= DownHold &&
                         target - pending >= DownStep && _fans is { } fans)
                {
                    z.Pending = null;
                    try { Apply(fans, i, z, pending, now); }
                    catch (IOException ex) { Console.Error.WriteLine($"openhw-helper: fans: {ex.Message}"); }
                }
            }
        }
    }

    public void Dispose()
    {
        _watchdog?.Dispose();
        lock (_gate)
        {
            if (_disposed) return;
            foreach (var i in _original.Keys.ToArray()) Restore(i);
            _disposed = true;
        }
    }

    // ── internals (under _gate) ──────────────────────────────────────────────────────────────────────────────────

    private void Apply(AsusBmcFans fans, int index, Zone zone, int percent, DateTime now)
    {
        var target = AsusBmcFans.Flat(_original[index], percent);
        try
        {
            if (zone.Active is null || !zone.Active.AsSpan().SequenceEqual(target))
            {
                fans.Write(index, target);
                zone.Writes++;
                zone.LastWrite = now;
            }
            zone.Active = target;
            zone.ActiveAt = now;
            zone.Target = percent;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            // Never leave a zone half-taken: back to the firmware curve, then report.
            Restore(index);
            throw new IOException(ex.Message + (_original.ContainsKey(index) ? " Restoring its curve also failed." : " Its curve was restored."), ex);
        }
    }

    private void Restore(int index)
    {
        var zone = _zones[index];
        if (_original.TryGetValue(index, out var state) && _fans is { } fans)
        {
            try
            {
                fans.Write(index, state);
                zone.Writes++;
                zone.LastWrite = _now();
                zone.Active = state;
                zone.ActiveAt = _now();
                _original.Remove(index);
                SaveJournal();
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                // The journal keeps the original: the next start (or the next restore attempt) puts it back.
                Console.Error.WriteLine($"openhw-helper: fans: restoring {ZoneId(index)} failed: {ex.Message}");
                return;
            }
        }
        zone.Owner = null;
        zone.Target = null;
        zone.Pending = null;
    }

    private void RenewAll(string token, DateTime now)
    {
        foreach (var z in _zones.Where(z => z.Owner == token)) z.Lease = now;
    }

    private (int Index, Zone Zone) Find(string zoneId)
    {
        var m = ZonePattern().Match(zoneId);
        var i = m.Success ? m.Groups[1].Value[0] - '0' : -1;
        if (i < 0 || i >= _zones.Length) throw new ArgumentException("unknown fan zone");
        return (i, _zones[i]);
    }

    private AsusBmcFans Fans() => TryFans() ?? throw new ArgumentException(_reason ?? "BMC fan control unavailable");

    private AsusBmcFans? TryFans()
    {
        EnsureOpen();
        return _fans;
    }

    private void EnsureOpen()
    {
        if (_fans is not null || _disposed || _now() - _lastOpenAttempt < RetryOpenAfter) return;
        _lastOpenAttempt = _now();
        try
        {
            var (fans, reason) = _open();
            _reason = reason;
            if (fans is null) return;
            _fans = fans;
            LoadOriginals();
            Recover();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
        {
            _fans = null;
            _reason = "BMC fan control: " + ex.Message;
        }
    }

    private static void CheckToken(string token)
    {
        if (!TokenPattern().IsMatch(token)) throw new ArgumentException("invalid client token");
    }

    // ── journal ──────────────────────────────────────────────────────────────────────────────────────────────────

    private sealed record Journal(string Identity, Dictionary<int, byte[]> Curves);

    private void SaveJournal()
    {
        if (_original.Count == 0)
        {
            File.Delete(_journalPath);
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(_journalPath)!);
        var temp = _journalPath + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            JsonSerializer.Serialize(stream, new Journal(_identity, _original));
            stream.Flush(flushToDisk: true);
        }
        File.Move(temp, _journalPath, overwrite: true);
    }

    /// <summary>After a crash: puts every journaled curve back before any client can take a zone.</summary>
    private void Recover()
    {
        if (!File.Exists(_journalPath)) return;
        var journal = JsonSerializer.Deserialize<Journal>(File.ReadAllText(_journalPath));
        if (journal?.Curves is null || journal.Identity != _identity || _identity == "unavailable" ||
            journal.Curves.Any(p => p.Key < 0 || p.Key >= _zones.Length || p.Value is null || !AsusBmcFans.ValidState(p.Value)))
            throw new InvalidDataException($"The fan restore journal ({_journalPath}) doesn't match this machine; fan writes disabled until it's checked.");
        foreach (var (zone, state) in journal.Curves) _original[zone] = state;
        foreach (var zone in _original.Keys.ToArray())
        {
            Console.Error.WriteLine($"openhw-helper: fans: restoring {ZoneId(zone)} from the journal");
            Restore(zone);
        }
        if (_original.Count > 0) throw new IOException("Some journaled fan curves couldn't be restored.");
    }

    [GeneratedRegex(@"^[0-9a-f]{32}$", RegexOptions.CultureInvariant)] private static partial Regex TokenPattern();
    [GeneratedRegex(@"^bmc:([0-9])$", RegexOptions.CultureInvariant)] private static partial Regex ZonePattern();
}
