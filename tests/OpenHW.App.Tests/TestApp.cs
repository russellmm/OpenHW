using Avalonia;
using Avalonia.Headless;

[assembly: AvaloniaTestApplication(typeof(OpenHW.App.Tests.TestApp))]

// Tests here point XDG_CONFIG_HOME (process-wide) at their own temp folders, so they must not overlap.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]

namespace OpenHW.App.Tests;

/// <summary>Real Skia rendering without a display, so tests can inspect and capture what the window draws.</summary>
public static class TestApp
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseSkia()
        .WithInterFont()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
