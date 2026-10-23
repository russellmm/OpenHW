using OpenFan.Core.Hardware;
using OpenFan.Linux.Hw;

namespace OpenHW.App.Sensors;

/// <summary>
/// Owns the openfan-linux sensor backends and samples all of them on one cadence, so the sensor window and tray
/// icons always show the same sample. Read-only: OpenHW never writes fan controls or limits.
/// </summary>
public sealed class SensorHub : IDisposable
{
    private readonly HwmonBackend _hwmon = new();
    private readonly LibSensorsBackend _libSensors = new();
    private readonly NvmlBackend _nvml = new();
    private readonly EdacBackend _edac = new();
    private readonly NvmeHealthBackend _nvmeHealth = new();
    private readonly NvmeActivityBackend _nvmeActivity = new();
    private readonly SystemBackend _system = new();
    private readonly NvmlExtrasBackend _nvmlExtras = new();
    private readonly NetworkBackend _network = new();
    private readonly InventoryRefreshCadence _inventoryCadence = new(TimeSpan.FromSeconds(5));
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private readonly object _gate = new();
    private bool _usingLibSensors;
    private IReadOnlyList<HardwareItem> _inventory = [];
    private string _fingerprint = "";

    public SensorStatistics Statistics { get; } = new();

    /// <summary>Current inventory; replaced (never mutated) when discovery finds a different set.</summary>
    public IReadOnlyList<HardwareItem> Inventory { get { lock (_gate) return _inventory; } }

    /// <summary>Aggregate row id → child row ids (per-core clocks and usage).</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Children => _system.Children;

    /// <summary>Board, CPU and drive identity used for group titles; read once.</summary>
    public SensorPresentation.MachineInfo Machine => _machine ??= new(
        ReadText("/sys/class/dmi/id/board_vendor"), ReadText("/sys/class/dmi/id/board_name"),
        SystemBackend.CpuModel(), _nvmeHealth.Devices);
    private SensorPresentation.MachineInfo? _machine;

    /// <summary>"libsensors" or the sysfs fallback, for the status line.</summary>
    public string ReaderDescription => _usingLibSensors ? "libsensors" : "sysfs fallback";

    /// <summary>Raised on the sampling thread after <see cref="Sample"/> when discovery changed the inventory.</summary>
    public event Action? InventoryChanged;

    /// <summary>One poll: rediscover when due, read every sensor, then fold the sample into the statistics.</summary>
    public void Sample()
    {
        var changed = RefreshInventory();
        var inventory = Inventory;
        var readings = inventory.ToDictionary(i => i.Id, _ => (double?)null, StringComparer.OrdinalIgnoreCase);
        _system.ReadInto(readings);
        if (_usingLibSensors) _libSensors.ReadInto(readings);
        else _hwmon.ReadInto(readings);
        if (_nvml.Available)
        {
            _nvml.ReadInto(readings);
            _nvmlExtras.ReadInto(readings);
        }
        _network.ReadInto(readings);
        _edac.ReadInto(readings);
        _nvmeHealth.ReadInto(readings);
        _nvmeActivity.ReadInto(readings);
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

    /// <summary>Drive model / description for an NVMe group key ("nvme:pci:…"), when known.</summary>
    public NvmeDevice? NvmeDevice(string group) => _nvmeHealth.Devices.FirstOrDefault(d => d.Group == group);

    private bool RefreshInventory()
    {
        if (!_inventoryCadence.IsDue(_clock.Elapsed)) return false;
        var library = _libSensors.Available ? _libSensors.Discover() : [];
        _usingLibSensors = _libSensors.Available && _libSensors.Error is null;
        var board = _usingLibSensors ? library : _hwmon.Discover().Where(i => i.Kind != HardwareKind.Control);
        var gpus = _nvml.Available ? _nvml.Discover() : [];
        _nvmlExtras.UseGpus(gpus);
        var merged = InventoryMerger.Merge(
            _system.Discover().Concat(board).Concat(_edac.Discover()).Concat(_nvmeHealth.Discover())
                .Concat(_nvmeActivity.Discover()).Concat(_network.Discover()),
            gpus.Concat(_nvmlExtras.Discover()));
        var fingerprint = string.Join('|', merged.Select(i => $"{i.Id}\t{i.Name}\t{i.Group}").OrderBy(x => x, StringComparer.Ordinal));
        if (fingerprint == _fingerprint) return false;
        lock (_gate)
        {
            _inventory = merged;
            _fingerprint = fingerprint;
        }
        return true;
    }

    private static string ReadText(string path)
    {
        try { return File.ReadAllText(path).Trim(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return ""; }
    }

    public void Dispose()
    {
        _libSensors.Dispose();
        _hwmon.Dispose();
        _nvml.Dispose();
        _edac.Dispose();
        _nvmeHealth.Dispose();
        _nvmeActivity.Dispose();
        _nvmlExtras.Dispose();
        _network.Dispose();
    }
}
