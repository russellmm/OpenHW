using System.Text.Json;
using OpenHW.Hardware;

namespace OpenHW.App.Power;

/// <summary>
/// The desktop user's side of lending the card: openhw-helper (root) does the handoff, but the user's own services
/// holding the card (e.g. a fan-control window run as a systemd user service) are this user's to stop and start.
/// Before asking for the lend they're stopped, with what's needed to start them again — a transient unit (made by
/// systemd-run) disappears when stopped, so its command, slice and description are kept; after the lend they're
/// started again (they then see the other cards), and when the card comes back they're restarted so they list it.
/// Apps the owner ticked "Close and reopen when lending" for (e.g. OpenFan's window, once it runs as a plain app) are
/// closed the same way — gracefully, by stopping their app scope — and reopened with the same command in a scope
/// named like the original, so the list entry that covers them still does.
/// The record lives in ~/.config/openhw/lend-paused.json, so this survives OpenHW restarting in between.
/// </summary>
public sealed class LendCoordinator(Func<string, string>? request = null, Func<string, IReadOnlyList<string>, (int Code, string Output)>? run = null,
    string? statePath = null, Action<IReadOnlyList<string>>? launch = null, string procRoot = "/proc", string cgroupRoot = "/sys/fs/cgroup")
{
    private readonly Func<string, string> _request = request ?? (line => HelperClient.Request(line, receiveTimeoutMs: 8000, maxReplyBytes: GpuWire.MaxReplyBytes));
    private readonly Func<string, IReadOnlyList<string>, (int, string)> _run = run ?? Run;
    private readonly Action<IReadOnlyList<string>> _launch = launch ?? Launch;
    private readonly string _statePath = statePath ?? Path.Combine(Settings.AppSettings.ConfigDir, "lend-paused.json");

    /// <summary>A paused user service and how to start it again.</summary>
    public sealed record PausedUnit(string Unit, string[] Argv, string Slice, string Description, bool HasFile);
    /// <summary>A closed app: its scope now (updated when reopened), command and working folder.</summary>
    public sealed record PausedApp(string Scope, string[] Argv, string Cwd);
    public sealed record Record(int Sequence, string Phase, List<PausedUnit> Units, List<PausedApp>? Apps = null);

    public Record? Current => Load();

    /// <summary>
    /// Stops the user's services holding the card and closes the ticked apps (<paramref name="reopen"/>: list
    /// patterns), then asks the helper to lend it. Any other app holding the card stops the lend before anything.
    /// </summary>
    public void Lend(LendStatus status, IReadOnlyCollection<string>? reopen = null)
    {
        reopen ??= [];
        var blocking = status.Apps.FirstOrDefault(a => a.Pattern is null || !reopen.Contains(a.Pattern));
        if (blocking is not null) throw new IOException($"{blocking.Program} ({blocking.Pid}) has the card open: close it, or tick \"Close and reopen when lending\"");
        var paused = new List<PausedUnit>();
        var apps = new List<PausedApp>();
        void Undo() { StartAll(paused); Reopen(apps); }
        foreach (var unit in status.UserServices)
        {
            var described = Describe(unit);
            var (code, output) = _run("systemctl", ["--user", "stop", unit]);
            if (code != 0) { Undo(); throw new IOException($"stopping {unit}: {output}"); }
            paused.Add(described);
        }
        foreach (var scope in status.Apps.Select(a => a.Cgroup).Distinct())
        {
            var app = DescribeApp(scope);
            var (code, output) = _run("systemctl", ["--user", "stop", app.Scope]); // SIGTERM, and waits for it to exit
            if (code != 0) { Undo(); throw new IOException($"closing {app.Scope}: {output}"); }
            apps.Add(app);
        }
        Save(new Record(status.Sequence, "lending", paused, apps));
        try { _request("lend"); }
        catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException)
        {
            Undo();
            Clear();
            throw;
        }
    }

    /// <summary>Follows the helper: services back once the lend is done (or failed), restarted when the card returns.</summary>
    public void Follow(LendStatus status)
    {
        if (Load() is not { } record || status.Sequence <= record.Sequence) return;
        switch (status.Stage, record.Phase)
        {
            case ("lent", "lending"):
                StartAll(record.Units);                     // they see the other cards while the VM has this one
                Save(record with { Sequence = status.Sequence, Phase = "lent", Apps = Reopen(record.Apps ?? []) });
                break;
            case ("failed", "lending"):
                StartAll(record.Units);
                Reopen(record.Apps ?? []);
                Clear();
                break;
            case ("returned", _):
                foreach (var u in record.Units) _run("systemctl", ["--user", "stop", u.Unit]);
                StartAll(record.Units);                     // a fresh start lists the returned card
                foreach (var a in record.Apps ?? []) _run("systemctl", ["--user", "stop", a.Scope]);
                Reopen(record.Apps ?? []);
                Clear();
                break;
            default:
                Save(record with { Sequence = status.Sequence });
                break;
        }
    }

    private PausedUnit Describe(string unit)
    {
        var (_, output) = _run("systemctl", ["--user", "show", unit, "--property=ExecStart,Slice,Description,FragmentPath,Transient"]);
        var props = output.Split('\n').Select(l => l.Split('=', 2)).Where(p => p.Length == 2).ToDictionary(p => p[0], p => p[1]);
        // ExecStart={ path=/usr/bin/openfan ; argv[]=/usr/bin/openfan ; ignore_errors=no ; … }
        var exec = props.GetValueOrDefault("ExecStart", "");
        var argv = exec.Split(" ; ").FirstOrDefault(f => f.StartsWith("argv[]=", StringComparison.Ordinal))?[7..].Split(' ', StringSplitOptions.RemoveEmptyEntries) ?? [];
        // A transient unit (systemd-run) reports a FragmentPath under /run/…/transient, but it's gone once stopped:
        // only a real unit file can simply be started again.
        return new PausedUnit(unit, argv, props.GetValueOrDefault("Slice", "app.slice"), props.GetValueOrDefault("Description", unit),
            props.GetValueOrDefault("FragmentPath", "").Length > 0 && props.GetValueOrDefault("Transient", "no") != "yes");
    }

    // The app's own process is the scope's first (lowest) PID: its command line and working folder start it again.
    private PausedApp DescribeApp(string cgroup)
    {
        var scope = cgroup.Split('/')[^1];
        var pids = File.ReadAllLines(Path.Combine(cgroupRoot, cgroup.TrimStart('/'), "cgroup.procs"))
            .Select(l => int.TryParse(l, out var p) ? p : int.MaxValue).Where(p => p != int.MaxValue).Order().ToList();
        if (pids.Count == 0) throw new IOException($"{scope} has no process");
        var proc = Path.Combine(procRoot, pids[0].ToString(System.Globalization.CultureInfo.InvariantCulture));
        var argv = File.ReadAllText(Path.Combine(proc, "cmdline")).Split('\0', StringSplitOptions.RemoveEmptyEntries);
        var cwd = new FileInfo(Path.Combine(proc, "cwd")).LinkTarget ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return new PausedApp(scope, argv, cwd);
    }

    // A scope named like the original ("app-gnome-openfan-12345.scope" → "app-gnome-openfan-<now>.scope"), so the list
    // entry covering it ("app-gnome-openfan-*.scope") still matches. systemd-run --scope runs the app in the foreground,
    // so it's launched and left running.
    private List<PausedApp> Reopen(IEnumerable<PausedApp> apps)
    {
        var reopened = new List<PausedApp>();
        foreach (var a in apps)
        {
            var stamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
            var scope = System.Text.RegularExpressions.Regex.IsMatch(a.Scope, @"-\d+\.scope$")
                ? System.Text.RegularExpressions.Regex.Replace(a.Scope, @"-\d+\.scope$", $"-{stamp}.scope")
                : a.Scope[..^".scope".Length] + $"-{stamp}.scope";
            _launch(["systemd-run", "--user", "--quiet", "--collect", "--scope", "--slice=app.slice", $"--unit={scope}",
                     $"--working-directory={a.Cwd}", "--", .. a.Argv]);
            reopened.Add(a with { Scope = scope });
            Thread.Sleep(5); // distinct stamps for two apps
        }
        return reopened;
    }

    private static void Launch(IReadOnlyList<string> argv)
    {
        var start = new System.Diagnostics.ProcessStartInfo(argv[0], argv.Skip(1)) { UseShellExecute = false };
        System.Diagnostics.Process.Start(start)?.Dispose(); // left running: the app lives in its own scope
    }

    private void StartAll(IEnumerable<PausedUnit> units)
    {
        foreach (var u in units)
        {
            if (u.HasFile) _run("systemctl", ["--user", "start", u.Unit]);
            else if (u.Argv.Length > 0)
                _run("systemd-run", ["--user", "--quiet", "--collect", $"--unit={u.Unit}", $"--slice={u.Slice}", $"--description={u.Description}", "--", .. u.Argv]);
        }
    }

    private Record? Load()
    {
        try { return JsonSerializer.Deserialize<Record>(File.ReadAllText(_statePath)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    private void Save(Record record)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
        File.WriteAllText(_statePath, JsonSerializer.Serialize(record));
    }

    private void Clear()
    {
        try { File.Delete(_statePath); } catch (IOException) { }
    }

    private static (int, string) Run(string program, IReadOnlyList<string> args)
    {
        var start = new System.Diagnostics.ProcessStartInfo(program, args) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        using var p = System.Diagnostics.Process.Start(start)!;
        var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit(30_000);
        return (p.ExitCode, output.Trim());
    }
}
