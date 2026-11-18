using System.Globalization;
using System.Text.Json.Nodes;
using OpenHW.Hardware;

namespace OpenHW.App.Bios;

/// <summary>
/// The hardware facts the BIOS menus depend on (which per-core / per-CCD rows show, which AMD CBS variant applies),
/// and the text the firmware fills in at runtime. Starts from the shipped WRX90E profile and fills in what Linux can
/// tell about this machine: CPU name, cores, CCDs, base clock, boot entries, the X710 MAC addresses.
/// </summary>
public static class BiosPlatform
{
    public static JsonObject Probe(JsonObject template, IFirmwareVariables? machine, string sys = "/sys", string proc = "/proc")
    {
        var p = (JsonObject)template.DeepClone();
        var cpu = p["cpu"] as JsonObject ?? (JsonObject)(p["cpu"] = new JsonObject());
        var info = SafeRead(Path.Combine(proc, "cpuinfo"));
        var model = info.Split('\n').FirstOrDefault(l => l.StartsWith("model name", StringComparison.Ordinal))?.Split(':', 2)[1].Trim();
        if (model is { Length: > 0 }) cpu["brand"] = model;
        var cpuDir = Path.Combine(sys, "devices", "system", "cpu");
        var cores = new HashSet<string>();
        var ccds = new HashSet<string>();
        if (Directory.Exists(cpuDir))
            foreach (var dir in Directory.GetDirectories(cpuDir, "cpu*").Where(d => int.TryParse(Path.GetFileName(d)[3..], out _)))
            {
                var pkg = SafeRead(Path.Combine(dir, "topology", "physical_package_id")).Trim();
                var core = SafeRead(Path.Combine(dir, "topology", "core_id")).Trim();
                if (core.Length > 0) cores.Add(pkg + ":" + core);
                var l3 = SafeRead(Path.Combine(dir, "cache", "index3", "shared_cpu_list")).Trim();
                if (l3.Length > 0) ccds.Add(l3);
            }
        if (cores.Count > 0) cpu["cores"] = cores.Count;
        if (ccds.Count > 0) cpu["ccds"] = ccds.Count;
        // amd-pstate's nominal frequency is the base clock BIOS Setup shows as "CPU Speed".
        if (long.TryParse(SafeRead(Path.Combine(cpuDir, "cpu0", "cpufreq", "amd_pstate_nominal_freq")).Trim(), out var khz) && khz > 0)
            cpu["speed_mhz"] = khz / 1000;
        if (model is { } m && m.Contains("PRO 9", StringComparison.Ordinal)) p["variant"] = "SHP";
        else if (model is { } m7 && m7.Contains("PRO 7", StringComparison.Ordinal)) p["variant"] = "STP";
        if (machine?.BootEntries() is { Count: > 0 } boot) p["boot_options"] = new JsonArray(boot.Select(b => (JsonNode)JsonValue.Create(b)!).ToArray());
        var text = p["text"] as JsonObject ?? (JsonObject)(p["text"] = new JsonObject());
        var x710 = Macs(sys, "i40e");
        for (var i = 0; i < Math.Min(2, x710.Count); i++) text[$"Main|Intel X710 LAN{i + 1} MAC"] = x710[i];
        p["source"] = "probed";
        p["profile_name"] = $"This machine ({cpu["brand"]?.GetValue<string>() ?? "unknown CPU"})";
        return p;
    }

    /// <summary>
    /// <see cref="Probe"/>'s Windows counterpart: the same facts from the registry, the processor topology and the
    /// network adapters (Windows has no /proc or /sys); the boot entries come from the UEFI variables via the service.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static JsonObject ProbeWindows(JsonObject template, IFirmwareVariables? machine)
    {
        var p = (JsonObject)template.DeepClone();
        var cpu = p["cpu"] as JsonObject ?? (JsonObject)(p["cpu"] = new JsonObject());
        var model = OpenHW.Hardware.Windows.WindowsPlatformFacts.CpuBrand();
        if (model is { Length: > 0 }) cpu["brand"] = model;
        if (OpenHW.Hardware.Windows.WindowsPlatformFacts.Cores() is > 0 and var cores) cpu["cores"] = cores;
        if (OpenHW.Hardware.Windows.WindowsPlatformFacts.Ccds() is > 0 and var ccds) cpu["ccds"] = ccds;
        if (OpenHW.Hardware.Windows.WindowsPlatformFacts.BaseMhz() is { } mhz) cpu["speed_mhz"] = mhz;
        if (model is { } m && m.Contains("PRO 9", StringComparison.Ordinal)) p["variant"] = "SHP";
        else if (model is { } m7 && m7.Contains("PRO 7", StringComparison.Ordinal)) p["variant"] = "STP";
        if (machine?.BootEntries() is { Count: > 0 } boot) p["boot_options"] = new JsonArray(boot.Select(b => (JsonNode)JsonValue.Create(b)!).ToArray());
        var text = p["text"] as JsonObject ?? (JsonObject)(p["text"] = new JsonObject());
        var x710 = OpenHW.Hardware.Windows.WindowsPlatformFacts.Macs("X710");
        for (var i = 0; i < Math.Min(2, x710.Count); i++) text[$"Main|Intel X710 LAN{i + 1} MAC"] = x710[i];
        p["source"] = "probed";
        p["profile_name"] = $"This machine ({cpu["brand"]?.GetValue<string>() ?? "unknown CPU"})";
        return p;
    }

    /// <summary>MAC addresses of the network ports a driver runs, by interface name, in BIOS style (AA-BB-…).</summary>
    private static List<string> Macs(string sys, string driver)
    {
        var net = Path.Combine(sys, "class", "net");
        if (!Directory.Exists(net)) return [];
        return Directory.GetDirectories(net).Order(StringComparer.Ordinal)
            .Where(d => { try { return Path.GetFileName(new DirectoryInfo(Path.Combine(d, "device", "driver")).ResolveLinkTarget(true)?.FullName ?? "") == driver; } catch (IOException) { return false; } })
            .Select(d => SafeRead(Path.Combine(d, "address")).Trim().ToUpperInvariant().Replace(':', '-'))
            .Where(a => a.Length > 0).ToList();
    }

    private static string SafeRead(string path)
    {
        try { return File.ReadAllText(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return ""; }
    }

    public static string Describe(JsonObject p) => string.Create(CultureInfo.InvariantCulture,
        $"{p["cpu"]?["brand"]} · {p["cpu"]?["cores"]} cores, {p["cpu"]?["ccds"]} CCDs");
}
