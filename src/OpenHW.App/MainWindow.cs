using System.Diagnostics;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Path = Avalonia.Controls.Shapes.Path;
using OpenHW.App.Sensors;
using OpenHW.App.SensorWindow;

namespace OpenHW.App;

/// <summary>
/// HWiNFO-style "Sensors Status" window: one virtualized list of group headings, sensor rows and spacers with
/// Current / Minimum / Maximum / Average columns. The tree is flattened into <see cref="_rows"/>; expanding or
/// collapsing inserts or removes just that slice so the scroll position stays put.
/// </summary>
public sealed class MainWindow : Window
{
    // Fixed widths so header and rows line up regardless of the list's scrollbar; the trailing * is HWiNFO's
    // empty band to the right of Average.
    private const string Columns = "362,96,96,96,96,*";
    private const double RowHeight = 20;

    private readonly SensorHub _hub;
    private readonly AvaloniaList<Row> _rows = [];
    private readonly List<GroupRow> _groups = [];
    private readonly ViewState _view = ViewState.Load();
    private readonly HashSet<string> _collapsedGroups;
    private readonly HashSet<string> _expandedSensors;
    private readonly Stopwatch _elapsed = Stopwatch.StartNew();
    private readonly TextBlock _elapsedText = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
    private readonly ListBox _list;

    public MainWindow(SensorHub hub)
    {
        _hub = hub;
        _collapsedGroups = _view.CollapsedGroups;
        _expandedSensors = _view.ExpandedSensors;
        Title = "OpenHW Sensors Status";
        Width = 820;
        Height = 1000;
        MinWidth = 500;
        Background = Palette.Window;
        Foreground = Palette.Text;
        FontSize = 12.5;

        _list = new ListBox
        {
            ItemsSource = _rows,
            Background = Palette.Window,
            Padding = new Thickness(0),
            // One template per row type, so a recycled container only ever receives a row of its own shape.
            DataTemplates =
            {
                new FuncDataTemplate<GroupRow>((_, _) => BuildGroupTemplate(), supportsRecycling: true),
                new FuncDataTemplate<SensorRow>((_, _) => BuildSensorTemplate(), supportsRecycling: true),
                new FuncDataTemplate<SpacerRow>((_, _) => BuildSpacerTemplate(), supportsRecycling: true),
            },
        };
        ScrollViewer.SetAllowAutoHide(_list, false);
        _list.Styles.Add(new Style(x => x.OfType<ListBoxItem>())
        {
            Setters =
            {
                new Setter(ListBoxItem.PaddingProperty, new Thickness(0)),
                new Setter(ListBoxItem.MinHeightProperty, 0.0),
                new Setter(ListBoxItem.HeightProperty, RowHeight),
            },
        });
        _list.DoubleTapped += (_, _) => Toggle(_list.SelectedItem as Row);
        _list.KeyDown += (_, e) =>
        {
            if (e.Key is Key.Left or Key.Right && _list.SelectedItem is Row row)
            {
                SetExpanded(row, e.Key == Key.Right);
                e.Handled = true;
            }
        };

        var footer = BuildFooter();
        var header = BuildHeader();
        DockPanel.SetDock(footer, Dock.Bottom);
        DockPanel.SetDock(header, Dock.Top);
        Content = new DockPanel { Children = { footer, header, _list } };

        RebuildTree();
    }

    /// <summary>
    /// Raised when rows were shown or hidden, after the hub has been told which sensors to read. Newly shown rows
    /// hold frozen values until the next poll, so the app polls right away.
    /// </summary>
    public event Action? ReadSetChanged;

    /// <summary>Re-reads the inventory into groups and rows, keeping each group's and subgroup's expansion.</summary>
    public void RebuildTree()
    {
        var byId = _hub.Inventory.ToDictionary(i => i.Id, StringComparer.Ordinal);
        _groups.Clear();
        foreach (var group in SensorPresentation.Build(_hub.Inventory, _hub.Children, _hub.Machine))
        {
            var groupRow = new GroupRow(group) { IsExpanded = !_collapsedGroups.Contains(group.Key) };
            foreach (var view in group.Sensors)
            {
                var row = new SensorRow(view, byId[view.Id], 0) { IsExpanded = _expandedSensors.Contains(view.Id) };
                row.Children.AddRange(view.Children.Select(child => new SensorRow(child, byId[child.Id], 1)));
                groupRow.Sensors.Add(row);
            }
            _groups.Add(groupRow);
        }

        var rows = new List<Row>();
        foreach (var group in _groups)
        {
            rows.Add(group);
            if (group.IsExpanded) rows.AddRange(VisibleRows(group));
            rows.Add(new SpacerRow());
        }
        _rows.Clear();
        _rows.AddRange(rows);
        Reshade();
        UpdateReadSet();
        RefreshValues();
    }

    // Only rows on screen (not inside a collapsed group or per-core list) are read. Sensors the window never
    // shows — configured limits, alarm bits — are not read either.
    private void UpdateReadSet()
    {
        var shown = _rows.OfType<SensorRow>().Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        _hub.SetPaused(_hub.Inventory.Select(i => i.Id).Where(id => !shown.Contains(id)));
        ReadSetChanged?.Invoke();
    }

    /// <summary>Pushes the hub's latest statistics into every row (collapsed ones too, so expanding is instant).</summary>
    public void RefreshValues()
    {
        foreach (var group in _groups)
            foreach (var sensor in group.Sensors)
            {
                sensor.Update(_hub.Get(sensor.Id), _hub.IsPaused(sensor.Id));
                foreach (var child in sensor.Children) child.Update(_hub.Get(child.Id), _hub.IsPaused(child.Id));
            }
        _elapsedText.Text = _elapsed.Elapsed.ToString(@"h\:mm\:ss");
    }

    private static IEnumerable<Row> VisibleRows(GroupRow group)
    {
        foreach (var sensor in group.Sensors)
        {
            yield return sensor;
            if (sensor.IsExpanded)
                foreach (var child in sensor.Children) yield return child;
        }
    }

    private void Toggle(Row? row)
    {
        switch (row)
        {
            case GroupRow group: SetExpanded(group, !group.IsExpanded); break;
            case SensorRow { HasChildren: true } sensor: SetExpanded(sensor, !sensor.IsExpanded); break;
        }
    }

    private void SetExpanded(Row row, bool expanded)
    {
        var index = _rows.IndexOf(row);
        if (index < 0) return;
        switch (row)
        {
            case GroupRow group when group.IsExpanded != expanded:
            {
                var slice = VisibleRows(group).ToList();
                group.IsExpanded = expanded;
                if (expanded) { _collapsedGroups.Remove(group.Group.Key); _rows.InsertRange(index + 1, slice); }
                else { _collapsedGroups.Add(group.Group.Key); _rows.RemoveRange(index + 1, slice.Count); }
                break;
            }
            case SensorRow { HasChildren: true } sensor when sensor.IsExpanded != expanded:
                sensor.IsExpanded = expanded;
                if (expanded) { _expandedSensors.Add(sensor.Id); _rows.InsertRange(index + 1, sensor.Children); }
                else { _expandedSensors.Remove(sensor.Id); _rows.RemoveRange(index + 1, sensor.Children.Count); }
                break;
            default:
                return;
        }
        Reshade();
        UpdateReadSet();
        _view.Save();
    }

    internal void SetAllExpanded(bool expanded)
    {
        foreach (var group in _groups)
            if (expanded) _collapsedGroups.Remove(group.Group.Key);
            else _collapsedGroups.Add(group.Group.Key);
        RebuildTree();
        _view.Save();
    }

    // HWiNFO restarts the black/grey alternation under every group heading.
    private void Reshade()
    {
        var odd = false;
        foreach (var row in _rows)
        {
            if (row is GroupRow) { row.Background = Palette.GroupHeader; odd = false; continue; }
            row.Background = odd ? Palette.RowB : Palette.RowA;
            odd = !odd;
        }
    }

    // ── templates ────────────────────────────────────────────────────────────────────────────────────────────

    private Control BuildGroupTemplate()
    {
        var grid = RowGrid();
        var chevron = ChevronButton();
        var title = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(10, 0, 0, 0),
            Children =
            {
                chevron,
                new Path { Data = Icons.Chip, Stroke = Icons.ChipGrey, StrokeThickness = 1.4, Fill = Icons.ChipGrey,
                           Width = 16, Height = 16, Margin = new Thickness(4, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center },
                Text(nameof(GroupRow.Title), FontWeight.Bold),
            },
        };
        Grid.SetColumnSpan(title, 5); // long titles run on over the value columns, as in HWiNFO
        AddValueCells(grid, withValues: false);
        grid.Children.Add(title);
        return grid;
    }

    private Control BuildSensorTemplate()
    {
        var grid = RowGrid();
        var chevron = ChevronButton();
        chevron.Bind(IsVisibleProperty, new Binding(nameof(SensorRow.ShowChevron)));
        var icon = new Path { Width = 16, Height = 16, StrokeThickness = 1.5, Stretch = Stretch.None,
                              Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
        icon.Bind(Path.DataProperty, new Binding(nameof(SensorRow.Icon)));
        icon.Bind(Shape.FillProperty, new Binding(nameof(SensorRow.IconBrush)));
        icon.Bind(Shape.StrokeProperty, new Binding(nameof(SensorRow.IconStroke)));
        var name = new StackPanel { Orientation = Orientation.Horizontal, Children = { chevron, icon, Text(nameof(SensorRow.Name)) } };
        name.Bind(MarginProperty, new Binding(nameof(SensorRow.Indent)));
        grid.Children.Add(name);
        AddValueCells(grid, withValues: true);
        return grid;
    }

    private static Control BuildSpacerTemplate()
    {
        var grid = RowGrid();
        AddValueCells(grid, withValues: false);
        return grid;
    }

    private static Grid RowGrid()
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions(Columns), Height = RowHeight };
        grid.Bind(Panel.BackgroundProperty, new Binding(nameof(Row.Background)));
        // Selection is an overlay so it shows over both shading and the "changed" highlight.
        var selection = new Border { Background = Palette.Selected, Opacity = 0.75, IsHitTestVisible = false };
        Grid.SetColumnSpan(selection, 5);
        selection.Bind(IsVisibleProperty, new Binding(nameof(ListBoxItem.IsSelected))
        {
            RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor) { AncestorType = typeof(ListBoxItem) },
        });
        grid.Children.Add(selection);
        return grid;
    }

    private static void AddValueCells(Grid grid, bool withValues)
    {
        string[] columns = [nameof(SensorRow.Current), nameof(SensorRow.Minimum), nameof(SensorRow.Maximum), nameof(SensorRow.Average)];
        for (var i = 0; i < columns.Length; i++)
        {
            var cell = new Border { BorderBrush = Palette.Separator, BorderThickness = new Thickness(1, 0, i == 3 ? 1 : 0, 0) };
            if (withValues)
            {
                cell.Child = Text(columns[i]);
                cell.Child.Bind(OpacityProperty, new Binding(nameof(SensorRow.ValueOpacity)));
                ((TextBlock)cell.Child).HorizontalAlignment = HorizontalAlignment.Right;
                ((TextBlock)cell.Child).Margin = new Thickness(0, 0, 6, 0);
                if (i == 0) cell.Bind(Border.BackgroundProperty, new Binding(nameof(SensorRow.CurrentBackground)));
            }
            Grid.SetColumn(cell, i + 1);
            grid.Children.Insert(0, cell); // under the selection overlay
        }
    }

    private Control ChevronButton()
    {
        var hit = new Border
        {
            Background = Brushes.Transparent, Width = 16, Height = RowHeight, Margin = new Thickness(0, 0, 2, 0),
            Child = new Path { Stroke = Icons.ChevronGrey, StrokeThickness = 1.3, Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Center },
        };
        ((Path)hit.Child).Bind(Path.DataProperty, new Binding("Chevron"));
        hit.Tapped += (_, e) => { Toggle(hit.DataContext as Row); e.Handled = true; };
        return hit;
    }

    private static TextBlock Text(string path, FontWeight weight = FontWeight.Normal)
    {
        var text = new TextBlock { FontWeight = weight, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        text.Bind(TextBlock.TextProperty, new Binding(path));
        return text;
    }

    // ── header / footer ──────────────────────────────────────────────────────────────────────────────────────

    private static Control BuildHeader()
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions(Columns), Height = 24, Background = Palette.ColumnHeader };
        string[] titles = ["Sensor", "Current", "Minimum", "Maximum", "Average"];
        for (var i = 0; i < titles.Length; i++)
        {
            var cell = new Border
            {
                BorderBrush = Palette.GroupHeader, BorderThickness = new Thickness(i == 0 ? 0 : 1, 0, 0, 1),
                Child = new TextBlock
                {
                    Text = titles[i], VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = i == 0 ? HorizontalAlignment.Left : HorizontalAlignment.Right,
                    Margin = i == 0 ? new Thickness(6, 0, 0, 0) : new Thickness(0, 0, 6, 0),
                },
            };
            Grid.SetColumn(cell, i);
            grid.Children.Add(cell);
        }
        return grid;
    }

    private Control BuildFooter()
    {
        var expand = FooterButton("Expand all", FooterIcons.Expand, () => SetAllExpanded(true));
        var collapse = FooterButton("Collapse all", FooterIcons.Collapse, () => SetAllExpanded(false));
        var reset = FooterButton("Reset Min/Max/Average", FooterIcons.Clock, () =>
        {
            _hub.ResetStatistics();
            _elapsed.Restart();
            RefreshValues();
        });
        var close = FooterButton("Close (OpenHW keeps running in the tray)", FooterIcons.Close, Hide);

        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { _elapsedText, reset, close } };
        var bar = new DockPanel { Height = 54, Background = Palette.Window, Margin = new Thickness(4, 0) };
        DockPanel.SetDock(right, Dock.Right);
        bar.Children.Add(right);
        bar.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { expand, collapse } });
        return bar;
    }

    private static Button FooterButton(string tip, (Geometry Data, IBrush? Fill, IBrush? Stroke) icon, Action onClick)
    {
        var button = new Button
        {
            Width = 50, Height = 44, Padding = new Thickness(0), VerticalAlignment = VerticalAlignment.Center,
            Background = FooterIcons.ButtonFace, BorderBrush = FooterIcons.ButtonEdge, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(2),
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
            Content = new Path { Data = icon.Data, Fill = icon.Fill, Stroke = icon.Stroke, StrokeThickness = 2.4,
                                 Width = 30, Height = 30, Stretch = Stretch.Uniform },
        };
        ToolTip.SetTip(button, tip);
        button.Click += (_, _) => onClick();
        return button;
    }

    // HWiNFO keeps running in the tray when its window closes; hide instead of destroying the window.
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!e.IsProgrammatic)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnClosing(e);
    }
}
