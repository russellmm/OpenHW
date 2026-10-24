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

    public ColumnLayout(IReadOnlyList<double>? saved)
    {
        _widths = saved is { Count: 5 } && saved.Select((w, i) => w >= Minimums[i] && w < 4000).All(ok => ok)
            ? saved.ToArray()
            : (double[])Defaults.Clone();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public IReadOnlyList<double> Widths => _widths;

    // Bindable per column: a Grid's ColumnDefinition.Width binds to these.
    public GridLength C0 => new(_widths[0]);
    public GridLength C1 => new(_widths[1]);
    public GridLength C2 => new(_widths[2]);
    public GridLength C3 => new(_widths[3]);
    public GridLength C4 => new(_widths[4]);

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
