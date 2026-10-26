using System.Text.Json;
using OpenHW.Hardware;

namespace OpenHW.App.Power;

/// <summary>
/// The desktop user's side of lending the card: openhw-helper (root) does the handoff, but the user's own services
/// holding the card (e.g. a fan-control window run as a systemd user service) are this user's to stop and start.
/// Before asking for the lend they're stopped, with what's needed to start them again — a transient unit (made by
/// systemd-run) disappears when stopped, so its command, slice and description are kept; after the lend they're
/// started again (they then see the other cards), and when the card comes back they're restarted so they list it.
/// The record lives in ~/.config/openhw/lend-paused.json, so this survives OpenHW restarting in between.
/// </summary>
public sealed class LendCoordinator(Func<string, string>? request = null, Func<string, IReadOnlyList<string>, (int Code, string Output)>? run = null, string? statePath = null)
{
    private readonly Func<string, string> _request = request ?? (line => HelperClient.Request(line, receiveTimeoutMs: 8000, maxReplyBytes: GpuWire.MaxReplyBytes));
    private readonly Func<string, IReadOnlyList<string>, (int, string)> _run = run ?? Run;
    private readonly string _statePath = statePath ?? Path.Combine(Settings.AppSettings.ConfigDir, "lend-paused.json");

    /// <summary>A paused user service and how to start it again.</summary>
    public sealed record PausedUnit(string Unit, string[] Argv, string Slice, string Description, bool HasFile);
    public sealed record Record(int Sequence, string Phase, List<PausedUnit> Units);

    public Record? Current => Load();

    /// <summary>Stops the user's services holding the card, then asks the helper to lend it.</summary>
    public void Lend(LendStatus status)
    {
        var paused = new List<PausedUnit>();
        foreach (var unit in status.UserServices)
        {
            paused.Add(Describe(unit));
            var (code, output) = _run("systemctl", ["--user", "stop", unit]);
            if (code != 0) { StartAll(paused.Take(paused.Count - 1)); throw new IOException($"stopping {unit}: {output}"); }
        }
        Save(new Record(status.Sequence, "lending", paused));
        try { _request("lend"); }
        catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException)
        {
            StartAll(paused);
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
                Save(record with { Sequence = status.Sequence, Phase = "lent" });
                break;
            case ("failed", "lending"):
                StartAll(record.Units);
                Clear();
                break;
            case ("returned", _):
                foreach (var u in record.Units) _run("systemctl", ["--user", "stop", u.Unit]);
                StartAll(record.Units);                     // a fresh start lists the returned card
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
