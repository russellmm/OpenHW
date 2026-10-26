using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OpenHW.Hardware;

/// <summary>One entry of the owner's list: programs launched under cgroups matching <see cref="Pattern"/>.</summary>
public sealed class CardFilterEntry
{
    public required string Pattern { get; set; }
    public string Name { get; set; } = "";
    /// <summary>Hidden from the moment the helper starts at boot, before the login screen.</summary>
    public bool HideAtBoot { get; set; }
}

/// <summary>/etc/openhw/card-filter.json: which card is lent, whether the filter is on, and the list.</summary>
public sealed class CardFilterSettings
{
    /// <summary>PCI address of the card to keep away from listed programs ("0000:e1:00.0"), or null for none.</summary>
    public string? Card { get; set; }
    /// <summary>The libvirt VM (system connection) the card is lent to; it must have the card assigned as a hostdev.</summary>
    public string? Vm { get; set; }
    public bool Enabled { get; set; }
    public List<CardFilterEntry> Entries { get; set; } = [];

    /// <summary>The starting list for a newly chosen card: the desktop, the login screen and OpenHW's own window.</summary>
    public static List<CardFilterEntry> Defaults(int? gdmUid) =>
    [
        new() { Pattern = "session.slice", Name = "GNOME desktop", HideAtBoot = true },
        .. gdmUid is { } uid ? [new CardFilterEntry { Pattern = $"user-{uid}.slice", Name = "Login screen", HideAtBoot = true }] : Array.Empty<CardFilterEntry>(),
        new() { Pattern = "app-*openhw*.scope", Name = "OpenHW window", HideAtBoot = true },
    ];
}

/// <summary>Pure rules: list patterns, verdicts, and the entry to suggest for a process.</summary>
public static partial class CardPatterns
{
    [GeneratedRegex(@"^[A-Za-z0-9@._:\-*]{1,128}\.(scope|service|slice)$", RegexOptions.CultureInvariant)]
    private static partial Regex Valid();

    /// <summary>A cgroup unit name, possibly with * wildcards: what a client may send.</summary>
    public static bool IsValid(string pattern) => Valid().IsMatch(pattern) && pattern != "*.slice" && pattern != "*.scope" && pattern != "*.service";

    public static bool Matches(string pattern, string name) =>
        Regex.IsMatch(name, "^" + Regex.Escape(pattern).Replace(@"\*", ".*") + "$", RegexOptions.CultureInvariant);

    /// <summary>
    /// Hidden when any component of the cgroup path (relative to the filter's root) matches a hidden pattern:
    /// everything inside a hidden slice or unit is hidden with it.
    /// </summary>
    public static bool IsHidden(IEnumerable<string> components, IReadOnlyCollection<string> hidden) =>
        components.Any(c => hidden.Any(p => Matches(p, c)));

    /// <summary>
    /// What the filter records for a cgroup. The kernel program goes by the deepest cgroup with a verdict and refuses
    /// when there is none, so only leaf units (a .scope or .service with no child cgroups — where processes live) are
    /// marked allowed; slices and the user manager (user@N.service) get none. A new app or login therefore has no
    /// allowed ancestor and is refused until the helper has looked at it. Hidden applies at any level.
    /// </summary>
    public static uint? Verdict(IReadOnlyList<string> components, bool leaf, IReadOnlyCollection<string> hidden)
    {
        if (IsHidden(components, hidden)) return Bpf.Hidden;
        var name = components[^1];
        var unit = name.EndsWith(".scope", StringComparison.Ordinal) || name.EndsWith(".service", StringComparison.Ordinal);
        return leaf && unit && !name.StartsWith("user@", StringComparison.Ordinal) ? Bpf.Allowed : null;
    }

    // "app-com.google.Chrome-14950.scope" → "app-com.google.Chrome-*.scope"; "vte-spawn-<uuid>.scope" → "vte-spawn-*.scope";
    // "org.gnome.Shell@ubuntu.service" → "org.gnome.Shell@*.service".
    [GeneratedRegex(@"-(\d+|[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})(?=\.(scope|service|slice)$)", RegexOptions.CultureInvariant)]
    private static partial Regex Instance();
    [GeneratedRegex(@"@[^.]*(?=\.(service|scope)$)", RegexOptions.CultureInvariant)]
    private static partial Regex Template();

    /// <summary>
    /// The list entry that covers a process, from its cgroup path relative to user.slice: the GNOME desktop for anything
    /// in a session.slice, the login screen for the gdm user's slice, otherwise its own app or terminal unit with the
    /// launch number made a wildcard. Null for a cgroup that can't be listed (e.g. a bare user-N.slice).
    /// </summary>
    public static string? Suggest(string relative, int? gdmUid)
    {
        var parts = relative.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (gdmUid is { } uid && parts.FirstOrDefault() == $"user-{uid}.slice") return $"user-{uid}.slice";
        if (parts.Contains("session.slice")) return "session.slice";
        var unit = parts.LastOrDefault(p => p.EndsWith(".scope", StringComparison.Ordinal) || p.EndsWith(".service", StringComparison.Ordinal));
        if (unit is null || unit.StartsWith("user@", StringComparison.Ordinal)) return null;
        return Template().Replace(Instance().Replace(unit, "-*"), "@*");
    }
}

/// <summary>An NVIDIA card the filter could lend, and its device nodes.</summary>
public sealed record CardNode(string Path, uint Major, uint Minor);
public sealed record NvidiaCard(string Pci, string Name);

public static class CardDevices
{
    public static IReadOnlyList<NvidiaCard> List(string procRoot = "/proc")
    {
        var dir = Path.Combine(procRoot, "driver", "nvidia", "gpus");
        if (!Directory.Exists(dir)) return [];
        return Directory.EnumerateDirectories(dir).Select(d =>
        {
            var model = File.ReadLines(Path.Combine(d, "information")).FirstOrDefault(l => l.StartsWith("Model:", StringComparison.Ordinal));
            return new NvidiaCard(Path.GetFileName(d).ToLowerInvariant(), model?[6..].Trim() ?? "");
        }).OrderBy(c => c.Pci, StringComparer.Ordinal).ToList();
    }

    /// <summary>The card's /dev/nvidiaN and DRM nodes while the NVIDIA driver owns it; empty otherwise (e.g. on VFIO).</summary>
    public static IReadOnlyList<CardNode> Nodes(string pci, string procRoot = "/proc", string sysRoot = "/sys")
    {
        var nodes = new List<CardNode>();
        var info = Path.Combine(procRoot, "driver", "nvidia", "gpus", pci, "information");
        if (!File.Exists(info)) return nodes;
        var minor = File.ReadLines(info).Where(l => l.StartsWith("Device Minor:", StringComparison.Ordinal))
            .Select(l => uint.Parse(l[13..].Trim(), CultureInfo.InvariantCulture)).FirstOrDefault(uint.MaxValue);
        if (minor == uint.MaxValue) return nodes;
        nodes.Add(new CardNode($"/dev/nvidia{minor}", 195, minor));
        var drm = Path.Combine(sysRoot, "bus", "pci", "devices", pci, "drm");
        if (Directory.Exists(drm))
            foreach (var d in Directory.EnumerateDirectories(drm).Order(StringComparer.Ordinal))
            {
                var name = Path.GetFileName(d);
                if (!name.StartsWith("card", StringComparison.Ordinal) && !name.StartsWith("renderD", StringComparison.Ordinal)) continue;
                var dev = SysFs.ReadText(Path.Combine(d, "dev")).Split(':');
                if (dev.Length == 2 && uint.TryParse(dev[0], out var ma) && uint.TryParse(dev[1], out var mi))
                    nodes.Add(new CardNode($"/dev/dri/{name}", ma, mi));
            }
        return nodes;
    }
}

/// <summary>A process with one of the card's nodes open.</summary>
public sealed record CardHolder(int Pid, string Program, string Cgroup, string? Pattern, bool Hidden);

/// <summary>Root side of the card filter: the eBPF program (<see cref="Bpf"/>), the list, and the classifier loop.</summary>
public sealed class CardFilter : IDisposable
{
    private readonly string _settingsPath, _runtimePath, _pinDir, _attachRoot, _procRoot, _sysRoot;
    private readonly object _gate = new();
    private CardFilterSettings _settings;
    private HashSet<string> _hiddenNow;
    private int _prog = -1, _devices = -1, _verdicts = -1;
    private IReadOnlyList<CardNode> _nodes = [];
    private Thread? _watcher;
    private volatile bool _stopping;
    private readonly int? _gdmUid;
    private readonly string _cgroupMount;

    public CardFilter(string settingsPath = "/etc/openhw/card-filter.json", string runtimePath = "/run/openhw/card-filter-now.json",
        string pinDir = "/sys/fs/bpf/openhw", string attachRoot = "/sys/fs/cgroup/user.slice", string procRoot = "/proc", string sysRoot = "/sys",
        string cgroupMount = "/sys/fs/cgroup")
    {
        _cgroupMount = cgroupMount;
        (_settingsPath, _runtimePath, _pinDir, _attachRoot, _procRoot, _sysRoot) = (settingsPath, runtimePath, pinDir, attachRoot, procRoot, sysRoot);
        _gdmUid = GdmUid();
        _settings = Load<CardFilterSettings>(_settingsPath) ?? new CardFilterSettings();
        // "Hide now" lasts until reboot: /run starts empty at boot, and then the list's "hide from boot" entries apply.
        _hiddenNow = Load<List<string>>(_runtimePath)?.ToHashSet(StringComparer.Ordinal)
                     ?? _settings.Entries.Where(e => e.HideAtBoot).Select(e => e.Pattern).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>Called at helper start: puts the filter in force if it is enabled (reusing the pinned program).</summary>
    public void Start(TimeSpan? waitForCard = null)
    {
        lock (_gate)
        {
            if (!_settings.Enabled || _settings.Card is not { } card) return;
            // At boot the helper runs before the login screen: let the card's nodes appear first (nvidia_drm creates
            // card/renderD a moment after the driver binds), so the filter knows what to refuse before GNOME starts.
            var deadline = DateTime.UtcNow + (waitForCard ?? TimeSpan.Zero);
            while (DateTime.UtcNow < deadline && CardStillComingUp(card)) Thread.Sleep(250);
            Engage();
        }
    }

    // On the PCI bus and not lent (VFIO), but its driver or any of its three nodes isn't there yet.
    private bool CardStillComingUp(string card)
    {
        var device = Path.Combine(_sysRoot, "bus", "pci", "devices", card);
        if (!Directory.Exists(device)) return false; // not in this machine: nothing to wait for
        var driver = new DirectoryInfo(Path.Combine(device, "driver"));
        if (driver.Exists && (driver.ResolveLinkTarget(true) ?? driver).Name == "vfio-pci") return false;
        return CardDevices.Nodes(card, _procRoot, _sysRoot).Count < 3;
    }

    // ── commands ───────────────────────────────────────────────────────────────────────────────────────────────

    public CardFilterStatus Status()
    {
        lock (_gate)
        {
            var nodes = _settings.Card is { } card ? CardDevices.Nodes(card, _procRoot, _sysRoot) : [];
            return new CardFilterStatus(_settings.Enabled, _prog >= 0 && IsAttached(), _settings.Card, CardDevices.List(_procRoot),
                nodes.Count > 0 ? nodes : _nodes,
                _settings.Entries.Select(e => new CardFilterEntryStatus(e.Pattern, e.Name, e.HideAtBoot, _hiddenNow.Contains(e.Pattern))).ToList(),
                Holders(nodes.Count > 0 ? nodes : _nodes));
        }
    }

    public string SelectCard(string? pci)
    {
        lock (_gate)
        {
            if (pci is not null && CardDevices.List(_procRoot).All(c => c.Pci != pci)) throw new ArgumentException("no such NVIDIA card");
            if (_settings.Enabled) throw new ArgumentException("turn the filter off before choosing another card");
            _settings.Card = pci;
            if (pci is not null && _settings.Entries.Count == 0)
            {
                _settings.Entries = CardFilterSettings.Defaults(_gdmUid);
                _hiddenNow = _settings.Entries.Where(e => e.HideAtBoot).Select(e => e.Pattern).ToHashSet(StringComparer.Ordinal);
            }
            Save();
            return pci ?? "none";
        }
    }

    public void SetHidden(string pattern, bool now, bool boot, bool hide, string? name)
    {
        if (!CardPatterns.IsValid(pattern)) throw new ArgumentException("invalid pattern");
        lock (_gate)
        {
            var entry = _settings.Entries.FirstOrDefault(e => e.Pattern == pattern);
            if (entry is null)
            {
                if (!hide) return;
                entry = new CardFilterEntry { Pattern = pattern, Name = name ?? "" };
                _settings.Entries.Add(entry);
            }
            if (name is not null) entry.Name = name;
            if (boot) entry.HideAtBoot = hide;
            if (now && hide) _hiddenNow.Add(pattern);
            if (now && !hide) _hiddenNow.Remove(pattern);
            Save();
            if (_prog >= 0) ClassifyAll();
        }
    }

    public void Forget(string pattern)
    {
        lock (_gate)
        {
            _settings.Entries.RemoveAll(e => e.Pattern == pattern);
            _hiddenNow.Remove(pattern);
            Save();
            if (_prog >= 0) ClassifyAll();
        }
    }

    public void SetEnabled(bool on)
    {
        lock (_gate)
        {
            if (on && _settings.Card is null) throw new ArgumentException("choose a card first");
            _settings.Enabled = on;
            Save();
            if (on) Engage(); else Disengage();
        }
    }

    public (string? Card, string? Vm) Target { get { lock (_gate) return (_settings.Card, _settings.Vm); } }

    public void SelectVm(string? vm)
    {
        lock (_gate)
        {
            _settings.Vm = vm;
            Save();
        }
    }

    /// <summary>The card's device files, read from this filter's /proc and /sys.</summary>
    public IReadOnlyList<CardNode> NodesOf(string card) => CardDevices.Nodes(card, _procRoot, _sysRoot);

    /// <summary>Every process with one of these device files open (the card's, and its audio function's).</summary>
    public IReadOnlyList<CardHolder> HoldersOf(IReadOnlyList<CardNode> nodes)
    {
        lock (_gate) return Holders(nodes);
    }

    // ── kernel side ───────────────────────────────────────────────────────────────────────────────────────────────

    private void Engage()
    {
        if (_prog < 0)
        {
            Directory.CreateDirectory(_pinDir);
            var (prog, devices, verdicts) = (Bpf.GetPinned(Path.Combine(_pinDir, "prog")), Bpf.GetPinned(Path.Combine(_pinDir, "devices")),
                Bpf.GetPinned(Path.Combine(_pinDir, "verdicts")));
            if (prog is null || devices is null || verdicts is null)
            {
                RemovePins(_pinDir);
                Directory.CreateDirectory(_pinDir);
                devices = Bpf.CreateDevicesMap();
                verdicts = Bpf.CreateVerdictsMap();
                prog = Bpf.LoadProgram(devices.Value, verdicts.Value);
                Bpf.Pin(devices.Value, Path.Combine(_pinDir, "devices"));
                Bpf.Pin(verdicts.Value, Path.Combine(_pinDir, "verdicts"));
                Bpf.Pin(prog.Value, Path.Combine(_pinDir, "prog"));
            }
            (_prog, _devices, _verdicts) = (prog.Value, devices.Value, verdicts.Value);
        }
        SyncDevices();
        ClassifyAll();                      // verdicts before the program can refuse anything
        if (!Directory.Exists(_attachRoot)) Directory.CreateDirectory(_attachRoot); // at boot, before systemd makes it
        if (!IsAttached()) Bpf.Attach(_prog, _attachRoot);
        if (_watcher is null)
        {
            _stopping = false;
            _watcher = new Thread(Watch) { IsBackground = true, Name = "card-filter" };
            _watcher.Start();
        }
    }

    private void Disengage()
    {
        _stopping = true;
        if (_prog >= 0 && IsAttached()) Bpf.Detach(_prog, _attachRoot);
        foreach (var fd in new[] { _prog, _devices, _verdicts }) if (fd >= 0) Bpf.Close(fd);
        (_prog, _devices, _verdicts) = (-1, -1, -1);
        RemovePins(_pinDir);
        _watcher = null;
    }

    /// <summary>Unpins the filter's program and maps and removes their bpffs folder; a missing folder is fine.</summary>
    public static void RemovePins(string pinDir)
    {
        if (!Directory.Exists(pinDir)) return;
        foreach (var name in new[] { "prog", "devices", "verdicts" }) File.Delete(Path.Combine(pinDir, name));
        if (!Directory.EnumerateFileSystemEntries(pinDir).Any()) Directory.Delete(pinDir);
    }

    private bool IsAttached()
    {
        try { return Directory.Exists(_attachRoot) && Bpf.AttachedIds(_attachRoot).Contains(Bpf.ProgramId(_prog)); }
        catch (IOException) { return false; }
    }

    // While the card is on VFIO (lent) its nodes don't exist; the last known ones stay in the map.
    private void SyncDevices()
    {
        if (_settings.Card is not { } card) return;
        var nodes = CardDevices.Nodes(card, _procRoot, _sysRoot);
        if (nodes.Count > 0) _nodes = nodes;
        for (uint slot = 0; slot < Bpf.DeviceSlots; slot++)
        {
            var want = slot < _nodes.Count ? Bpf.EncodeDevice(_nodes[(int)slot].Major, _nodes[(int)slot].Minor) : 0;
            if (Bpf.GetDevice(_devices, slot) != want) Bpf.SetDevice(_devices, slot, want);
        }
    }

    /// <summary>Every cgroup under the attach point gets a verdict; ones that vanished are dropped.</summary>
    private void ClassifyAll()
    {
        var seen = new HashSet<ulong>();
        Classify(_attachRoot, [], seen);
        foreach (var stale in Bpf.VerdictKeys(_verdicts).Where(k => !seen.Contains(k))) Bpf.DeleteVerdict(_verdicts, stale);
    }

    private void Classify(string dir, List<string> components, HashSet<ulong>? seen)
    {
        IEnumerable<string> children;
        try { children = Directory.EnumerateDirectories(dir).ToList(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }
        foreach (var child in children)
        {
            var path = new List<string>(components) { Path.GetFileName(child) };
            ulong id;
            try { id = Bpf.CgroupId(child); }
            catch (IOException) { continue; } // gone already
            Record(child, id, path);
            if (CardPatterns.Verdict(path, IsLeaf(child), _hiddenNow) is not null) seen?.Add(id);
            Classify(child, path, seen);
        }
    }

    private void Record(string dir, ulong id, IReadOnlyList<string> components)
    {
        var verdict = CardPatterns.Verdict(components, IsLeaf(dir), _hiddenNow);
        var current = Bpf.GetVerdict(_verdicts, id);
        if (verdict is null) { if (current is not null) Bpf.DeleteVerdict(_verdicts, id); }
        else if (current != verdict) Bpf.SetVerdict(_verdicts, id, verdict.Value);
    }

    private static bool IsLeaf(string dir)
    {
        try { return !Directory.EnumerateDirectories(dir).Any(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    // New cgroups are classified the moment systemd creates them (inotify on cgroupfs); a rescan every 2 s catches
    // anything missed and drops vanished cgroups. Until classified, a cgroup is refused the card.
    private void Watch()
    {
        var fd = Inotify.inotify_init1(0x80000 /* IN_CLOEXEC */);
        var watches = new Dictionary<int, string>();
        void Add(string dir)
        {
            var wd = Inotify.inotify_add_watch(fd, dir, 0x100 | 0x01000000 /* IN_CREATE | IN_ONLYDIR */);
            if (wd >= 0) watches[wd] = dir;
            try { foreach (var child in Directory.EnumerateDirectories(dir)) Add(child); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        if (fd >= 0) Add(_attachRoot);
        var buffer = new byte[64 * 1024];
        var lastFull = Environment.TickCount64;
        while (!_stopping)
        {
            if (fd >= 0 && Inotify.Poll(fd, 500))
            {
                var read = Inotify.Read(fd, buffer);
                for (var offset = 0; offset + 16 <= read;)
                {
                    var wd = BitConverter.ToInt32(buffer, offset);
                    var length = BitConverter.ToInt32(buffer, offset + 12);
                    var name = System.Text.Encoding.UTF8.GetString(buffer, offset + 16, length).TrimEnd('\0');
                    offset += 16 + length;
                    if (!watches.TryGetValue(wd, out var parent) || name.Length == 0) continue;
                    var created = Path.Combine(parent, name);
                    lock (_gate)
                    {
                        if (_prog < 0) break;
                        var relative = Path.GetRelativePath(_attachRoot, created).Split('/');
                        try
                        {
                            Record(created, Bpf.CgroupId(created), relative);
                            Classify(created, relative.ToList(), null);
                            // Its parent may have just stopped being a leaf: containers carry no allowed verdict.
                            if (relative.Length > 1) Record(parent, Bpf.CgroupId(parent), relative[..^1]);
                        }
                        catch (IOException) { }
                    }
                    Add(created);
                }
            }
            else if (fd < 0) Thread.Sleep(250);
            if (Environment.TickCount64 - lastFull >= 2000)
            {
                lastFull = Environment.TickCount64;
                lock (_gate)
                {
                    if (_prog < 0) break;
                    try { SyncDevices(); ClassifyAll(); } catch (IOException) { }
                }
            }
        }
        if (fd >= 0) Inotify.close(fd);
    }

    // ── holders and helpers ─────────────────────────────────────────────────────────────────────────────────────

    private IReadOnlyList<CardHolder> Holders(IReadOnlyList<CardNode> nodes)
    {
        if (nodes.Count == 0) return [];
        var paths = nodes.Select(n => n.Path).ToHashSet(StringComparer.Ordinal);
        var holders = new List<CardHolder>();
        foreach (var proc in Directory.EnumerateDirectories(_procRoot))
        {
            if (!int.TryParse(Path.GetFileName(proc), out var pid)) continue;
            try
            {
                if (!Directory.EnumerateFileSystemEntries(Path.Combine(proc, "fd"))
                        .Any(fd => new FileInfo(fd).LinkTarget is { } target && paths.Contains(target))) continue;
                var cgroup = File.ReadLines(Path.Combine(proc, "cgroup")).FirstOrDefault(l => l.StartsWith("0::", StringComparison.Ordinal))?[3..] ?? "";
                var underRoot = "/" + Path.GetRelativePath(_cgroupMount, _attachRoot);
                var relative = cgroup.StartsWith(underRoot + "/", StringComparison.Ordinal) ? cgroup[(underRoot.Length + 1)..] : null;
                var program = SysFs.ReadText(Path.Combine(proc, "comm"));
                holders.Add(new CardHolder(pid, program, cgroup, relative is null ? null : CardPatterns.Suggest(relative, _gdmUid),
                    relative is not null && CardPatterns.IsHidden(relative.Split('/'), _hiddenNow)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } // exited
        }
        return holders.OrderBy(h => h.Program, StringComparer.OrdinalIgnoreCase).ThenBy(h => h.Pid).ToList();
    }

    private static int? GdmUid()
    {
        try
        {
            return File.ReadLines("/etc/passwd").Select(l => l.Split(':')).Where(f => f.Length > 2 && f[0] == "gdm")
                .Select(f => int.TryParse(f[2], out var uid) ? uid : (int?)null).FirstOrDefault();
        }
        catch (IOException) { return null; }
    }

    private static T? Load<T>(string path) where T : class
    {
        try { return JsonSerializer.Deserialize<T>(File.ReadAllText(path)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
        File.WriteAllText(_settingsPath, JsonSerializer.Serialize(_settings, new JsonSerializerOptions { WriteIndented = true }));
        Directory.CreateDirectory(Path.GetDirectoryName(_runtimePath)!);
        File.WriteAllText(_runtimePath, JsonSerializer.Serialize(_hiddenNow.Order(StringComparer.Ordinal).ToList()));
    }

    public void Dispose()
    {
        _stopping = true; // the pinned program stays attached: a helper restart (e.g. around a handoff) keeps the filter
        foreach (var fd in new[] { _prog, _devices, _verdicts }) if (fd >= 0) Bpf.Close(fd);
    }

    private static class Inotify
    {
        [DllImport("libc", SetLastError = true)] public static extern int inotify_init1(int flags);
        [DllImport("libc", SetLastError = true)] public static extern int inotify_add_watch(int fd, string path, uint mask);
        [DllImport("libc")] public static extern int close(int fd);
        [DllImport("libc", SetLastError = true)] private static extern unsafe nint read(int fd, byte* buffer, nint count);
        [DllImport("libc", SetLastError = true)] private static extern unsafe int poll(PollFd* fds, nuint count, int timeout);

        [StructLayout(LayoutKind.Sequential)] private struct PollFd { public int Fd; public short Events; public short Revents; }

        public static unsafe bool Poll(int fd, int timeoutMs)
        {
            var p = new PollFd { Fd = fd, Events = 1 /* POLLIN */ };
            return poll(&p, 1, timeoutMs) > 0 && (p.Revents & 1) != 0;
        }

        public static unsafe int Read(int fd, byte[] buffer)
        {
            fixed (byte* b = buffer) return (int)Math.Max(0, read(fd, b, buffer.Length));
        }
    }
}

public sealed record CardFilterEntryStatus(string Pattern, string Name, bool HideAtBoot, bool HiddenNow);
public sealed record CardFilterStatus(bool Enabled, bool Attached, string? Card, IReadOnlyList<NvidiaCard> Cards,
    IReadOnlyList<CardNode> Nodes, IReadOnlyList<CardFilterEntryStatus> Entries, IReadOnlyList<CardHolder> Holders);
