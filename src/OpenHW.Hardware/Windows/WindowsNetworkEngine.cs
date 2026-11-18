using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace OpenHW.Hardware.Windows;

/// <summary>
/// The Windows twin of <see cref="NetworkEngine"/>: download/upload rates and totals per physical adapter from the IP
/// Helper interface table (GetIfTable2, 64-bit octet counters). Only hardware interfaces with a connector are shown —
/// not the filter drivers, virtual switches, tunnels or loopback Windows also lists. Same row ids and groups as on
/// Linux, keyed by the adapter's name ("Ethernet 2").
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsNetworkEngine : ISensorEngine
{
    private const string EngineName = "openhw-net";
    private const byte HardwareInterface = 1, FilterInterface = 2, ConnectorPresent = 4;
    private const uint IfOperStatusNotPresent = 6;
    private static readonly string[] Keys = ["rx-rate", "tx-rate", "rx-total", "tx-total"];

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Dictionary<string, (double Rx, double Tx, TimeSpan At)> _previous = [];
    private IReadOnlyList<string> _interfaces = [];

    public string Name => EngineName;
    public bool Available => true;

    public IReadOnlyList<HardwareItem> Discover()
    {
        _interfaces = Read().Keys.Order(StringComparer.OrdinalIgnoreCase).ToList();
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
        var counters = Read();
        foreach (var iface in _interfaces)
        {
            if (requested is not null && !Keys.Any(k => requested.Contains(Id(iface, k)))) { _previous.Remove(iface); continue; }
            if (!counters.TryGetValue(iface, out var c)) continue;
            readings[Id(iface, "rx-total")] = c.Rx / 1e6;
            readings[Id(iface, "tx-total")] = c.Tx / 1e6;
            if (_previous.TryGetValue(iface, out var last) && now > last.At)
            {
                var seconds = (now - last.At).TotalSeconds;
                readings[Id(iface, "rx-rate")] = Math.Max(0, (c.Rx - last.Rx) / 1e3 / seconds);
                readings[Id(iface, "tx-rate")] = Math.Max(0, (c.Tx - last.Tx) / 1e3 / seconds);
            }
            _previous[iface] = (c.Rx, c.Tx, now);
        }
    }

    /// <summary>Physical adapters by name → bytes received and sent.</summary>
    private static unsafe Dictionary<string, (double Rx, double Tx)> Read()
    {
        var result = new Dictionary<string, (double, double)>(StringComparer.OrdinalIgnoreCase);
        if (Win32.GetIfTable2(out var table) != 0) return result;
        try
        {
            // MIB_IF_TABLE2: NumEntries, padding, then the rows (8-byte aligned).
            var count = *(uint*)table;
            var rows = (Win32.MibIfRow2*)(table + 8);
            for (var i = 0; i < count; i++)
            {
                var row = rows + i;
                var flags = row->InterfaceAndOperStatusFlags;
                if ((flags & HardwareInterface) == 0 || (flags & FilterInterface) != 0 || (flags & ConnectorPresent) == 0) continue;
                if (row->OperStatus == IfOperStatusNotPresent) continue; // an adapter Windows remembers but isn't installed now

                var alias = new string(row->Alias).TrimEnd('\0');
                if (alias.Length == 0) continue;
                result[alias] = (row->InOctets, row->OutOctets);
            }
        }
        finally { Win32.FreeMibTable(table); }
        return result;
    }

    internal static int RowSize => Marshal.SizeOf<Win32.MibIfRow2>();

    private static string Id(string iface, string key) => $"{EngineName}:{iface}:{key}";

    private static HardwareItem Item(string iface, string key, string name, string unit) => new()
    {
        Id = Id(iface, key), Name = name, Kind = HardwareKind.Data, Unit = unit, Group = NetworkEngine.GroupKey(iface),
        Backend = EngineName, CanSet = false, Source = "Windows interface table",
    };

    public void Dispose() { }
}
