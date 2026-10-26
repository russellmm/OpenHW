using System.Diagnostics;

namespace OpenHW.Hardware;

/// <summary>
/// Root self-test of the card filter's kernel side, run with <c>openhw-helper --selftest-card-filter &lt;pci&gt;</c>.
/// Everything happens in a throwaway cgroup tree beside user.slice (/sys/fs/cgroup/openhw-filter-test), with its own
/// pinned program and list, so the desktop — and RTXinVM's guard, which sits on user.slice — is never affected.
/// Each check moves a short-lived shell into a test cgroup and lets it open the card's /dev/nvidiaN.
/// </summary>
public static class CardFilterSelfTest
{
    private const string Root = "/sys/fs/cgroup/openhw-filter-test";

    public static int Run(string pci, TextWriter output)
    {
        var nodes = CardDevices.Nodes(pci);
        if (nodes.Count == 0) { output.WriteLine($"FAIL {pci}: no NVIDIA device nodes (wrong address, or the card is lent?)"); return 1; }
        var node = nodes[0].Path;
        var other = CardDevices.List().Where(c => c.Pci != pci).SelectMany(c => CardDevices.Nodes(c.Pci)).FirstOrDefault()?.Path;
        var work = Directory.CreateTempSubdirectory("openhw-filter-selftest").FullName;
        var failures = 0;
        void Check(string what, bool ok) { output.WriteLine($"{(ok ? "PASS" : "FAIL")} {what}"); if (!ok) failures++; }

        Cleanup();
        Directory.CreateDirectory(Root);
        var visible = Mkdir("app-visible-1.scope");
        var hidden = Mkdir("app-hidden-1.scope");
        var desktop = Mkdir("session.slice");
        var desktopShell = Mkdir("session.slice/org.gnome.Shell@test.service");
        var filter = new CardFilter(Path.Combine(work, "card-filter.json"), Path.Combine(work, "now.json"), "/sys/fs/bpf/openhw-test", Root);
        try
        {
            filter.SelectCard(pci);
            filter.SetHidden("app-hidden-*.scope", now: true, boot: false, hide: true, name: "test");
            filter.SetEnabled(true);
            Check("filter attached to the test cgroup", filter.Status().Attached);
            Check($"visible app opens {node}", Opens(visible, node));
            Check($"hidden app is refused {node}", !Opens(hidden, node));
            Check($"GNOME desktop (session.slice) is refused {node}", !Opens(desktopShell, node));
            if (other is not null) Check($"hidden app still opens another card's {other}", Opens(hidden, other));

            // A cgroup created after the filter is on: classified at once (inotify), before a program could start.
            var sw = Stopwatch.StartNew();
            var fresh = Mkdir("app-fresh-1.scope");
            var freshHidden = Mkdir("app-hidden-2.scope");
            Thread.Sleep(50);
            Check($"new visible app opens {node} 50 ms after its cgroup appeared", Opens(fresh, node));
            Check("new hidden app is refused", !Opens(freshHidden, node));
            output.WriteLine($"     (classification within {sw.ElapsedMilliseconds} ms incl. the shell)");

            // Unhide is immediate for anything that opens the card afterwards.
            filter.SetHidden("app-hidden-*.scope", now: true, boot: false, hide: false, name: null);
            Check("unhidden app opens the card", Opens(hidden, node));

            // A helper restart (RTXinVM's switch stops it) keeps the pinned filter in force.
            filter.Dispose();
            filter = new CardFilter(Path.Combine(work, "card-filter.json"), Path.Combine(work, "now.json"), "/sys/fs/bpf/openhw-test", Root);
            filter.SetHidden("session.slice", now: true, boot: false, hide: true, name: null);
            filter.Start();
            Check("after a helper restart the filter is still attached", filter.Status().Attached);
            Check("…and still refuses the desktop", !Opens(desktopShell, node));

            filter.SetEnabled(false);
            Check("turned off: the desktop opens the card again", Opens(desktopShell, node));
        }
        catch (Exception ex)
        {
            output.WriteLine($"FAIL {ex.GetType().Name}: {ex.Message}");
            failures++;
        }
        finally
        {
            try { filter.SetEnabled(false); } catch (Exception) { }
            filter.Dispose();
            try { Cleanup(); }
            finally { Directory.Delete(work, recursive: true); }
        }
        output.WriteLine(failures == 0 ? "ALL PASS" : $"{failures} FAILED");
        return failures == 0 ? 0 : 1;
    }

    private static string Mkdir(string relative)
    {
        var path = Path.Combine(Root, relative);
        Directory.CreateDirectory(path);
        return path;
    }

    // A shell joins the cgroup, then tries to open the node read-only (no ioctl: nothing is done with the card).
    private static bool Opens(string cgroup, string node)
    {
        var start = new ProcessStartInfo("/bin/sh", ["-c", $"echo $$ > '{cgroup}/cgroup.procs' && exec 3< '{node}' && echo OPENED"])
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        using var p = Process.Start(start)!;
        var text = p.StandardOutput.ReadToEnd();
        p.WaitForExit(5000);
        return text.Contains("OPENED", StringComparison.Ordinal);
    }

    private static void Cleanup()
    {
        if (!Directory.Exists(Root)) return;
        // Deepest first; the test shells have exited, so every cgroup is empty.
        foreach (var dir in Directory.EnumerateDirectories(Root, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length))
            try { Directory.Delete(dir); } catch (IOException) { }
        try { Directory.Delete(Root); } catch (IOException) { }
        CardFilter.RemovePins("/sys/fs/bpf/openhw-test");
    }
}
