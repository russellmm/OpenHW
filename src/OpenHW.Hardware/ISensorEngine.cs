
namespace OpenHW.Hardware;

/// <summary>
/// One sensor suite (hwmon, NVIDIA, NVMe, EDAC, system, network): its own discovery and reads, so a change
/// to one suite cannot disturb another. Engines must tolerate re-enumeration every few seconds so hot-plugged
/// devices appear without a restart, and degrade to an empty inventory when their driver is missing.
/// </summary>
public interface ISensorEngine : IDisposable
{
    /// <summary>Engine name, for logs and diagnostics.</summary>
    string Name { get; }

    /// <summary>False when the backing driver/driver node is unavailable (degrade, don't crash).</summary>
    bool Available { get; }

    /// <summary>Re-enumerate sensors. Separate from live sampling; the hub rescans every five seconds.</summary>
    IReadOnlyList<HardwareItem> Discover();

    /// <summary>Read current values for all discovered items into <paramref name="readings"/>, keyed by item Id.</summary>
    void ReadInto(IDictionary<string, double?> readings);

    /// <summary>Aggregate row id → child row ids shown nested under it (per-core rows); most engines have none.</summary>
    IReadOnlyDictionary<string, IReadOnlyList<string>> Children => new Dictionary<string, IReadOnlyList<string>>();
}
