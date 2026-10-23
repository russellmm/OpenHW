
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

    /// <summary>
    /// Read current values into <paramref name="readings"/>, keyed by item Id. When <paramref name="requested"/> is
    /// given, only those ids are needed: engines skip whatever hardware access they can for the rest (collapsed
    /// groups in the window), and may leave unrequested ids out of <paramref name="readings"/>.
    /// </summary>
    void ReadInto(IDictionary<string, double?> readings, IReadOnlySet<string>? requested);

    /// <summary>Read everything.</summary>
    void ReadInto(IDictionary<string, double?> readings) => ReadInto(readings, null);

    /// <summary>Aggregate row id → child row ids shown nested under it (per-core rows); most engines have none.</summary>
    IReadOnlyDictionary<string, IReadOnlyList<string>> Children => new Dictionary<string, IReadOnlyList<string>>();
}
