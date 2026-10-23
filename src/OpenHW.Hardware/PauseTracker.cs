namespace OpenHW.Hardware;

/// <summary>
/// Decides, poll by poll, which sensors are frozen: collapsed ones (unless pinned, e.g. tray icons), plus ones
/// that just came back on screen but have not produced a value yet. Rate and usage sensors need two samples
/// after their baseline was dropped, so without the second rule they would show blank for a poll on resume;
/// with it they keep showing their last (dimmed) value until a real reading arrives.
/// </summary>
public sealed class PauseTracker
{
    /// <summary>Polls a resumed sensor may take to produce a value before "no value" is accepted as its reading.</summary>
    public const int ResumeAttempts = 2;

    private readonly object _gate = new();
    private readonly Dictionary<string, int> _resuming = new(StringComparer.Ordinal);
    private IReadOnlySet<string> _paused = new HashSet<string>();
    private IReadOnlySet<string> _pinned = new HashSet<string>();
    private HashSet<string> _lastFrozen = new(StringComparer.Ordinal);

    public void SetPaused(IEnumerable<string> ids)
    {
        var set = ids.ToHashSet(StringComparer.Ordinal);
        lock (_gate) _paused = set;
    }

    public void SetPinned(IEnumerable<string> ids)
    {
        var set = ids.ToHashSet(StringComparer.Ordinal);
        lock (_gate) _pinned = set;
    }

    /// <summary>True while the sensor's values are frozen: collapsed and not pinned, or resuming without a value yet.</summary>
    public bool IsPaused(string id)
    {
        lock (_gate) return (_paused.Contains(id) && !_pinned.Contains(id)) || _resuming.ContainsKey(id);
    }

    /// <summary>Start of a poll: the ids not to read. Ids that were frozen last poll and are not now start resuming.</summary>
    public IReadOnlySet<string> BeginPoll()
    {
        lock (_gate)
        {
            var frozen = _paused.Where(id => !_pinned.Contains(id)).ToHashSet(StringComparer.Ordinal);
            foreach (var id in _lastFrozen.Where(id => !frozen.Contains(id))) _resuming[id] = ResumeAttempts;
            foreach (var id in frozen) _resuming.Remove(id);
            _lastFrozen = frozen;
            return frozen;
        }
    }

    /// <summary>
    /// End of a poll: removes frozen ids from <paramref name="readings"/> (engines that read whole sections may
    /// have filled them) and holds back resuming ids that still have no value. Returns the ids whose statistics
    /// must stay as they were.
    /// </summary>
    public IReadOnlySet<string> EndPoll(IDictionary<string, double?> readings, IReadOnlySet<string> frozen)
    {
        lock (_gate)
        {
            var hold = new HashSet<string>(frozen, StringComparer.Ordinal);
            foreach (var id in frozen) readings.Remove(id);
            foreach (var (id, left) in _resuming.ToList())
            {
                if (readings.TryGetValue(id, out var value) && value is not null) _resuming.Remove(id);
                else if (left <= 1) _resuming.Remove(id); // still nothing: the sensor genuinely has no value now
                else
                {
                    _resuming[id] = left - 1;
                    hold.Add(id);
                    readings.Remove(id);
                }
            }
            return hold;
        }
    }
}
