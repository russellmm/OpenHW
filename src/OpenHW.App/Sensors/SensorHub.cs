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

    /// <summary>One poll: rediscover when due, read every sensor, then fold the sample into the statistics.</summary>
    public void Sample()
    {
        var changed = RefreshInventory();
        var readings = Inventory.ToDictionary(i => i.Id, _ => (double?)null, StringComparer.OrdinalIgnoreCase);
        foreach (var engine in _engines) engine.ReadInto(readings);
        lock (_gate) Statistics.Record(readings);
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
