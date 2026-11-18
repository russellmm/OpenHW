using System.Runtime.Versioning;

namespace OpenHW.Hardware.Windows;

/// <summary>
/// The CPU's HSMP readings on Windows, in the OpenHW Helper service (AMD's HSMP device is Administrator-only): the same
/// mailbox messages Linux's amd_hsmp driver turns into its sysfs files, so <see cref="WindowsHsmpEngine"/> shows
/// <see cref="HsmpEngine"/>'s rows (plus package power and the PPT limit, which Linux has from hwmon). Read-only
/// messages only (Linux's amd_hsmp.h numbering): 0x04 socket power, 0x06 power limit, 0x0B PROCHOT, 0x0F FCLK/MCLK,
/// 0x10 core clock throttle limit, 0x11 C0 residency, 0x14 DRAM bandwidth, 0x19 socket frequency limit and its source.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsHsmpSensors(Func<WindowsHsmp?> open) : IDisposable
{
    private readonly object _gate = new();
    private WindowsHsmp? _hsmp;
    private DateTime _retryAt;

    public string? Error { get; private set; }

    /// <summary>Key → value (the keys of <see cref="HsmpEngine"/>, plus "socket-power" W, "ppt-limit" W, "ddr-max" GB/s,
    /// "limit-sources" bitmask); null when the HSMP device can't be opened.</summary>
    public Dictionary<string, double>? Read()
    {
        lock (_gate)
        {
            if (_hsmp is null)
            {
                if (DateTime.UtcNow < _retryAt) return null;
                _hsmp = open();
                if (_hsmp is null) { _retryAt = DateTime.UtcNow.AddMinutes(1); Error ??= "AMD's HSMP device isn't available."; return null; }
            }
            var values = new Dictionary<string, double>(StringComparer.Ordinal);
            void Try(Action read)
            {
                try { read(); }
                catch (IOException ex) { Error = ex.Message; } // one message refused: the others still count
            }
            Try(() => values["socket-power"] = _hsmp.Send(0x04, [], 1)[0] / 1000.0);
            Try(() => values["ppt-limit"] = _hsmp.Send(0x06, [], 1)[0] / 1000.0);
            Try(() => values["prochot"] = _hsmp.Send(0x0B, [], 1)[0] & 1);
            Try(() =>
            {
                var clocks = _hsmp.Send(0x0F, [], 2);
                values["fclk"] = clocks[0];
                values["mclk"] = clocks[1];
            });
            Try(() => values["cclk-limit"] = _hsmp.Send(0x10, [], 1)[0]);
            Try(() => values["c0"] = _hsmp.Send(0x11, [], 1)[0]);
            Try(() =>
            {
                var bw = _hsmp.Send(0x14, [], 1)[0];
                values["ddr-max"] = bw >> 20;
                values["ddr-bw"] = (bw >> 8) & 0xFFF;
                values["ddr-bw-perc"] = bw & 0xFF;
            });
            Try(() =>
            {
                var limit = _hsmp.Send(0x19, [], 1)[0];
                values["active-limit"] = limit >> 16;
                values["limit-sources"] = limit & 0xFFFF;
            });
            if (values.Count > 0) Error = null;
            return values;
        }
    }

    public void Dispose()
    {
        lock (_gate) _hsmp?.Dispose();
    }
}

/// <summary>
/// The app's side of <see cref="WindowsHsmpSensors"/>: the service's <c>hsmpread</c> reply as <see cref="HsmpEngine"/>'s
/// rows (same ids, group "cpu-enhanced"), plus CPU Package Power and the PPT limit.
/// </summary>
public sealed class WindowsHsmpEngine(Func<string>? request = null) : ISensorEngine
{
    private static readonly (string Key, string Name, HardwareKind Kind, string Unit)[] Rows =
    [
        ("socket-power", "CPU Package Power", HardwareKind.Power, "W"),
        ("ppt-limit", "CPU PPT Limit", HardwareKind.Power, "W"),
        ("fclk", "Infinity Fabric Clock (FCLK)", HardwareKind.Frequency, "MHz"),
        ("mclk", "Memory Clock (MCLK)", HardwareKind.Frequency, "MHz"),
        ("cclk-limit", "Core Clock Limit", HardwareKind.Frequency, "MHz"),
        ("active-limit", "Active Frequency Limit", HardwareKind.Frequency, "MHz"),
        ("c0", "Socket C0 Residency", HardwareKind.Load, "%"),
        ("ddr-bw", "DRAM Bandwidth", HardwareKind.Data, "GB/s"),
        ("ddr-bw-perc", "DRAM Bandwidth Utilization", HardwareKind.Load, "%"),
        ("prochot", "Thermal Throttling (PROCHOT)", HardwareKind.Status, "flag"),
    ];

    private readonly Func<string> _request = request ?? (() => HelperClient.Request("hsmpread", receiveTimeoutMs: 2500, maxReplyBytes: 4096));
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private Dictionary<string, double>? _last;
    private TimeSpan _lastAt = TimeSpan.FromDays(-1), _failedAt = TimeSpan.FromDays(-1);

    public string Name => "hsmp";
    public bool Available => _last is { Count: > 0 };

    public IReadOnlyList<HardwareItem> Discover()
    {
        if (Fetch() is not { } values) return [];
        var items = Rows.Where(r => values.ContainsKey(r.Key))
            .Select(r => Item(r.Key, r.Name, r.Kind, r.Unit, r.Key == "ddr-bw" && values.TryGetValue("ddr-max", out var max) ? $"HSMP (AMD driver) · peak {max:0} GB/s" : "HSMP (AMD driver)"))
            .ToList();
        if (values.ContainsKey("limit-sources"))
            items.AddRange(HsmpEngine.LimitSources.Select(s => Item("limit:" + s.ToLowerInvariant().Replace(' ', '-'), $"Frequency Limited by {s}", HardwareKind.Status, "flag", $"HSMP · frequency limit source \"{s}\"")));
        return items;
    }

    private static HardwareItem Item(string key, string name, HardwareKind kind, string unit, string details) => new()
    {
        Id = "hsmp:" + key, Name = name, Kind = kind, Unit = unit, Group = HsmpEngine.Group, Backend = "openhw",
        Source = "HSMP", CanSet = false, Details = details,
    };

    public void ReadInto(IDictionary<string, double?> readings, IReadOnlySet<string>? requested)
    {
        if (requested is not null && !requested.Any(id => id.StartsWith("hsmp:", StringComparison.Ordinal))) return;
        if (Fetch() is not { } values) return;
        foreach (var (key, _, _, _) in Rows) readings["hsmp:" + key] = values.TryGetValue(key, out var v) ? v : null;
        if (values.TryGetValue("limit-sources", out var mask))
            for (var i = 0; i < HsmpEngine.LimitSources.Length; i++)
                readings["hsmp:limit:" + HsmpEngine.LimitSources[i].ToLowerInvariant().Replace(' ', '-')] = ((long)mask >> i & 1) != 0 ? 1 : 0;
    }

    private Dictionary<string, double>? Fetch()
    {
        if (_last is not null && _clock.Elapsed - _lastAt < TimeSpan.FromMilliseconds(900)) return _last;
        if (_last is null && _clock.Elapsed - _failedAt < TimeSpan.FromSeconds(30)) return null;
        try
        {
            _last = GpuWire.Decode<Dictionary<string, double>>(_request());
            _lastAt = _clock.Elapsed;
        }
        catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or System.Text.Json.JsonException or FormatException)
        {
            if (_last is null || _clock.Elapsed - _lastAt > TimeSpan.FromSeconds(30)) { _last = null; _failedAt = _clock.Elapsed; }
        }
        return _last;
    }

    public void Dispose() { }
}
