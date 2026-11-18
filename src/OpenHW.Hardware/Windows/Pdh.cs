using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace OpenHW.Hardware.Windows;

/// <summary>
/// A Windows performance-counter query (PDH) over wildcard counters such as <c>\Processor Information(*)\% Processor Time</c>.
/// Rate counters need two collections, so the first <see cref="Collect"/> only sets the baseline. English counter
/// names, so it works on any display language. Not thread-safe: one engine, one query.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed unsafe class Pdh : IDisposable
{
    private nint _query;
    private readonly Dictionary<string, nint> _counters = new(StringComparer.Ordinal);

    public Pdh(IEnumerable<string> paths)
    {
        if (Win32.PdhOpenQuery(null, 0, out _query) != 0) { _query = 0; return; }
        foreach (var path in paths)
            if (Win32.PdhAddEnglishCounter(_query, path, 0, out var counter) == 0) _counters[path] = counter;
    }

    public bool Has(string path) => _counters.ContainsKey(path);

    public bool Collect() => _query != 0 && Win32.PdhCollectQueryData(_query) == 0;

    /// <summary>Instance name → value for one counter, from the last collection; empty until two collections exist.</summary>
    public IReadOnlyDictionary<string, double> Values(string path)
    {
        var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        if (!_counters.TryGetValue(path, out var counter)) return result;
        uint size = 0;
        var status = Win32.PdhGetFormattedCounterArray(counter, Win32.PdhFmtDouble | Win32.PdhFmtNoCap100, ref size, out _, null);
        if (status != Win32.PdhMoreData || size == 0) return result;
        var buffer = new byte[size];
        fixed (byte* p = buffer)
        {
            if (Win32.PdhGetFormattedCounterArray(counter, Win32.PdhFmtDouble | Win32.PdhFmtNoCap100, ref size, out var count, p) != 0) return result;
            // PDH_FMT_COUNTERVALUE_ITEM_W: name pointer, then { CStatus, (pad), double } = 24 bytes on x64.
            var stride = nint.Size + 16;
            for (var i = 0; i < count; i++)
            {
                var item = p + i * stride;
                var name = Marshal.PtrToStringUni(*(nint*)item) ?? "";
                var cstatus = *(uint*)(item + nint.Size);
                if (cstatus is Win32.PdhCstatusValidData or Win32.PdhCstatusNewData) result[name] = *(double*)(item + nint.Size + 8);
            }
        }
        return result;
    }

    public void Dispose()
    {
        if (_query != 0) Win32.PdhCloseQuery(_query);
        _query = 0;
    }
}
