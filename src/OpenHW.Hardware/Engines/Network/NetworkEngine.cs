using System.Diagnostics;
using System.Globalization;

namespace OpenHW.Hardware;

/// <summary>
/// HWiNFO's "Network:" rows — download/upload rate and totals per physical interface, from
/// /sys/class/net/*/statistics. Virtual interfaces (lo, docker0, bridges) have no device link and are skipped.
/// </summary>
public sealed class NetworkEngine(string root = "/sys/class/net") : ISensorEngine
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Dictionary<string, (double Rx, double Tx, TimeSpan At)> _previous = [];
    private IReadOnlyList<string> _interfaces = [];

    public string Name => "openhw-net";
    public bool Available => Directory.Exists(root);

    private static readonly string[] Keys = ["rx-rate", "tx-rate", "rx-total", "tx-total"];

    public static string GroupKey(string iface) => "net:" + iface;

    public IReadOnlyList<HardwareItem> Discover()
    {
        _interfaces = Directory.Exists(root)
            ? Directory.EnumerateDirectories(root).Where(d => Directory.Exists(Path.Combine(d, "device")))
                .Select(Path.GetFileName).OfType<string>().Order().ToList()
            : [];
        return _interfaces.SelectMany(iface => new[]
        {
            Item(iface, "rx-rate", "Current DL Rate", "KB/s"),
            Item(iface, "tx-rate", "Current UP Rate", "KB/s"),
            Item(iface, "rx-total", "Total DL", "MB"),
            Item(iface, "tx-total", "Total UP", "MB"),
        }).ToList();
    }

    public void ReadInto(IDictionary<string, double?> readings, IReadOnlySet<string>? requested)
    {
        var now = _clock.Elapsed;
        foreach (var iface in _interfaces)
        {
            if (requested is not null && !Keys.Any(k => requested.Contains(Id(iface, k))))
            {
                _previous.Remove(iface); // a resumed interface restarts its rate from a fresh baseline
                continue;
            }
            var rx = ReadBytes(iface, "rx_bytes");
            var tx = ReadBytes(iface, "tx_bytes");
            if (rx is null || tx is null) continue;
            readings[Id(iface, "rx-total")] = rx / 1e6;
            readings[Id(iface, "tx-total")] = tx / 1e6;
            if (_previous.TryGetValue(iface, out var last) && now > last.At)
            {
                var seconds = (now - last.At).TotalSeconds;
                readings[Id(iface, "rx-rate")] = Math.Max(0, (rx.Value - last.Rx) / 1e3 / seconds);
                readings[Id(iface, "tx-rate")] = Math.Max(0, (tx.Value - last.Tx) / 1e3 / seconds);
            }
            _previous[iface] = (rx.Value, tx.Value, now);
        }
    }

    private double? ReadBytes(string iface, string counter)
    {
        try
        {
            return double.Parse(File.ReadAllText(Path.Combine(root, iface, "statistics", counter)), CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is IOException or FormatException) { return null; }
    }

    private string Id(string iface, string key) => $"{Name}:{iface}:{key}";

    private HardwareItem Item(string iface, string key, string name, string unit) => new()
    {
        Id = Id(iface, key), Name = name, Kind = HardwareKind.Data, Unit = unit, Group = GroupKey(iface),
        Backend = Name, CanSet = false, Source = "/sys/class/net",
    };

    public void Dispose() { }
}
