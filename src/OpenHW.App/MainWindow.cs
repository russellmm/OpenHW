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

/// <summary>App-level actions the window's menu and footer shortcuts call.</summary>
public sealed record WindowCommands(Action ShowCpuFrequency, Action ShowPowerLimits, Action Exit);

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
    private readonly PowerModeController? _power;
    private ComboBox? _powerMode;
    private Button? _removeCap;
    private TextBlock? _powerError;
    private Button? _cpuFrequency;
    private readonly WindowCommands? _commands;
    private MenuItem? _powerMenu;
    private readonly List<MenuItem> _profileItems = [];
    private MenuItem? _fixCapItem;
    private bool _showingPower; // RefreshPower is setting the selection: not a user choice

    public MainWindow(SensorHub hub, PowerModeController? power = null, WindowCommands? commands = null)
    {
        _hub = hub;
        _power = power;
        _commands = commands;
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

        var menu = BuildMenu();
        var footer = BuildFooter();
        var header = BuildHeader();
        DockPanel.SetDock(menu, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        DockPanel.SetDock(header, Dock.Top);
        Content = new DockPanel { Children = { menu, footer, header, _list } };

        RebuildTree();
    }

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
    // shows — configured limits, alarm bits — are not read either. Newly shown rows stay gray with their frozen
    // values until the next regular poll brings them back to life.
    private void UpdateReadSet()
    {
        var shown = _rows.OfType<SensorRow>().Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        _hub.SetPaused(_hub.Inventory.Select(i => i.Id).Where(id => !shown.Contains(id)));
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
        grid.Bind(ToolTip.TipProperty, new Binding(nameof(SensorRow.Tooltip)));
        var chevron = ChevronButton();
        chevron.Bind(IsVisibleProperty, new Binding(nameof(SensorRow.ShowChevron)));
        var icon = new Path { Width = 16, Height = 16, StrokeThickness = 1.5, Stretch = Stretch.None,
                              Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
        icon.Bind(Path.DataProperty, new Binding(nameof(SensorRow.Icon)));
        icon.Bind(Shape.FillProperty, new Binding(nameof(SensorRow.IconBrush)));
        icon.Bind(Shape.StrokeProperty, new Binding(nameof(SensorRow.IconStroke)));
        var label = Text(nameof(SensorRow.Name));
        label.Bind(TextBlock.ForegroundProperty, new Binding(nameof(SensorRow.TextBrush)));
        var name = new StackPanel { Orientation = Orientation.Horizontal, Children = { chevron, icon, label } };
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
        file.Items.Add(Item("_Hide Window", Hide, "Ctrl+W"));
        if (_commands is { } c)
        {
            file.Items.Add(new Separator());
            file.Items.Add(Item("E_xit OpenHW", c.Exit, "Ctrl+Q"));
        }

        var view = new MenuItem { Header = "_View" };
        view.Items.Add(Item("_Expand All", () => SetAllExpanded(true), "Ctrl+E"));
        view.Items.Add(Item("_Collapse All", () => SetAllExpanded(false), "Ctrl+Shift+E"));
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

    private Control BuildFooter()
    {
        var expand = FooterButton("Expand all", FooterIcons.Expand, () => SetAllExpanded(true));
        var collapse = FooterButton("Collapse all", FooterIcons.Collapse, () => SetAllExpanded(false));
        var reset = FooterButton("Reset Min/Max/Average", FooterIcons.Clock, ResetStatistics);
        var close = FooterButton("Close (OpenHW keeps running in the tray)", FooterIcons.Close, Hide);

        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { _elapsedText, reset, close } };
        if (BuildPowerControls() is { } power) right.Children.Insert(0, power);
        // The version takes whatever width is left and trims, so the power controls never draw over it.
        var version = new TextBlock
        {
            Text = VersionText(), Foreground = Palette.Footnote, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 6, 0),
        };
        ToolTip.SetTip(version, version.Text);
        var left = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { expand, collapse } };
        var bar = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Height = 54, Background = Palette.Window, Margin = new Thickness(4, 0) };
        Grid.SetColumn(version, 1);
        Grid.SetColumn(right, 2);
        bar.Children.Add(left);
        bar.Children.Add(version);
        bar.Children.Add(right);
        return bar;
    }

    // ── power mode ───────────────────────────────────────────────────────────────────────────────────────────

    private Control? BuildPowerControls()
    {
        if (_power is null) return null;
        _powerMode = new ComboBox { Width = 130, VerticalAlignment = VerticalAlignment.Center, IsVisible = false };
        ToolTip.SetTip(_powerMode, "Power mode (power-profiles-daemon). If the CPU is still held at base clock after " +
                                   "leaving Power saver, OpenHW lifts that cap so boost really comes back.");
        _powerMode.SelectionChanged += (_, _) =>
        {
            if (!_showingPower && _powerMode.SelectedItem is ComboBoxItem { Tag: string profile }) _ = _power.SetProfileAsync(profile);
        };
        _removeCap = new Button
        {
            IsVisible = false, VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(8, 4),
            Background = FooterIcons.ButtonFace, BorderBrush = FooterIcons.ButtonEdge, BorderThickness = new Thickness(1),
            Foreground = new SolidColorBrush(Color.Parse("#F0B429")),
        };
        _removeCap.Click += (_, _) => _ = _power.RestoreCapsAsync();
        _powerError = new TextBlock
        {
            IsVisible = false, Foreground = new SolidColorBrush(Color.Parse("#F07070")), FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center, MaxWidth = 260, TextWrapping = TextWrapping.Wrap,
        };
        var shortcuts = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        if (_commands is { } commands)
        {
            _cpuFrequency = FooterButton("CPU frequency caps (Ctrl+F)", FooterIcons.Gauge, commands.ShowCpuFrequency);
            _cpuFrequency.IsVisible = _power.Cores.Count > 0;
            shortcuts.Children.Add(_cpuFrequency);
            shortcuts.Children.Add(FooterButton("Power limits: CPU and GPUs (Ctrl+L)", FooterIcons.Bolt, commands.ShowPowerLimits));
        }
        RefreshPower();
        return new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 0, 8, 0),
            Children = { _powerError, _removeCap, _powerMode, shortcuts },
        };
    }

    /// <summary>Shows the active profile, any CPU frequency cap, and the last error. UI thread.</summary>
    public void RefreshPower()
    {
        if (_power is null || _powerMode is null || _removeCap is null || _powerError is null) return;
        _showingPower = true;
        try
        {
            _powerMode.IsVisible = _power.Available;
            if (_powerMode.Items.Count != _power.Profiles.Count)
            {
                _powerMode.Items.Clear();
                foreach (var profile in _power.Profiles)
                    _powerMode.Items.Add(new ComboBoxItem { Content = PowerModeController.Label(profile), Tag = profile });
            }
            _powerMode.SelectedItem = _powerMode.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string?)i.Tag == _power.ActiveProfile);
        }
        finally
        {
            _showingPower = false;
        }

        var cpu = _power.Cpu;
        var unexpected = _power.UnexpectedCapKHz;
        _removeCap.IsVisible = unexpected is not null;
        _removeCap.Content = $"Capped at {PowerModeController.Ghz(unexpected ?? 0)}";
        ToolTip.SetTip(_removeCap, $"Something other than OpenHW's caps holds the CPU below {PowerModeController.Ghz(cpu.HardwareMaxKHz)}. " +
                                   "Click to put every core back to its OpenHW cap or the hardware maximum (needs openhw-helper).");
        var caps = cpu.Policies.Select(p => _power.CapOf(p.Id)).OfType<long>().ToArray();
        if (_cpuFrequency is not null)
            ToolTip.SetTip(_cpuFrequency, "CPU frequency caps (Ctrl+F)" + (caps.Length == 0 ? "" : $": OpenHW caps set, lowest {PowerModeController.Ghz(caps.Min())}"));
        if (_fixCapItem is not null) _fixCapItem.IsVisible = unexpected is not null;
        RefreshPowerMenu();
        _powerError.Text = _power.LastError ?? "";
        _powerError.IsVisible = _power.LastError is not null;
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

    /// <summary>"OpenHW 0.1.0 · b8d3adf" — the git revision is stamped in at build time (see OpenHW.App.csproj).</summary>
    internal static string VersionText()
    {
        var informational = typeof(MainWindow).Assembly
            .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";
        var parts = informational.Split('+', 2);
        return parts.Length == 2 ? $"OpenHW {parts[0]} · {parts[1]}" : $"OpenHW {informational}";
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
