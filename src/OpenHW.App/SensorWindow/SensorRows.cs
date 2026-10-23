using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Media;
using OpenHW.Hardware;
using OpenHW.App.Sensors;

namespace OpenHW.App.SensorWindow;

/// <summary>
/// One line of the flattened tree the list virtualizes. Containers are recycled across rows, so everything a
/// row template shows (indent, icon, shading) is a bindable property here rather than baked into the control.
/// </summary>
public abstract class Row : INotifyPropertyChanged
{
    private IBrush _background = Palette.RowA;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Alternating shading, recomputed whenever rows are inserted or removed.</summary>
    public IBrush Background { get => _background; set => Set(ref _background, value); }

    protected void Notify(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

public sealed class GroupRow(SensorGroupView group) : Row
{
    private bool _isExpanded = true;

    public SensorGroupView Group { get; } = group;
    public string Title => Group.Title;
    public List<SensorRow> Sensors { get; } = [];
    public bool IsExpanded { get => _isExpanded; set { Set(ref _isExpanded, value); Notify(nameof(Chevron)); } }
    public Geometry Chevron => IsExpanded ? Icons.ChevronDown : Icons.ChevronRight;
}

/// <summary>Blank line between groups, as HWiNFO draws.</summary>
public sealed class SpacerRow : Row;

public sealed class SensorRow : Row
{
    private string _current = "", _minimum = "", _maximum = "", _average = "";
    private bool _changed, _isExpanded;
    private IBrush _currentBackground = Brushes.Transparent;

    public SensorRow(SensorView view, HardwareItem item, int depth)
    {
        View = view;
        Item = item;
        Depth = depth;
        Children = view.Children.Count == 0 ? [] : new List<SensorRow>(view.Children.Count);
        (Icon, IconBrush, IconStroke) = Icons.For(view.Kind);
    }

    public SensorView View { get; }
    public HardwareItem Item { get; }
    public string Id => View.Id;
    public string Name => View.Name;
    public int Depth { get; }
    public List<SensorRow> Children { get; }
    public bool HasChildren => Children.Count > 0;

    public Geometry Icon { get; }
    public IBrush? IconBrush { get; }
    public IBrush? IconStroke { get; }

    // HWiNFO: chevron column at 22 px, icon at 38 px; a child row shifts both one level right.
    public Thickness Indent => new(Depth == 0 ? 22 : 40, 0, 0, 0);
    public bool ShowChevron => HasChildren;
    public Geometry Chevron => IsExpanded ? Icons.ChevronDown : Icons.ChevronRight;

    public bool IsExpanded
    {
        get => _isExpanded;
        set { Set(ref _isExpanded, value); Notify(nameof(Chevron)); }
    }

    public string Current { get => _current; private set => Set(ref _current, value); }
    public string Minimum { get => _minimum; private set => Set(ref _minimum, value); }
    public string Maximum { get => _maximum; private set => Set(ref _maximum, value); }
    public string Average { get => _average; private set => Set(ref _average, value); }

    /// <summary>"Highlight changing values": the Current cell turns blue while it differs from the last sample.</summary>
    public IBrush CurrentBackground { get => _currentBackground; private set => Set(ref _currentBackground, value); }

    public void Update(SensorStatistics.Snapshot snapshot)
    {
        var unit = View.Unit;
        double? Scaled(double? v) => v is { } x ? SensorPresentation.Scale(Item, x) : null;
        var current = SensorPresentation.Format(Scaled(snapshot.Current), unit);
        _changed = Current.Length > 0 && current != Current;
        Current = current;
        Minimum = SensorPresentation.Format(Scaled(snapshot.Minimum), unit);
        Maximum = SensorPresentation.Format(Scaled(snapshot.Maximum), unit);
        Average = SensorPresentation.FormatAverage(Scaled(snapshot.Average), unit);
        CurrentBackground = _changed ? Palette.Changed : Brushes.Transparent;
    }
}

public static class Palette
{
    // Sampled from the HWiNFO 8.54 reference captures (dark theme).
    public static readonly IBrush Window = new SolidColorBrush(Color.Parse("#000000"));
    public static readonly IBrush RowA = new SolidColorBrush(Color.Parse("#000000"));
    public static readonly IBrush RowB = new SolidColorBrush(Color.Parse("#171717"));
    public static readonly IBrush GroupHeader = new SolidColorBrush(Color.Parse("#232323"));
    public static readonly IBrush ColumnHeader = new SolidColorBrush(Color.Parse("#141414"));
    public static readonly IBrush Changed = new SolidColorBrush(Color.Parse("#152137"));
    public static readonly IBrush Separator = new SolidColorBrush(Color.Parse("#202932"));
    public static readonly IBrush Text = new SolidColorBrush(Color.Parse("#E6E6E6"));
    public static readonly IBrush Selected = new SolidColorBrush(Color.Parse("#0E3A66"));
}
