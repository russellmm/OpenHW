using System.Globalization;
using System.Text;

namespace OpenHW.App.Sensors;

/// <summary>A CSV column: the sensor, its header ("Device: Sensor [unit]"), and how to turn a raw reading into the logged value.</summary>
public sealed record LogColumn(string Id, string Header, Func<double, double> Scale);

/// <summary>
/// File → Start Logging: one CSV row per poll — Date, Time, then each column's current value (blank when the sensor
/// had no reading). Columns are fixed when the log starts. Invariant culture ("." decimals), RFC 4180 quoting, flushed
/// every row so the file is usable while it grows and complete if OpenHW stops.
/// </summary>
public sealed class SensorLogger : IDisposable
{
    private readonly StreamWriter _writer;
    private readonly IReadOnlyList<LogColumn> _columns;
    private readonly object _gate = new();

    public string Path { get; }
    public int Rows { get; private set; }

    public SensorLogger(string path, IReadOnlyList<LogColumn> columns)
    {
        Path = path;
        _columns = columns;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
        _writer = new StreamWriter(path, append: false, new UTF8Encoding(false));
        _writer.WriteLine(string.Join(",", new[] { "Date", "Time" }.Concat(columns.Select(c => Quote(c.Header)))));
        _writer.Flush();
    }

    /// <summary>Appends one row: <paramref name="value"/> gives each sensor's raw current reading (null = none).</summary>
    public void Append(DateTime at, Func<string, double?> value)
    {
        var cells = new List<string>(_columns.Count + 2)
        {
            at.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            at.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
        };
        foreach (var column in _columns)
            cells.Add(value(column.Id) is { } raw && double.IsFinite(raw)
                ? column.Scale(raw).ToString("0.###", CultureInfo.InvariantCulture)
                : "");
        lock (_gate)
        {
            _writer.WriteLine(string.Join(",", cells));
            _writer.Flush();
            Rows++;
        }
    }

    /// <summary>
    /// The columns a new log gets: every sensor the window shows, in window order, except those with monitoring off
    /// or logging switched off — headed "Device: Sensor [unit]" with the user's names.
    /// </summary>
    public static IReadOnlyList<LogColumn> ColumnsFor(SensorHub hub)
    {
        var custom = SensorCustomizations.Current;
        var items = hub.Inventory.ToDictionary(i => i.Id, StringComparer.Ordinal);
        var columns = new List<LogColumn>();
        void Add(string group, SensorView view)
        {
            if (!custom.IsDisabled(view.Id) && custom.IsLogged(view.Id) && items.TryGetValue(view.Id, out var item))
                columns.Add(new LogColumn(view.Id, $"{group}: {view.Name}" + (view.Unit is "" or "flag" ? "" : $" [{view.Unit}]"),
                    raw => SensorPresentation.Scale(item, raw)));
            foreach (var child in view.Children) Add(group, child);
        }
        foreach (var group in SensorPresentation.Build(hub.Inventory, hub.Children, hub.Machine))
            foreach (var view in group.Sensors) Add(group.Title, view);
        return columns;
    }

    private static string Quote(string text) =>
        text.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + text.Replace("\"", "\"\"") + "\"" : text;

    public void Dispose()
    {
        lock (_gate) _writer.Dispose();
    }
}
