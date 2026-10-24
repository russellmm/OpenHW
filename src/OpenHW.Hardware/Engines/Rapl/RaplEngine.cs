using System.Globalization;

namespace OpenHW.Hardware;

/// <summary>A RAPL energy counter: its powercap name ("package-0", "core"), microjoules, and where it wraps to zero.</summary>
public sealed record RaplCounter(string Domain, ulong EnergyUJ, ulong RangeUJ);

/// <summary>
/// Root side: the RAPL energy counters under /sys/class/powercap (intel-rapl, which also drives AMD's RAPL). Since the
/// PLATYPUS side channel, energy_uj is root-only (0400), so openhw-helper reads it. Only the package and core domains.
/// </summary>
public sealed class RaplReader(string powercapRoot = "/sys/class/powercap")
{
    private static readonly string[] Domains = ["package-0", "core"];

    public IReadOnlyList<RaplCounter> Read()
    {
        var counters = new List<RaplCounter>();
        if (!Directory.Exists(powercapRoot)) return counters;
        foreach (var dir in Directory.EnumerateDirectories(powercapRoot, "intel-rapl:*").Order(StringComparer.Ordinal))
        {
            var name = SysFs.ReadText(Path.Combine(dir, "name"));
            if (!Domains.Contains(name) || counters.Any(c => c.Domain == name)) continue;
            if (!ulong.TryParse(SysFs.ReadText(Path.Combine(dir, "energy_uj")), NumberStyles.None, CultureInfo.InvariantCulture, out var energy)) continue;
            ulong.TryParse(SysFs.ReadText(Path.Combine(dir, "max_energy_range_uj")), NumberStyles.None, CultureInfo.InvariantCulture, out var range);
            counters.Add(new RaplCounter(name, energy, range));
        }
        return counters;
    }

    /// <summary>The helper's reply: "package-0 123 456;core 78 456".</summary>
    public static string Format(IReadOnlyList<RaplCounter> counters) =>
        string.Join(';', counters.Select(c => $"{c.Domain} {c.EnergyUJ} {c.RangeUJ}"));

    public static IReadOnlyList<RaplCounter> Parse(string reply) =>
        reply.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split(' '))
            .Where(f => f.Length == 3 && ulong.TryParse(f[1], out _) && ulong.TryParse(f[2], out _))
            .Select(f => new RaplCounter(f[0], ulong.Parse(f[1], CultureInfo.InvariantCulture), ulong.Parse(f[2], CultureInfo.InvariantCulture)))
            .ToList();
}

/// <summary>
/// CPU package and core power from RAPL energy, through openhw-helper: watts = energy used since the last poll ÷ the
/// time between them, allowing for the counter wrapping at its range. Without the helper (or an older one) there are no
/// rows; the engine asks again at most once a minute.
/// </summary>
public sealed class RaplEngine(Func<string>? request = null) : ISensorEngine
{
    private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromMinutes(1);
    private static readonly (string Domain, string Key, string Name)[] Rows =
    [
        ("package-0", "package", "CPU Package Power (RAPL)"),
        ("core", "cores", "CPU Cores Power (RAPL)"),
    ];

    private readonly Func<string> _request = request ?? (() => HelperClient.Request("rapl"));
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private readonly Dictionary<string, (ulong Energy, TimeSpan At)> _last = [];
    private IReadOnlyList<string> _domains = [];
    private TimeSpan? _failedAt;

    public string Name => "rapl";
    public bool Available => _domains.Count > 0;

    public IReadOnlyList<HardwareItem> Discover()
    {
        if (_domains.Count == 0 && (_failedAt is null || _clock.Elapsed - _failedAt > RetryAfterFailure))
        {
            var counters = Request();
            _domains = counters?.Select(c => c.Domain).ToList() ?? [];
            if (counters is null) _failedAt = _clock.Elapsed;
        }
        return Rows.Where(r => _domains.Contains(r.Domain)).Select(r => new HardwareItem
        {
            Id = "rapl:" + r.Key, Name = r.Name, Kind = HardwareKind.Power, Unit = "W", Group = HsmpEngine.Group,
            Backend = "openhw", Source = "RAPL (openhw-helper)", CanSet = false,
            Details = $"powercap intel-rapl {r.Domain} energy_uj, read as root by openhw-helper",
        }).ToList();
    }

    public void ReadInto(IDictionary<string, double?> readings, IReadOnlySet<string>? requested)
    {
        if (_domains.Count == 0) return;
        var rows = Rows.Where(r => _domains.Contains(r.Domain) && (requested is null || requested.Contains("rapl:" + r.Key))).ToList();
        if (rows.Count == 0) return;
        var counters = Request();
        var now = _clock.Elapsed;
        foreach (var (domain, key, _) in rows)
        {
            var counter = counters?.FirstOrDefault(c => c.Domain == domain);
            if (counter is null) { readings["rapl:" + key] = null; continue; }
            double? watts = null;
            if (_last.TryGetValue(domain, out var last) && now > last.At)
            {
                var used = counter.EnergyUJ >= last.Energy ? counter.EnergyUJ - last.Energy : counter.RangeUJ - last.Energy + counter.EnergyUJ;
                watts = used / 1e6 / (now - last.At).TotalSeconds;
            }
            _last[domain] = (counter.EnergyUJ, now);
            readings["rapl:" + key] = watts; // the first poll has no interval yet
        }
    }

    private IReadOnlyList<RaplCounter>? Request()
    {
        try { return RaplReader.Parse(_request()); }
        catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException) { return null; }
    }

    public void Dispose() { }
}
