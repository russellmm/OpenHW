using System.Security.AccessControl;
using System.Security.Principal;

namespace OpenHW.Hardware;

/// <summary>
/// Where OpenHW keeps its files and how it keeps them private, on Linux and on Windows. On Linux a private file is mode
/// 0600; on Windows it gets its own access list (the creating user, SYSTEM and Administrators, nothing inherited), so a
/// file in ProgramData isn't readable by every user the way the folder's inherited rules would allow.
/// </summary>
public static class OsFiles
{
    /// <summary>
    /// The per-user configuration root: $XDG_CONFIG_HOME when set (tests point it at a temporary folder), else
    /// ~/.config on Linux and %APPDATA% on Windows. OpenHW's own files go in its "openhw" folder below this.
    /// </summary>
    public static string ConfigHome =>
        Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } config ? config
        : OperatingSystem.IsWindows() ? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");

    /// <summary>The per-user OpenHW folder: ~/.config/openhw, %APPDATA%\openhw.</summary>
    public static string ConfigDir => Path.Combine(ConfigHome, "openhw");

    /// <summary>The configuration both operating systems share, on Windows (C:) — the same folder seen from either side.</summary>
    public const string SharedConfigWindows = @"C:\StandAlone\OpenHW\configs";
    public const string SharedConfigLinux = "/mnt/DriveC/StandAlone/OpenHW/configs";

    /// <summary>
    /// The folder for settings that must match on Linux and Windows (the fan configuration: the BMC keeps whatever the
    /// last OS wrote, so both have to work from one file). $OPENHW_SHARED_CONFIG overrides it ("off" = none, as tests
    /// use); otherwise the folder above, when OpenHW's StandAlone folder is there (on Linux: C: is mounted). Null =
    /// not available: callers keep their files in <see cref="ConfigDir"/>.
    /// </summary>
    public static string? SharedConfigDir
    {
        get
        {
            if (Environment.GetEnvironmentVariable("OPENHW_SHARED_CONFIG") is { Length: > 0 } set) return set == "off" ? null : set;
            var dir = OperatingSystem.IsWindows() ? SharedConfigWindows : SharedConfigLinux;
            return Directory.Exists(Path.GetDirectoryName(dir)) ? dir : null;
        }
    }

    /// <summary>
    /// Machine-wide state the helper keeps (fan journal, saved originals, BMC login): /etc/openhw on Linux,
    /// %ProgramData%\OpenHW on Windows.
    /// </summary>
    public static string HelperStateDir => OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "OpenHW")
        : "/etc/openhw";

    /// <summary>Makes an existing file readable and writable by its owner only (0600 / owner + SYSTEM + Administrators).</summary>
    public static void OwnerOnly(string path)
    {
        if (OperatingSystem.IsWindows()) new FileInfo(path).SetAccessControl(PrivateSecurity());
        else File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    /// <summary>Sets a Unix file mode; Windows has none, and its files keep the folder's inherited access list.</summary>
    public static void SetMode(string path, UnixFileMode mode)
    {
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, mode);
    }

    /// <summary>Creates (or truncates) a file for writing that only its owner can read: the temp file of an atomic save.</summary>
    public static FileStream CreatePrivate(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            if (File.Exists(path)) File.Delete(path); // the access list is set only when the file is created
            return new FileInfo(path).Create(FileMode.CreateNew, FileSystemRights.Write | FileSystemRights.ReadData,
                FileShare.None, 4096, FileOptions.None, PrivateSecurity());
        }
        var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return stream;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static FileSecurity PrivateSecurity()
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        var owner = WindowsIdentity.GetCurrent().User!;
        foreach (var sid in new[] { owner, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null) }.Distinct())
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, AccessControlType.Allow));
        return security;
    }
}
