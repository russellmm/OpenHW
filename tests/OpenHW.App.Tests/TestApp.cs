using Avalonia;
using Avalonia.Headless;

[assembly: AvaloniaTestApplication(typeof(OpenHW.App.Tests.TestApp))]

namespace OpenHW.App.Tests;

/// <summary>Real Skia rendering without a display, so tests can inspect and capture what the window draws.</summary>
public static class TestApp
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseSkia()
        .WithInterFont()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
