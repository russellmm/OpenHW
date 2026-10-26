using System.Text.RegularExpressions;

namespace OpenHW.Hardware;

/// <summary>
/// Identifies already-bound DDR5 SPD hubs. No bus probing or EEPROM access: measurement reads
/// remain with libsensors (or the existing sysfs fallback). Kernel enumeration numbers are not identity.
/// </summary>
public static class DramSensorIdentity
{
    public static HardwareItem Describe(HardwareItem item, string hwmonPath)
    {
        var parts = item.Id.Split(':');
        if (parts.Length < 5 || parts[0] != "hwmon" || parts[1] != "spd5118" || parts[3] != "temp") return item;
        try
        {
            // Resolve the class link first: relative 'device' links are based on its physical
            // directory. Resolving the child directly through /sys/class can yield /sys/0-0050.
            var hwmon = new DirectoryInfo(hwmonPath);
            var physical = hwmon.ResolveLinkTarget(true) as DirectoryInfo ?? hwmon;
            var device = new DirectoryInfo(Path.Combine(physical.FullName, "device")).ResolveLinkTarget(true) as DirectoryInfo;
            if (device is null || Locate(device) is not { } hub) return item;
            var (identity, who, controller, bus, address) = hub;
            var generic = item.Name == "temp" + parts[4] || item.Name == "Temperature #" + parts[4];
            // Keep labels configured in sensors.d, including the suffixes on optional status flags.
            var label = generic ? "DRAM" : item.Name.StartsWith("temp" + parts[4] + " · ", StringComparison.Ordinal)
                ? "DRAM" + item.Name[("temp" + parts[4]).Length..] : item.Name;
            return new HardwareItem
            {
                Id = string.Join(':', parts.Take(2).Append(identity).Concat(parts.Skip(3))),
                Name = $"{label} · {who}", Kind = item.Kind, Backend = item.Backend,
                Group = "DDR5 DIMMs", CanSet = false, Unit = item.Unit.Length == 0 ? "°C" : item.Unit,
                Source = item.Source,
                Details = $"DDR5 SPD hub temperature · controller {controller} · I2C {bus}, address 0x{address}" +
                    " · physical DIMM slot not yet mapped · " + item.Details,
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return item; // Metadata is optional; a missing link must not hide a valid library channel.
        }
    }

    /// <summary>
    /// A hub's stable identity from its I2C client directory ("…/i2c-0/0-0050"): "device-" + the escaped physical
    /// path of the I2C controller plus the hub address, which survives hwmon and adapter renumbering; and a short
    /// "AMDI0010:01 / 0x50" for names.
    /// </summary>
    public static (string Identity, string Who, string Controller, string Bus, string Address)? Locate(DirectoryInfo device)
    {
        var client = Regex.Match(device.Name, @"^(\d+)-00([0-9a-f]{2})$", RegexOptions.CultureInvariant);
        var adapter = device.Parent;
        if (!client.Success || adapter?.Name != "i2c-" + client.Groups[1].Value || adapter.Parent is not { } controller) return null;
        var address = client.Groups[2].Value;
        return ("device-" + Uri.EscapeDataString(controller.FullName + "/" + address), $"{controller.Name} / 0x{address}",
            controller.FullName, client.Groups[1].Value, address);
    }

    /// <summary><see cref="Locate(DirectoryInfo)"/> for a client name as sysfs lists it ("0-0050").</summary>
    public static (string Identity, string Who, string Controller, string Bus, string Address)? Locate(string client, string sysRoot = "/sys")
    {
        try
        {
            var link = new DirectoryInfo(Path.Combine(sysRoot, "bus", "i2c", "devices", client));
            return link.Exists && (link.ResolveLinkTarget(true) as DirectoryInfo ?? link) is { } device ? Locate(device) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }
}
