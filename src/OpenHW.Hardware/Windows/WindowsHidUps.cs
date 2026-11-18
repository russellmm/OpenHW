using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace OpenHW.Hardware.Windows;

/// <summary>
/// A USB UPS on Windows, read directly as a HID Power Device (usage pages 0x84 Power Device and 0x85 Battery System) —
/// Windows has no NUT server, and its own battery driver only passes on charge and runtime. Read-only: feature
/// reports are only fetched (HidD_GetFeature), never set. <see cref="Query"/> answers NUT's <c>LIST UPS</c> and
/// <c>LIST VAR</c> with NUT's variable names (input.voltage, ups.load, battery.runtime, ups.status…), so
/// <see cref="NutEngine"/> shows the same rows as on Linux.
/// </summary>
[SupportedOSPlatform("windows")]
public static class WindowsHidUps
{
    private const ushort PowerPage = 0x84, BatteryPage = 0x85;

    /// <summary>One value the UPS reports: where it sits (the collections above it), and its value in base units.</summary>
    public sealed record HidValue(ushort Page, ushort Usage, IReadOnlyList<(ushort Page, ushort Usage)> Path, double Value);

    public sealed record HidUps(string Name, string Manufacturer, string Product, string Serial, IReadOnlyList<HidValue> Values);

    private static readonly object Gate = new();
    private static (DateTime At, List<HidUps> Found)? _cache;

    /// <summary>NUT's LIST UPS / LIST VAR, answered from the HID UPSes plugged in (read at most once a second).</summary>
    public static IReadOnlyList<string> Query(string command)
    {
        List<HidUps> found;
        lock (Gate)
        {
            if (_cache is not { } c || DateTime.UtcNow - c.At > TimeSpan.FromSeconds(1)) _cache = (DateTime.UtcNow, ReadAll());
            found = _cache.Value.Found;
        }
        if (command == "LIST UPS") return found.Select(u => $"UPS {u.Name} \"{(u.Manufacturer + " " + u.Product).Trim()} (USB HID)\"").ToList();
        if (command.StartsWith("LIST VAR ", StringComparison.Ordinal) && found.FirstOrDefault(u => u.Name == command[9..]) is { } ups)
            return Vars(ups).Select(v => $"VAR {ups.Name} {v.Key} \"{v.Value}\"").ToList();
        throw new IOException("ERR UNKNOWN-UPS");
    }

    /// <summary>The HID values as NUT variables.</summary>
    public static IReadOnlyDictionary<string, string> Vars(HidUps ups)
    {
        var vars = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["device.mfr"] = ups.Manufacturer, ["device.model"] = ups.Product, ["driver.name"] = "usbhid (Windows HID)",
        };
        if (ups.Serial.Length > 0) vars["device.serial"] = ups.Serial;
        string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
        double? Find(ushort page, ushort usage, ushort? collection = null) =>
            ups.Values.FirstOrDefault(v => v.Page == page && v.Usage == usage && (collection is null || v.Path.Any(p => p == (PowerPage, collection.Value))))?.Value;
        void Put(string key, double? v) { if (v is { } x && double.IsFinite(x)) vars[key] = F(x); }

        Put("input.voltage", Find(PowerPage, 0x30, 0x1A));        // Voltage in Input
        Put("input.frequency", Find(PowerPage, 0x32, 0x1A));      // Frequency in Input
        Put("output.voltage", Find(PowerPage, 0x30, 0x1C));       // Voltage in Output
        Put("output.frequency", Find(PowerPage, 0x32, 0x1C));
        Put("ups.load", Find(PowerPage, 0x35));                   // PercentLoad
        Put("ups.realpower", Find(PowerPage, 0x34));              // ActivePower
        Put("ups.power", Find(PowerPage, 0x33));                  // ApparentPower
        Put("ups.realpower.nominal", Find(PowerPage, 0x44));      // ConfigActivePower
        Put("ups.temperature", Find(PowerPage, 0x36) is { } k ? k - 273.15 : null); // HID temperatures are kelvin
        Put("battery.charge", Find(BatteryPage, 0x66));           // RemainingCapacity
        Put("battery.runtime", Find(BatteryPage, 0x68));          // RunTimeToEmpty, seconds
        // Voltage in Battery; CyberPower reports it in PowerSummary instead (27.3 V on the CP2000PFCRM2U, as HWiNFO).
        Put("battery.voltage", Find(PowerPage, 0x30, 0x12) ?? Find(PowerPage, 0x30, 0x24));
        // No active power reported (CyberPower's): load × rating, as NUT's driver does.
        if (!vars.ContainsKey("ups.realpower") && Find(PowerPage, 0x35) is { } load && Find(PowerPage, 0x44) is { } rating)
            Put("ups.realpower", Math.Round(load / 100 * rating));

        var status = new List<string>();
        bool Flag(ushort page, ushort usage) => Find(page, usage) is { } f && f != 0;
        if (Find(BatteryPage, 0xD0) is { } ac) status.Add(ac != 0 ? "OL" : "OB");      // ACPresent
        if (Flag(BatteryPage, 0x44)) status.Add("CHRG");                               // Charging
        if (Flag(BatteryPage, 0x45)) status.Add("DISCHRG");                            // Discharging
        if (Flag(BatteryPage, 0x42)) status.Add("LB");                                 // BelowRemainingCapacityLimit
        if (Flag(BatteryPage, 0x4B)) status.Add("RB");                                 // NeedReplacement
        if (Flag(PowerPage, 0x65)) status.Add("OVER");                                 // Overload
        if (status.Count > 0) vars["ups.status"] = string.Join(' ', status);
        return vars;
    }

    /// <summary>Every HID UPS plugged in (top-level collection Power Device / UPS), with all its feature values.</summary>
    public static List<HidUps> ReadAll()
    {
        var found = new List<HidUps>();
        foreach (var path in DevicePaths())
        {
            try { if (Read(path) is { } ups) found.Add(ups with { Name = found.Count == 0 ? "ups" : $"ups{found.Count + 1}" }); }
            catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception) { }
        }
        return found;
    }

    private static HidUps? Read(string path)
    {
        using var handle = Open(path);
        if (handle is null) return null;
        if (!HidD_GetPreparsedData(handle, out var preparsed)) return null;
        try
        {
            var caps = new byte[64];
            if (HidP_GetCaps(preparsed, caps) != Success) return null;
            var usage = BinaryPrimitives.ReadUInt16LittleEndian(caps);
            var page = BinaryPrimitives.ReadUInt16LittleEndian(caps.AsSpan(2));
            if (page != PowerPage || usage != 0x04) return null; // top-level collection: UPS
            var featureLength = BinaryPrimitives.ReadUInt16LittleEndian(caps.AsSpan(8));
            var nodes = Nodes(preparsed, BinaryPrimitives.ReadUInt16LittleEndian(caps.AsSpan(44)));
            var valueCount = BinaryPrimitives.ReadUInt16LittleEndian(caps.AsSpan(60));
            var buttonCount = BinaryPrimitives.ReadUInt16LittleEndian(caps.AsSpan(58));

            var reports = new Dictionary<byte, byte[]?>();
            byte[]? Report(byte id)
            {
                if (reports.TryGetValue(id, out var r)) return r;
                var buffer = new byte[featureLength];
                buffer[0] = id;
                return reports[id] = HidD_GetFeature(handle, buffer, buffer.Length) ? buffer : null;
            }

            var values = new List<HidValue>();
            if (valueCount > 0)
            {
                var all = new byte[72 * valueCount];
                var n = valueCount;
                if (HidP_GetValueCaps(Feature, all, ref n, preparsed) == Success)
                    for (var i = 0; i < n; i++)
                    {
                        var c = all.AsSpan(72 * i, 72);
                        var (capPage, report, link) = (U16(c, 0), c[2], U16(c, 6));
                        if (c[12] != 0) continue; // a usage range: not used by power devices for single values
                        var capUsage = U16(c, 56);
                        var exponent = (int)(BinaryPrimitives.ReadUInt32LittleEndian(c[32..]) & 0xF);
                        if (exponent > 7) exponent -= 16; // a signed nibble
                        // HID units are CGS-based: a volt is 10^7 of the unit 0x00F0D121 (g·cm²·s⁻³·A⁻¹), a watt or
                        // volt-ampere 10^7 of 0x0000D121 — so exponent 7 there means plain volts and watts.
                        if (BinaryPrimitives.ReadUInt32LittleEndian(c[36..]) is 0x00F0D121 or 0x0000D121) exponent -= 7;
                        var logicalMin = BinaryPrimitives.ReadInt32LittleEndian(c[40..]);
                        if (Report(report) is not { } data) continue;
                        if (HidP_GetUsageValue(Feature, capPage, link, capUsage, out var raw, preparsed, data, data.Length) != Success) continue;
                        // Negative logical ranges are sign-extended; the rest are as reported.
                        var bits = U16(c, 18);
                        double value = logicalMin < 0 && bits is > 0 and < 32 && (raw & (1u << (bits - 1))) != 0 ? (int)(raw | (~0u << bits)) : raw;
                        values.Add(new HidValue(capPage, capUsage, PathOf(nodes, link), value * Math.Pow(10, exponent)));
                    }
            }
            if (buttonCount > 0)
            {
                var all = new byte[72 * buttonCount];
                var n = buttonCount;
                if (HidP_GetButtonCaps(Feature, all, ref n, preparsed) == Success)
                    for (var i = 0; i < n; i++)
                    {
                        var c = all.AsSpan(72 * i, 72);
                        var (capPage, report, link) = (U16(c, 0), c[2], U16(c, 6));
                        if (c[12] != 0) continue;
                        var capUsage = U16(c, 56);
                        if (Report(report) is not { } data) continue;
                        var on = new ushort[64];
                        var count = (uint)on.Length;
                        if (HidP_GetUsages(Feature, capPage, link, on, ref count, preparsed, data, data.Length) != Success) continue;
                        values.Add(new HidValue(capPage, capUsage, PathOf(nodes, link), on.AsSpan(0, (int)count).Contains(capUsage) ? 1 : 0));
                    }
            }
            return new HidUps("", Text(handle, HidD_GetManufacturerString), Text(handle, HidD_GetProductString), Text(handle, HidD_GetSerialNumberString), values);
        }
        finally { HidD_FreePreparsedData(preparsed); }
    }

    private static ushort U16(ReadOnlySpan<byte> s, int at) => BinaryPrimitives.ReadUInt16LittleEndian(s[at..]);

    // Link collection nodes: (usage, page, parent) — the collections a value sits in tell input voltage from output.
    private static (ushort Usage, ushort Page, ushort Parent)[] Nodes(nint preparsed, ushort count)
    {
        if (count == 0) return [];
        var size = IntPtr.Size == 8 ? 24 : 20;
        var buffer = new byte[size * count];
        var n = (uint)count;
        if (HidP_GetLinkCollectionNodes(buffer, ref n, preparsed) != Success) return [];
        return Enumerable.Range(0, (int)n).Select(i => (U16(buffer, size * i), U16(buffer, size * i + 2), U16(buffer, size * i + 4))).ToArray();
    }

    private static IReadOnlyList<(ushort, ushort)> PathOf((ushort Usage, ushort Page, ushort Parent)[] nodes, ushort link)
    {
        var path = new List<(ushort, ushort)>();
        for (var i = 0; link < nodes.Length && i < 16; i++)
        {
            path.Add((nodes[link].Page, nodes[link].Usage));
            if (link == 0) break;
            link = nodes[link].Parent;
        }
        return path;
    }

    private static string Text(SafeFileHandle handle, Func<SafeFileHandle, char[], int, bool> get)
    {
        var buffer = new char[128];
        return get(handle, buffer, buffer.Length * 2) ? new string(buffer).TrimEnd('\0').Trim() : "";
    }

    // Shared, no access rights needed beyond reading feature reports; the battery driver keeps its own handle.
    private static SafeFileHandle? Open(string path)
    {
        foreach (var access in new[] { 0xC0000000u /* GENERIC_READ | GENERIC_WRITE */, 0u })
        {
            var h = CreateFileW(path, access, 3 /* FILE_SHARE_READ | WRITE */, 0, 3 /* OPEN_EXISTING */, 0, 0);
            if (!h.IsInvalid) return h;
            h.Dispose();
        }
        return null;
    }

    private static IEnumerable<string> DevicePaths()
    {
        HidD_GetHidGuid(out var guid);
        var set = SetupDiGetClassDevsW(ref guid, null, 0, 0x2 | 0x10 /* DIGCF_PRESENT | DIGCF_DEVICEINTERFACE */);
        if (set == -1) yield break;
        try
        {
            var data = new SpDeviceInterfaceData { cbSize = Marshal.SizeOf<SpDeviceInterfaceData>() };
            for (uint i = 0; SetupDiEnumDeviceInterfaces(set, 0, ref guid, i, ref data); i++)
            {
                SetupDiGetDeviceInterfaceDetailW(set, ref data, 0, 0, out var needed, 0);
                if (needed <= 0) continue;
                var detail = Marshal.AllocHGlobal(needed);
                try
                {
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6); // cbSize of SP_DEVICE_INTERFACE_DETAIL_DATA_W
                    if (SetupDiGetDeviceInterfaceDetailW(set, ref data, detail, needed, out _, 0) && Marshal.PtrToStringUni(detail + 4) is { } p)
                        yield return p;
                }
                finally { Marshal.FreeHGlobal(detail); }
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
    }

    // ---- interop ----

    private const int Success = 0x00110000; // HIDP_STATUS_SUCCESS
    private const int Feature = 2;          // HidP_Feature

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDeviceInterfaceData { public int cbSize; public Guid InterfaceClassGuid; public int Flags; public nint Reserved; }

    [DllImport("hid.dll")] private static extern void HidD_GetHidGuid(out Guid guid);
    [DllImport("hid.dll")] private static extern bool HidD_GetPreparsedData(SafeFileHandle device, out nint preparsed);
    [DllImport("hid.dll")] private static extern bool HidD_FreePreparsedData(nint preparsed);
    [DllImport("hid.dll")] private static extern bool HidD_GetFeature(SafeFileHandle device, byte[] buffer, int length);
    [DllImport("hid.dll", CharSet = CharSet.Unicode)] private static extern bool HidD_GetManufacturerString(SafeFileHandle device, char[] buffer, int length);
    [DllImport("hid.dll", CharSet = CharSet.Unicode)] private static extern bool HidD_GetProductString(SafeFileHandle device, char[] buffer, int length);
    [DllImport("hid.dll", CharSet = CharSet.Unicode)] private static extern bool HidD_GetSerialNumberString(SafeFileHandle device, char[] buffer, int length);
    [DllImport("hid.dll")] private static extern int HidP_GetCaps(nint preparsed, byte[] caps);
    [DllImport("hid.dll")] private static extern int HidP_GetValueCaps(int type, byte[] caps, ref ushort length, nint preparsed);
    [DllImport("hid.dll")] private static extern int HidP_GetButtonCaps(int type, byte[] caps, ref ushort length, nint preparsed);
    [DllImport("hid.dll")] private static extern int HidP_GetLinkCollectionNodes(byte[] nodes, ref uint length, nint preparsed);
    [DllImport("hid.dll")]
    private static extern int HidP_GetUsageValue(int type, ushort page, ushort link, ushort usage, out uint value, nint preparsed, byte[] report, int length);
    [DllImport("hid.dll")]
    private static extern int HidP_GetUsages(int type, ushort page, ushort link, ushort[] usages, ref uint length, nint preparsed, byte[] report, int reportLength);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, nint security, uint disposition, uint flags, nint template);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode)]
    private static extern nint SetupDiGetClassDevsW(ref Guid guid, string? enumerator, nint parent, uint flags);
    [DllImport("setupapi.dll")]
    private static extern bool SetupDiEnumDeviceInterfaces(nint set, nint info, ref Guid guid, uint index, ref SpDeviceInterfaceData data);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode)]
    private static extern bool SetupDiGetDeviceInterfaceDetailW(nint set, ref SpDeviceInterfaceData data, nint detail, int size, out int required, nint info);
    [DllImport("setupapi.dll")] private static extern bool SetupDiDestroyDeviceInfoList(nint set);
}
