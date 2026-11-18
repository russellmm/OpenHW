using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace OpenHW.Hardware.Windows;

/// <summary>
/// NVMe drives on Windows, all without elevation: a handle opened for queries only answers the storage-protocol
/// queries (Identify Controller, the SMART / Health log page) and the disk performance counters. Drives are found
/// through the disk interface list; each one's PCI address comes from its controller in the device tree, so the
/// drive identity (PCI address + model + serial, hashed) is the same one Linux computes from sysfs and a drive keeps
/// its rows, names and tray icons across both systems. Never keyed by PhysicalDrive number: that order differs from
/// Linux's nvmeN and can change between boots.
/// </summary>
[SupportedOSPlatform("windows")]
public static unsafe class WindowsNvmeDevices
{
    private const uint IoctlStorageQueryProperty = 0x2D1400, IoctlStorageGetDeviceNumber = 0x2D1080;
    private const int StorageDeviceProperty = 0, StorageAdapterProtocolSpecificProperty = 49, StorageDeviceProtocolSpecificProperty = 50;
    private const int ProtocolTypeNvme = 3, NvmeDataTypeIdentify = 1, NvmeDataTypeLogPage = 2;
    private const byte BusTypeNvme = 0x11;
    private const int SpecificDataSize = 40; // STORAGE_PROTOCOL_SPECIFIC_DATA
    private const int DescriptorHeader = 8;  // STORAGE_PROTOCOL_DATA_DESCRIPTOR: Version, Size

    public static IReadOnlyList<NvmeDevice> Discover()
    {
        var devices = new List<(int Number, NvmeDevice Device)>();
        foreach (var path in Win32.InterfacePaths(Win32.DiskInterface))
        {
            try { if (Describe(path) is { } found) devices.Add(found); }
            catch (IOException) { }
        }
        return devices.OrderBy(d => d.Number).Select(d => d.Device).ToList();
    }

    private static (int, NvmeDevice)? Describe(string interfacePath)
    {
        using var handle = Win32.OpenForQuery(interfacePath);
        if (handle.IsInvalid) return null;
        var number = new byte[12];
        fixed (byte* p = number)
            if (!Win32.DeviceIoControl(handle, IoctlStorageGetDeviceNumber, null, 0, p, 12, out _, 0)) return null;
        var disk = BinaryPrimitives.ReadInt32LittleEndian(number.AsSpan(4));

        var descriptor = Query(handle, StorageDeviceProperty, 1024);
        if (descriptor is null || descriptor[0x1C] != BusTypeNvme) return null;

        // Identify Controller: the serial and model exactly as Linux's sysfs shows them (the device descriptor's
        // "serial" is the namespace EUI-64 on many drives, which would give a different identity).
        var identify = ProtocolQuery(handle, StorageAdapterProtocolSpecificProperty, NvmeDataTypeIdentify, 1, 4096);
        if (identify is null) return null;
        var serial = Ascii(identify, 4, 20);
        var model = Ascii(identify, 24, 40);
        var firmware = Ascii(identify, 64, 8);
        if (serial.Length == 0 || model.Length == 0) return null;

        var pci = PciAddress(interfacePath);
        if (pci is null) return null;
        var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(pci + "\n" + model + "\n" + serial))).ToLowerInvariant();
        var controller = "PhysicalDrive" + disk.ToString(CultureInfo.InvariantCulture);
        return (disk, new NvmeDevice(controller, pci, model, firmware.Length > 0 ? firmware : "unknown", identity, null, interfacePath));
    }

    /// <summary>"0000:41:00.0": the disk's parent in the device tree is its NVMe controller, a PCI function.</summary>
    private static string? PciAddress(string interfacePath)
    {
        if (Win32.InterfaceString(interfacePath, Win32.InstanceId) is not { } instance) return null;
        if (Win32.CM_Locate_DevNode(out var node, instance, 0) != Win32.CrSuccess) return null;
        for (var depth = 0; depth < 4; depth++)
        {
            if (Win32.CM_Get_Parent(out var parent, node, 0) != Win32.CrSuccess) return null;
            node = parent;
            if (!string.Equals(Win32.DevNodeString(node, Win32.EnumeratorName), "PCI", StringComparison.OrdinalIgnoreCase)) continue;
            if (Win32.DevNodeUInt(node, Win32.BusNumber) is not { } bus || Win32.DevNodeUInt(node, Win32.Address) is not { } address) return null;
            return string.Create(CultureInfo.InvariantCulture, $"0000:{bus:x2}:{address >> 16:x2}.{address & 0xFFFF:x1}");
        }
        return null;
    }

    /// <summary>The 512-byte SMART / Health Information log page (02h).</summary>
    public static byte[] HealthLog(string controller)
    {
        using var handle = Win32.OpenForQuery(@"\\.\" + controller);
        if (handle.IsInvalid) throw new IOException($"{controller}: can't open ({System.Runtime.InteropServices.Marshal.GetLastPInvokeError()})");
        return ProtocolQuery(handle, StorageDeviceProtocolSpecificProperty, NvmeDataTypeLogPage, 2, 512)
            ?? throw new IOException($"{controller}: the drive didn't return its health log");
    }

    /// <summary>IOCTL_DISK_PERFORMANCE's cumulative counters, as the Linux block counters the activity rows are computed from.</summary>
    public static NvmeActivityCounters? Performance(string controller)
    {
        using var handle = Win32.OpenForQuery(@"\\.\" + controller);
        if (handle.IsInvalid) return null;
        var buffer = new byte[88];
        fixed (byte* p = buffer)
            if (!Win32.DeviceIoControl(handle, 0x70020, null, 0, p, (uint)buffer.Length, out _, 0)) return null;
        long At(int offset) => BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(offset));
        // Times are in 100 ns; busy = time since the counters started minus the idle time.
        return new((ulong)At(0) / 512, (ulong)At(8) / 512, (ulong)At(16) / 10_000, (ulong)At(24) / 10_000, (ulong)Math.Max(0, At(56) - At(32)) / 10_000);
    }

    private static byte[]? Query(Microsoft.Win32.SafeHandles.SafeFileHandle handle, int property, int size)
    {
        var query = new byte[12];
        BinaryPrimitives.WriteInt32LittleEndian(query, property);
        var output = new byte[size];
        fixed (byte* q = query)
        fixed (byte* o = output)
            return Win32.DeviceIoControl(handle, IoctlStorageQueryProperty, q, (uint)query.Length, o, (uint)output.Length, out _, 0) ? output : null;
    }

    private static byte[]? ProtocolQuery(Microsoft.Win32.SafeHandles.SafeFileHandle handle, int property, int dataType, int request, int length)
    {
        // STORAGE_PROPERTY_QUERY (PropertyId, QueryType) followed by STORAGE_PROTOCOL_SPECIFIC_DATA, then room for the data.
        var buffer = new byte[DescriptorHeader + SpecificDataSize + length];
        var span = buffer.AsSpan();
        BinaryPrimitives.WriteInt32LittleEndian(span, property);
        BinaryPrimitives.WriteInt32LittleEndian(span[8..], ProtocolTypeNvme);
        BinaryPrimitives.WriteInt32LittleEndian(span[12..], dataType);
        BinaryPrimitives.WriteInt32LittleEndian(span[16..], request);
        BinaryPrimitives.WriteInt32LittleEndian(span[24..], SpecificDataSize); // data offset, from the specific-data struct
        BinaryPrimitives.WriteInt32LittleEndian(span[28..], length);
        fixed (byte* p = buffer)
            if (!Win32.DeviceIoControl(handle, IoctlStorageQueryProperty, p, (uint)buffer.Length, p, (uint)buffer.Length, out _, 0)) return null;
        var offset = DescriptorHeader + BinaryPrimitives.ReadInt32LittleEndian(span[(DescriptorHeader + 16)..]);
        var returned = BinaryPrimitives.ReadInt32LittleEndian(span[(DescriptorHeader + 20)..]);
        if (returned < length || offset + length > buffer.Length) return null;
        return buffer.AsSpan(offset, length).ToArray();
    }

    private static string Ascii(byte[] data, int offset, int length) =>
        Encoding.ASCII.GetString(data, offset, length).TrimEnd('\0', ' ').Trim();
}

/// <summary>Cumulative I/O counters in the units Linux's block statistics use (512-byte sectors, milliseconds).</summary>
public readonly record struct NvmeActivityCounters(ulong ReadSectors, ulong WriteSectors, ulong ReadMs, ulong WriteMs, ulong BusyMs);

/// <summary>The health log through a query handle, for <see cref="NvmeHealthBackend"/> (Linux reads it through the helper).</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsNvmeSmartReader : INvmeSmartReader
{
    public byte[] Read(string controller, string identity) => WindowsNvmeDevices.HealthLog(controller);
    public void Dispose() { }
}

/// <summary>
/// The Windows NVMe engine: the same SMART rows as Linux (every 30 s, by <see cref="NvmeHealthBackend"/>), the drive
/// temperatures Linux gets from hwmon (composite and sensors 1–8, read each poll from the same log page), and the
/// activity rows from the disk performance counters.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsNvmeEngine : ISensorEngine
{
    private readonly NvmeHealthBackend _health;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Dictionary<string, (TimeSpan At, NvmeActivityBackend.Counters Value)> _previous = [];
    private IReadOnlyList<NvmeDevice> _devices = [];
    private IReadOnlyDictionary<string, IReadOnlyList<int>> _sensors = new Dictionary<string, IReadOnlyList<int>>();

    public WindowsNvmeEngine() => _health = new NvmeHealthBackend(new WindowsNvmeSmartReader(), () => _devices);

    public string Name => "nvme";
    public bool Available => _devices.Count > 0;
    public IReadOnlyList<NvmeDevice> Devices => _devices;

    public static string TemperatureId(NvmeDevice d, int sensor) => $"nvme-health:{d.Identity}:temp:{sensor}";

    public IReadOnlyList<HardwareItem> Discover()
    {
        _devices = WindowsNvmeDevices.Discover();
        var items = new List<HardwareItem>();
        var sensors = new Dictionary<string, IReadOnlyList<int>>();
        foreach (var d in _devices)
        {
            // Sensor 0 is the composite; 1–8 are listed only where the drive implements them (non-zero in the log).
            byte[]? log = null;
            try { log = WindowsNvmeDevices.HealthLog(d.Controller); }
            catch (IOException) { }
            var present = new List<int> { 0 };
            if (log is not null)
                for (var i = 1; i <= 8; i++) if (BinaryPrimitives.ReadUInt16LittleEndian(log.AsSpan(200 + 2 * (i - 1))) != 0) present.Add(i);
            sensors[d.Identity] = present;
            items.AddRange(present.Select(i => new HardwareItem
            {
                Id = TemperatureId(d, i), Name = i == 0 ? "Drive temperature" : $"Drive temperature {i}", Kind = HardwareKind.Temperature,
                Unit = "°C", Backend = "nvme-health", Group = d.Group, CanSet = false, Source = "NVMe SMART",
                Details = (i == 0
                    ? "Composite is the drive's overall thermal-management reading; it need not be one physical thermometer."
                    : $"NVMe SMART temperature sensor {i}. Its physical location is vendor-defined.") + " · " + d.Description,
            }));
            items.AddRange(NvmeActivityBackend.Metrics.Select(m => new HardwareItem
            {
                Id = NvmeActivityBackend.Id(d, m.Key), Name = m.Name, Unit = m.Unit, Kind = m.Kind, Group = d.Group,
                Backend = "nvme-activity", CanSet = false, Source = "Windows disk performance counters",
                Details = m.Details.Replace("Linux block", "Windows disk").Replace("Linux busy", "Busy") + " · " + d.Description,
            }));
        }
        _sensors = sensors;
        var identities = _devices.Select(d => d.Identity).ToHashSet();
        foreach (var gone in _previous.Keys.Where(k => !identities.Contains(k)).ToList()) _previous.Remove(gone);
        items.AddRange(_health.Discover());
        return items;
    }

    public void ReadInto(IDictionary<string, double?> readings, IReadOnlySet<string>? requested)
    {
        _health.ReadInto(readings, requested);
        var now = _clock.Elapsed;
        foreach (var d in _devices)
        {
            var temps = _sensors.GetValueOrDefault(d.Identity) ?? [];
            if (temps.Any(i => requested is null || requested.Contains(TemperatureId(d, i))))
            {
                byte[]? log = null;
                try { log = WindowsNvmeDevices.HealthLog(d.Controller); }
                catch (IOException) { }
                foreach (var i in temps)
                {
                    var kelvin = log is null ? 0 : BinaryPrimitives.ReadUInt16LittleEndian(log.AsSpan(i == 0 ? 1 : 200 + 2 * (i - 1)));
                    readings[TemperatureId(d, i)] = kelvin == 0 ? null : kelvin - 273.15;
                }
            }

            if (!NvmeActivityBackend.Metrics.Any(m => requested is null || requested.Contains(NvmeActivityBackend.Id(d, m.Key))))
            {
                _previous.Remove(d.Identity);
                continue;
            }
            Dictionary<string, double?> values;
            if (WindowsNvmeDevices.Performance(d.Controller) is { } c)
            {
                var counters = new NvmeActivityBackend.Counters(c.ReadSectors, c.WriteSectors, c.ReadMs, c.WriteMs, c.BusyMs);
                values = _previous.TryGetValue(d.Identity, out var p)
                    ? NvmeActivityBackend.Values(counters, p.Value, (now - p.At).TotalSeconds)
                    : NvmeActivityBackend.Values(counters, null, 0);
                _previous[d.Identity] = (now, counters);
            }
            else
            {
                _previous.Remove(d.Identity);
                values = NvmeActivityBackend.Metrics.ToDictionary(m => m.Key, _ => (double?)null);
            }
            // Windows counts from boot (or the driver's start), so the "since device start" totals stay meaningful.
            foreach (var m in NvmeActivityBackend.Metrics)
            {
                var id = NvmeActivityBackend.Id(d, m.Key);
                if (requested is null || requested.Contains(id)) readings[id] = values[m.Key];
            }
        }
    }

    public string Status(string group) => _health.Status(group);

    public void Dispose() => _health.Dispose();
}
