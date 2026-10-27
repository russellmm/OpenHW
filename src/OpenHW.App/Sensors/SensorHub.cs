using OpenHW.Hardware;

namespace OpenHW.App.Sensors;

/// <summary>
/// Owns one engine per sensor suite and samples all of them on one cadence, so the sensor window and tray icons
/// always show the same sample. Read-only: OpenHW never writes fan controls or limits.
/// </summary>
public sealed class SensorHub : IDisposable
{
    private readonly HwmonEngine _hwmon = new();
    // GPUs come from openhw-helper: the app never opens a card (see HelperGpuEngine).
    private readonly ISensorEngine _nvidia = new HelperGpuEngine();
    private readonly NvmeEngine _nvme = new();
    private readonly SystemEngine _system = new();
    private readonly ISensorEngine[] _engines;
    private readonly InventoryRefreshCadence _inventoryCadence = new(TimeSpan.FromSeconds(5));
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private readonly object _gate = new();
    private IReadOnlyList<HardwareItem> _inventory = [];
    private string _fingerprint = "";
    private readonly PauseTracker _pauses = new();

    public SensorHub()
    {
        _engines = [_system, _hwmon, new HsmpEngine(), new RaplEngine(), new DimmEngine(), new BoardVrEngine(), new EdacEngine(), _nvme, new NetworkEngine(), _nvidia];
    }

    public SensorStatistics Statistics { get; } = new();

    /// <summary>The last 30 minutes of what each poll read, for graphs (sized for the shortest poll, 1 s).</summary>
    public SensorHistory History { get; } = new(TimeSpan.FromSeconds(1));

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
    public void SetPaused(IEnumerable<string> ids) => _pauses.SetPaused(ids);

    /// <summary>Sensors that must stay live even when collapsed (tray icons).</summary>
    public void SetPinned(IEnumerable<string> ids) => _pauses.SetPinned(ids);

    /// <summary>True while the sensor's values are frozen (collapsed, or just expanded and not yet re-read).</summary>
    public bool IsPaused(string id) => _pauses.IsPaused(id);

    /// <summary>One poll: rediscover when due, read the sensors that are not paused, then fold the sample into the statistics.</summary>
    public void Sample()
    {
        var changed = RefreshInventory();
        var frozen = _pauses.BeginPoll();
        var requested = Inventory.Select(i => i.Id).Where(id => !frozen.Contains(id)).ToHashSet(StringComparer.Ordinal);

        var readings = requested.ToDictionary(id => id, _ => (double?)null, StringComparer.Ordinal);
        // With S.M.A.R.T. off, the NVMe engine gets the requested set, so it skips the health reads (they aren't asked).
        var only = frozen.Count == 0 && _readSmart ? null : requested;
        foreach (var engine in _engines.Where(IsOn)) engine.ReadInto(readings, only);
        if (!_readSmart) foreach (var id in readings.Keys.Where(IsSmart).ToList()) readings.Remove(id);
        var hold = _pauses.EndPoll(readings, frozen);
        lock (_gate) Statistics.Record(readings, hold);
        History.Record(readings.Where(r => r.Value is not null && !hold.Contains(r.Key)).Select(r => (r.Key, r.Value!.Value)));
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

    /// <summary>Sensor Settings → Averaging Interval: Average over the last N samples, 0 = the whole run.</summary>
    public void SetAverageSamples(int samples)
    {
        lock (_gate) Statistics.AverageSamples = samples;
    }

    private volatile bool _readNvidia = true, _readSmbus = true, _readSmart = true;
    private volatile bool _rediscover;

    /// <summary>
    /// Main Settings' Enable NVML, SMBus Support and S.M.A.R.T. Support: which readers run. A reader that's off isn't
    /// asked for anything and its sensors leave the inventory at the next poll; on again, they come back.
    /// </summary>
    public void SetReaders(bool nvidia, bool smbus, bool smart)
    {
        if (nvidia == _readNvidia && smbus == _readSmbus && smart == _readSmart) return;
        (_readNvidia, _readSmbus, _readSmart) = (nvidia, smbus, smart);
        _rediscover = true;
    }

    private bool IsOn(ISensorEngine engine) => engine switch
    {
        _ when engine == _nvidia => _readNvidia,
        DimmEngine or BoardVrEngine => _readSmbus,
        _ => true,
    };

    // NVMe S.M.A.R.T. readings (nvme-health:<drive>:smart:<metric>); the drive's other sensors stay.
    private static bool IsSmart(string id) => id.StartsWith("nvme-health:", StringComparison.Ordinal);

    /// <summary>The CPU temperature the tray shows: k10temp Tctl, else any sensor named like a package temperature.</summary>
    public string? CpuTemperatureId()
    {
        var inventory = Inventory;
        return inventory.FirstOrDefault(i => i.Id.StartsWith("hwmon:k10temp:", StringComparison.Ordinal) && i.Id.EndsWith(":temp:1", StringComparison.Ordinal))?.Id
            ?? inventory.FirstOrDefault(i => i.Kind == HardwareKind.Temperature && i.Name is "Tctl" or "Tdie" or "CPU Package")?.Id;
    }

    private bool RefreshInventory()
    {
        var force = _rediscover;
        if (!_inventoryCadence.IsDue(_clock.Elapsed, force)) return false;
        _rediscover = false;
        // GPUs listed by hwmon (e.g. nouveau) are dropped when NVML reports the same card.
        var merged = InventoryMerger.Merge(
            _engines.Where(e => e != _nvidia && IsOn(e)).SelectMany(e => e.Discover()),
            _readNvidia ? _nvidia.Discover() : []);
        if (!_readSmart) merged = merged.Where(i => !IsSmart(i.Id)).ToList();
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
