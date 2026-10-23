using Avalonia;

namespace OpenHW.App;

internal static class Program
{
    /// <summary>This process's claim to be the one OpenHW; null under tests and --dump.</summary>
    public static Settings.SingleInstance? Instance { get; private set; }

    /// <summary>--tray: start with only the tray icons (used by the login autostart entry).</summary>
    public static bool StartInTray { get; private set; }

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains("--dump")) return Dump();
        Instance = Settings.SingleInstance.Claim();
        if (Instance is null) return 0; // the running OpenHW was asked to show its window
        StartInTray = args.Contains("--tray");
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    /// <summary>Prints the sensor tree as the window shows it (two samples, so usage deltas exist).</summary>
    private static int Dump()
    {
        using var hub = new Sensors.SensorHub();
        hub.Sample();
        Thread.Sleep(1000);
        hub.Sample();
        var items = hub.Inventory.ToDictionary(i => i.Id);
        void Print(Sensors.SensorView view, string indent)
        {
            var value = hub.Get(view.Id).Current is { } v ? Sensors.SensorPresentation.Scale(items[view.Id], v) : (double?)null;
            Console.WriteLine($"{indent}{view.Name,-48} {Sensors.SensorPresentation.Format(value, view.Unit)}");
            foreach (var child in view.Children) Print(child, indent + "    ");
        }
        foreach (var group in Sensors.SensorPresentation.Build(hub.Inventory, hub.Children, hub.Machine))
        {
            Console.WriteLine(group.Title);
            foreach (var view in group.Sensors) Print(view, "  ");
        }
        return 0;
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();
}
