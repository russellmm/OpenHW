using OpenHW.Hardware;

namespace OpenHW.App.Sensors;

/// <summary>
/// Owns one engine per sensor suite and samples all of them on one cadence, so the sensor window and tray icons
/// always show the same sample. Read-only: OpenHW never writes fan controls or limits.
/// </summary>
public sealed class SensorHub : IDisposable
{
    private readonly HwmonEngine _hwmon = new();
    private readonly NvidiaEngine _nvidia = new();
    private readonly NvmeEngine _nvme = new();
    private readonly SystemEngine _system = new();
    private readonly ISensorEngine[] _engines;
    private readonly InventoryRefreshCadence _inventoryCadence = new(TimeSpan.FromSeconds(5));
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private readonly object _gate = new();
    private IReadOnlyList<HardwareItem> _inventory = [];
    private string _fingerprint = "";
    private IReadOnlySet<string> _paused = new HashSet<string>();
    private IReadOnlySet<string> _pinned = new HashSet<string>();

    public SensorHub()
    {
        _engines = [_system, _hwmon, new EdacEngine(), _nvme, new NetworkEngine(), _nvidia];
    }

    public SensorStatistics Statistics { get; } = new();

    /// <summary>Current inventory; replaced (never mutated) when discovery finds a different set.</summary>
    public IReadOnlyList<HardwareItem> Inventory { get { lock (_gate) return _inventory; } }

    /// <summary>Aggregate row id → child row ids (per-core clocks, usage, C-states), across all engines.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Children =>
        _engines.SelectMany(e => e.Children).ToDictionary(kv => kv.Key, kv => kv.Value);

    /// <summary>Board, CPU and drive identity used for group titles; read once.</summary>
    public SensorPresentation.MachineInfo Machine => _machine ??= new(
        SysFs.ReadText("/sys/class/dmi/id/board_vendor"), SysFs.ReadText("/sys/class/dmi/id/board_name"),
        SystemEngine.CpuModel(), _nvme.Devices);
    private SensorPresentation.MachineInfo? _machine;

    /// <summary>"libsensors" or the sysfs fallback, for the status line.</summary>
    public string ReaderDescription => _hwmon.ReaderDescription;

    /// <summary>Raised on the sampling thread after <see cref="Sample"/> when discovery changed the inventory.</summary>
    public event Action? InventoryChanged;

    /// <summary>
    /// Sensors the window has collapsed. They are not read (engines skip the hardware access where they can) and
    /// their statistics freeze until they are shown again — unless they are also <see cref="SetPinned">pinned</see>.
    /// </summary>
    public void SetPaused(IEnumerable<string> ids)
    {
        var set = ids.ToHashSet(StringComparer.Ordinal);
        lock (_gate) _paused = set;
    }

    /// <summary>Sensors that must stay live even when collapsed (tray icons).</summary>
    public void SetPinned(IEnumerable<string> ids)
    {
        var set = ids.ToHashSet(StringComparer.Ordinal);
        lock (_gate) _pinned = set;
    }

    /// <summary>True when the sensor is collapsed and not pinned, i.e. its values are frozen.</summary>
    public bool IsPaused(string id)
    {
        lock (_gate) return _paused.Contains(id) && !_pinned.Contains(id);
    }

    /// <summary>One poll: rediscover when due, read the sensors that are not paused, then fold the sample into the statistics.</summary>
    public void Sample()
    {
        var changed = RefreshInventory();
        HashSet<string> frozen;
        lock (_gate) frozen = _paused.Where(id => !_pinned.Contains(id)).ToHashSet(StringComparer.Ordinal);
        var requested = Inventory.Select(i => i.Id).Where(id => !frozen.Contains(id)).ToHashSet(StringComparer.Ordinal);

        var readings = requested.ToDictionary(id => id, _ => (double?)null, StringComparer.Ordinal);
        foreach (var engine in _engines) engine.ReadInto(readings, frozen.Count == 0 ? null : requested);
        foreach (var id in frozen) readings.Remove(id); // engines that read whole sections may still fill these
        lock (_gate) Statistics.Record(readings, frozen);
        if (changed) InventoryChanged?.Invoke();
    }

    public SensorStatistics.Snapshot Get(string id)
    {
        lock (_gate) return Statistics.Get(id);
    }

    public void ResetStatistics()
    {
        lock (_gate) Statistics.Reset();
    }

    /// <summary>The CPU temperature the tray shows: k10temp Tctl, else any sensor named like a package temperature.</summary>
    public string? CpuTemperatureId()
    {
        var inventory = Inventory;
        return inventory.FirstOrDefault(i => i.Id.StartsWith("hwmon:k10temp:", StringComparison.Ordinal) && i.Id.EndsWith(":temp:1", StringComparison.Ordinal))?.Id
            ?? inventory.FirstOrDefault(i => i.Kind == HardwareKind.Temperature && i.Name is "Tctl" or "Tdie" or "CPU Package")?.Id;
    }

    private bool RefreshInventory()
    {
        if (!_inventoryCadence.IsDue(_clock.Elapsed)) return false;
        // GPUs listed by hwmon (e.g. nouveau) are dropped when NVML reports the same card.
        var merged = InventoryMerger.Merge(
            _engines.Where(e => e != _nvidia).SelectMany(e => e.Discover()),
            _nvidia.Discover());
        var fingerprint = string.Join('|', merged.Select(i => $"{i.Id}\t{i.Name}\t{i.Group}").OrderBy(x => x, StringComparer.Ordinal));
        if (fingerprint == _fingerprint) return false;
        lock (_gate)
        {
            _inventory = merged;
            _fingerprint = fingerprint;
        }
        return true;
    }

    public void Dispose()
    {
        foreach (var engine in _engines) engine.Dispose();
    }
}
