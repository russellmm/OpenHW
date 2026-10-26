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
        // No global-menu export over D-Bus (GNOME doesn't show one; OpenHW's menu bar is in the window).
        var x11 = new X11PlatformOptions { UseDBusMenu = false };
        // Settings → "Draw the window in software": no OpenGL context, so no GPU driver mappings in this process.
        if (settings.SoftwareRendering) x11.RenderingMode = [X11RenderingMode.Software];
        builder = builder.With(x11);
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
        var command = new List<string> { exe };
        if (Path.GetFileNameWithoutExtension(exe) == "dotnet") command.Add(Environment.GetCommandLineArgs()[0]);
        command.AddRange(args.Where(a => a != "--tray"));
        // Started as a systemd user service (e.g. RTXinVM's `rtxgpu --app openhw`, which runs OpenHW in its GPU
        // slice): when this process exits, systemd stops the unit and kills everything left in it, a child included.
        // So a small waiter outside the unit starts it again, same name and slice, once this one has finished.
        var start = SystemdRestart(File.ReadAllText("/proc/self/cgroup"), command, Description) is { } waiter
            ? new System.Diagnostics.ProcessStartInfo("systemd-run", waiter)
            : new System.Diagnostics.ProcessStartInfo(command[0], command.Skip(1));
        start.UseShellExecute = false;
        try { System.Diagnostics.Process.Start(start)?.Dispose(); }
        catch (System.ComponentModel.Win32Exception ex) { Console.Error.WriteLine($"openhw: could not restart ({ex.Message})"); }
    }

    private static string Description(string unit)
    {
        try
        {
            var show = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("systemctl",
                ["--user", "show", "--property=Description", "--value", unit]) { RedirectStandardOutput = true, UseShellExecute = false });
            var text = show?.StandardOutput.ReadToEnd().Trim() ?? "";
            show?.WaitForExit(2000);
            return text.Length > 0 ? text : "OpenHW";
        }
        catch (System.ComponentModel.Win32Exception) { return "OpenHW"; }
    }

    // The waiter gives up after 20 s, in case the unit never stops.
    private const string Waiter = """
        u=$1 s=$2 d=$3; shift 3; n=0
        while systemctl --user is-active --quiet "$u" && [ $n -lt 100 ]; do sleep 0.2; n=$((n+1)); done
        exec systemd-run --user --quiet --collect --unit="$u" --slice="$s" --description="$d" -- "$@"
        """;

    /// <summary>
    /// systemd-run arguments for a waiter that restarts this process's user service once it has stopped; null when
    /// OpenHW isn't a user service (a desktop launch runs in a scope, where a child outlives its parent).
    /// </summary>
    public static IReadOnlyList<string>? SystemdRestart(string cgroup, IReadOnlyList<string> command, Func<string, string> description)
    {
        // "0::/user.slice/user-1000.slice/user@1000.service/rtxgpu.slice/rtxgpu-openhw.service"
        var path = cgroup.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(l => l.StartsWith("0::", StringComparison.Ordinal))?[3..];
        var parts = path?.Split('/', StringSplitOptions.RemoveEmptyEntries) ?? [];
        var manager = Array.FindIndex(parts, p => p.StartsWith("user@", StringComparison.Ordinal) && p.EndsWith(".service", StringComparison.Ordinal));
        if (manager < 0 || parts.Length < manager + 3 || !parts[^1].EndsWith(".service", StringComparison.Ordinal)) return null;
        var (unit, slice) = (parts[^1], parts[^2]);
        if (!slice.EndsWith(".slice", StringComparison.Ordinal)) return null;
        return ["--user", "--quiet", "--collect", $"--slice={slice}", "--", "/bin/sh", "-c", Waiter, "sh", unit, slice, description(unit), .. command];
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
