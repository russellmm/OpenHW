using System.ComponentModel;
using System.Globalization;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;

namespace OpenHW.Hardware.Windows;

/// <summary>
/// UEFI variables on Windows, in the OpenHW Helper service: GetFirmwareEnvironmentVariableExW needs
/// SeSystemEnvironmentPrivilege, which LocalSystem holds but has to switch on. Read-only — nothing here writes a
/// variable. Linux's efivarfs lets any user read them, so the service's <c>uefivar</c> command does the same for the
/// OpenHW Users allowed on its pipe.
/// </summary>
[SupportedOSPlatform("windows")]
public static partial class WindowsUefi
{
    private const int ErrorInsufficientBuffer = 122, ErrorEnvvarNotFound = 203;
    private const int MaxSize = 1 << 20;
    private static readonly object Gate = new();
    private static bool _privilege;

    /// <summary>The variable's data, or null when it doesn't exist; IOException for anything else (no privilege, not UEFI).</summary>
    public static byte[]? Read(string name, Guid guid)
    {
        lock (Gate)
        {
            if (!_privilege) _privilege = EnablePrivilege("SeSystemEnvironmentPrivilege");
            var size = 4096;
            while (true)
            {
                var buffer = new byte[size];
                var read = GetFirmwareEnvironmentVariableExW(name, guid.ToString("B"), buffer, (uint)size, out _);
                if (read > 0) return buffer[..(int)read];
                var error = Marshal.GetLastWin32Error();
                if (error == ErrorEnvvarNotFound) return null;
                if (error == ErrorInsufficientBuffer && size < MaxSize) { size *= 4; continue; }
                if (error == 0) return []; // an empty variable
                throw new IOException(new Win32Exception(error).Message);
            }
        }
    }

    /// <summary>A UEFI variable name as BIOS Setup uses them: letters, digits, '_' and '-' only (no paths, no spaces).</summary>
    public static bool ValidName(string name) => NamePattern().IsMatch(name);

    [GeneratedRegex("^[A-Za-z0-9_\\-]{1,128}$")]
    private static partial Regex NamePattern();

    private static bool EnablePrivilege(string privilege)
    {
        if (!OpenProcessToken(GetCurrentProcess(), 0x0020 | 0x0008, out var token)) return false; // ADJUST_PRIVILEGES | QUERY
        try
        {
            if (!LookupPrivilegeValueW(null, privilege, out var luid)) return false;
            var state = new TokenPrivileges { Count = 1, Luid = luid, Attributes = 0x2 }; // SE_PRIVILEGE_ENABLED
            return AdjustTokenPrivileges(token, false, ref state, 0, 0, 0) && Marshal.GetLastWin32Error() == 0;
        }
        finally { CloseHandle(token); }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct TokenPrivileges { public uint Count; public long Luid; public uint Attributes; }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint GetFirmwareEnvironmentVariableExW(string name, string guid, byte[] buffer, uint size, out uint attributes);
    [DllImport("kernel32.dll")] private static extern nint GetCurrentProcess();
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(nint process, uint access, out nint token);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool LookupPrivilegeValueW(string? system, string name, out long luid);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(nint token, bool disableAll, ref TokenPrivileges state, uint length, nint previous, nint returnLength);
}

/// <summary>
/// The app's side on Windows: each variable asked of the OpenHW Helper service (<c>uefivar &lt;name&gt; &lt;guid&gt;</c>,
/// reply "ok &lt;base64&gt;" or "ok none"). Read-only, like <see cref="LinuxFirmwareVariables"/>.
/// </summary>
public sealed class HelperFirmwareVariables(Func<string, string>? request = null) : IFirmwareVariables
{
    private const string GlobalGuid = "8be4df61-93ca-11d2-aa0d-00e098032b8c";
    private readonly Func<string, string> _request = request ?? (line => HelperClient.Request(line, receiveTimeoutMs: 5000, maxReplyBytes: 3 << 20));

    /// <summary>True when the service answers the command (BootOrder exists on every UEFI machine).</summary>
    public bool Available
    {
        get
        {
            try { return _request($"uefivar BootOrder {GlobalGuid}").StartsWith("ok ", StringComparison.Ordinal); }
            catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or TimeoutException) { return false; }
        }
    }

    public byte[]? Read(string name, string guid)
    {
        if (name.Contains(' ') || guid.Contains(' ')) return null;
        try
        {
            var reply = _request($"uefivar {name} {guid}");
            if (!reply.StartsWith("ok ", StringComparison.Ordinal)) return null;
            var data = reply[3..].Trim();
            return data == "none" ? null : Convert.FromBase64String(data);
        }
        catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or TimeoutException or FormatException) { return null; }
    }

    public IReadOnlyList<string> BootEntries()
    {
        var order = Read("BootOrder", GlobalGuid);
        if (order is null) return [];
        var names = new List<string>();
        for (var i = 0; i + 1 < order.Length; i += 2)
        {
            var number = order[i] | order[i + 1] << 8;
            if (Read($"Boot{number:X4}", GlobalGuid) is { } entry && LinuxFirmwareVariables.Describe(entry) is { Length: > 0 } text) names.Add(text);
        }
        return names;
    }
}

/// <summary>What BIOS Setup's per-machine rows need, read the Windows way (Linux reads /proc and /sys).</summary>
[SupportedOSPlatform("windows")]
public static class WindowsPlatformFacts
{
    public static string? CpuBrand() => Registry(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString") as string is { } s ? s.Trim() : null;

    /// <summary>The rated clock Windows reports for CPU 0 (MHz), the closest it has to amd-pstate's nominal frequency.</summary>
    public static long? BaseMhz() => Registry(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0", "~MHz") is int mhz && mhz > 0 ? mhz : null;

    public static int Cores() => WindowsSystemEngine.Topology().Count;

    /// <summary>L3 caches = CCDs on Zen.</summary>
    public static unsafe int Ccds()
    {
        const int RelationCache = 2;
        uint length = 0;
        Win32.GetLogicalProcessorInformationEx(RelationCache, null, ref length);
        if (length == 0) return 0;
        var buffer = new byte[length];
        var count = 0;
        fixed (byte* start = buffer)
        {
            if (!Win32.GetLogicalProcessorInformationEx(RelationCache, start, ref length)) return 0;
            for (var p = start; p < start + length;)
            {
                if (*(p + 8) == 3) count++; // CACHE_RELATIONSHIP.Level
                p += *(uint*)(p + 4);
            }
        }
        return count;
    }

    /// <summary>MAC addresses (AA-BB-…) of the adapters whose description contains <paramref name="model"/>, in Windows' order.</summary>
    public static IReadOnlyList<string> Macs(string model) => NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.Description.Contains(model, StringComparison.OrdinalIgnoreCase))
        .OrderBy(n => n.Description, StringComparer.Ordinal)
        .Select(n => string.Join("-", n.GetPhysicalAddress().GetAddressBytes().Select(b => b.ToString("X2", CultureInfo.InvariantCulture))))
        .Where(m => m.Length > 0).ToList();

    private static object? Registry(string key, string value)
    {
        using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(key);
        return k?.GetValue(value);
    }
}
