using System.Globalization;
using System.Text.Json;
using OpenHW.Hardware;

namespace OpenHW.App.HardwareCheck;

/// <summary>A GPU as the snapshot keeps it: NVML's UUID is the card's own identity; the PCI address is where it sits.</summary>
public sealed record SnapshotGpu(string Uuid, string Name, string Pci);

/// <summary>A drive: its identity (hash of model + serial, no serial stored), model, PCI address and hwmon number.</summary>
public sealed record SnapshotDrive(string Identity, string Model, string Pci, int? Hwmon);

/// <summary>
/// What hardware OpenHW saw, saved in ~/.config/openhw/hardware.json so the next start can tell what changed —
/// a new motherboard, CPU, GPUs moved to other slots, DIMMs added — and point saved choices at the new sensors.
/// </summary>
public sealed class HardwareSnapshot
{
    public DateTime Taken { get; set; }
    public string BoardVendor { get; set; } = "";
    public string Board { get; set; } = "";
    public string Bios { get; set; } = "";
    public string Cpu { get; set; } = "";
    public int Threads { get; set; }
    /// <summary>hwmon chips by driver name ("nct6798", "asusec", "k10temp"), without the kernel's numbering.</summary>
    public List<string> Chips { get; set; } = [];
    public List<SnapshotGpu> Gpus { get; set; } = [];
    public List<SnapshotDrive> Drives { get; set; } = [];
    public int MemoryGiB { get; set; }
    /// <summary>DIMMs OpenHW reads over SPD (their own groups).</summary>
    public int DimmsReadable { get; set; }
    /// <summary>DIMMs the BMC reports a temperature for; 0 without a BMC.</summary>
    public int DimmsBmc { get; set; }
    public string Bmc { get; set; } = "";
    public List<string> Networks { get; set; } = [];

    /// <summary>The sensor ids that existed — so a later check can name what a missing saved sensor was.</summary>
    public Dictionary<string, string> SensorNames { get; set; } = [];

    public static string FilePath => Path.Combine(Settings.AppSettings.ConfigDir, "hardware.json");

    public static HardwareSnapshot Take(IReadOnlyList<HardwareItem> inventory, Sensors.SensorPresentation.MachineInfo machine,
        string bmc, Func<HardwareItem, string> displayName)
    {
        var gpus = inventory.Where(i => i.Backend == "nvml" && i.Id.StartsWith("nvml:GPU-", StringComparison.Ordinal))
            .GroupBy(i => i.Id.Split(':')[1])
            .Select(g => SplitGpuGroup(g.Key, g.First().Group))
            .OrderBy(g => g.Pci, StringComparer.Ordinal).ToList();
        var chips = inventory.Where(i => i.Id.StartsWith("hwmon:", StringComparison.Ordinal))
            .Select(i => i.Id.Split(':')[1]).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        return new HardwareSnapshot
        {
            Taken = DateTime.Now,
            BoardVendor = machine.BoardVendor, Board = machine.BoardName,
            Bios = SysFs.ReadText("/sys/class/dmi/id/bios_version"),
            Cpu = machine.CpuModel, Threads = Environment.ProcessorCount,
            Chips = chips, Gpus = gpus,
            Drives = machine.Drives.Select(d => new SnapshotDrive(d.Identity, d.Model, d.Pci,
                d.HwmonPath is { } p && int.TryParse(Path.GetFileName(p).Replace("hwmon", ""), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null))
                .OrderBy(d => d.Pci, StringComparer.Ordinal).ToList(),
            MemoryGiB = MemTotalGiB(),
            DimmsReadable = inventory.Select(i => i.Group).Where(g => g.StartsWith("dimm:", StringComparison.Ordinal)).Distinct().Count()
                            + inventory.Where(i => i.Id.StartsWith("hwmon:spd5118:", StringComparison.Ordinal) && !i.Id.Contains(":device-", StringComparison.Ordinal)).Count(),
            DimmsBmc = inventory.Count(i => i.Id.StartsWith(BmcEngine.IdPrefix, StringComparison.Ordinal) && IsBmcDimm(i.Name)),
            Bmc = bmc,
            Networks = inventory.Where(i => i.Group.StartsWith("net:", StringComparison.Ordinal)).Select(i => i.Group[4..])
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList(),
            SensorNames = inventory.ToDictionary(i => i.Id, displayName, StringComparer.Ordinal),
        };
    }

    public static bool IsBmcDimm(string name) =>
        name.StartsWith("DIMM", StringComparison.OrdinalIgnoreCase) && name.Contains("Temp", StringComparison.OrdinalIgnoreCase);

    // NVML groups are "<name> <bus>" ("RTX PRO 6000 Blackwell Workstation Edition C1:00.0").
    public static SnapshotGpu SplitGpuGroup(string uuid, string group)
    {
        var space = group.LastIndexOf(' ');
        return space > 0 && group[(space + 1)..].Contains(':')
            ? new SnapshotGpu(uuid, group[..space], group[(space + 1)..])
            : new SnapshotGpu(uuid, group, "");
    }

    private static int MemTotalGiB()
    {
        foreach (var line in SysFs.ReadText("/proc/meminfo").Split('\n'))
            if (line.StartsWith("MemTotal:", StringComparison.Ordinal) &&
                long.TryParse(line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1], NumberStyles.None, CultureInfo.InvariantCulture, out var kb))
                return (int)Math.Round(kb / 1048576.0);
        return 0;
    }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static HardwareSnapshot? Load(string? path = null)
    {
        try { return JsonSerializer.Deserialize<HardwareSnapshot>(File.ReadAllText(path ?? FilePath)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    public void Save(string? path = null)
    {
        path ??= FilePath;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"openhw: could not save the hardware snapshot ({ex.Message})");
        }
    }
}

/// <summary>One line of "what changed": a part added, removed or replaced.</summary>
public sealed record HardwareChange(string Area, string Text);

public static class HardwareDiff
{
    /// <summary>What differs between the saved snapshot and now; empty when nothing a person would call a change.</summary>
    public static List<HardwareChange> Compare(HardwareSnapshot old, HardwareSnapshot now, bool gpuLent = false)
    {
        var changes = new List<HardwareChange>();
        if (!Same(old.Board, now.Board) || !Same(old.BoardVendor, now.BoardVendor))
            changes.Add(new("Motherboard", $"{old.Board} → {now.Board}"));
        else if (!Same(old.Bios, now.Bios) && old.Bios.Length > 0)
            changes.Add(new("BIOS", $"{old.Bios} → {now.Bios}"));
        if (!Same(old.Cpu, now.Cpu)) changes.Add(new("CPU", $"{old.Cpu} → {now.Cpu}"));
        if (old.MemoryGiB > 0 && Math.Abs(old.MemoryGiB - now.MemoryGiB) > 2)
            changes.Add(new("Memory", $"{old.MemoryGiB} GiB → {now.MemoryGiB} GiB"));
        if (!Same(old.Bmc, now.Bmc))
            changes.Add(new("BMC", old.Bmc.Length == 0 ? $"New: {now.Bmc}" : now.Bmc.Length == 0 ? $"No longer answering: {old.Bmc}" : $"{old.Bmc} → {now.Bmc}"));

        foreach (var chip in now.Chips.Except(old.Chips)) changes.Add(new("Sensor chip", "New: " + chip));
        foreach (var chip in old.Chips.Except(now.Chips)) changes.Add(new("Sensor chip", "Gone: " + chip));

        foreach (var gpu in now.Gpus)
        {
            var was = old.Gpus.FirstOrDefault(g => g.Uuid == gpu.Uuid);
            if (was is null) changes.Add(new("GPU", $"New: {gpu.Name} ({gpu.Pci})"));
            else if (was.Pci != gpu.Pci) changes.Add(new("GPU", $"Moved: {gpu.Name} {was.Pci} → {gpu.Pci}"));
        }
        // A card lent to a VM leaves the host for a while; that isn't a hardware change.
        if (!gpuLent)
            foreach (var gpu in old.Gpus.Where(g => now.Gpus.All(n => n.Uuid != g.Uuid)))
                changes.Add(new("GPU", $"Gone: {gpu.Name} ({gpu.Pci})"));

        foreach (var drive in now.Drives)
        {
            var was = old.Drives.FirstOrDefault(d => d.Identity == drive.Identity);
            if (was is null) changes.Add(new("Drive", $"New: {drive.Model} ({drive.Pci})"));
            else if (was.Pci != drive.Pci) changes.Add(new("Drive", $"Moved: {drive.Model} {was.Pci} → {drive.Pci}"));
        }
        foreach (var drive in old.Drives.Where(d => now.Drives.All(n => n.Identity != d.Identity)))
            changes.Add(new("Drive", $"Gone: {drive.Model} ({drive.Pci})"));

        foreach (var net in now.Networks.Except(old.Networks)) changes.Add(new("Network", "New: " + net));
        foreach (var net in old.Networks.Except(now.Networks)) changes.Add(new("Network", "Gone: " + net));
        return changes;
    }

    private static bool Same(string a, string b) => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
}
