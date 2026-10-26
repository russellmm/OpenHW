using System.Runtime.InteropServices;
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
        Gen0Budget(args);
        if (args.Contains("--dump")) return Dump();
        Instance = Settings.SingleInstance.Claim();
        if (Instance is null) return 0; // the running OpenHW was asked to show its window
        StartInTray = args.Contains("--tray");
        var settings = Settings.AppSettings.Load();
        Settings.WindowScale.Apply(settings.WindowScale);
        var builder = BuildAvaloniaApp();
        // Settings → "Draw the window in software": no OpenGL context, so no GPU driver mappings in this process.
        if (settings.SoftwareRendering)
            builder = builder.With(new X11PlatformOptions { RenderingMode = [X11RenderingMode.Software] });
        var exit = builder.StartWithClassicDesktopLifetime(args);
        if (RestartRequested) Restart(args);
        return exit;
    }

    /// <summary>Set before shutting down to start OpenHW again (Settings → Restart now) once this one has let go.</summary>
    public static bool RestartRequested { get; set; }

    // Runs after the lifetime's Exit handlers, so the single-instance socket is gone and the new process claims it.
    // The window opens: whoever asked for the restart is looking at OpenHW, even if this run started --tray.
    private static void Restart(string[] args)
    {
        if (Environment.ProcessPath is not { } exe) return;
        var start = new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = false };
        if (Path.GetFileNameWithoutExtension(exe) == "dotnet") start.ArgumentList.Add(Environment.GetCommandLineArgs()[0]);
        foreach (var arg in args.Where(a => a != "--tray")) start.ArgumentList.Add(arg);
        try { System.Diagnostics.Process.Start(start)?.Dispose(); }
        catch (System.ComponentModel.Win32Exception ex) { Console.Error.WriteLine($"openhw: could not restart ({ex.Message})"); }
    }

    /// <summary>
    /// The GC sizes its gen0 allocation budget from the L3 cache, which on a 128 MB-L3 Threadripper lets the heap
    /// run tens of MB ahead of each collection. A 4 MB budget saved ~19 MB resident (measured 2026-10-24) at no CPU
    /// cost, but the runtime reads it only from the environment (runtimeconfig's System.GC.Gen0Size is ignored), so
    /// the app re-executes itself once with it set. Setting DOTNET_GCgen0size yourself (0 = the GC's own choice)
    /// skips this. Only the native launcher re-executes; `dotnet openhw.dll` runs as it is.
    /// </summary>
    private static void Gen0Budget(string[] args)
    {
        const string Variable = "DOTNET_GCgen0size";
        if (Environment.GetEnvironmentVariable(Variable) is not null) return;
        if (Environment.ProcessPath is not { } exe || Path.GetFileName(exe) != "openhw") return;
        Environment.SetEnvironmentVariable(Variable, "0x400000");
        var argv = new[] { exe }.Concat(args).Append(null).ToArray();
        var envp = Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
            .Select(e => $"{e.Key}={e.Value}").Append(null).ToArray();
        execve(exe, argv, envp);
        // execve only returns on failure; carry on with the GC's default budget.
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int execve(string path, string?[] argv, string?[] envp);

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
            Console.WriteLine($"{indent}{view.Name,-48} {Sensors.SensorPresentation.Format(items[view.Id], value)}");
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
