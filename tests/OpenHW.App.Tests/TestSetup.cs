using System.Runtime.CompilerServices;

namespace OpenHW.App.Tests;

internal static class TestSetup
{
    /// <summary>The Windows login items live in the registry, not in XDG_CONFIG_HOME: tests use a scratch key.</summary>
    public const string RunKey = @"Software\OpenHW-Tests\Run";

    /// <summary>The login entry OpenHW wrote: the autostart file on Linux, the Run value on Windows; null when there is none.</summary>
    public static string? AutostartEntry()
    {
        if (OperatingSystem.IsWindows())
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue("OpenHW") as string;
        }
        return File.Exists(OpenHW.App.Settings.AppSettings.AutostartPath) ? File.ReadAllText(OpenHW.App.Settings.AppSettings.AutostartPath) : null;
    }

    [ModuleInitializer]
    internal static void Initialize()
    {
        if (!OperatingSystem.IsWindows()) return;
        OpenHW.App.Settings.WindowsAutostart.RunKey = RunKey;
        OpenHW.App.Settings.WindowsAutostart.Approved = @"Software\OpenHW-Tests\StartupApproved";
    }
}
