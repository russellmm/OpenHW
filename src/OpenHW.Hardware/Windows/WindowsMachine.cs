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
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void Register()
    {
        if (!OperatingSystem.IsWindows()) return;
        NativeLibrary.SetDllImportResolver(typeof(NativeLibraries).Assembly, (name, assembly, path) =>
            name == "libnvidia-ml.so.1" && NativeLibrary.TryLoad("nvml.dll", assembly, DllImportSearchPath.System32, out var handle)
                ? handle
                : 0);
    }
}
