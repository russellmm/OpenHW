using System.Globalization;
using System.Runtime.Versioning;

namespace OpenHW.Hardware.Windows;

/// <summary>
/// The Windows twin of <see cref="SystemEngine"/>: the "System" group (memory, commit, page file), per-core CPU clocks
/// and usage, and C-state residency, with the same sensor ids so layouts, tray icons, alerts and logs carry over.
/// Everything comes from user-mode APIs (no driver, no elevation): memory from GetPerformanceInfo, usage, clocks and
/// C-states from the Processor Information performance counters, cores from GetLogicalProcessorInformationEx.
/// Clocks are Windows' own estimate (base frequency × % Processor Performance, what Task Manager shows), not the
/// per-core MSR readings HWiNFO's driver takes; those need kernel access (docs/windows-port.md, phase 3).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsSystemEngine : ISensorEngine
{
    private const string Id = "openhw";
    private const string Processor = @"\Processor Information(*)\";
    private static readonly string[] CStates = ["C1", "C2", "C3"];
    private static readonly string[] Paths =
    [
        Processor + "% Processor Time", Processor + "% Processor Performance", Processor + "Processor Frequency",
        .. CStates.Select(c => Processor + $"% {c} Time"),
        @"\Paging File(_Total)\% Usage",
    ];

    private Pdh? _pdh;
    private IReadOnlyList<(int Core, IReadOnlyList<string> Threads)> _cores = [];

    public string Name => Id;
    public bool Available => true;

    public IReadOnlyDictionary<string, IReadOnlyList<string>> Children { get; private set; } =
        new Dictionary<string, IReadOnlyList<string>>();

    public IReadOnlyList<HardwareItem> Discover()
    {
        _pdh ??= new Pdh(Paths);
        _cores = Topology();
        var items = new List<HardwareItem>
        {
            Item("mem:committed", "Virtual Memory Committed", HardwareKind.Data, "MB", SystemEngine.SystemGroup),
            Item("mem:commit-limit", "Virtual Memory Commit Limit", HardwareKind.Data, "MB", SystemEngine.SystemGroup),
            Item("mem:commit-load", "Virtual Memory Load", HardwareKind.Load, "%", SystemEngine.SystemGroup),
            Item("mem:used", "Physical Memory Used", HardwareKind.Data, "MB", SystemEngine.SystemGroup),
            Item("mem:available", "Physical Memory Available", HardwareKind.Data, "MB", SystemEngine.SystemGroup),
            Item("mem:load", "Physical Memory Load", HardwareKind.Load, "%", SystemEngine.SystemGroup),
            Item("swap:load", "Page File Usage", HardwareKind.Load, "%", SystemEngine.SystemGroup),
        };

        var clocks = new List<string>();
        var usages = new List<string>();
        items.Add(Item("cpu:clock", "Core Clocks", HardwareKind.Frequency, "MHz", SystemEngine.CpuGroup));
        foreach (var (core, _) in _cores)
        {
            items.Add(Item($"cpu:clock:{core}", $"Core {core} Clock", HardwareKind.Frequency, "MHz", SystemEngine.CpuGroup));
            clocks.Add(FullId($"cpu:clock:{core}"));
        }
        items.Add(Item("cpu:clock:max", "Max Core Clock", HardwareKind.Frequency, "MHz", SystemEngine.CpuGroup));
        items.Add(Item("cpu:usage", "Core Usage", HardwareKind.Load, "%", SystemEngine.CpuGroup));
        foreach (var (core, threads) in _cores)
            for (var t = 0; t < threads.Count; t++)
            {
                items.Add(Item($"cpu:usage:{Key(threads[t])}", $"Core {core} T{t} Usage", HardwareKind.Load, "%", SystemEngine.CpuGroup));
                usages.Add(FullId($"cpu:usage:{Key(threads[t])}"));
            }
        items.Add(Item("cpu:usage:max", "Max CPU/Thread Usage", HardwareKind.Load, "%", SystemEngine.CpuGroup));
        items.Add(Item("cpu:usage:total", "Total CPU Usage", HardwareKind.Load, "%", SystemEngine.CpuGroup));

        var children = new Dictionary<string, IReadOnlyList<string>>
        {
            [FullId("cpu:clock")] = clocks,
            [FullId("cpu:usage")] = usages,
        };
        foreach (var name in new[] { "C0" }.Concat(CStates))
        {
            var key = $"cstate:{name}";
            items.Add(Item(key, $"Core {name} Residency", HardwareKind.Load, "%", SystemEngine.CStateGroup));
            items.AddRange(_cores.Select(c => Item($"{key}:{c.Core}", $"Core {c.Core} {name} Residency", HardwareKind.Load, "%", SystemEngine.CStateGroup)));
            children[FullId(key)] = _cores.Select(c => FullId($"{key}:{c.Core}")).ToList();
        }
        Children = children;
        return items;
    }

    // PDH names a logical processor "group,number"; ids use "group-number" (a colon or comma would split the id).
    private static string Key(string instance) => instance.Replace(',', '-');

    public void ReadInto(IDictionary<string, double?> readings, IReadOnlySet<string>? requested)
    {
        ReadMemory(readings);
        if (_pdh is null || !_pdh.Collect()) return;
        var busy = _pdh.Values(Processor + "% Processor Time");
        var performance = _pdh.Values(Processor + "% Processor Performance");
        var frequency = _pdh.Values(Processor + "Processor Frequency");

        // A core's clock is its busiest thread's (SMT siblings share one clock domain; an idle thread reports low).
        var clocks = new List<double>();
        foreach (var (core, threads) in _cores)
        {
            var mhz = threads.Select(t => frequency.TryGetValue(t, out var f) && performance.TryGetValue(t, out var p) ? f * p / 100 : (double?)null)
                .OfType<double>().DefaultIfEmpty(double.NaN).Max();
            readings[FullId($"cpu:clock:{core}")] = double.IsNaN(mhz) ? null : mhz;
            if (!double.IsNaN(mhz)) clocks.Add(mhz);
        }
        readings[FullId("cpu:clock")] = clocks.Count > 0 ? clocks.Average() : null;
        readings[FullId("cpu:clock:max")] = clocks.Count > 0 ? clocks.Max() : null;

        var usages = new List<double>();
        foreach (var thread in _cores.SelectMany(c => c.Threads))
        {
            double? value = busy.TryGetValue(thread, out var b) ? Math.Clamp(b, 0, 100) : null;
            readings[FullId($"cpu:usage:{Key(thread)}")] = value;
            if (value is { } v) usages.Add(v);
        }
        double? total = busy.TryGetValue("_Total", out var all) ? Math.Clamp(all, 0, 100) : usages.Count > 0 ? usages.Average() : null;
        readings[FullId("cpu:usage")] = total;
        readings[FullId("cpu:usage:total")] = total;
        readings[FullId("cpu:usage:max")] = usages.Count > 0 ? usages.Max() : null;

        // Residency per core = the mean of its threads; C0 = the time in none of the idle states.
        var states = CStates.ToDictionary(c => c, c => _pdh.Values(Processor + $"% {c} Time"));
        var totals = new Dictionary<string, List<double>>();
        foreach (var (core, threads) in _cores)
        {
            var perState = new Dictionary<string, double>();
            foreach (var state in CStates)
            {
                var shares = threads.Select(t => states[state].TryGetValue(t, out var v) ? v : (double?)null).OfType<double>().ToList();
                if (shares.Count > 0) perState[state] = Math.Clamp(shares.Average(), 0, 100);
            }
            if (perState.Count > 0) perState["C0"] = Math.Max(0, 100 - perState.Values.Sum());
            foreach (var (name, value) in perState)
            {
                readings[FullId($"cstate:{name}:{core}")] = value;
                (totals.TryGetValue(name, out var list) ? list : totals[name] = []).Add(value);
            }
        }
        foreach (var (name, values) in totals) readings[FullId($"cstate:{name}")] = values.Average();

        if (_pdh.Values(@"\Paging File(_Total)\% Usage").TryGetValue("_Total", out var pageFile)) readings[FullId("swap:load")] = pageFile;
    }

    private static void ReadMemory(IDictionary<string, double?> readings)
    {
        var info = new Win32.PerformanceInformation { Size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Win32.PerformanceInformation>() };
        if (!Win32.GetPerformanceInfo(ref info, info.Size)) return;
        double Mb(nuint pages) => pages * (double)info.PageSize / (1024 * 1024);
        var total = Mb(info.PhysicalTotal);
        var available = Mb(info.PhysicalAvailable);
        readings[FullId("mem:committed")] = Mb(info.CommitTotal);
        readings[FullId("mem:commit-limit")] = Mb(info.CommitLimit);
        readings[FullId("mem:commit-load")] = info.CommitLimit > 0 ? 100.0 * info.CommitTotal / info.CommitLimit : null;
        readings[FullId("mem:used")] = total - available;
        readings[FullId("mem:available")] = available;
        readings[FullId("mem:load")] = total > 0 ? (total - available) / total * 100 : null;
    }

    /// <summary>Physical cores in order, each with its logical processors as PDH instance names ("0,12").</summary>
    internal static unsafe IReadOnlyList<(int Core, IReadOnlyList<string> Threads)> Topology()
    {
        uint length = 0;
        Win32.GetLogicalProcessorInformationEx(Win32.RelationProcessorCore, null, ref length);
        if (length == 0) return [];
        var buffer = new byte[length];
        var cores = new List<IReadOnlyList<(int Group, int Number)>>();
        fixed (byte* start = buffer)
        {
            if (!Win32.GetLogicalProcessorInformationEx(Win32.RelationProcessorCore, start, ref length)) return [];
            for (var p = start; p < start + length;)
            {
                var size = *(uint*)(p + 4);
                // PROCESSOR_RELATIONSHIP at +8: Flags, EfficiencyClass, Reserved[20], GroupCount (+30), GROUP_AFFINITY[] (+32, 16 bytes each).
                var groupCount = *(ushort*)(p + 8 + 22);
                var threads = new List<(int, int)>();
                for (var g = 0; g < groupCount; g++)
                {
                    var affinity = p + 8 + 24 + g * 16;
                    var mask = *(ulong*)affinity;
                    var group = *(ushort*)(affinity + 8);
                    for (var bit = 0; bit < 64; bit++)
                        if ((mask & (1UL << bit)) != 0) threads.Add((group, bit));
                }
                cores.Add(threads);
                p += size;
            }
        }
        return cores.OrderBy(c => c.Min()).Select((c, index) =>
            (index, (IReadOnlyList<string>)c.Order().Select(t => string.Create(CultureInfo.InvariantCulture, $"{t.Group},{t.Number}")).ToList())).ToList();
    }

    private static string FullId(string key) => $"{Id}:{key}";

    private static HardwareItem Item(string key, string name, HardwareKind kind, string unit, string group) => new()
    {
        Id = FullId(key), Name = name, Kind = kind, Unit = unit, Group = group,
        Backend = Id, CanSet = false, Source = group switch
        {
            SystemEngine.SystemGroup => "Windows memory counters",
            SystemEngine.CStateGroup => @"\Processor Information · % C1/C2/C3 Time",
            _ => @"\Processor Information · % Processor Time / Performance",
        },
    };

    public void Dispose() => _pdh?.Dispose();
}
