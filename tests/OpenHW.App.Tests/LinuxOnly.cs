using Xunit;
using Xunit.Sdk;

namespace OpenHW.App.Tests;

/// <summary>
/// An [AvaloniaFact] that runs only on Linux: tests of Linux-only features (GPU Lending, the card filter, systemd) or
/// that need this machine's Linux-only sensors (k10temp's Tctl, HSMP's FCLK), which Windows doesn't read until the
/// kernel-access phase of docs/windows-port.md. Elsewhere it is reported as skipped, with the reason.
/// </summary>
[XunitTestCaseDiscoverer("Avalonia.Headless.XUnit.AvaloniaUIFactDiscoverer", "Avalonia.Headless.XUnit")]
public sealed class LinuxAvaloniaFactAttribute : FactAttribute
{
    public LinuxAvaloniaFactAttribute()
    {
        if (!OperatingSystem.IsLinux()) Skip = "Linux only (Linux sensors or features)";
    }
}

/// <summary>A plain [Fact] that runs only on Linux.</summary>
public sealed class LinuxFactAttribute : FactAttribute
{
    public LinuxFactAttribute()
    {
        if (!OperatingSystem.IsLinux()) Skip = "Linux only (Linux sensors or features)";
    }
}
