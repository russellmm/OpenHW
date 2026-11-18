using System.Runtime.Versioning;
using Microsoft.Win32;

namespace OpenHW.App.Settings;

/// <summary>
/// Start at login on Windows: an "OpenHW" value under HKCU\Software\Microsoft\Windows\CurrentVersion\Run — the
/// per-user, no-elevation twin of the freedesktop autostart file. The registry value is the truth (the user may
/// remove it in Task Manager → Startup apps, which disables it under StartupApproved instead).
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WindowsAutostart
{
    // Tests point these at a scratch key, so they never touch the real login items.
    internal static string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    internal static string Approved = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string Name = "OpenHW";

    public static bool Enabled
    {
        get
        {
            try
            {
                using var run = Registry.CurrentUser.OpenSubKey(RunKey);
                if (run?.GetValue(Name) is not string) return false;
                // Task Manager's "Disabled": the first byte of the StartupApproved entry is odd (3) instead of 2.
                using var approved = Registry.CurrentUser.OpenSubKey(Approved);
                return approved?.GetValue(Name) is not byte[] { Length: > 0 } state || (state[0] & 1) == 0;
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return false; }
        }
    }

    public static void Set(bool enabled, bool inTray)
    {
        using var run = Registry.CurrentUser.CreateSubKey(RunKey);
        if (!enabled)
        {
            run.DeleteValue(Name, throwOnMissingValue: false);
            return;
        }
        var exe = Environment.ProcessPath ?? "openhw.exe";
        run.SetValue(Name, $"\"{exe}\"" + (inTray ? " --tray" : ""));
        // Turned on in OpenHW after being disabled in Task Manager: enable it there too, or Windows still skips it.
        using var approved = Registry.CurrentUser.OpenSubKey(Approved, writable: true);
        if (approved?.GetValue(Name) is byte[] { Length: > 0 } state && (state[0] & 1) != 0) approved.DeleteValue(Name, false);
    }
}
