using System.Text.RegularExpressions;

namespace OpenHW.Hardware;

/// <summary>One NVIDIA card's fan as the GPU fan driver sees it.</summary>
public sealed record GpuFanCard(string Uuid, string Name, string Pci, int Fans, int MinPercent, int MaxPercent);

/// <summary>NVML fan access (root): the cards, a manual duty, the card's own automatic control back.</summary>
public interface INvmlFans
{
    IReadOnlyList<GpuFanCard> Cards();
    /// <summary>0 on success, else the NVML error code.</summary>
    int SetManual(string uuid, int fan, int percent);
    int SetDefault(string uuid, int fan);
}

/// <summary>One GPU fan for clients: id "nvml:&lt;uuid&gt;:fan:&lt;n&gt;" (the same id as its speed reading).</summary>
public sealed record GpuFanStatus(string Id, string Card, string Pci, int Fan, int MinPercent, int MaxPercent, bool Owned, bool OwnedByYou, int? Target);

public sealed record GpuFanDriverStatus(string? Reason, IReadOnlyList<GpuFanStatus> Fans);

/// <summary>
/// openhw-helper's NVIDIA fan driver (protocol 15), with the BMC fan driver's client model: a client (random token)
/// takes a fan by asking for a duty, keeps it by renewing its lease (any request or <c>fanbeat</c>), and the fan goes
/// back to the card's own automatic control when the client releases it, stops renewing (15 s), the helper stops, or a
/// card is lent to a VM. A duty of 0 % means the card's automatic control (which may stop the fan); others are clamped
/// to the card's minimum. NVML settings aren't stored anywhere, so there's no write budget — unlike the BMC.
/// Refused while OpenFan's helper runs (it writes the same fans).
/// </summary>
public sealed partial class GpuFanDriver : IDisposable
{
    public static readonly TimeSpan LeaseTime = FanDriver.LeaseTime;
    public const string OpenFanSocket = "/run/openfan/helper.sock";

    private sealed class Held
    {
        public string Owner = "";
        public DateTime Lease;
        public int Target;
    }

    private readonly object _gate = new();
    private readonly INvmlFans _nvml;
    private readonly Func<DateTime> _now;
    private readonly Func<string?> _otherController;
    private readonly Dictionary<string, Held> _held = new(StringComparer.OrdinalIgnoreCase);
    private readonly Timer? _watchdog;
    private bool _suspended, _disposed;

    public GpuFanDriver(INvmlFans nvml, Func<DateTime>? now = null, Func<string?>? otherController = null, bool startWatchdog = false)
    {
        _nvml = nvml;
        _now = now ?? (() => DateTime.UtcNow);
        _otherController = otherController ?? OpenFanController;
        if (startWatchdog) _watchdog = new Timer(_ => Tick(), null, 1000, 1000);
    }

    /// <summary>OpenFan's helper writes NVIDIA fans too: never both.</summary>
    public static string? OpenFanController() =>
        File.Exists(OpenFanSocket) ? "OpenFan's helper is running (it controls GPU fans too): stop it to let OpenHW drive GPU fans." : null;

    public static string FanId(string uuid, int fan) => $"nvml:{uuid}:fan:{fan}";

    public GpuFanDriverStatus Status(string? token)
    {
        lock (_gate)
        {
            if (token is not null) Renew(token);
            var cards = _suspended || _disposed ? [] : _nvml.Cards();
            var fans = cards.SelectMany(c => Enumerable.Range(0, c.Fans).Select(f =>
            {
                var id = FanId(c.Uuid, f);
                _held.TryGetValue(id, out var h);
                return new GpuFanStatus(id, c.Name, c.Pci, f, c.MinPercent, c.MaxPercent, h is not null, h is not null && h.Owner == token, h?.Target);
            })).ToList();
            return new GpuFanDriverStatus(_suspended ? "A card is being lent to a VM." : _otherController(), fans);
        }
    }

    public string Set(string token, string id, int percent)
    {
        CheckToken(token);
        if (percent is < 0 or > 100) throw new ArgumentException("duty must be 0–100 % (0 = the card's automatic control)");
        lock (_gate)
        {
            if (_suspended) throw new ArgumentException("a card is being lent to a VM");
            var (card, fan) = Find(id);
            if (_held.TryGetValue(id, out var h) && h.Owner != token) throw new ArgumentException($"{id} is controlled by another client");
            if (h is null && _otherController() is { } other) throw new ArgumentException(other);
            var target = percent == 0 ? 0 : Math.Clamp(percent, card.MinPercent, Math.Max(card.MinPercent, card.MaxPercent));
            var code = target == 0 ? _nvml.SetDefault(card.Uuid, fan) : _nvml.SetManual(card.Uuid, fan, target);
            if (code != 0) throw new IOException($"NVML refused the fan speed for {card.Name} fan {fan} (error {code}).");
            _held[id] = h ??= new Held { Owner = token };
            h.Target = target;
            h.Lease = _now();
            Renew(token);
            return $"{id} {target}";
        }
    }

    public string Release(string token, string id)
    {
        CheckToken(token);
        lock (_gate)
        {
            var ids = id == "all" ? _held.Where(p => p.Value.Owner == token).Select(p => p.Key).ToList() : [id];
            foreach (var i in ids)
            {
                if (_held.TryGetValue(i, out var h) && h.Owner != token) throw new ArgumentException($"{i} is controlled by another client");
                Restore(i);
            }
            return ids.Count == 0 ? "nothing held" : string.Join(' ', ids);
        }
    }

    public int Beat(string token)
    {
        lock (_gate)
        {
            Renew(token);
            return _held.Values.Count(h => h.Owner == token);
        }
    }

    /// <summary>Before a card is lent (NVML closes): every held fan back to automatic, and no takeovers until <see cref="Resume"/>.</summary>
    public void Suspend()
    {
        lock (_gate)
        {
            var count = _held.Count;
            foreach (var id in _held.Keys.ToList()) Restore(id);
            _suspended = true;
            Console.Error.WriteLine($"openhw-helper: gpu fans: suspended for a lend; {count} fan(s) back to their cards' own control");
        }
    }

    public void Resume()
    {
        lock (_gate)
        {
            if (!_suspended) return;
            _suspended = false;
            Console.Error.WriteLine("openhw-helper: gpu fans: available again");
        }
    }

    public void Tick()
    {
        lock (_gate)
        {
            if (_disposed) return;
            var now = _now();
            foreach (var (id, h) in _held.ToList())
                if (now - h.Lease > LeaseTime)
                {
                    Console.Error.WriteLine($"openhw-helper: gpu fans: {id} lease expired; back to automatic");
                    Restore(id);
                }
        }
    }

    public void Dispose()
    {
        _watchdog?.Dispose();
        lock (_gate)
        {
            foreach (var id in _held.Keys.ToList()) Restore(id);
            _disposed = true;
        }
    }

    private void Restore(string id)
    {
        if (!_held.Remove(id)) return;
        try
        {
            var (card, fan) = Find(id);
            var code = _nvml.SetDefault(card.Uuid, fan);
            if (code != 0) Console.Error.WriteLine($"openhw-helper: gpu fans: back to automatic failed for {id} (NVML {code})");
        }
        catch (ArgumentException) { } // the card is gone (lent): nothing to restore
    }

    private void Renew(string token)
    {
        var now = _now();
        foreach (var h in _held.Values.Where(h => h.Owner == token)) h.Lease = now;
    }

    private (GpuFanCard Card, int Fan) Find(string id)
    {
        var m = FanIdPattern().Match(id);
        if (!m.Success) throw new ArgumentException("unknown GPU fan");
        var card = _nvml.Cards().FirstOrDefault(c => c.Uuid.Equals(m.Groups[1].Value, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException("no such GPU (or it's lent to a VM)");
        var fan = int.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
        if (fan >= card.Fans) throw new ArgumentException("no such fan on that GPU");
        return (card, fan);
    }

    private static void CheckToken(string token)
    {
        if (!TokenPattern().IsMatch(token)) throw new ArgumentException("invalid client token");
    }

    [GeneratedRegex(@"^[0-9a-f]{32}$", RegexOptions.CultureInvariant)] private static partial Regex TokenPattern();
    [GeneratedRegex(@"^nvml:(GPU-[0-9a-fA-F-]{36}):fan:([0-9])$", RegexOptions.CultureInvariant)] private static partial Regex FanIdPattern();
}

/// <summary>The live NVML side (root). NVML stays open; <see cref="Suspend"/> closes it while a card is lent.</summary>
public sealed class LiveNvmlFans : INvmlFans, IDisposable
{
    private readonly object _gate = new();
    private bool _initialised, _suspended;

    public void Suspend()
    {
        lock (_gate)
        {
            _suspended = true;
            if (_initialised) NvmlNative.nvmlShutdown();
            _initialised = false;
        }
    }

    public void Resume()
    {
        lock (_gate) _suspended = false;
    }

    public IReadOnlyList<GpuFanCard> Cards()
    {
        lock (_gate)
        {
            if (!Init()) return [];
            var cards = new List<GpuFanCard>();
            if (NvmlNative.nvmlDeviceGetCount_v2(out var count) != NvmlNative.Success) return cards;
            for (uint i = 0; i < count; i++)
            {
                if (NvmlNative.nvmlDeviceGetHandleByIndex_v2(i, out var h) != NvmlNative.Success) continue;
                if (NvmlNative.nvmlDeviceGetNumFans(h, out var fans) != NvmlNative.Success || fans == 0) continue;
                var (min, max) = NvmlNative.nvmlDeviceGetMinMaxFanSpeed(h, out var mn, out var mx) == NvmlNative.Success ? ((int)mn, (int)mx) : (30, 100);
                var pci = new NvmlNative.PciInfo();
                cards.Add(new GpuFanCard(NvmlNative.ReadString((b, n) => NvmlNative.nvmlDeviceGetUUID(h, b, n), 96),
                    NvmlNative.ReadString((b, n) => NvmlNative.nvmlDeviceGetName(h, b, n)),
                    NvmlNative.nvmlDeviceGetPciInfo(h, ref pci) == NvmlNative.Success ? pci.busId : "", (int)fans, min, max));
            }
            return cards;
        }
    }

    public int SetManual(string uuid, int fan, int percent) => With(uuid, h => NvmlNative.nvmlDeviceSetFanSpeed_v2(h, (uint)fan, (uint)percent));

    public int SetDefault(string uuid, int fan) => With(uuid, h => NvmlNative.nvmlDeviceSetDefaultFanSpeed_v2(h, (uint)fan));

    private int With(string uuid, Func<nint, int> action)
    {
        lock (_gate)
        {
            if (!Init() || NvmlNative.nvmlDeviceGetCount_v2(out var count) != NvmlNative.Success) return 999;
            for (uint i = 0; i < count; i++)
                if (NvmlNative.nvmlDeviceGetHandleByIndex_v2(i, out var h) == NvmlNative.Success &&
                    NvmlNative.ReadString((b, n) => NvmlNative.nvmlDeviceGetUUID(h, b, n), 96).Equals(uuid, StringComparison.OrdinalIgnoreCase))
                    return action(h);
            return 999;
        }
    }

    private bool Init()
    {
        if (_suspended) return false;
        if (_initialised) return true;
        _initialised = NvmlNative.nvmlInit_v2() == NvmlNative.Success;
        return _initialised;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_initialised) NvmlNative.nvmlShutdown();
            _initialised = false;
        }
    }
}
