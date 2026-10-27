namespace OpenHW.Hardware;

/// <summary>Sample statistics shared by views; recording happens once per hardware poll, never per repaint.</summary>
public sealed class SensorStatistics
{
    private double WindowAverage(string id, double current)
    {
        var (window, sum) = _windows.TryGetValue(id, out var w) ? w : (new Queue<double>(), 0);
        window.Enqueue(current);
        sum += current;
        while (window.Count > _averageSamples) sum -= window.Dequeue();
        _windows[id] = (window, sum);
        return sum / window.Count;
    }

    public readonly record struct Snapshot(double? Current, double? Minimum, double? Maximum, double? Average, long Samples);
    private readonly Dictionary<string, Snapshot> _values = new(StringComparer.OrdinalIgnoreCase);
    // Averaging over the last N samples (HWiNFO's "Average last N samples"): each sensor's window and its sum.
    private readonly Dictionary<string, (Queue<double> Window, double Sum)> _windows = new(StringComparer.OrdinalIgnoreCase);
    private int _averageSamples;

    public Snapshot Get(string id) => _values.GetValueOrDefault(id);

    public void Reset()
    {
        _values.Clear();
        _windows.Clear();
    }

    /// <summary>
    /// How many recent samples Average covers; 0 (the default) = every sample since the last reset. A change starts
    /// each window afresh from the next sample (the averages shown until then are kept).
    /// </summary>
    public int AverageSamples
    {
        get => _averageSamples;
        set
        {
            value = Math.Max(0, value);
            if (value == _averageSamples) return;
            _averageSamples = value;
            _windows.Clear();
        }
    }
    /// <param name="frozen">Ids that were deliberately not read this poll (collapsed in the window): their
    /// statistics are kept exactly as they were instead of being cleared or updated.</param>
    public void Record(IReadOnlyDictionary<string, double?> readings, IReadOnlySet<string>? frozen = null)
    {
        foreach (var id in _values.Keys.Where(id => !readings.ContainsKey(id) && frozen?.Contains(id) != true).ToArray())
        {
            _values.Remove(id);
            _windows.Remove(id);
        }
        foreach (var (id, value) in readings)
        {
            var previous = Get(id);
            if (value is not { } current || !double.IsFinite(current))
            {
                _values[id] = previous with { Current = null };
                continue;
            }
            var count = previous.Samples + 1;
            _values[id] = new Snapshot(current, Math.Min(previous.Minimum ?? current, current),
                Math.Max(previous.Maximum ?? current, current),
                _averageSamples > 0 ? WindowAverage(id, current)
                    : previous.Average is { } average ? average + (current - average) / count : current, count);
        }
    }
}

/// <summary>Separates slow inventory discovery from live sampling. Callers can always force a rescan.</summary>
public sealed class InventoryRefreshCadence(TimeSpan interval)
{
    private TimeSpan? _last;
    public bool IsDue(TimeSpan now, bool force = false)
    {
        if (!force && _last is { } last && now - last < interval) return false;
        _last = now;
        return true;
    }
}
