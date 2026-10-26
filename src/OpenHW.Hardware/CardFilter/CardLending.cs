using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace OpenHW.Hardware;

/// <summary>Runs an external program (virsh, nvidia-smi, systemctl); tests substitute a fake.</summary>
public interface ICommandRunner
{
    (int Code, string Output) Run(string program, IReadOnlyList<string> args, TimeSpan timeout);
}

public sealed class ProcessRunner : ICommandRunner
{
    public (int Code, string Output) Run(string program, IReadOnlyList<string> args, TimeSpan timeout)
    {
        var start = new ProcessStartInfo(program, args)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, UseShellExecute = false,
        };
        // The helper's sandbox hides /root: give libvirt's client somewhere to keep its (unused) config and cache.
        foreach (var v in new[] { "HOME", "XDG_CONFIG_HOME", "XDG_CACHE_HOME", "XDG_RUNTIME_DIR" }) start.Environment[v] = "/run/openhw";
        start.Environment["LC_ALL"] = "C";
        using var p = Process.Start(start)!;
        p.StandardInput.Close();
        var output = p.StandardOutput.ReadToEndAsync();
        var error = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(timeout))
        {
            try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            return (124, $"{Path.GetFileName(program)} timed out");
        }
        return (p.ExitCode, (output.Result + error.Result).Trim());
    }
}

/// <summary>A VM on the system libvirt connection, and whether the lent card is assigned to it.</summary>
public sealed record LendVm(string Name, string State, bool HasCard);

/// <summary>What the page shows: the VM, the stage of a lend or return, and what stands in the way of lending.</summary>
public sealed record LendStatus(string? Vm, string VmState, bool Lent, string Stage, string? Error, int Sequence,
    IReadOnlyList<string> Problems, IReadOnlyList<string> SystemServices, IReadOnlyList<string> UserServices);

/// <summary>
/// Root side of lending the card to a VM (ROADMAP "GPU lending to a VM", step 4), after RTXinVM's switch: the libvirt
/// hook still does the hardware handoff (saving and restoring power limit and persistence, VFIO binding); this does
/// what has to happen around it — check nothing stands in the way, pause the system services holding the card,
/// release the helper's own NVML handles, turn persistence off so nvidia-persistenced lets go, wait until the card
/// is free, put back the values the hook should record, start the VM, then restart what was paused. The desktop
/// user's services holding the card (e.g. a fan-control window) are paused by OpenHW's window, which runs as that
/// user, before it asks for the lend. Return: a normal guest shutdown (never forced); the card coming back — however
/// the VM was shut down — is noticed and the helper's GPUs and the paused services come back.
/// </summary>
public sealed partial class CardLending : IDisposable
{
    private const string Connection = "qemu:///system";
    private static readonly TimeSpan Short = TimeSpan.FromSeconds(30), VmStart = TimeSpan.FromSeconds(90);
    private static readonly string[] NeverPaused = ["openhw-helper.service", "nvidia-persistenced.service"];

    private readonly CardFilter _filter;
    private readonly Action _suspendGpus, _resumeGpus;
    private readonly ICommandRunner _run;
    private readonly string _sysRoot, _handoffRecord, _virsh, _smi, _systemctl;
    private readonly TimeSpan _freeTimeout, _shutdownTimeout;
    private readonly object _gate = new();
    private string _stage = "idle";
    private string? _error;
    private int _sequence;
    private List<string> _paused = [];
    private Task? _work;
    private readonly Timer _watch;
    private readonly string _statePath;
    private sealed record Saved(string Stage, List<string> Paused);

    public CardLending(CardFilter filter, Action suspendGpus, Action resumeGpus, ICommandRunner? runner = null, string sysRoot = "/sys",
        string handoffRecord = "/run/windows-gpu-handoff/state.json", TimeSpan? freeTimeout = null, TimeSpan? shutdownTimeout = null,
        string virsh = "/usr/bin/virsh", string smi = "/usr/bin/nvidia-smi", string systemctl = "/usr/bin/systemctl",
        string statePath = "/run/openhw/lend.json")
    {
        _statePath = statePath;
        // A helper restart while the card is lent (RTXinVM's guard restarts helpers on return) keeps what was paused.
        try
        {
            if (System.Text.Json.JsonSerializer.Deserialize<Saved>(File.ReadAllText(statePath)) is { } saved)
                (_stage, _paused) = (saved.Stage, saved.Paused);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { }
        (_filter, _suspendGpus, _resumeGpus, _run, _sysRoot, _handoffRecord) = (filter, suspendGpus, resumeGpus, runner ?? new ProcessRunner(), sysRoot, handoffRecord);
        (_virsh, _smi, _systemctl) = (virsh, smi, systemctl);
        _freeTimeout = freeTimeout ?? TimeSpan.FromSeconds(25);
        _shutdownTimeout = shutdownTimeout ?? TimeSpan.FromMinutes(5); // Windows updates can take a while
        _watch = new Timer(_ => WatchForReturn(), null, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3));
    }

    // ── commands ────────────────────────────────────────────────────────────────────────────────────────────

    public IReadOnlyList<LendVm> Vms()
    {
        var (card, _) = _filter.Target;
        var (code, output) = Virsh(["list", "--all", "--name"]);
        if (code != 0) throw new IOException("virsh: " + output);
        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(name => new LendVm(name, VmState(name), card is not null && HasCard(name, card))).ToList();
    }

    public void SelectVm(string? vm)
    {
        if (vm is not null && (!VmName().IsMatch(vm) || Vms().All(v => v.Name != vm || !v.HasCard)))
            throw new ArgumentException("no such VM with the card assigned");
        _filter.SelectVm(vm);
    }

    public LendStatus Status()
    {
        var (card, vm) = _filter.Target;
        var state = vm is null ? "" : VmState(vm);
        var holders = card is null ? [] : _filter.HoldersOf(LendNodes(card));
        var (problems, system, user) = Assess(card, vm, state, holders);
        lock (_gate)
            return new LendStatus(vm, state, state == "running" && Driver(card) == "vfio-pci", _stage, _error, _sequence, problems, system, user);
    }

    /// <summary>Starts lending in the background; the page follows it through <see cref="Status"/>.</summary>
    public void Lend()
    {
        lock (_gate)
        {
            if (_work is { IsCompleted: false }) throw new ArgumentException("a lend or return is already under way");
            var s = Status();
            if (s.Problems.Count > 0) throw new ArgumentException(s.Problems[0]);
            (_stage, _error) = ("starting", null);
            _work = Task.Run(LendSteps);
        }
    }

    /// <summary>Asks the VM for a normal shutdown (through its guest agent, else ACPI); never forces it off.</summary>
    public void Return()
    {
        lock (_gate)
        {
            if (_work is { IsCompleted: false }) throw new ArgumentException("a lend or return is already under way");
            var (_, vm) = _filter.Target;
            if (vm is null || VmState(vm) != "running") throw new ArgumentException("the VM isn't running");
            (_stage, _error) = ("asking the VM to shut down", null);
            _work = Task.Run(() => ReturnSteps(vm));
        }
    }

    // ── lend ──────────────────────────────────────────────────────────────────────────────────────────────────

    private void LendSteps()
    {
        var (card, vm) = _filter.Target;
        string? limit = null;
        bool? persistence = null;
        var released = false;
        try
        {
            Stage("pausing system services");
            foreach (var unit in Assess(card, vm, VmState(vm!), _filter.HoldersOf(LendNodes(card!))).System)
            {
                Check(Systemctl(["stop", unit]), $"stopping {unit}");
                lock (_gate) { _paused.Add(unit); Persist(); }
            }
            Stage("releasing OpenHW's own handles");
            _suspendGpus();
            released = true;
            limit = Smi(card!, ["--query-gpu=power.limit", "--format=csv,noheader,nounits"]);
            persistence = Smi(card!, ["--query-gpu=persistence_mode", "--format=csv,noheader"]) == "Enabled";
            Smi(card!, ["-pm", "0"]); // nvidia-persistenced lets go of the card too

            Stage("waiting until nothing has the card open");
            var deadline = DateTime.UtcNow + _freeTimeout;
            IReadOnlyList<CardHolder> holders;
            while ((holders = _filter.HoldersOf(LendNodes(card!))).Count > 0)
            {
                if (DateTime.UtcNow > deadline)
                    throw new IOException("still open by " + string.Join(", ", holders.Select(h => $"{h.Program} ({h.Pid})")));
                Thread.Sleep(500);
            }
            if (Smi(card!, ["--query-gpu=display_active", "--format=csv,noheader"]) != "Disabled")
                throw new IOException("the card reports an active display");

            // The hook records the values it finds and restores them on return: give it the real ones.
            Smi(card!, ["-pl", limit]);
            Smi(card!, ["-pm", persistence == true ? "1" : "0"]);
            Stage("starting " + vm);
            Check(Virsh(["start", vm!], VmStart), "starting " + vm);
            if (VmState(vm!) != "running" || Driver(card) != "vfio-pci") throw new IOException(vm + " didn't reach running with the card on VFIO");
            Finish("lent", null);
        }
        catch (Exception ex)
        {
            // Not lent: put the card's settings back (the hook does it itself once it has made its record).
            if (card is not null && VmState(vm!) == "shut off" && Driver(card) == "nvidia" && !File.Exists(_handoffRecord))
            {
                if (limit is not null) try { Smi(card, ["-pl", limit]); } catch (IOException) { }
                if (persistence is not null) try { Smi(card, ["-pm", persistence == true ? "1" : "0"]); } catch (IOException) { }
            }
            Finish("failed", ex.Message);
        }
        finally
        {
            if (released) _resumeGpus();
            RestartPaused(); // while Windows runs they see the other cards; on return they're restarted again
            lock (_gate) if (_stage == "failed") { _paused = []; Persist(); } // not lent: nothing to bring back later
        }
    }

    // ── return ───────────────────────────────────────────────────────────────────────────────────────────────

    private void ReturnSteps(string vm)
    {
        try
        {
            var (code, output) = Virsh(["shutdown", vm, "--mode", "agent"]);
            if (code != 0) Check(Virsh(["shutdown", vm]), "asking " + vm + " to shut down"); // no guest agent: ACPI
            Stage("waiting for " + vm + " to shut down");
            var deadline = DateTime.UtcNow + _shutdownTimeout;
            while (VmState(vm) != "shut off")
            {
                if (DateTime.UtcNow > deadline)
                    throw new IOException(vm + " is still shutting down (installing updates?); it was left running, never forced off");
                Thread.Sleep(2000);
            }
            Stage("waiting for the card to come back");
            WaitForReturn(TimeSpan.FromSeconds(30));
        }
        catch (Exception ex)
        {
            Finish("lent", ex.Message); // still lent: the card comes back whenever the VM does shut down
        }
    }

    // Every 3 s: if the card was lent and is back on NVIDIA with the hook done, bring everything back (also when the
    // VM was shut down from inside Windows).
    private void WatchForReturn()
    {
        lock (_gate)
        {
            if (_stage != "lent" || _work is { IsCompleted: false }) return;
            var (card, vm) = _filter.Target;
            if (card is null || vm is null || Driver(card) != "nvidia" || File.Exists(_handoffRecord) || VmState(vm) != "shut off") return;
            _work = Task.Run(() =>
            {
                try { WaitForReturn(TimeSpan.Zero); }
                catch (Exception ex) { Finish("failed", ex.Message); }
            });
        }
    }

    private void WaitForReturn(TimeSpan wait)
    {
        var (card, _) = _filter.Target;
        var deadline = DateTime.UtcNow + wait;
        while (Driver(card) != "nvidia" || File.Exists(_handoffRecord))
        {
            if (DateTime.UtcNow > deadline)
                throw new IOException("the VM is off but the card didn't come back to the NVIDIA driver; see the hook's log (nothing was forced)");
            Thread.Sleep(1000);
        }
        Stage("bringing the card back");
        _suspendGpus(); // a fresh NVML session sees the returned card
        _resumeGpus();
        foreach (var unit in _paused.ToList()) Systemctl(["restart", unit]); // so they list the card again
        Finish("returned", null);
    }

    // ── checks ───────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// What stands in the way (problems), and what a lend would pause: system services (by the helper) and the
    /// desktop user's services (by OpenHW's window, before it asks). Apps can't be paused: close or hide them.
    /// </summary>
    private (List<string> Problems, List<string> System, List<string> User) Assess(string? card, string? vm, string state, IReadOnlyList<CardHolder> holders)
    {
        var problems = new List<string>();
        var system = new List<string>();
        var user = new List<string>();
        if (card is null) { problems.Add("Choose the card to lend."); return (problems, system, user); }
        if (vm is null) problems.Add("Choose the VM to lend it to.");
        else if (state == "running") problems.Add($"{vm} is running.");
        else if (state != "shut off") problems.Add($"{vm} isn't fully off ({state}).");
        if (File.Exists(_handoffRecord)) problems.Add($"A previous handoff left its record ({_handoffRecord}); recover it first.");
        var driver = Driver(card);
        if (driver != "nvidia") problems.Add(driver == "vfio-pci" ? "The card is lent already." : $"The card isn't on the NVIDIA driver ({driver ?? "none"}).");
        var device = Path.Combine(_sysRoot, "bus", "pci", "devices", card);
        if (Directory.Exists(Path.Combine(device, "graphics")) && Directory.EnumerateDirectories(Path.Combine(device, "graphics"), "fb*").Any())
            problems.Add("Linux made a framebuffer on the card (a monitor was plugged in at boot): unplug it and reboot.");
        var drm = Path.Combine(device, "drm");
        if (Directory.Exists(drm) && Directory.EnumerateDirectories(drm, "card*").SelectMany(c => Directory.EnumerateDirectories(c, "card*-*"))
                .Any(output => SysFs.ReadText(Path.Combine(output, "status")) == "connected"))
            problems.Add("A monitor is plugged into the card: unplug it, and plug it in once the VM is running.");

        foreach (var h in holders)
        {
            var unit = h.Cgroup.Split('/').LastOrDefault() ?? "";
            if (h.Program == "openhw-helper" || NeverPaused.Contains(unit)) continue; // releases itself / -pm 0
            if (h.Cgroup.StartsWith("/system.slice/", StringComparison.Ordinal) && unit.EndsWith(".service", StringComparison.Ordinal))
            {
                if (!system.Contains(unit)) system.Add(unit);
            }
            else if (h.Cgroup.Contains("/user@", StringComparison.Ordinal) && unit.EndsWith(".service", StringComparison.Ordinal)
                     && !h.Cgroup.Contains("/session.slice/", StringComparison.Ordinal))
            {
                if (!user.Contains(unit)) user.Add(unit);
            }
            else problems.Add($"{h.Program} ({h.Pid}) has the card open: close it{(h.Pattern is null ? "" : ", or hide it and restart it")}.");
        }
        return (problems, system, user);
    }

    /// <summary>The card's device files and its audio function's (the hook refuses while either is open).</summary>
    private IReadOnlyList<CardNode> LendNodes(string card)
    {
        var nodes = _filter.NodesOf(card).ToList();
        var audio = Regex.Replace(card, @"\.\d$", ".1");
        var sound = Path.Combine(_sysRoot, "bus", "pci", "devices", audio, "sound");
        if (Directory.Exists(sound))
            foreach (var c in Directory.EnumerateDirectories(sound, "card*"))
            {
                var n = Path.GetFileName(c)[4..];
                foreach (var dev in Directory.Exists("/dev/snd") ? Directory.EnumerateFiles("/dev/snd").Where(f => Path.GetFileName(f).Contains("C" + n, StringComparison.Ordinal)) : [])
                    nodes.Add(new CardNode(dev, 116, 0));
            }
        return nodes;
    }

    private bool HasCard(string vm, string card)
    {
        var (code, xml) = Virsh(["dumpxml", vm]);
        if (code != 0) return false;
        var m = Regex.Match(card, @"^([0-9a-f]{4}):([0-9a-f]{2}):([0-9a-f]{2})\.([0-7])$");
        int Hex(int g) => Convert.ToInt32(m.Groups[g].Value, 16);
        try
        {
            return XDocument.Parse(xml).Descendants("hostdev").Elements("source").Elements("address").Any(a =>
                Number(a.Attribute("bus")?.Value) == Hex(2) && Number(a.Attribute("slot")?.Value) == Hex(3) && Number(a.Attribute("function")?.Value) == Hex(4));
        }
        catch (System.Xml.XmlException) { return false; }
    }

    private static int? Number(string? v) => v is null ? null : v.StartsWith("0x", StringComparison.Ordinal)
        ? Convert.ToInt32(v[2..], 16) : int.TryParse(v, out var d) ? d : null;

    private string VmState(string vm)
    {
        var (code, output) = Virsh(["domstate", vm]);
        return code == 0 ? output.Trim() : "unknown";
    }

    private string? Driver(string? card)
    {
        if (card is null) return null;
        var link = new DirectoryInfo(Path.Combine(_sysRoot, "bus", "pci", "devices", card, "driver"));
        return link.Exists ? (link.ResolveLinkTarget(true) ?? link).Name : null;
    }

    private (int, string) Virsh(IReadOnlyList<string> args, TimeSpan? timeout = null) => _run.Run(_virsh, ["-c", Connection, .. args], timeout ?? Short);
    private (int, string) Systemctl(IReadOnlyList<string> args) => _run.Run(_systemctl, args, Short);

    private string Smi(string card, IReadOnlyList<string> args)
    {
        var (code, output) = _run.Run(_smi, ["-i", card, .. args], Short);
        if (code != 0) throw new IOException("nvidia-smi " + string.Join(' ', args) + ": " + output);
        return output.Trim();
    }

    private static void Check((int Code, string Output) result, string what)
    {
        if (result.Code != 0) throw new IOException($"{what}: {result.Output}");
    }

    private void RestartPaused()
    {
        foreach (var unit in _paused.ToList()) _run.Run(_systemctl, ["start", unit], Short);
    }

    private void Stage(string stage) { lock (_gate) _stage = stage; }

    private void Finish(string stage, string? error)
    {
        lock (_gate)
        {
            (_stage, _error) = (stage, error);
            _sequence++;
            if (stage == "returned") _paused = [];
            Persist();
        }
    }

    private void Persist()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
            File.WriteAllText(_statePath, System.Text.Json.JsonSerializer.Serialize(new Saved(_stage, _paused)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    [GeneratedRegex(@"^[A-Za-z0-9_.+\-]{1,64}$", RegexOptions.CultureInvariant)] public static partial Regex VmName();

    public void Dispose() => _watch.Dispose();
}
