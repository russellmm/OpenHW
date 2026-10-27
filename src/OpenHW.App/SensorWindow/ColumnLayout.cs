using System.ComponentModel;
using Avalonia.Controls;

namespace OpenHW.App.SensorWindow;

/// <summary>
/// The sensor list's column widths (Sensor, Current, Minimum, Maximum, Average), shared by the header and every row so
/// dragging a header edge resizes the column everywhere. Saved with the view state.
/// </summary>
public sealed class ColumnLayout : INotifyPropertyChanged
{
    public static readonly double[] Defaults = [362, 96, 96, 96, 96];
    /// <summary>Narrowest each column may go: the Sensor column keeps room for its indent, icon and a few characters.</summary>
    public static readonly double[] Minimums = [140, 48, 48, 48, 48];

    private readonly double[] _widths;
    private readonly bool[] _shown = [true, true, true, true, true];

    public ColumnLayout(IReadOnlyList<double>? saved)
    {
        _widths = saved is { Count: 5 } && saved.Select((w, i) => w >= Minimums[i] && w < 4000).All(ok => ok)
            ? saved.ToArray()
            : (double[])Defaults.Clone();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public IReadOnlyList<double> Widths => _widths;

    // Bindable per column: a Grid's ColumnDefinition.Width binds to these.
    public GridLength C0 => Width(0);
    public GridLength C1 => Width(1);
    public GridLength C2 => Width(2);
    public GridLength C3 => Width(3);
    public GridLength C4 => Width(4);

    private GridLength Width(int column) => new(_shown[column] ? _widths[column] : 0);

    /// <summary>Sensor Settings → Show columns: a hidden column takes no width (its saved width is kept).</summary>
    public bool IsShown(int column) => _shown[column];

    public void SetShown(int column, bool shown)
    {
        if (column == 0 || _shown[column] == shown) return; // the Sensor column always shows
        _shown[column] = shown;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("C" + column));
    }

    public void Set(int column, double width)
    {
        width = Math.Max(Minimums[column], Math.Round(width));
        if (_widths[column] == width) return;
        _widths[column] = width;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("C" + column));
    }

    public void Reset(int column) => Set(column, Defaults[column]);

    /// <summary>Five bound columns plus the trailing "*" band, for a row or the header.</summary>
    public ColumnDefinitions Definitions()
    {
        var definitions = new ColumnDefinitions();
        for (var i = 0; i < 5; i++)
        {
            var definition = new ColumnDefinition();
            definition.Bind(ColumnDefinition.WidthProperty, new Avalonia.Data.Binding("C" + i) { Source = this });
            definitions.Add(definition);
        }
        definitions.Add(new ColumnDefinition(GridLength.Star));
        return definitions;
    }
}
