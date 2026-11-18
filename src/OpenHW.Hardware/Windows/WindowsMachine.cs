using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace OpenHW.Hardware.Windows;

/// <summary>
/// Board and CPU identity on Windows, from the registry copy of the SMBIOS tables Windows keeps at boot
/// (HKLM\HARDWARE\DESCRIPTION\System) — what Linux reads from /sys/class/dmi/id and /proc/cpuinfo.
/// </summary>
[SupportedOSPlatform("windows")]
public static class WindowsMachine
{
    private const string Bios = @"HARDWARE\DESCRIPTION\System\BIOS";

    public static string BoardVendor => Read(Bios, "BaseBoardManufacturer");
    public static string BoardName => Read(Bios, "BaseBoardProduct");
    public static string BiosVersion => Read(Bios, "BIOSVersion");
    public static string SystemVendor => Read(Bios, "SystemManufacturer");
    public static string SystemName => Read(Bios, "SystemProductName");

    /// <summary>"AMD Ryzen Threadripper PRO 9975WX 32-Cores", trimmed (Windows pads the brand string).</summary>
    public static string CpuBrand => Read(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString");

    /// <summary>
    /// The SMBIOS system UUID, lowercase as Linux's /sys/class/dmi/id/product_uuid shows it: the machine identity
    /// that fan journals and saved originals carry, so a file copied between the two systems is recognised.
    /// </summary>
    public static string Uuid
    {
        get
        {
            try
            {
                using var searcher = new System.Management.ManagementObjectSearcher("SELECT UUID FROM Win32_ComputerSystemProduct");
                var uuid = searcher.Get().Cast<System.Management.ManagementBaseObject>().Select(o => o["UUID"] as string).FirstOrDefault();
                return string.IsNullOrWhiteSpace(uuid) ? "unavailable" : uuid.Trim().ToLowerInvariant();
            }
            catch (Exception ex) when (ex is System.Management.ManagementException or UnauthorizedAccessException) { return "unavailable"; }
        }
    }

    private static string Read(string key, string value)
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(key);
            return (k?.GetValue(value) as string)?.Trim() ?? "";
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return ""; }
    }
}

/// <summary>Native library names that differ by platform: NVML is libnvidia-ml.so.1 on Linux and nvml.dll on Windows.</summary>
internal static class NativeLibraries
{
#pragma warning disable CA2255 // a library needs the resolver before its first NVML call
    [System.Runtime.CompilerServices.ModuleInitializer]
#pragma warning restore CA2255
    internal static void Register()
    {
        if (!OperatingSystem.IsWindows()) return;
        // One resolver per assembly: NVML under its Linux name, and PawnIO's library from where its installer puts it
        // (Program Files\PawnIO isn't on the DLL search path).
        NativeLibrary.SetDllImportResolver(typeof(NativeLibraries).Assembly, (name, assembly, path) => name switch
        {
            "libnvidia-ml.so.1" when NativeLibrary.TryLoad("nvml.dll", assembly, DllImportSearchPath.System32, out var nvml) => nvml,
            "PawnIOLib" when NativeLibrary.TryLoad(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PawnIO", "PawnIOLib.dll"), out var pawnio) => pawnio,
            _ => 0,
        });
    }
}
