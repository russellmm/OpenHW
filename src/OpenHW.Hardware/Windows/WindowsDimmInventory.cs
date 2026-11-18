using System.Globalization;

namespace OpenHW.Hardware.Windows;

/// <summary>
/// Windows (2026-11-18): the BMC's DIMM inventory (Redfish Systems/Self/Memory, through the service's <c>dimms</c>) as
/// a "DDR5 DIMMs" group — titled with the modules' part, size and speed — with DIMMs Installed, DIMMs Healthy (an
/// alert can watch it), Total Memory and Memory Speed. Each module's details (the BMC's channel/slot, size, ranks,
/// speed, health) are in the rows' tooltips: the BMC's channel/slot numbering isn't mapped to the board's DIMMA1–H1
/// (identical modules can't be told apart), so no row claims a slot. No serial numbers.
/// </summary>
public sealed class WindowsDimmInventory(Func<string>? request = null) : ISensorEngine
{
    public const string Group = "dimm-inventory";
    private const string Prefix = "openhw:dimms:";
    private static readonly TimeSpan Every = TimeSpan.FromSeconds(30);

    private readonly Func<string> _request = request ?? (() => HelperClient.Request("dimms", receiveTimeoutMs: 30000, maxReplyBytes: GpuWire.MaxReplyBytes));
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private List<DimmInventory>? _last;
    private TimeSpan _at = TimeSpan.FromDays(-1);

    public string Name => "dimm-inventory";
    public bool Available => _last is { Count: > 0 };

    public IReadOnlyList<HardwareItem> Discover()
    {
        if (Fetch() is not { Count: > 0 } dimms) return [];
        var (title, details) = (Title(dimms), Details(dimms));
        HardwareItem Row(string key, string name, HardwareKind kind, string unit) => new()
        {
            Id = Prefix + key, Name = name, Kind = kind, Unit = unit, Group = Group, Backend = "openhw", CanSet = false,
            Source = title, Details = details,
        };
        return
        [
            Row("installed", "DIMMs Installed", HardwareKind.Counter, ""),
            Row("healthy", "DIMMs Healthy", HardwareKind.Counter, ""),
            Row("total", "Total Memory", HardwareKind.Data, "GiB"),
            Row("speed", "Memory Speed", HardwareKind.Frequency, "MT/s"),
        ];
    }

    public void ReadInto(IDictionary<string, double?> readings, IReadOnlySet<string>? requested)
    {
        if (requested is not null && !requested.Any(id => id.StartsWith(Prefix, StringComparison.Ordinal))) return;
        if (Fetch() is not { Count: > 0 } dimms) return;
        readings[Prefix + "installed"] = dimms.Count;
        readings[Prefix + "healthy"] = dimms.Count(Healthy);
        readings[Prefix + "total"] = dimms.Sum(d => d.CapacityMiB ?? 0) / 1024.0;
        readings[Prefix + "speed"] = dimms.Max(d => d.SpeedMts);
    }

    private static bool Healthy(DimmInventory d) => d.Health == "OK" && d.State is null or "Enabled";

    /// <summary>"DDR5 DIMMs: 8 × Kingston KF556R28-32 (32 GiB, 5,200 MT/s)" — or a count when the modules differ.</summary>
    public static string Title(IReadOnlyList<DimmInventory> dimms)
    {
        var type = dimms.Select(d => d.Type).FirstOrDefault(t => t is { Length: > 0 }) ?? "Memory";
        var kinds = dimms.Select(d => (d.Manufacturer, d.PartNumber, d.CapacityMiB)).Distinct().ToList();
        var speed = dimms.Max(d => d.SpeedMts) is { } mts ? mts.ToString("N0", CultureInfo.InvariantCulture) + " MT/s" : null;
        if (kinds.Count != 1) return $"{type} DIMMs: {dimms.Count} modules" + (speed is null ? "" : $" ({speed})");
        var (mfr, part, mib) = kinds[0];
        var size = mib is { } m ? $"{m / 1024} GiB" : null;
        var detail = string.Join(", ", new[] { size, speed }.OfType<string>());
        return $"{type} DIMMs: {dimms.Count} × {mfr} {part}".TrimEnd() + (detail.Length > 0 ? $" ({detail})" : "");
    }

    /// <summary>One line per module for the tooltip, in the BMC's own numbering.</summary>
    public static string Details(IReadOnlyList<DimmInventory> dimms) =>
        "From the BMC (Redfish Systems/Self/Memory), in its numbering: " + string.Join("; ", dimms.Select(d =>
            $"channel {d.Channel?.ToString(CultureInfo.InvariantCulture) ?? "?"} slot {d.Slot?.ToString(CultureInfo.InvariantCulture) ?? "?"}: " +
            $"{d.CapacityMiB / 1024} GiB {d.Ranks}R {d.SpeedMts} MT/s, {d.Health ?? "?"}"));

    private List<DimmInventory>? Fetch()
    {
        if (_clock.Elapsed - _at < Every) return _last;
        _at = _clock.Elapsed;
        try { _last = GpuWire.Decode<List<DimmInventory>>(_request()) ?? []; }
        catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or System.Text.Json.JsonException or FormatException)
        {
            // Keep the last inventory: the modules don't change while the BMC is briefly unreachable.
        }
        return _last;
    }

    public void Dispose() { }
}
