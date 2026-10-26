using System.Text.RegularExpressions;
using OpenHW.Hardware;

namespace OpenHW.App.Power;

/// <summary>
/// One launch of an app can live in several app scopes. Google Chrome, started from the dock in
/// app-gnome-google\x2dchrome-37026.scope, moves its main process (37026) into its own app-com.google.Chrome-37026.scope,
/// and the zygotes it already forked — where its GPU process comes from — stay behind. Both hold the card, and closing
/// one doesn't close Chrome, so they're one app to ask about, to close and to reopen (found 2026-10-26).
/// Two scopes in the same app.slice are the same launch when one is named after a process it holds ("-37026.scope"
/// with 37026 in it) and the other has the same number, or has processes that process started. The app's own first
/// process is left out of the second test: it's started by whatever launched the app (OpenHW reopening it, or a
/// shell), which isn't part of it.
/// </summary>
public sealed partial class AppLaunches(string procRoot = "/proc", string cgroupRoot = "/sys/fs/cgroup")
{
    private sealed record Scope(string Cgroup, long? Number, IReadOnlyList<int> Pids, bool OwnsNumber);

    /// <summary>The scopes of <paramref name="cgroup"/>'s launch, the main one (the app's own process) first.</summary>
    public IReadOnlyList<string> Of(string cgroup)
    {
        var parent = cgroup[..Math.Max(cgroup.LastIndexOf('/'), 0)];
        var dir = Path.Combine(cgroupRoot, parent.TrimStart('/'));
        if (!cgroup.EndsWith(".scope", StringComparison.Ordinal) || !Directory.Exists(dir)) return [cgroup];
        var scopes = Directory.EnumerateDirectories(dir, "*.scope").Select(d => Read(parent + "/" + Path.GetFileName(d), d)).ToList();
        if (scopes.All(s => s.Cgroup != cgroup)) return [cgroup];
        var linked = new List<Scope> { scopes.First(s => s.Cgroup == cgroup) };
        for (var i = 0; i < linked.Count; i++)
            foreach (var other in scopes.Where(o => !linked.Contains(o) && SameLaunch(linked[i], o)))
                linked.Add(other);
        return linked.OrderBy(s => s.OwnsNumber ? 0 : 1).ThenBy(s => s.OwnsNumber ? s.Number : s.Pids.FirstOrDefault()).Select(s => s.Cgroup).ToList();
    }

    /// <summary>The app's own process in a scope: the one it's named after, else its first.</summary>
    public int? MainPid(string cgroup)
    {
        var s = Read(cgroup, Path.Combine(cgroupRoot, cgroup.TrimStart('/')));
        return s.OwnsNumber ? (int)s.Number!.Value : s.Pids.Count > 0 ? s.Pids[0] : null;
    }

    /// <summary>The list patterns that cover any of the launch's scopes.</summary>
    public static IEnumerable<string> Patterns(IEnumerable<string> launch) =>
        launch.Select(c => CardPatterns.Suggest(c, null)).OfType<string>().Distinct(StringComparer.Ordinal);

    private bool SameLaunch(Scope a, Scope b) => Links(a, b) || Links(b, a);

    // b is named after its own process, and a has that number too or holds processes it started.
    private bool Links(Scope a, Scope b) =>
        b.OwnsNumber && (a.Number == b.Number || a.Pids.Skip(1).Any(p => Parent(p) == b.Number));

    private Scope Read(string cgroup, string dir)
    {
        var pids = new List<int>();
        try { pids.AddRange(File.ReadAllLines(Path.Combine(dir, "cgroup.procs")).Select(l => int.TryParse(l, out var p) ? p : -1).Where(p => p > 0)); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        pids.Sort();
        var m = Number().Match(cgroup);
        long? number = m.Success && long.TryParse(m.Groups[1].Value, out var n) ? n : null;
        return new Scope(cgroup, number, pids, number is { } k && pids.Contains((int)Math.Min(k, int.MaxValue)));
    }

    // /proc/<pid>/stat: "pid (comm) state ppid …" — the comm can hold spaces and parentheses, so read after the last ')'.
    private long? Parent(int pid)
    {
        try
        {
            var stat = File.ReadAllText(Path.Combine(procRoot, pid.ToString(System.Globalization.CultureInfo.InvariantCulture), "stat"));
            var fields = stat[(stat.LastIndexOf(')') + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return fields.Length > 1 && long.TryParse(fields[1], out var ppid) ? ppid : null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    [GeneratedRegex(@"-(\d+)\.scope$")]
    private static partial Regex Number();
}
