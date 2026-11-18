using Xunit;

namespace OpenHW.Tests;

/// <summary>
/// A [Fact] that runs only on Linux: tests of Linux-only features (GPU Lending, the card filter, boot power limits) or
/// readers whose fake sysfs trees use names Windows can't create ("AMDI0010:01", "intel-rapl:0"). Elsewhere it is
/// reported as skipped, with the reason.
/// </summary>
public sealed class LinuxFactAttribute : FactAttribute
{
    public LinuxFactAttribute()
    {
        if (!OperatingSystem.IsLinux()) Skip = "Linux only (Linux readers or features)";
    }
}

/// <summary>A [Theory] that runs only on Linux.</summary>
public sealed class LinuxTheoryAttribute : TheoryAttribute
{
    public LinuxTheoryAttribute()
    {
        if (!OperatingSystem.IsLinux()) Skip = "Linux only (Linux readers or features)";
    }
}

/// <summary>A [Fact] that runs only on Windows (the Windows helper's named pipe, Win32 readers).</summary>
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "Windows only";
    }
}
