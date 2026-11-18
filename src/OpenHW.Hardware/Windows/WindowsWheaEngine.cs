using System.Diagnostics.Eventing.Reader;
using System.Runtime.Versioning;

namespace OpenHW.Hardware.Windows;

/// <summary>
/// Hardware error counts on Windows — the counterpart of Linux's EDAC rows: Windows' hardware error architecture
/// (WHEA) logs each machine check, PCIe and memory error in the System log (provider Microsoft-Windows-WHEA-Logger),
/// which any user may read, so no driver or service is needed. Corrected errors: events 17, 18, 19 and 47; fatal ones:
/// event 1, written on the boot after the crash (one of 2026-11-18 10:15 is there). Counted at most once a minute.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsWheaEngine : ISensorEngine
{
    public const string Group = "whea";
    private const string Provider = "Microsoft-Windows-WHEA-Logger";
    private static readonly TimeSpan Every = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan Window = TimeSpan.FromDays(30);

    private static readonly (string Key, string Name, string Ids, bool SinceBoot)[] Rows =
    [
        ("corrected-boot", "Corrected Hardware Errors (since boot)", "EventID=17 or EventID=18 or EventID=19 or EventID=47", true),
        ("corrected-30d", "Corrected Hardware Errors (30 days)", "EventID=17 or EventID=18 or EventID=19 or EventID=47", false),
        ("fatal-30d", "Fatal Hardware Errors (30 days)", "EventID=1", false),
    ];

    private Dictionary<string, double>? _counts;
    private DateTime _at = DateTime.MinValue;

    public string Name => "whea";
    public bool Available => _counts is not null;

    public IReadOnlyList<HardwareItem> Discover()
    {
        if (Counts() is null) return [];
        return Rows.Select(r => new HardwareItem
        {
            Id = "whea:" + r.Key, Name = r.Name, Kind = HardwareKind.Counter, Unit = "", Group = Group, Backend = "openhw",
            CanSet = false, Source = "Windows System log (WHEA-Logger)",
            Details = $"WHEA-Logger events {r.Ids.Replace("EventID=", "").Replace(" or ", ", ")} " + (r.SinceBoot ? "since this boot" : "in the last 30 days"),
        }).ToList();
    }

    public void ReadInto(IDictionary<string, double?> readings, IReadOnlySet<string>? requested)
    {
        if (requested is not null && !requested.Any(id => id.StartsWith("whea:", StringComparison.Ordinal))) return;
        if (Counts() is not { } counts) return;
        foreach (var r in Rows) readings["whea:" + r.Key] = counts.TryGetValue(r.Key, out var c) ? c : null;
    }

    private Dictionary<string, double>? Counts()
    {
        if (_counts is not null && DateTime.UtcNow - _at < Every) return _counts;
        _at = DateTime.UtcNow;
        try
        {
            var boot = DateTime.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64);
            var counts = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var (key, _, ids, sinceBoot) in Rows)
            {
                var since = (sinceBoot ? boot : DateTime.UtcNow - Window).ToString("yyyy-MM-ddTHH:mm:ss.fffZ", System.Globalization.CultureInfo.InvariantCulture);
                var xpath = $"*[System[Provider[@Name='{Provider}'] and ({ids}) and TimeCreated[@SystemTime>='{since}']]]";
                using var reader = new EventLogReader(new EventLogQuery("System", PathType.LogName, xpath));
                var n = 0;
                for (var e = reader.ReadEvent(); e is not null; e = reader.ReadEvent()) { e.Dispose(); n++; }
                counts[key] = n;
            }
            _counts = counts;
        }
        catch (Exception ex) when (ex is EventLogException or UnauthorizedAccessException) { _counts = null; }
        return _counts;
    }

    public void Dispose() { }
}
