using System.Globalization;
using System.Text.RegularExpressions;
using OpenHW.Hardware;

namespace OpenHW.App.Sensors;

/// <summary>One sensor row as shown; <see cref="Children"/> nest under it (e.g. Core Clocks → Core 0 Clock…).</summary>
public sealed record SensorView(string Id, string Name, HardwareKind Kind, string Unit, IReadOnlyList<SensorView> Children);

/// <summary>One device heading, HWiNFO-style ("CPU [#0]: …", "GPU [#1]: …", "Drive: …").</summary>
public sealed record SensorGroupView(string Key, string Title, IReadOnlyList<SensorView> Sensors);

/// <summary>
/// Turns the engines' flat inventory into HWiNFO's layout: one group per physical device, rows ordered
/// temperatures → voltages → fans → … with HWiNFO-like names, and values formatted the way HWiNFO does.
/// Pure presentation — no I/O beyond the machine identity strings passed in.
/// </summary>
public static partial class SensorPresentation
{
    public sealed record MachineInfo(string BoardVendor, string BoardName, string CpuModel, IReadOnlyList<NvmeDevice> Drives);

    private static readonly HardwareKind[] KindOrder =
    [
        HardwareKind.Temperature, HardwareKind.Voltage, HardwareKind.Tach, HardwareKind.Current, HardwareKind.Power,
        HardwareKind.Energy, HardwareKind.Frequency, HardwareKind.Load, HardwareKind.Control, HardwareKind.Data,
        HardwareKind.Counter, HardwareKind.Humidity, HardwareKind.Status,
    ];

    /// <summary>Configured limits, alarm flags and writable fan controls are settings, not readings.</summary>
    public static bool IsShown(HardwareItem item) => item.Kind switch
    {
        HardwareKind.TemperatureLimit => false,
        // GPU performance limiters and HSMP frequency-limit sources/PROCHOT; hwmon alarm bits are noise
        HardwareKind.Status => item.Backend == "nvml" || item.Id.StartsWith("hsmp:", StringComparison.Ordinal),
        HardwareKind.Control => item.Backend == "nvml", // GPU fan % is a reading on NVIDIA; hwmon PWM is a setting
        _ => true,
    };

    public static IReadOnlyList<SensorGroupView> Build(IReadOnlyList<HardwareItem> inventory,
        IReadOnlyDictionary<string, IReadOnlyList<string>> children, MachineInfo machine)
    {
        var shown = inventory.Where(IsShown).ToList();
        var childIds = children.Values.SelectMany(ids => ids).ToHashSet(StringComparer.Ordinal);
        var byId = shown.ToDictionary(i => i.Id, StringComparer.Ordinal);
        var gpuOrder = shown.Where(i => i.Backend == "nvml").Select(i => i.Group).Distinct()
            .OrderBy(PciSortKey, StringComparer.OrdinalIgnoreCase).ToList();

        SensorView View(HardwareItem item) => new(item.Id, DisplayName(item), item.Kind, DisplayUnit(item),
            children.TryGetValue(item.Id, out var ids)
                ? ids.Where(byId.ContainsKey).Select(id => View(byId[id])).ToList()
                : []);

        return shown.Where(i => !childIds.Contains(i.Id))
            .GroupBy(i => GroupKey(i, machine))
            .Select(g =>
            {
                var (order, title) = GroupTitle(g.Key, g.First(), machine, gpuOrder);
                // openhw rows keep their own (HWiNFO) order; hardware rows sort by kind, then naturally by name.
                var rows = g.First().Backend == "openhw"
                    ? g.Select(View)
                    : g.OrderBy(i => Array.IndexOf(KindOrder, i.Kind)).ThenBy(i => SortKey(i), StringComparer.OrdinalIgnoreCase).Select(View);
                return (order, title, group: new SensorGroupView(g.Key, title, rows.ToList()));
            })
            .OrderBy(g => g.order).ThenBy(g => g.title, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.group)
            .ToList();
    }

    private static string Chip(HardwareItem item) => item.Id.Split(':').ElementAtOrDefault(1) ?? "";

    private static string GroupKey(HardwareItem item, MachineInfo machine)
    {
        switch (item.Backend)
        {
            case "openhw" or "openhw-net": return item.Group;
            case "nvml": return "gpu:" + item.Group;
            case "edac": return "edac";
            case "nvme-health" or "nvme-activity": return item.Group;
        }
        return Chip(item) switch
        {
            "k10temp" or "amd_hsmp_hwmon" => "cpu-enhanced",
            "spd5118" or "jc42" => "dimms",
            var chip when IsNetwork(chip) => NetworkEngine.GroupKey(chip), // hwmon PHY temps join the rates
            // A drive's hwmon temperature joins its SMART/activity rows under one "Drive:" heading.
            "nvme" => machine.Drives.FirstOrDefault(d => d.HwmonPath is { } p &&
                          Path.GetFileName(p) == "hwmon" + item.Id.Split(':')[2])?.Group ?? item.Group,
            _ => item.Group,
        };
    }

    private static (int Order, string Title) GroupTitle(string key, HardwareItem sample, MachineInfo machine, IReadOnlyList<string> gpus)
    {
        var vendor = ShortVendor(machine.BoardVendor);
        if (key == SystemEngine.SystemGroup) return (0, $"System: {vendor}");
        if (key == SystemEngine.CpuGroup) return (1, $"CPU [#0]: {machine.CpuModel}");
        if (key == SystemEngine.CStateGroup) return (2, $"CPU [#0]: {machine.CpuModel}: C-State Residency");
        if (key == "cpu-enhanced") return (2, $"CPU [#0]: {machine.CpuModel}: Enhanced");
        if (key == "dimms") return (3, "DDR5 DIMMs: SPD5118 Hubs");
        if (key == "edac") return (4, "Memory Controller: EDAC");
        if (key.StartsWith("gpu:", StringComparison.Ordinal))
        {
            var index = gpus.ToList().IndexOf(sample.Group);
            return (10 + index, $"GPU [#{index}]: NVIDIA {sample.Group}");
        }
        if (key.StartsWith("nvme:pci:", StringComparison.Ordinal))
        {
            var drive = machine.Drives.FirstOrDefault(d => d.Group == key);
            var pci = GpuFormat.CompactPciBus(drive?.Pci ?? Uri.UnescapeDataString(key[9..]));
            return (100, drive is null ? $"Drive: {pci}" : $"Drive: {drive.Model} ({pci})");
        }
        if (key.StartsWith("net:", StringComparison.Ordinal)) return (200, $"Network: {key[4..]}");
        var chip = Chip(sample);
        if (chip.StartsWith("nct", StringComparison.OrdinalIgnoreCase) || chip.StartsWith("it8", StringComparison.OrdinalIgnoreCase))
            return (5, $"{vendor} {machine.BoardName} ({BoardProfiles.Known.FirstOrDefault(p => p.Matches(machine.BoardVendor, machine.BoardName, chip))?.ChipName ?? SuperIoName(chip)})");
        if (chip == "asusec") return (6, $"ASUS EC: {vendor}");
        return (300, sample.Group);
    }

    private static bool IsNetwork(string chip) =>
        chip.StartsWith("en", StringComparison.Ordinal) || chip.StartsWith("eth", StringComparison.Ordinal) || chip.StartsWith("wl", StringComparison.Ordinal);

    private static string SuperIoName(string chip) => chip.StartsWith("nct", StringComparison.OrdinalIgnoreCase)
        ? "Nuvoton " + chip.ToUpperInvariant()
        : "ITE " + chip.ToUpperInvariant();

    private static string ShortVendor(string vendor) =>
        vendor.StartsWith("ASUSTeK", StringComparison.OrdinalIgnoreCase) ? "ASUS"
        : vendor.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? vendor;

    /// <summary>The name shown everywhere: the user's own (sensor menu → Rename) or OpenHW's.</summary>
    public static string DisplayName(HardwareItem item) => SensorCustomizations.Current.NameOf(item.Id) ?? BuiltInName(item);

    /// <summary>OpenHW's own name for the sensor, HWiNFO-style; used for sorting and explanations so a rename moves neither.</summary>
    public static string BuiltInName(HardwareItem item)
    {
        var name = item.Name;
        switch (item.Backend)
        {
            case "nvml": return GpuName(item);
            case "edac":
            {
                var rank = item.Group.Split(" · ").ElementAtOrDefault(1);
                var label = TitleCase(name);
                return rank is null ? label : $"{TitleCase(RankNumber().Replace(rank, " $1"))} {label}";
            }
            case "nvme-health" or "nvme-activity": return TitleCase(name);
        }
        var chip = Chip(item);
        if (chip == "amd_hsmp_hwmon")
            return item.Id.EndsWith(":cap", StringComparison.Ordinal) ? "CPU PPT Limit" : "CPU Package Power";
        if (chip == "k10temp")
        {
            if (name is "Tctl" or "Tdie") return "CPU (Tctl/Tdie)";
            var ccd = CcdName().Match(name);
            if (ccd.Success) return $"CCD{ccd.Groups[1].Value} (Tdie)";
        }
        if (chip is "spd5118" or "jc42") return "SPD Hub Temperature (" + name.Replace("DRAM · ", "") + ")";
        if (chip == "nvme" && name.StartsWith("Drive temperature", StringComparison.Ordinal)) return "Drive Temperature";
        return name;
    }

    private static string GpuName(HardwareItem item)
    {
        var suffix = item.Id.Split(':', 3).ElementAtOrDefault(2) ?? "";
        if (suffix.StartsWith("extra:", StringComparison.Ordinal))
            return NvidiaEngine.ExtraDisplayName(suffix[6..]) ?? item.Name;
        var fan = FanIndex().Match(suffix);
        if (fan.Success) return $"GPU Fan{int.Parse(fan.Groups[1].Value, CultureInfo.InvariantCulture) + 1}";
        return suffix switch
        {
            "temp:core" => "GPU Temperature",
            "metric:load" => "GPU Core Load",
            "metric:memory-load" => "GPU Memory Controller Load",
            "metric:power" => "GPU Power",
            "metric:power-limit" => "GPU Power Limit",
            "metric:graphics-clock" => "GPU Clock",
            "metric:sm-clock" => "GPU SM Clock",
            "metric:memory-clock" => "GPU Memory Clock",
            "metric:memory-used" => "GPU Memory Allocated",
            "metric:memory-total" => "GPU Memory Total",
            _ => "GPU " + item.Name.Replace(item.Group, "").Trim(),
        };
    }

    /// <summary>
    /// Row tooltip: the sensor's name, a warning first when its mapping is not trustworthy, the hardware input it
    /// came from (and any scaling), the source details, and its stable id.
    /// </summary>
    public static string Tooltip(HardwareItem item)
    {
        var name = DisplayName(item);
        var builtIn = BuiltInName(item);
        var lines = new List<string> { name };
        if (name != builtIn) lines.Add($"Renamed from: {builtIn}");
        // An uncertain name is called out before anything is said about what it means.
        if (!item.MappingConfirmed)
            lines.Add("Unconfirmed mapping: this name and scale were inferred by comparing readings with HWiNFO " +
                      "and have not been verified on this board yet.");
        else if (item.Details.Contains(BoardProfiles.UnmappedRailNote, StringComparison.Ordinal))
            lines.Add("Unmapped input: no board profile names this pin, so it shows the raw voltage at the chip.");
        else if (item.OriginalName.Length == 0 && IsSuperIo(item) && item.Kind is HardwareKind.Temperature or HardwareKind.Tach)
            lines.Add("Unmapped input: this is the chip's own name for the input; which board sensor or fan header " +
                      "it is has not been mapped yet.");

        var explanation = SensorExplanations.Explain(item, builtIn);
        if (explanation.Length > 0) lines.Add(explanation);
        lines.Add(""); // what it means above, where it comes from below

        if (item.OriginalName.Length > 0)
            lines.Add($"Board input: {item.OriginalName} on {ChipName(item)}" +
                      (item.Scale != 1 ? $", raw reading × {item.Scale.ToString("0.###", CultureInfo.InvariantCulture)}" : ""));
        else if (!string.Equals(item.Name, builtIn, StringComparison.Ordinal))
            lines.Add($"Reported as: {item.Name}");

        var details = item.Details.Replace(BoardProfiles.UnmappedRailNote, "");
        if (details.Length > 0) lines.Add(details);
        else if (item.Source.Length > 0) lines.Add($"Source: {item.Source}");
        // Prose wraps; the id stays on one line so it can be read (and copied) whole.
        return string.Join("\n", lines.Select(l => Wrap(l)).Append($"Sensor ID: {item.Id}"));
    }

    /// <summary>Breaks text into lines of about <paramref name="width"/> characters at spaces (tooltips and tray menus don't wrap).</summary>
    public static string Wrap(string text, int width = 72)
    {
        var lines = new List<string>();
        var line = new System.Text.StringBuilder();
        foreach (var word in text.Split(' '))
        {
            if (line.Length > 0 && line.Length + 1 + word.Length > width)
            {
                lines.Add(line.ToString());
                line.Clear();
            }
            if (line.Length > 0) line.Append(' ');
            line.Append(word);
        }
        if (line.Length > 0) lines.Add(line.ToString());
        return string.Join("\n", lines);
    }

    private static bool IsSuperIo(HardwareItem item) =>
        item.Backend == "hwmon" && (Chip(item).StartsWith("nct", StringComparison.Ordinal) || Chip(item).StartsWith("it8", StringComparison.Ordinal));

    private static string ChipName(HardwareItem item) => item.Group.Length > 0 ? item.Group : Chip(item);

    public static string DisplayUnit(HardwareItem item) => item.Unit switch
    {
        "" => item.Kind switch
        {
            HardwareKind.Temperature => "°C",
            HardwareKind.Tach => "RPM",
            HardwareKind.Control => "%",
            _ => "",
        },
        "GiB" => "MB", // HWiNFO reports memory in MB; values are converted in Scale
        "count" or "cmd" or "events" or "cycles" or "flags" or "errors" => "", // HWiNFO shows counts bare
        _ => item.Unit,
    };

    /// <summary>Raw reading → displayed unit (only GiB→MB needs it today).</summary>
    public static double Scale(HardwareItem item, double value) => item.Unit == "GiB" ? value * 1024 : value;

    public static string Format(double? value, string unit)
    {
        if (value is not { } v || !double.IsFinite(v)) return "";
        if (unit == "flag") return v >= 0.5 ? "Yes" : "No";
        if (unit == "pstate") return "P" + Math.Round(v).ToString(CultureInfo.InvariantCulture);
        if (unit == "lanes") return "x" + Math.Round(v).ToString(CultureInfo.InvariantCulture);
        var decimals = unit switch
        {
            "°C" or "MHz" or "%" or "KB/s" => 1,
            "V" or "A" or "W" or "MB/s" or "GB" or "TB" or "GT/s" => 3,
            "RPM" or "MB" => 0,
            _ => Math.Abs(v - Math.Round(v)) < 1e-9 ? 0 : 2,
        };
        var text = v.ToString("N" + decimals, CultureInfo.InvariantCulture);
        return unit.Length == 0 ? text : $"{text} {unit}";
    }

    /// <summary>HWiNFO shows a yes/no sensor's average as the share of samples that were "Yes".</summary>
    public static string FormatAverage(double? value, string unit) =>
        unit == "flag" ? Format(value * 100, "%") : Format(value, unit);

    private static string TitleCase(string text) => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(text);

    private static string PciSortKey(string label) => PciToken().Match(label).Value;

    // hwmon rows keep the chip's register order (as HWiNFO lists them: Vcore, +5V, AVSB…); everything else sorts by name.
    private static string SortKey(HardwareItem item) => NaturalKey(item.Backend == "hwmon" ? item.Id : BuiltInName(item));

    // Pads digit runs so "in2" sorts before "in10" and "Core 2" before "Core 10".
    private static string NaturalKey(string text) => DigitRun().Replace(text, m => m.Value.PadLeft(8, '0'));

    [GeneratedRegex(@"^Tccd(\d+)$")] private static partial Regex CcdName();
    [GeneratedRegex(@"^(?:fan|tach):(\d+)$")] private static partial Regex FanIndex();
    [GeneratedRegex(@"[0-9A-Fa-f]+:[0-9A-Fa-f]+\.[0-9A-Fa-f]+")] private static partial Regex PciToken();
    [GeneratedRegex(@"\d+")] private static partial Regex DigitRun();
    [GeneratedRegex(@"(?<=\D)(\d+)$")] private static partial Regex RankNumber();
}
