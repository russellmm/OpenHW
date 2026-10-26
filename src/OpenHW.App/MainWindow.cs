using System.Diagnostics;
using System.Reflection;
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
using OpenHW.App.Power;
using OpenHW.App.Sensors;
using OpenHW.App.SensorWindow;

namespace OpenHW.App;

/// <summary>App-level actions the window's menus and footer shortcuts call.</summary>
public sealed record WindowCommands(Action ShowCpuFrequency, Action ShowPowerLimits, Action ShowSettings, Action Exit)
{
    /// <summary>Tray icons for sensor rows (the row context menu); null where the app has no tray.</summary>
    public Func<string, TrayState>? TrayStateOf { get; init; }
    public Action<string>? AddToTray { get; init; }
    public Action<string>? RemoveFromTray { get; init; }

    /// <summary>File → Tray Icons… and a sensor's "Tray icon settings…".</summary>
    public Action? ShowTrayIcons { get; init; }

    /// <summary>Power → GPU Lending… (the card filter's setup page).</summary>
    public Action? ShowGpuLending { get; init; }

    /// <summary>File → Alerts… and a sensor's "Alert settings…".</summary>
    public Action? ShowAlerts { get; init; }
    public Action<string>? AlertSettings { get; init; }

    /// <summary>Sensors with a firing alert (their Current cell turns red).</summary>
    public Func<IReadOnlySet<string>>? Alerting { get; init; }

    /// <summary>View → Layout Settings… and a sensor's "Layout settings…" (which selects that sensor).</summary>
    public Action<string?>? ShowLayout { get; init; }

    /// <summary>Opens (or brings forward) a sensor's graph window.</summary>
    public Action<string>? ShowGraph { get; init; }

    /// <summary>CSV logging (File menu); null where the app doesn't log.</summary>
    public Func<bool>? IsLogging { get; init; }
    public Action? StartLogging { get; init; }
    public Action? StopLogging { get; init; }
}

/// <summary>Whether a sensor can be added to, or removed from, the tray — and why not.</summary>
public sealed record TrayState(bool InTray, bool CanAdd, string? WhyNot);

/// <summary>
/// HWiNFO-style "Sensors Status" window: one virtualized list of group headings, sensor rows and spacers with
/// Current / Minimum / Maximum / Average columns. The tree is flattened into <see cref="_rows"/>; expanding or
/// collapsing inserts or removes just that slice so the scroll position stays put.
/// </summary>
public sealed class MainWindow : Window
{
    // Fixed widths so header and rows line up regardless of the list's scrollbar; the trailing * is HWiNFO's
    // empty band to the right of Average.
    private readonly ColumnLayout _columns;
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
    private readonly SensorCustomizations _custom = SensorCustomizations.Current;
    private bool _showHidden; // View → Show Hidden Sensors: hidden rows come back, grayed, so they can be unhidden
    private readonly PowerModeController? _power;
    // Power mode, caps and limits live in the Power menu; the footer only reports a failed power action.
    private readonly TextBlock _powerError = new()
    {
        IsVisible = false, Foreground = new SolidColorBrush(Color.Parse("#F07070")), FontSize = 11,
        VerticalAlignment = VerticalAlignment.Center, MaxWidth = 260, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 8, 0),
    };
    private readonly TextBlock _logging = new()
    {
        IsVisible = false, Foreground = new SolidColorBrush(Color.Parse("#F07070")), FontSize = 11,
        VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0),
    };
    private readonly WindowCommands? _commands;
    private MenuItem? _powerMenu;
    private readonly List<MenuItem> _profileItems = [];
    private MenuItem? _fixCapItem;

    public MainWindow(SensorHub hub, PowerModeController? power = null, WindowCommands? commands = null)
    {
        _hub = hub;
        _power = power;
        _commands = commands;
        _columns = new ColumnLayout(_view.ColumnWidths);
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

        _custom.Changed += RebuildTree;
        _list.KeyDown += (_, e) =>
        {
            if (_list.SelectedItem is not SensorRow row) return;
            switch (e.Key)
            {
                case Key.Delete when e.KeyModifiers == KeyModifiers.Shift: _custom.SetHidden(row.Id, !row.HiddenByUser); break;
                case Key.Delete: _custom.SetDisabled(row.Id, true); break;
                case Key.Insert: _custom.SetDisabled(row.Id, false); break;
                case Key.F2: _ = RenameAsync(row); break;
                default: return;
            }
            e.Handled = true;
        };

        var menu = BuildMenu();
        var footer = BuildFooter();
        var header = BuildHeader();
        DockPanel.SetDock(menu, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        DockPanel.SetDock(header, Dock.Top);
        Content = new DockPanel { Children = { menu, footer, header, _list } };

        RestoreWindow();
        RebuildTree();
    }

    /// <summary>Re-reads the inventory into groups and rows, keeping each group's and subgroup's expansion.</summary>
    public void RebuildTree()
    {
        var byId = _hub.Inventory.ToDictionary(i => i.Id, StringComparer.Ordinal);
        bool Shown(string id) => _showHidden || !_custom.IsHidden(id);
        SensorRow Row(SensorView view, int depth) => new(view, byId[view.Id], depth)
        {
            Disabled = _custom.IsDisabled(view.Id), HiddenByUser = _custom.IsHidden(view.Id),
            IsExpanded = depth == 0 && _expandedSensors.Contains(view.Id),
        };
        _groups.Clear();
        foreach (var group in SensorPresentation.Build(_hub.Inventory, _hub.Children, _hub.Machine))
        {
            var groupRow = new GroupRow(group) { IsExpanded = !_collapsedGroups.Contains(group.Key) };
            foreach (var view in group.Sensors.Where(v => Shown(v.Id)))
            {
                var row = Row(view, 0);
                row.Children.AddRange(view.Children.Where(c => Shown(c.Id)).Select(child => Row(child, 1)));
                groupRow.Sensors.Add(row);
            }
            if (groupRow.Sensors.Count > 0) _groups.Add(groupRow); // a group whose rows are all hidden goes too
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
    // shows — configured limits, alarm bits — are not read either. Newly shown rows stay gray with their frozen
    // values until the next regular poll brings them back to life.
    private void UpdateReadSet()
    {
        // Monitoring turned off counts as not shown: those sensors aren't read either (tray icons still pin theirs).
        // Settings → "Pause sensors while the window is hidden": a hidden or minimized window shows nothing.
        var hidden = _pauseWhenHidden && (!IsVisible || WindowState == WindowState.Minimized);
        var shown = hidden
            ? new HashSet<string>(StringComparer.Ordinal)
            : _rows.OfType<SensorRow>().Where(r => !r.Disabled).Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        _hub.SetPaused(_hub.Inventory.Select(i => i.Id).Where(id => !shown.Contains(id)));
    }

    private bool _pauseWhenHidden;

    /// <summary>Settings → pause sensors while the window is hidden; pinned sensors (tray, alerts, logs, graphs) stay live.</summary>
    public bool PauseWhenHidden
    {
        get => _pauseWhenHidden;
        set { _pauseWhenHidden = value; UpdateReadSet(); }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (_pauseWhenHidden && (change.Property == IsVisibleProperty || change.Property == WindowStateProperty)) UpdateReadSet();
    }

    /// <summary>Pushes the hub's latest statistics into every row (collapsed ones too, so expanding is instant).</summary>
    public void RefreshValues()
    {
        var alerting = _commands?.Alerting?.Invoke() ?? new HashSet<string>();
        foreach (var group in _groups)
            foreach (var sensor in group.Sensors)
            {
                sensor.Update(_hub.Get(sensor.Id), _hub.IsPaused(sensor.Id), alerting.Contains(sensor.Id));
                foreach (var child in sensor.Children) child.Update(_hub.Get(child.Id), _hub.IsPaused(child.Id), alerting.Contains(child.Id));
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
        grid.Bind(ToolTip.TipProperty, new Binding(nameof(SensorRow.Tooltip)));
        var chevron = ChevronButton();
        chevron.Bind(IsVisibleProperty, new Binding(nameof(SensorRow.ShowChevron)));
        var icon = new Path { Width = 16, Height = 16, StrokeThickness = 1.5, Stretch = Stretch.None,
                              Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
        icon.Bind(Path.DataProperty, new Binding(nameof(SensorRow.Icon)));
        icon.Bind(Shape.FillProperty, new Binding(nameof(SensorRow.IconBrush)));
        icon.Bind(Shape.StrokeProperty, new Binding(nameof(SensorRow.IconStroke)));
        var label = Text(nameof(SensorRow.Label));
        label.Bind(TextBlock.ForegroundProperty, new Binding(nameof(SensorRow.TextBrush)));
        // A DockPanel, not a StackPanel: the name gets only the column's remaining width (and trims with "…"), so a
        // long name can't widen the column past what the header shows.
        DockPanel.SetDock(chevron, Dock.Left);
        DockPanel.SetDock(icon, Dock.Left);
        var name = new DockPanel { LastChildFill = true, Children = { chevron, icon, label } };
        name.Bind(MarginProperty, new Binding(nameof(SensorRow.Indent)));
        grid.Children.Add(name);
        AddValueCells(grid, withValues: true);
        var menu = new ContextMenu();
        menu.Opening += (_, _) =>
        {
            if (grid.DataContext is not SensorRow row) return;
            _list.SelectedItem = row; // the row the menu acts on, highlighted as HWiNFO does
            FillRowMenu(menu, row);
        };
        grid.ContextMenu = menu;
        return grid;
    }

    // ── row context menu ─────────────────────────────────────────────────────────────────────────────────────

    private const string NotYet = "To be implemented";

    private async Task RenameAsync(SensorRow row)
    {
        var name = await RenameDialog.AskAsync(this, row.Name, SensorPresentation.BuiltInName(row.Item));
        if (name is not null) _custom.Rename(row.Id, name);
    }

    internal async Task CustomizeAsync(SensorRow row)
    {
        var reading = _hub.Get(row.Id).Current is { } raw ? SensorPresentation.BuiltInScale(row.Item, raw) : (double?)null;
        if (await CustomizeDialog.AskAsync(this, row.Item, reading) is not { } result) return;
        _custom.Rename(row.Id, result.Name);
        _custom.Customize(row.Id, result.Values);
    }

    /// <summary>
    /// HWiNFO's sensor right-click menu, item for item and in its order. What OpenHW does today works; the rest is
    /// shown grayed with a "To be implemented" tooltip, so the menu's shape stays familiar as features land.
    /// </summary>
    private void FillRowMenu(ContextMenu menu, SensorRow row)
    {
        MenuItem Item(string header, Action? onClick = null, string? gesture = null, string? disabledTip = NotYet, bool bold = false)
        {
            var item = new MenuItem { Header = header, IsEnabled = onClick is not null };
            if (bold) item.FontWeight = FontWeight.Bold;
            if (gesture is not null) item.InputGesture = KeyGesture.Parse(gesture);
            if (onClick is not null) item.Click += (_, _) => onClick();
            else if (disabledTip is not null)
            {
                ToolTip.SetTip(item, disabledTip);
                ToolTip.SetShowOnDisabled(item, true);
            }
            return item;
        }

        var tray = _commands?.TrayStateOf?.Invoke(row.Id);
        var add = Item("Add to tray", tray is { InTray: false, CanAdd: true } ? () => _commands!.AddToTray?.Invoke(row.Id) : null,
            disabledTip: tray is null ? NotYet : tray.InTray ? "Already in the tray" : tray.WhyNot);
        var remove = Item("Remove from tray", tray is { InTray: true } ? () => _commands!.RemoveFromTray?.Invoke(row.Id) : null,
            disabledTip: tray is null ? NotYet : "Not in the tray");
        menu.Items.Clear();
        foreach (var item in new Control[]
        {
            Item("Show Graph", _commands?.ShowGraph is { } graph ? () => graph(row.Id) : null, bold: true),
            add, remove,
            Item("Disable monitoring", row.Disabled ? null : () => _custom.SetDisabled(row.Id, true), "Delete", "Monitoring is already off"),
            Item("Enable monitoring", row.Disabled ? () => _custom.SetDisabled(row.Id, false) : null, "Insert", "Monitoring is already on"),
            Item("Rename", () => _ = RenameAsync(row), "F2"),
            row.HiddenByUser
                ? Item("Unhide", () => _custom.SetHidden(row.Id, false), "Shift+Delete")
                : Item("Hide", () => _custom.SetHidden(row.Id, true), "Shift+Delete"),
            Item("Enable Logging", _custom.IsLogged(row.Id) ? null : () => _custom.SetLogged(row.Id, true), disabledTip: "Already in the log (File → Start Logging)"),
            Item("Disable Logging", _custom.IsLogged(row.Id) ? () => _custom.SetLogged(row.Id, false) : null, disabledTip: "Not in the log — Enable Logging to add it"),
            new Separator(),
            Item("Layout settings…", _commands?.ShowLayout is { } layout ? () => layout(row.Id) : null),
            Item("Customize values…", SensorPresentation.CanCustomize(row.Item) ? () => _ = CustomizeAsync(row) : null,
                disabledTip: "Yes/No, P-state and link-width readings are labels, not numbers to rescale"),
            Item("Tray icon settings…", _commands?.ShowTrayIcons ?? _commands?.ShowSettings),
            Item("OSD settings…"),
            Item("OSD (RTSS) settings…"),
            Item("Alert settings…", _commands?.AlertSettings is { } alert ? () => alert(row.Id) : null),
            Item("LG LCD settings…"),
        })
            menu.Items.Add(item);
    }

    private Control BuildSpacerTemplate()
    {
        var grid = RowGrid();
        AddValueCells(grid, withValues: false);
        return grid;
    }

    private Grid RowGrid()
    {
        var grid = new Grid { ColumnDefinitions = _columns.Definitions(), Height = RowHeight };
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
                cell.Child.Bind(TextBlock.ForegroundProperty, new Binding(nameof(SensorRow.TextBrush)));
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

    private Control BuildHeader()
    {
        var grid = new Grid { ColumnDefinitions = _columns.Definitions(), Height = 24, Background = Palette.ColumnHeader };
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

            // Drag the column's right edge to resize it (in every row); double-click it for the default width. A plain
            // Border with a transparent fill so it takes the pointer, showing a faint line on hover.
            var column = i;
            var handle = new Border
            {
                Width = 8, HorizontalAlignment = HorizontalAlignment.Right, Background = Brushes.Transparent,
                Cursor = new Cursor(StandardCursorType.SizeWestEast),
            };
            double? dragFrom = null;
            double startWidth = 0;
            handle.PointerEntered += (_, _) => handle.Background = Palette.Separator;
            handle.PointerExited += (_, _) => { if (dragFrom is null) handle.Background = Brushes.Transparent; };
            handle.PointerPressed += (_, e) =>
            {
                if (!e.GetCurrentPoint(handle).Properties.IsLeftButtonPressed) return;
                if (e.ClickCount == 2)
                {
                    _columns.Reset(column);
                    SaveColumns();
                }
                else
                {
                    dragFrom = e.GetPosition(grid).X;
                    startWidth = _columns.Widths[column];
                    e.Pointer.Capture(handle);
                }
                e.Handled = true;
            };
            handle.PointerMoved += (_, e) =>
            {
                if (dragFrom is { } from) _columns.Set(column, startWidth + e.GetPosition(grid).X - from);
            };
            handle.PointerReleased += (_, e) =>
            {
                if (dragFrom is null) return;
                dragFrom = null;
                e.Pointer.Capture(null);
                handle.Background = Brushes.Transparent;
                SaveColumns();
            };
            ToolTip.SetTip(handle, "Drag to resize; double-click for the default width");
            Grid.SetColumn(handle, i);
            grid.Children.Add(handle);
        }
        return grid;
    }

    internal ColumnLayout ColumnLayout => _columns;

    internal void SaveColumns()
    {
        _view.ColumnWidths = _columns.Widths.ToList();
        _view.Save();
    }

    // ── window size and position ────────────────────────────────────────────────────────────────────────────

    private void RestoreWindow()
    {
        if (_view.WindowWidth is { } w && _view.WindowHeight is { } h && w >= MinWidth && h >= 200 && w < 10000 && h < 10000)
        {
            Width = w;
            Height = h;
        }
        if (_view.WindowX is { } x && _view.WindowY is { } y)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = new PixelPoint(x, y);
        }
        if (_view.Maximized) WindowState = WindowState.Maximized;
        Opened += (_, _) =>
        {
            // Saved on a monitor that's gone: start centred on the primary screen instead.
            if (Screens.ScreenFromPoint(Position) is null && Screens.Primary is { } screen)
                Position = new PixelPoint(screen.WorkingArea.X + (int)Math.Max(0, (screen.WorkingArea.Width - Width * screen.Scaling) / 2),
                                          screen.WorkingArea.Y + (int)Math.Max(0, (screen.WorkingArea.Height - Height * screen.Scaling) / 2));
        };
    }

    /// <summary>Remembers size, position and maximized state for next time (called when hidden and at exit).</summary>
    public void SaveWindowState()
    {
        if (!IsVisible) return; // a hidden window's geometry isn't meaningful
        _view.Maximized = WindowState == WindowState.Maximized;
        if (!_view.Maximized)
        {
            _view.WindowWidth = Width;
            _view.WindowHeight = Height;
            _view.WindowX = Position.X;
            _view.WindowY = Position.Y;
        }
        _view.Save();
    }

    /// <summary>Hides to the tray (File → Hide Window, the close button), remembering the window's place.</summary>
    public void HideWindow()
    {
        SaveWindowState();
        Hide();
    }

    // ── menu ─────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// File / View / Power / Help. Everything the footer offers is here too; the footer keeps one-click shortcuts.
    /// New features (settings, more windows) go in a menu first.
    /// </summary>
    private Menu BuildMenu()
    {
        static MenuItem Item(string header, Action onClick, string? gesture = null)
        {
            var item = new MenuItem { Header = header };
            if (gesture is not null) item.InputGesture = KeyGesture.Parse(gesture);
            item.Click += (_, _) => onClick();
            return item;
        }

        var file = new MenuItem { Header = "_File" };
        file.Items.Add(Item("_Hide Window", HideWindow, "Ctrl+W"));
        if (_commands is { StartLogging: { } start, StopLogging: { } stop, IsLogging: { } logging })
        {
            var startItem = Item("Start _Logging…", start);
            var stopItem = Item("Stop L_ogging", stop);
            file.Items.Add(startItem);
            file.Items.Add(stopItem);
            file.SubmenuOpened += (_, _) => { startItem.IsVisible = !logging(); stopItem.IsVisible = logging(); };
            file.Items.Add(new Separator());
        }
        if (_commands is { } c)
        {
            if (c.ShowAlerts is { } alerts) file.Items.Add(Item("_Alerts…", alerts));
            if (c.ShowTrayIcons is { } trayIcons) file.Items.Add(Item("_Tray Icons…", trayIcons));
            file.Items.Add(Item("_Settings…", c.ShowSettings));
            file.Items.Add(new Separator());
            file.Items.Add(Item("E_xit OpenHW", c.Exit, "Ctrl+Q"));
        }

        var view = new MenuItem { Header = "_View" };
        view.Items.Add(Item("_Expand All", () => SetAllExpanded(true), "Ctrl+E"));
        view.Items.Add(Item("_Collapse All", () => SetAllExpanded(false), "Ctrl+Shift+E"));
        view.Items.Add(new Separator());
        var showHidden = new MenuItem { Header = "Show _Hidden Sensors", ToggleType = MenuItemToggleType.CheckBox };
        showHidden.Click += (_, _) =>
        {
            _showHidden = !_showHidden;
            showHidden.IsChecked = _showHidden;
            RebuildTree();
        };
        view.Items.Add(showHidden);
        view.Items.Add(Item("_Unhide All Sensors", _custom.UnhideAll));
        if (_commands?.ShowLayout is { } showLayout) view.Items.Add(Item("_Layout Settings…", () => showLayout(null)));
        view.Items.Add(new Separator());
        view.Items.Add(Item("_Reset Min/Max/Average", ResetStatistics, "Ctrl+R"));

        var menu = new Menu { Background = Palette.ColumnHeader, Items = { file, view } };
        if (_power is not null && _commands is { } commands)
        {
            _powerMenu = new MenuItem { Header = "_Power" };
            _fixCapItem = Item("Remove Unexpected CPU Cap", () => _ = _power.RestoreCapsAsync());
            _fixCapItem.IsVisible = false;
            _powerMenu.Items.Add(new Separator());
            _powerMenu.Items.Add(Item("CPU _Frequency…", commands.ShowCpuFrequency, "Ctrl+F"));
            _powerMenu.Items.Add(Item("Power _Limits…", commands.ShowPowerLimits, "Ctrl+L"));
            if (commands.ShowGpuLending is { } lending) _powerMenu.Items.Add(Item("GPU Le_nding…", lending));
            _powerMenu.Items.Add(_fixCapItem);
            menu.Items.Add(_powerMenu);
        }

        var help = new MenuItem { Header = "_Help" };
        help.Items.Add(Item("_About OpenHW", () => new AboutWindow().ShowDialog(this)));
        menu.Items.Add(help);

        // Menu gestures are only labels in Avalonia; the window binds the keys.
        foreach (var item in menu.Items.OfType<MenuItem>().SelectMany(m => m.Items.OfType<MenuItem>()).Where(i => i.InputGesture is not null))
        {
            var target = item;
            KeyBindings.Add(new KeyBinding { Gesture = target.InputGesture!, Command = new RelayCommand(() => target.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent))) });
        }
        return menu;
    }

    private void ResetStatistics()
    {
        _hub.ResetStatistics();
        _elapsed.Restart();
        RefreshValues();
    }

    // HWiNFO's bottom bar: expand/collapse all on the left; elapsed time, reset (clock), settings (gear) and close on
    // the right. Power mode, caps and limits are in the Power menu.
    private Control BuildFooter()
    {
        var expand = FooterButton("Expand all", FooterIcons.Expand, () => SetAllExpanded(true));
        var collapse = FooterButton("Collapse all", FooterIcons.Collapse, () => SetAllExpanded(false));
        var reset = FooterButton("Reset Min/Max/Average", FooterIcons.Clock, ResetStatistics);
        var close = FooterButton("Close (OpenHW keeps running in the tray)", FooterIcons.Close, HideWindow);

        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { _powerError, _logging, _elapsedText, reset } };
        if (_commands is { } commands) right.Children.Add(FooterButton("Settings", FooterIcons.Gear, commands.ShowSettings));
        right.Children.Add(close);
        // The version takes whatever width is left and trims, so nothing on the right draws over it.
        var version = new TextBlock
        {
            Text = VersionText(), Foreground = Palette.Footnote, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 6, 0),
        };
        ToolTip.SetTip(version, version.Text);
        var left = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { expand, collapse } };
        var bar = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Height = 58, Background = Palette.Window, Margin = new Thickness(4, 0) };
        // Fluent's own hover drops the frame and flattens the face; HWiNFO keeps the frame and lightens the face.
        foreach (var (state, face) in new[] { (":pointerover", FooterIcons.ToolbarHover), (":pressed", FooterIcons.ToolbarPressed) })
            bar.Styles.Add(new Style(x => x.OfType<Button>().Class(state).Template().OfType<Avalonia.Controls.Presenters.ContentPresenter>().Name("PART_ContentPresenter"))
            {
                Setters =
                {
                    new Setter(Avalonia.Controls.Presenters.ContentPresenter.BackgroundProperty, face),
                    new Setter(Avalonia.Controls.Presenters.ContentPresenter.BorderBrushProperty, FooterIcons.ToolbarEdge),
                },
            });
        Grid.SetColumn(version, 1);
        Grid.SetColumn(right, 2);
        bar.Children.Add(left);
        bar.Children.Add(version);
        bar.Children.Add(right);
        RefreshPower();
        return bar;
    }

    // ── power mode ───────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The Power menu's profile and unexpected-cap items, and the footer's last power error. UI thread.</summary>
    public void RefreshPower()
    {
        if (_power is null) return;
        if (_fixCapItem is not null) _fixCapItem.IsVisible = _power.UnexpectedCapKHz is not null;
        RefreshPowerMenu();
        _powerError.Text = _power.LastError ?? "";
        _powerError.IsVisible = _power.LastError is not null;
        ToolTip.SetTip(_powerError, _power.LastError);
    }

    // Radio items for the daemon's profiles, ahead of the separator; rebuilt only when the list changes.
    private void RefreshPowerMenu()
    {
        if (_powerMenu is null || _power is null) return;
        if (_profileItems.Count != _power.Profiles.Count)
        {
            foreach (var old in _profileItems) _powerMenu.Items.Remove(old);
            _profileItems.Clear();
            foreach (var (profile, i) in _power.Profiles.Select((p, i) => (p, i)))
            {
                var item = new MenuItem { Header = PowerModeController.Label(profile), ToggleType = MenuItemToggleType.Radio, GroupName = "power-profile" };
                item.Click += (_, _) => _ = _power.SetProfileAsync(profile);
                _profileItems.Add(item);
                _powerMenu.Items.Insert(i, item);
            }
        }
        foreach (var (item, profile) in _profileItems.Zip(_power.Profiles)) item.IsChecked = profile == _power.ActiveProfile;
    }

    /// <summary>Shows "● Logging" in the footer while a CSV log is being written (null when not). UI thread.</summary>
    public void SetLogging(string? path)
    {
        _logging.IsVisible = path is not null;
        _logging.Text = path is null ? "" : "● Logging";
        ToolTip.SetTip(_logging, path is null ? null : $"Writing {path} — File → Stop Logging to finish");
    }

    /// <summary>"OpenHW 0.2.0 · b8d3adf" — the git revision is stamped in at build time (see OpenHW.App.csproj).</summary>
    internal static string VersionText()
    {
        var informational = typeof(MainWindow).Assembly
            .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";
        var parts = informational.Split('+', 2);
        return parts.Length == 2 ? $"OpenHW {parts[0]} · {parts[1]}" : $"OpenHW {informational}";
    }

    private static Button FooterButton(string tip, IImage icon, Action onClick)
    {
        var button = new Button
        {
            Width = 60, Height = 50, Padding = new Thickness(0), VerticalAlignment = VerticalAlignment.Center,
            Background = FooterIcons.ToolbarFace, BorderBrush = FooterIcons.ToolbarEdge, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
            Content = new Image { Source = icon, Width = 44, Height = 44, Stretch = Stretch.Uniform },
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
            HideWindow();
        }
        base.OnClosing(e);
    }
}
