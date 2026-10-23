using System.Globalization;

namespace OpenHW.Hardware;

/// <summary>
/// HWiNFO's system and CPU groups, which no other engine covers: "System" (memory, pressure stall
/// info, load average), per-core CPU clocks/usage (cpufreq + /proc/stat) and C-state residency (cpuidle).
/// Per-core rows are children of an aggregate row, so <see cref="Children"/> tells the window which rows nest
/// under which.
/// </summary>
public sealed partial class SystemEngine : ISensorEngine
{
    public const string SystemGroup = "openhw:system";
    public const string CpuGroup = "openhw:cpu";
    public const string CStateGroup = "openhw:cstates";
    private const string Id = "openhw";

    private readonly Dictionary<int, (long Idle, long Total)> _previousTimes = [];
    private readonly Dictionary<(int Cpu, int State), long> _previousIdleUs = [];
    private readonly System.Diagnostics.Stopwatch _wall = System.Diagnostics.Stopwatch.StartNew();
    private TimeSpan? _previousWall;
    private IReadOnlyList<(int Index, string Name)> _idleStates = [];
    private IReadOnlyList<(int Core, IReadOnlyList<int> Threads)> _cores = [];

    public string Name => Id;
    public bool Available => true;

    /// <summary>Aggregate row id → its per-core / per-thread child ids, in display order.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Children { get; private set; } =
        new Dictionary<string, IReadOnlyList<string>>();

    public IReadOnlyList<HardwareItem> Discover()
    {
        _cores = ReadTopology();
        var items = new List<HardwareItem>
        {
            Item("mem:committed", "Virtual Memory Committed", HardwareKind.Data, "MB", SystemGroup),
            Item("mem:commit-limit", "Virtual Memory Commit Limit", HardwareKind.Data, "MB", SystemGroup),
            Item("mem:used", "Physical Memory Used", HardwareKind.Data, "MB", SystemGroup),
            Item("mem:available", "Physical Memory Available", HardwareKind.Data, "MB", SystemGroup),
            Item("mem:load", "Physical Memory Load", HardwareKind.Load, "%", SystemGroup),
            Item("swap:used", "Swap Used", HardwareKind.Data, "MB", SystemGroup),
            Item("swap:total", "Swap Total", HardwareKind.Data, "MB", SystemGroup),
            Item("swap:load", "Swap Load", HardwareKind.Load, "%", SystemGroup),
            Item("psi:cpu", "CPU Pressure (some, 10 s)", HardwareKind.Load, "%", SystemGroup),
            Item("psi:memory", "Memory Pressure (some, 10 s)", HardwareKind.Load, "%", SystemGroup),
            Item("psi:memory-full", "Memory Pressure (full, 10 s)", HardwareKind.Load, "%", SystemGroup),
            Item("psi:io", "I/O Pressure (some, 10 s)", HardwareKind.Load, "%", SystemGroup),
            Item("psi:io-full", "I/O Pressure (full, 10 s)", HardwareKind.Load, "%", SystemGroup),
            Item("loadavg:1", "Load Average (1 min)", HardwareKind.Load, "", SystemGroup),
            Item("loadavg:5", "Load Average (5 min)", HardwareKind.Load, "", SystemGroup),
        };

        var clocks = new List<string>();
        var usages = new List<string>();
        items.Add(Item("cpu:clock", "Core Clocks", HardwareKind.Frequency, "MHz", CpuGroup));
        foreach (var (core, _) in _cores)
        {
            items.Add(Item($"cpu:clock:{core}", $"Core {core} Clock", HardwareKind.Frequency, "MHz", CpuGroup));
            clocks.Add(FullId($"cpu:clock:{core}"));
        }
        items.Add(Item("cpu:clock:max", "Max Core Clock", HardwareKind.Frequency, "MHz", CpuGroup));
        items.Add(Item("cpu:usage", "Core Usage", HardwareKind.Load, "%", CpuGroup));
        foreach (var (core, threads) in _cores)
            for (var t = 0; t < threads.Count; t++)
            {
                items.Add(Item($"cpu:usage:{threads[t]}", $"Core {core} T{t} Usage", HardwareKind.Load, "%", CpuGroup));
                usages.Add(FullId($"cpu:usage:{threads[t]}"));
            }
        items.Add(Item("cpu:usage:max", "Max CPU/Thread Usage", HardwareKind.Load, "%", CpuGroup));
        items.Add(Item("cpu:usage:total", "Total CPU Usage", HardwareKind.Load, "%", CpuGroup));

        var children = new Dictionary<string, IReadOnlyList<string>>
        {
            [FullId("cpu:clock")] = clocks,
            [FullId("cpu:usage")] = usages,
        };

        // C0 is "not in a sleep state"; POLL is a busy-wait, so HWiNFO-style it counts toward C0.
        _idleStates = ReadIdleStates();
        foreach (var name in new[] { "C0" }.Concat(_idleStates.Where(s => s.Name != "POLL").Select(s => s.Name)))
        {
            var key = $"cstate:{name}";
            items.Add(Item(key, $"Core {name} Residency", HardwareKind.Load, "%", CStateGroup));
            var perCore = _cores.Select(c => FullId($"{key}:{c.Core}")).ToList();
            items.AddRange(_cores.Select(c => Item($"{key}:{c.Core}", $"Core {c.Core} {name} Residency", HardwareKind.Load, "%", CStateGroup)));
            children[FullId(key)] = perCore;
        }

        Children = children;
        return items;
    }

    public void ReadInto(IDictionary<string, double?> readings)
    {
        ReadMemory(readings);
        ReadClocks(readings);
        ReadUsage(readings);
        ReadPressure(readings);
        ReadCStates(readings);
    }

    // /proc/pressure/*: share of the last 10 s in which some (or all) runnable tasks were stalled on the resource.
    private static void ReadPressure(IDictionary<string, double?> readings)
    {
        foreach (var (resource, key) in new[] { ("cpu", "psi:cpu"), ("memory", "psi:memory"), ("io", "psi:io") })
        {
            try
            {
                foreach (var line in File.ReadLines("/proc/pressure/" + resource))
                {
                    var avg10 = line.Split(' ').FirstOrDefault(f => f.StartsWith("avg10=", StringComparison.Ordinal));
                    if (avg10 is null || !double.TryParse(avg10[6..], NumberStyles.Float, CultureInfo.InvariantCulture, out var value)) continue;
                    if (line.StartsWith("some", StringComparison.Ordinal)) readings[FullId(key)] = value;
                    else if (resource != "cpu") readings[FullId(key + "-full")] = value;
                }
            }
            catch (IOException) { }
        }

        try
        {
            var fields = File.ReadAllText("/proc/loadavg").Split(' ');
            readings[FullId("loadavg:1")] = double.Parse(fields[0], CultureInfo.InvariantCulture);
            readings[FullId("loadavg:5")] = double.Parse(fields[1], CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is IOException or FormatException) { }
    }

    // Residency = time spent in each cpuidle state since the previous poll ÷ wall time; a core's value is the
    // mean of its threads. The first poll only records the baseline.
    private void ReadCStates(IDictionary<string, double?> readings)
    {
        var now = _wall.Elapsed;
        var wallUs = _previousWall is { } last ? (now - last).TotalMicroseconds : 0;
        _previousWall = now;
        var totals = new Dictionary<string, List<double>>();
        foreach (var (core, threads) in _cores)
        {
            var perState = new Dictionary<string, List<double>>();
            foreach (var cpu in threads)
            {
                double sleeping = 0;
                foreach (var (index, name) in _idleStates)
                {
                    if (ReadNumber($"/sys/devices/system/cpu/cpu{cpu}/cpuidle/state{index}/time") is not { } us) continue;
                    var previous = _previousIdleUs.GetValueOrDefault((cpu, index), (long)us);
                    _previousIdleUs[(cpu, index)] = (long)us;
                    if (wallUs <= 0 || name == "POLL") continue;
                    var share = Math.Clamp((us - previous) / wallUs * 100, 0, 100);
                    sleeping += share;
                    (perState.TryGetValue(name, out var list) ? list : perState[name] = []).Add(share);
                }
                if (wallUs > 0) (perState.TryGetValue("C0", out var c0) ? c0 : perState["C0"] = []).Add(Math.Max(0, 100 - sleeping));
            }
            foreach (var (name, shares) in perState)
            {
                var value = shares.Average();
                readings[FullId($"cstate:{name}:{core}")] = value;
                (totals.TryGetValue(name, out var all) ? all : totals[name] = []).Add(value);
            }
        }
        foreach (var (name, values) in totals) readings[FullId($"cstate:{name}")] = values.Average();
    }

    private static IReadOnlyList<(int Index, string Name)> ReadIdleStates()
    {
        const string dir = "/sys/devices/system/cpu/cpu0/cpuidle";
        if (!Directory.Exists(dir)) return [];
        return Directory.EnumerateDirectories(dir, "state*")
            .Select(d => (Index: int.TryParse(Path.GetFileName(d).AsSpan(5), out var i) ? i : -1, Dir: d))
            .Where(s => s.Index >= 0)
            .Select(s => (s.Index, Name: File.ReadAllText(Path.Combine(s.Dir, "name")).Trim()))
            .OrderBy(s => s.Index)
            .ToList();
    }

    private static void ReadMemory(IDictionary<string, double?> readings)
    {
        var info = new Dictionary<string, double>();
        try
        {
            foreach (var line in File.ReadLines("/proc/meminfo"))
            {
                var parts = line.Split(':', 2);
                var value = parts.Length == 2 ? parts[1].Trim().Split(' ')[0] : "";
                if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var kib))
                    info[parts[0]] = kib / 1024; // MB, as HWiNFO reports memory
            }
        }
        catch (IOException) { return; }

        double? Get(string key) => info.TryGetValue(key, out var v) ? v : null;
        var total = Get("MemTotal");
        var available = Get("MemAvailable");
        var used = total - available;
        var swapTotal = Get("SwapTotal");
        var swapUsed = swapTotal - Get("SwapFree");
        readings[FullId("mem:committed")] = Get("Committed_AS");
        readings[FullId("mem:commit-limit")] = Get("CommitLimit");
        readings[FullId("mem:used")] = used;
        readings[FullId("mem:available")] = available;
        readings[FullId("mem:load")] = total > 0 ? used / total * 100 : null;
        readings[FullId("swap:used")] = swapUsed;
        readings[FullId("swap:total")] = swapTotal;
        readings[FullId("swap:load")] = swapTotal > 0 ? swapUsed / swapTotal * 100 : 0;
    }

    // A core's clock is its first thread's cpufreq reading: SMT siblings share one clock domain.
    private void ReadClocks(IDictionary<string, double?> readings)
    {
        var clocks = new List<double>();
        foreach (var (core, threads) in _cores)
        {
            var khz = ReadNumber($"/sys/devices/system/cpu/cpu{threads[0]}/cpufreq/scaling_cur_freq");
            var mhz = khz / 1000;
            readings[FullId($"cpu:clock:{core}")] = mhz;
            if (mhz is { } m) clocks.Add(m);
        }
        readings[FullId("cpu:clock")] = clocks.Count > 0 ? clocks.Average() : null;
        readings[FullId("cpu:clock:max")] = clocks.Count > 0 ? clocks.Max() : null;
    }

    // Busy % per logical CPU from /proc/stat deltas since the previous poll; the first poll has no delta.
    private void ReadUsage(IDictionary<string, double?> readings)
    {
        var usages = new List<double>();
        double? total = null;
        try
        {
            foreach (var line in File.ReadLines("/proc/stat"))
            {
                if (!line.StartsWith("cpu", StringComparison.Ordinal)) break;
                var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var cpu = fields[0] == "cpu" ? -1 : int.Parse(fields[0].AsSpan(3), CultureInfo.InvariantCulture);
                var times = fields.Skip(1).Take(8).Select(f => long.Parse(f, CultureInfo.InvariantCulture)).ToArray();
                var idle = times[3] + times[4]; // idle + iowait
                var all = times.Sum();
                double? busy = null;
                if (_previousTimes.TryGetValue(cpu, out var previous) && all > previous.Total)
                    busy = Math.Clamp(100.0 * (1 - (double)(idle - previous.Idle) / (all - previous.Total)), 0, 100);
                _previousTimes[cpu] = (idle, all);

                if (cpu < 0) total = busy;
                else
                {
                    readings[FullId($"cpu:usage:{cpu}")] = busy;
                    if (busy is { } b) usages.Add(b);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or FormatException) { return; }

        readings[FullId("cpu:usage")] = total;
        readings[FullId("cpu:usage:total")] = total;
        readings[FullId("cpu:usage:max")] = usages.Count > 0 ? usages.Max() : null;
    }

    public static string CpuModel()
    {
        try
        {
            var line = File.ReadLines("/proc/cpuinfo").FirstOrDefault(l => l.StartsWith("model name", StringComparison.Ordinal));
            // HWiNFO drops the marketing core count suffix ("… 9970X 32-Cores").
            return CoreCountSuffix().Replace(line?.Split(':', 2)[1].Trim() ?? "CPU", "");
        }
        catch (IOException) { return "CPU"; }
    }

    private static IReadOnlyList<(int Core, IReadOnlyList<int> Threads)> ReadTopology()
    {
        var threads = new List<(int Core, int Cpu)>();
        foreach (var dir in Directory.EnumerateDirectories("/sys/devices/system/cpu", "cpu*"))
        {
            if (!int.TryParse(Path.GetFileName(dir).AsSpan(3), out var cpu)) continue;
            if (ReadNumber(Path.Combine(dir, "topology/core_id")) is { } coreId) threads.Add(((int)coreId, cpu));
        }
        // core_id can be sparse on multi-CCD parts; renumber densely so rows read Core 0..N-1 like HWiNFO.
        return threads.GroupBy(t => t.Core).OrderBy(g => g.Key)
            .Select((g, index) => (index, (IReadOnlyList<int>)g.Select(t => t.Cpu).Order().ToList()))
            .ToList();
    }

    private static double? ReadNumber(string path)
    {
        try
        {
            return double.TryParse(File.ReadAllText(path).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private static string FullId(string key) => $"{Id}:{key}";

    private static HardwareItem Item(string key, string name, HardwareKind kind, string unit, string group) => new()
    {
        Id = FullId(key), Name = name, Kind = kind, Unit = unit, Group = group,
        Backend = Id, CanSet = false, Source = group switch
        {
            SystemGroup => "/proc/meminfo · /proc/pressure · /proc/loadavg",
            CStateGroup => "cpuidle",
            _ => "cpufreq · /proc/stat",
        },
    };

    [System.Text.RegularExpressions.GeneratedRegex(@"\s+\d+-Cores?$")]
    private static partial System.Text.RegularExpressions.Regex CoreCountSuffix();

    public void Dispose() { }
}
