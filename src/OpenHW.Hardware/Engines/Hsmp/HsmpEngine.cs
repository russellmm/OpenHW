using System.Globalization;

namespace OpenHW.Hardware;

/// <summary>
/// AMD HSMP readings the kernel's amd_hsmp driver exposes without root, on the HSMP platform device (the parent of
/// the <c>amd_hsmp_hwmon</c> hwmon node): fabric and memory clocks, frequency limits and what is imposing them,
/// socket C0 residency, DRAM bandwidth and PROCHOT. Each read is an HSMP mailbox message to the SMU, so rows that
/// aren't on screen aren't read. Package power and the PPT limit stay with the hwmon engine.
/// </summary>
public sealed class HsmpEngine(string hwmonRoot = "/sys/class/hwmon") : ISensorEngine
{
    public const string Group = "cpu-enhanced";

    /// <summary>The driver's frequency-limit source names, in its order; the source attribute lists the active ones.</summary>
    public static readonly string[] LimitSources =
        ["cHTC-Active", "PROCHOT", "TDC limit", "PPT Limit", "OPN Max", "Reliability Limit", "APML Agent", "HSMP Agent"];

    private static readonly (string Key, string File, string Name, HardwareKind Kind, string Unit)[] Values =
    [
        ("fclk", "fclk_input", "Infinity Fabric Clock (FCLK)", HardwareKind.Frequency, "MHz"),
        ("mclk", "mclk_input", "Memory Clock (MCLK)", HardwareKind.Frequency, "MHz"),
        ("cclk-limit", "cclk_freq_limit_input", "Core Clock Limit", HardwareKind.Frequency, "MHz"),
        ("active-limit", "pwr_current_active_freq_limit", "Active Frequency Limit", HardwareKind.Frequency, "MHz"),
        ("c0", "c0_residency_input", "Socket C0 Residency", HardwareKind.Load, "%"),
        ("ddr-bw", "ddr_utilised_bw_input", "DRAM Bandwidth", HardwareKind.Data, "GB/s"),
        ("ddr-bw-perc", "ddr_utilised_bw_perc_input", "DRAM Bandwidth Utilization", HardwareKind.Load, "%"),
        ("prochot", "prochot_status", "Thermal Throttling (PROCHOT)", HardwareKind.Status, "flag"),
    ];

    private const string SourceFile = "pwr_current_active_freq_limit_source";
    private string? _device;
    private readonly List<(HardwareItem Item, string? File, string? Source)> _items = [];

    public string Name => "hsmp";
    public bool Available => FindDevice() is not null;

    private string? FindDevice()
    {
        if (!Directory.Exists(hwmonRoot)) return null;
        foreach (var dir in Directory.EnumerateDirectories(hwmonRoot).Order(StringComparer.Ordinal))
        {
            if (SysFs.ReadText(Path.Combine(dir, "name")) != "amd_hsmp_hwmon") continue;
            var device = Path.Combine(dir, "device");
            if (File.Exists(Path.Combine(device, "fclk_input")) || File.Exists(Path.Combine(device, SourceFile))) return device;
        }
        return null;
    }

    public IReadOnlyList<HardwareItem> Discover()
    {
        _items.Clear();
        _device = FindDevice();
        if (_device is null) return [];
        var maxBandwidth = SysFs.ReadText(Path.Combine(_device, "ddr_max_bw"));
        foreach (var (key, file, name, kind, unit) in Values)
        {
            if (!File.Exists(Path.Combine(_device, file))) continue;
            var details = key == "ddr-bw" && maxBandwidth.Length > 0 ? $"HSMP · {file} · peak {maxBandwidth} GB/s" : $"HSMP · {file}";
            _items.Add((Item(key, name, kind, unit, details), file, null));
        }
        if (File.Exists(Path.Combine(_device, SourceFile)))
            foreach (var source in LimitSources)
                _items.Add((Item("limit:" + Slug(source), $"Frequency Limited by {source}", HardwareKind.Status, "flag",
                    $"HSMP · {SourceFile} lists \"{source}\""), null, source));
        return _items.Select(i => i.Item).ToArray();
    }

    private HardwareItem Item(string key, string name, HardwareKind kind, string unit, string details) => new()
    {
        Id = "hsmp:" + key, Name = name, Kind = kind, Unit = unit, Group = Group, Backend = "openhw",
        Source = "HSMP", CanSet = false, Details = details,
    };

    public void ReadInto(IDictionary<string, double?> readings, IReadOnlySet<string>? requested)
    {
        if (_device is null) return;
        string? sources = null; // one read serves all eight limit rows
        foreach (var (item, file, source) in _items)
        {
            if (requested is not null && !requested.Contains(item.Id)) continue;
            if (file is not null)
                readings[item.Id] = double.TryParse(SysFs.ReadText(Path.Combine(_device, file)), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
            else
            {
                sources ??= SysFs.ReadText(Path.Combine(_device, SourceFile));
                readings[item.Id] = sources.Length == 0 ? null : ActiveSources(sources).Contains(source!) ? 1 : 0;
            }
        }
    }

    /// <summary>
    /// The driver prints each active source name followed by a space, and names contain spaces ("OPN Max"), so
    /// match known names rather than splitting.
    /// </summary>
    public static IReadOnlySet<string> ActiveSources(string text) =>
        LimitSources.Where(s => (" " + text.Trim() + " ").Contains(" " + s + " ", StringComparison.Ordinal)).ToHashSet();

    private static string Slug(string name) => name.ToLowerInvariant().Replace(' ', '-');

    public void Dispose() { }
}
