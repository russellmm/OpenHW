namespace OpenHW.App.Sensors;

/// <summary>
/// The last <see cref="Span"/> of readings for every sensor that was read, for graphs. One ring per sensor of
/// (seconds since start, value) as floats — about 4 MB for ~600 sensors at 2 s polls. A poll that didn't read a
/// sensor (collapsed group, monitoring off) records nothing, so a graph shows a gap rather than a made-up line.
/// </summary>
public sealed class SensorHistory
{
    public static readonly TimeSpan Span = TimeSpan.FromMinutes(30);

    private readonly Dictionary<string, Ring> _rings = new(StringComparer.Ordinal);
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private readonly object _gate = new();
    private readonly int _capacity;

    public SensorHistory(TimeSpan pollPeriod) => _capacity = (int)Math.Ceiling(Span / pollPeriod) + 2;

    public double Now => _clock.Elapsed.TotalSeconds;

    /// <summary>Adds one poll: every sensor the hub read this time, by id.</summary>
    public void Record(IEnumerable<(string Id, double Value)> readings)
    {
        var t = (float)Now;
        lock (_gate)
            foreach (var (id, value) in readings)
            {
                if (!double.IsFinite(value)) continue;
                if (!_rings.TryGetValue(id, out var ring)) _rings[id] = ring = new Ring(_capacity);
                ring.Add(t, (float)value);
            }
    }

    /// <summary>The sensor's points within <see cref="Span"/>, oldest first, as (seconds before now, value).</summary>
    public IReadOnlyList<(double Age, double Value)> Get(string id)
    {
        var now = Now;
        lock (_gate)
        {
            if (!_rings.TryGetValue(id, out var ring)) return [];
            return ring.Items().Where(p => now - p.T <= Span.TotalSeconds).Select(p => (now - p.T, (double)p.V)).ToList();
        }
    }

    private sealed class Ring(int capacity)
    {
        private readonly float[] _t = new float[capacity], _v = new float[capacity];
        private int _next, _count;

        public void Add(float t, float v)
        {
            _t[_next] = t;
            _v[_next] = v;
            _next = (_next + 1) % _t.Length;
            _count = Math.Min(_count + 1, _t.Length);
        }

        public IEnumerable<(float T, float V)> Items()
        {
            var start = (_next - _count + _t.Length) % _t.Length;
            for (var i = 0; i < _count; i++)
            {
                var k = (start + i) % _t.Length;
                yield return (_t[k], _v[k]);
            }
        }
    }
}
