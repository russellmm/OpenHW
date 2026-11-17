using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;

namespace OpenHW.App.Bios;

/// <summary>One row on the BIOS screen.</summary>
public sealed class BiosRow
{
    public required BiosItem Item { get; init; }
    public required string Fsid { get; init; }
    public long? Fid { get; init; }
    public string Title { get; init; } = "";
    /// <summary>Breadcrumb, for rows shown away from their page (My Favorites).</summary>
    public string? Path { get; init; }
    public BiosState State { get; set; }
    public string Prompt { get; set; } = "";
    public string? Value { get; set; }
    public bool Changed { get; set; }
    public bool Favorite { get; set; }
    public bool Selectable => Item.T is not ("sub" or "text");
    internal Border? ValueBox;
}

/// <summary>
/// File → BIOS Setup…: the ASUS UEFI BIOS Utility (Advanced Mode) screen for this board, ported from the standalone
/// BIOS emulator — every page, setting, choice, default, help text and show/hide rule decoded from the BIOS capsule,
/// with this machine's current values read from its UEFI variables. Browse, search, read what each setting does, and
/// plan changes; F10 keeps the plan to enter in the real BIOS. OpenHW doesn't write the firmware.
/// </summary>
public sealed class BiosWindow : Window
{
    private readonly BiosModel _m;
    private readonly BiosHelp _help;
    private readonly Func<string, double?>? _reading;
    private int _tab = 1;
    private readonly Dictionary<int, List<(string Fsid, long? Fid, int Row)>> _stacks = [];
    private readonly List<ToggleButton> _tabButtons = [];
    private readonly ListBox _list = new() { Background = BiosTheme.Bg, BorderThickness = new Thickness(0) };
    private readonly Button _crumb = new() { Background = Brushes.Transparent, BorderThickness = new Thickness(0), Foreground = BiosTheme.Text, FontSize = 14, Padding = new Thickness(0, 4), Focusable = false };
    private readonly StackPanel _helpPanel = new() { Spacing = 3 };
    private readonly TextBlock _clock = new() { FontSize = 15 };
    private readonly TextBlock _status = new() { Foreground = BiosTheme.Warn, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly ToggleButton _hiddenButton = new() { Focusable = false };
    private readonly Dictionary<string, TextBlock> _hwm = [];
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private List<BiosRow> _rows = [];
    private int _lastIndex;
    private bool _allowClose;

    public BiosModel Model => _m;
    public IReadOnlyList<BiosRow> Rows => _rows;

    /// <param name="reading">Current sensor readings for the Hardware Monitor panel (OpenHW's sensors), or null.</param>
    public BiosWindow(BiosModel model, Func<string, double?>? reading = null)
    {
        _m = model;
        _help = new BiosHelp(model);
        _reading = reading;
        Title = $"BIOS Setup — {model.Db.Board} · BIOS {model.Db.BiosVersion}";
        Width = 1366;
        Height = 860;
        Background = BiosTheme.Bg;
        Foreground = BiosTheme.Text;
        FontSize = 13;
        for (var i = 0; i < BiosModel.Tabs.Length; i++) _stacks[i] = [];
        Content = Build();
        AddHandler(KeyDownEvent, OnKey, RoutingStrategies.Tunnel);
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
        Closed += (_, _) => _timer.Stop();
        Closing += OnClosing;
        SyncHidden();
        SelectTab(1);
        Tick();
    }

    // ── layout ──────────────────────────────────────────────────────────────────────────────────────────────────────

    private Control Build()
    {
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(14, 8, 14, 4) };
        var logo = new TextBlock { Text = "ASUS", FontSize = 26, FontWeight = FontWeight.Black, FontStyle = FontStyle.Italic, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 18, 0) };
        header.Children.Add(logo);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        foreach (var (text, act) in new (string, Action)[]
                 {
                     ("☆ MyFavorite(F3)", () => SelectTab(0)), ("✇ Qfan Control(F6)", Qfan), ("? Search(F9)", () => _ = SearchAsync()),
                 })
            buttons.Children.Add(HeaderButton(text, act));
        _hiddenButton.Click += (_, _) => ToggleHidden();
        _hiddenButton.Background = Brushes.Transparent;
        _hiddenButton.Foreground = BiosTheme.Text;
        _hiddenButton.BorderBrush = BiosTheme.Line;
        _hiddenButton.BorderThickness = new Thickness(1);
        ToolTip.SetTip(_hiddenButton, "Show settings the BIOS normally hides (Ctrl+H)");
        buttons.Children.Add(_hiddenButton);
        buttons.Children.Add(HeaderButton("⟳ Read again", Reread, "Read this machine's settings again (they change only after a reboot)."));
        var machine = HeaderButton("⚙ This machine…", () => _ = MachineAsync());
        machine.Foreground = BiosTheme.Warn;
        machine.BorderBrush = BiosTheme.Warn;
        machine.BorderThickness = new Thickness(1);
        buttons.Children.Add(machine);
        var right = new StackPanel
        {
            Spacing = 2,
            Children =
            {
                new TextBlock { Text = "UEFI BIOS Utility – Advanced Mode", FontSize = 18 },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 18, Children = { _clock, buttons } },
            },
        };
        Grid.SetColumn(right, 1);
        header.Children.Add(right);

        var tabs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Margin = new Thickness(10, 0) };
        for (var i = 0; i < BiosModel.Tabs.Length; i++)
        {
            var n = i;
            var b = new ToggleButton
            {
                Content = BiosModel.Tabs[i].Name, FontSize = 15, Padding = new Thickness(14, 6), Focusable = false,
                Background = Brushes.Transparent, Foreground = BiosTheme.Text, BorderThickness = new Thickness(0, 0, 0, 3), BorderBrush = Brushes.Transparent,
            };
            b.Click += (_, _) => SelectTab(n);
            _tabButtons.Add(b);
            tabs.Children.Add(b);
        }
        tabs.Styles.Add(new Style(x => x.OfType<ToggleButton>().Class(":checked").Template().OfType<Avalonia.Controls.Presenters.ContentPresenter>().Name("PART_ContentPresenter"))
        {
            Setters = { new Setter(Avalonia.Controls.Presenters.ContentPresenter.BackgroundProperty, BiosTheme.Accent), new Setter(Avalonia.Controls.Presenters.ContentPresenter.BorderBrushProperty, BiosTheme.Text) },
        });

        _crumb.Click += (_, _) => GoBack();
        _list.ItemTemplate = new FuncDataTemplate<BiosRow>((row, _) => RowView(row), supportsRecycling: false);
        _list.Styles.Add(new Style(x => x.OfType<ListBoxItem>())
        {
            Setters = { new Setter(ListBoxItem.PaddingProperty, new Thickness(0)), new Setter(ListBoxItem.MinHeightProperty, 0.0) },
        });
        foreach (var state in new[] { ":selected", ":pointerover", ":selected:pointerover", ":selected:focus" })
            _list.Styles.Add(new Style(x => x.OfType<ListBoxItem>().Class(state.Split(':', StringSplitOptions.RemoveEmptyEntries)[0] is var first ? ":" + first : state)
                .Template().OfType<Avalonia.Controls.Presenters.ContentPresenter>().Name("PART_ContentPresenter"))
            {
                Setters = { new Setter(Avalonia.Controls.Presenters.ContentPresenter.BackgroundProperty, state.StartsWith(":selected", StringComparison.Ordinal) ? BiosTheme.Selected : BiosTheme.Panel) },
            });
        _list.SelectionChanged += (_, _) => OnSelection();
        _list.DoubleTapped += (_, _) => _ = ActivateAsync();
        _list.ContextRequested += (_, e) => { _ = ContextMenuAsync(); e.Handled = true; };

        var left = new DockPanel { Margin = new Thickness(18, 6, 8, 0) };
        DockPanel.SetDock(_crumb, Dock.Top);
        left.Children.Add(_crumb);
        left.Children.Add(_list);

        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("*,270") };
        body.Children.Add(left);
        var hwm = BuildHardwareMonitor();
        Grid.SetColumn(hwm, 1);
        body.Children.Add(hwm);

        var helpBox = new Border
        {
            Background = BiosTheme.Panel, BorderBrush = BiosTheme.Line, BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(18, 8, 18, 6),
            Child = new ScrollViewer { Content = _helpPanel },
        };
        var split = new Grid { RowDefinitions = new RowDefinitions("*,5,230") };
        split.Children.Add(body);
        var splitter = new GridSplitter { Background = BiosTheme.Line, ResizeDirection = GridResizeDirection.Rows };
        Grid.SetRow(splitter, 1);
        split.Children.Add(splitter);
        Grid.SetRow(helpBox, 2);
        split.Children.Add(helpBox);

        var foot = new DockPanel { Margin = new Thickness(14, 2, 14, 4), LastChildFill = true };
        var footButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        footButtons.Children.Add(HeaderButton("Last Modified", () => _ = LastModifiedAsync()));
        footButtons.Children.Add(HeaderButton("Changed from defaults", () => _ = NonDefaultAsync()));
        footButtons.Children.Add(HeaderButton("Hot Keys ?", () => _ = HotKeysAsync()));
        DockPanel.SetDock(footButtons, Dock.Right);
        foot.Children.Add(footButtons);
        foot.Children.Add(_status);

        var root = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(tabs, Dock.Top);
        var sep = new Border { Height = 1, Background = BiosTheme.Line };
        DockPanel.SetDock(sep, Dock.Top);
        DockPanel.SetDock(foot, Dock.Bottom);
        root.Children.Add(header);
        root.Children.Add(tabs);
        root.Children.Add(sep);
        root.Children.Add(foot);
        root.Children.Add(split);
        return root;
    }

    private static Button HeaderButton(string text, Action act, string? tip = null)
    {
        var b = new Button { Content = text, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Foreground = BiosTheme.Text, Padding = new Thickness(8, 2), Focusable = false };
        b.Click += (_, _) => act();
        if (tip is not null) ToolTip.SetTip(b, tip);
        return b;
    }

    private Control BuildHardwareMonitor()
    {
        var g = new StackPanel { Margin = new Thickness(14, 6) };
        g.Children.Add(new TextBlock { Text = "▣ Hardware Monitor", FontSize = 15, Margin = new Thickness(0, 4, 0, 4) });
        void Sec(string name) => g.Children.Add(new TextBlock { Text = name, FontSize = 14, Margin = new Thickness(0, 10, 0, 2) });
        void Pair(string a, string? b)
        {
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), Margin = new Thickness(0, 2) };
            foreach (var (k, col) in new[] { (a, 0), (b, 1) })
            {
                if (k is null) continue;
                var v = new TextBlock { Text = "-", FontSize = 14 };
                _hwm[k] = v;
                var cell = new StackPanel { Children = { new TextBlock { Text = k, Foreground = BiosTheme.Dim, FontSize = 12 }, v } };
                Grid.SetColumn(cell, col);
                grid.Children.Add(cell);
            }
            g.Children.Add(grid);
        }
        Sec("CPU/Memory");
        Pair("Frequency", "Temperature");
        Pair("Core Voltage", "Capacity");
        Pair("DRAM Freq.", "MC Volt.");
        Sec("Voltage");
        Pair("+12V", "+5V");
        Pair("+3.3V", null);
        g.Children.Add(new TextBlock { Text = "Live readings from OpenHW (BMC).", Foreground = BiosTheme.Dim, FontSize = 11, Margin = new Thickness(0, 12, 0, 0), TextWrapping = TextWrapping.Wrap });
        return new Border { Background = BiosTheme.Panel, BorderBrush = BiosTheme.Line, BorderThickness = new Thickness(1, 0, 0, 0), Child = g };
    }

    // ── periodic ────────────────────────────────────────────────────────────────────────────────────────────────────

    private void Tick()
    {
        var now = DateTime.Now;
        _clock.Text = now.ToString("MM/dd/yyyy dddd HH:mm", CultureInfo.InvariantCulture);
        string F(string id, string fmt) => _reading?.Invoke(id) is { } v ? v.ToString(fmt, CultureInfo.InvariantCulture) : "-";
        var mhz = CpuMhz();
        _hwm["Frequency"].Text = mhz is { } f ? $"{f:0} MHz" : "-";
        _hwm["Temperature"].Text = F("bmc:CPU Package Temp", "0") + "°C";
        _hwm["Core Voltage"].Text = F("bmc:+VCORE_0", "0.000") + " V";
        _hwm["Capacity"].Text = _m.TextVars()["total_mb"] + " MB";
        _hwm["DRAM Freq."].Text = _m.MemoryConfig(BiosSource.Saved)["speed"] + " MHz";
        _hwm["MC Volt."].Text = F("bmc:+VDDIO", "0.000") + " V";
        _hwm["+12V"].Text = F("bmc:+12V", "0.000") + " V";
        _hwm["+5V"].Text = F("bmc:+5V", "0.000") + " V";
        _hwm["+3.3V"].Text = F("bmc:+3.3V", "0.000") + " V";
        var planned = _m.PendingChanges().Count;
        var source = _m.FromMachine.Count > 0 ? $"THIS MACHINE — settings read {_m.ReadAt:HH:mm:ss}" : "FACTORY DEFAULTS — this machine's settings couldn't be read";
        _status.Text = source + (planned > 0 ? $" • {planned} planned change{(planned == 1 ? "" : "s")} (F10 saves the plan — OpenHW doesn't write the BIOS)" : "");
    }

    private static double? CpuMhz()
    {
        try
        {
            var lines = File.ReadAllLines("/proc/cpuinfo").Where(l => l.StartsWith("cpu MHz", StringComparison.Ordinal))
                .Select(l => double.Parse(l.Split(':')[1], CultureInfo.InvariantCulture)).ToList();
            return lines.Count > 0 ? lines.Average() : null;
        }
        catch (Exception ex) when (ex is IOException or FormatException or UnauthorizedAccessException) { return null; }
    }

    // ── pages ───────────────────────────────────────────────────────────────────────────────────────────────────────

    private (string Fsid, long? Fid, int Row)? CurrentPage => _stacks[_tab].Count > 0 ? _stacks[_tab][^1] : null;

    public void SelectTab(int i)
    {
        _tab = i;
        for (var n = 0; n < _tabButtons.Count; n++) _tabButtons[n].IsChecked = n == i;
        if (_stacks[i].Count == 0)
        {
            var (_, fsid, fid) = BiosModel.Tabs[i];
            if (fsid == "fav") _stacks[i].Add(("fav", null, 0));
            else if (fid is { } f) _stacks[i].Add((fsid, f, 0));
            else if (_m.FormRoot(fsid) is { } root) _stacks[i].Add((root.Fsid, root.Fid, 0));
        }
        Render();
    }

    public void OpenPage(string fsid, long fid)
    {
        RememberRow();
        _stacks[_tab].Add((fsid, fid, 0));
        Render();
    }

    private void RememberRow()
    {
        if (_stacks[_tab].Count == 0) return;
        var top = _stacks[_tab][^1];
        _stacks[_tab][^1] = (top.Fsid, top.Fid, _list.SelectedIndex);
    }

    public void GoBack()
    {
        var st = _stacks[_tab];
        if (st.Count > 1) { st.RemoveAt(st.Count - 1); Render(); }
        else _ = ExitAsync();
    }

    private List<BiosRow> RowsForPage()
    {
        var page = CurrentPage!.Value;
        var rows = new List<BiosRow>();
        if (page.Fsid == "fav")
        {
            rows.Add(new BiosRow { Item = new BiosItem { T = "sub", P = "My Favorites  (select any setting and press F4 to add it here)" }, Fsid = "setup" });
            foreach (var id in _m.Favorites)
                if (_m.Items.TryGetValue(id, out var at))
                    rows.Add(new BiosRow { Item = at.Item, Fsid = at.Fsid, Fid = at.Fid, Title = _m.Form(at.Fsid, at.Fid).Title, Path = string.Join(" > ", _m.PathOf(at.Fsid, at.Fid)) });
            return rows;
        }
        var title = _m.Form(page.Fsid, page.Fid!.Value).Title;
        foreach (var it in _m.PageItems(page.Fsid, page.Fid.Value))
            rows.Add(new BiosRow { Item = it, Fsid = page.Fsid, Fid = page.Fid, Title = title });
        return rows;
    }

    public void Render(int? keepRow = null)
    {
        var page = CurrentPage;
        if (page is null) return;
        var favs = _m.Favorites.ToHashSet(StringComparer.Ordinal);
        var rows = new List<BiosRow>();
        foreach (var row in RowsForPage())
        {
            var it = row.Item;
            var state = it.HasRules || it.HasQf ? _m.StateOf(row.Fsid, it) : BiosState.Ok;
            if (it.P == "  Load from Profile" && Enumerable.Range(1, 8).Any(n => _m.ProfileInfo(n) is not null)) state = BiosState.Ok;
            if (state == BiosState.Hide && (!_m.ShowHidden || _m.InternalIds.Contains(it.Id))) continue;
            if (it.T is "act" or "ref" or "text" or "password" && it.Prompt.Length == 0 && string.IsNullOrEmpty(it.V)) continue;
            row.State = state;
            row.Prompt = it.P.Replace("\r", " ").Replace("\n", " ");
            string? value = null;
            if (it.IsSetting)
            {
                value = _m.Display(it);
                row.Changed = !Equals(_m.Get(it, BiosSource.Work), _m.Get(it, BiosSource.Saved));
            }
            else if (it.T == "text")
            {
                value = _m.RuntimeText(row.Title, it.P) ?? it.V ?? "";
                if (row.Title == "ASUS User Profile" && it.P.StartsWith("Profile ", StringComparison.Ordinal) && it.P.Contains("status", StringComparison.Ordinal)
                    && int.TryParse(it.P.Split(' ')[1], out var n))
                    value = _m.ProfileInfo(n) is { } pi ? $"Assigned: {pi.Name}" : "Not assigned";
                if (row.Title == "Security" && it.P is "Administrator Password" or "User Password")
                    value = _m.Passwords[it.P.StartsWith("Admin", StringComparison.Ordinal) ? "admin" : "user"] ? "Installed" : "Not Installed";
            }
            else if (it.T == "sub" && it.P.Contains(':') && it.P.Contains("N/A", StringComparison.Ordinal))
            {
                var label = it.P.Split(':')[0];
                if (_m.RuntimeText(row.Title, label) is { } rt) row.Prompt = $"{label.TrimEnd()} : {rt}";
            }
            else if (it.T == "date") value = DateTime.Now.ToString("MM/dd/yyyy", CultureInfo.InvariantCulture);
            else if (it.T == "time") value = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
            else if (it.T == "bootopt")
            {
                var names = _m.BootNames();
                var idx = _m.BootOrder[it.Slot];
                value = idx < names.Count ? names[idx] : "Disabled";
            }
            if (row.Path is not null) row.Prompt = row.Prompt.Trim();
            row.Value = value;
            row.Favorite = favs.Contains(it.Id) && page.Value.Fsid != "fav";
            rows.Add(row);
        }
        _rows = rows;
        _list.ItemsSource = rows;
        var crumbs = page.Value.Fsid == "fav" ? ["My Favorites"] : _m.PathOf(page.Value.Fsid, page.Value.Fid!.Value);
        _crumb.Content = (_stacks[_tab].Count > 1 ? "←  " : "") + string.Join(" \\ ", crumbs);
        var target = keepRow ?? page.Value.Row;
        var first = rows.FindIndex(r => r.Selectable);
        _lastIndex = target;
        _list.SelectedIndex = target > 0 && target < rows.Count && rows[target].Selectable ? target : first;
        if (_list.SelectedIndex < 0) ShowHelp(null);
        Dispatcher.UIThread.Post(() => { if (_list.SelectedItem is { } sel) _list.ScrollIntoView(sel); _list.Focus(); });
    }

    public void Refresh() => Render(keepRow: _list.SelectedIndex);

    private void RefreshAll()
    {
        foreach (var k in _stacks.Keys) if (_stacks[k].Count > 1) _stacks[k].RemoveRange(1, _stacks[k].Count - 1);
        SelectTab(_tab);
    }

    private BiosRow? Current => _list.SelectedItem as BiosRow;

    private void OnSelection()
    {
        var i = _list.SelectedIndex;
        if (i >= 0 && i < _rows.Count && !_rows[i].Selectable)
        {
            // Subtitles and text lines can't be selected: skip on in the direction of travel.
            var dir = i >= _lastIndex ? 1 : -1;
            var j = i;
            while (j >= 0 && j < _rows.Count && !_rows[j].Selectable) j += dir;
            if (j < 0 || j >= _rows.Count) { j = i; while (j >= 0 && j < _rows.Count && !_rows[j].Selectable) j -= dir; }
            if (j >= 0 && j < _rows.Count && j != i) { _list.SelectedIndex = j; return; }
        }
        _lastIndex = i;
        ShowHelp(Current);
    }

    // ── row view ────────────────────────────────────────────────────────────────────────────────────────────────────

    private Control RowView(BiosRow? row)
    {
        if (row is null) return new Border();
        var it = row.Item;
        if (it.T == "sub" && it.Prompt.Length == 0) return new Border { Height = 12 };
        var dim = row.State != BiosState.Ok;
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("16,*,14,280,16"), Height = 30 };
        if (it.T == "ref") grid.Children.Add(new TextBlock { Text = ">", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0) });
        var prompt = new TextBlock
        {
            Text = row.Prompt, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = dim ? BiosTheme.Dim : BiosTheme.Text, FontWeight = it.T == "sub" ? FontWeight.Bold : FontWeight.Normal,
            FontStyle = row.State == BiosState.Hide ? FontStyle.Italic : FontStyle.Normal,
        };
        if (row.State == BiosState.Hide) prompt.Inlines = [new Run(row.Prompt), new Run("   [hidden]") { Foreground = BiosTheme.HiddenTag }];
        Grid.SetColumn(prompt, 1);
        if (it.T == "text" || it.Value() is null) Grid.SetColumnSpan(prompt, row.Value is null ? 3 : 1);
        grid.Children.Add(prompt);
        if (row.Value is { Length: > 0 } value)
        {
            Control v;
            if (it.IsSetting || it.T is "bootopt" or "date" or "time" or "password")
            {
                var box = new Border
                {
                    Background = BiosTheme.ValueBg, CornerRadius = new CornerRadius(3), Margin = new Thickness(0, 3), Padding = new Thickness(10, 0, 8, 0),
                    Child = new DockPanel
                    {
                        Children =
                        {
                            Docked(new TextBlock { Text = it.T is "oneof" or "bootopt" or "check" ? "⌄" : "", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, -6, 0, 0) }, Dock.Right),
                            new TextBlock { Text = value, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = dim ? BiosTheme.Dim : BiosTheme.Text },
                        },
                    },
                };
                box.PointerPressed += (_, e) =>
                {
                    if (!e.GetCurrentPoint(box).Properties.IsLeftButtonPressed) return;
                    _list.SelectedItem = row;
                    _ = ActivateAsync();
                    e.Handled = true;
                };
                row.ValueBox = box;
                v = box;
                if (row.Changed)
                {
                    var dot = new Avalonia.Controls.Shapes.Ellipse { Width = 6, Height = 6, Fill = BiosTheme.Warn, VerticalAlignment = VerticalAlignment.Center };
                    ToolTip.SetTip(dot, "Planned: differs from this machine's current value");
                    Grid.SetColumn(dot, 2);
                    grid.Children.Add(dot);
                }
            }
            else v = new TextBlock { Text = value, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            Grid.SetColumn(v, 3);
            grid.Children.Add(v);
        }
        if (row.Favorite)
        {
            var star = new TextBlock { Text = "★", Foreground = BiosTheme.Warn, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0) };
            Grid.SetColumn(star, 4);
            grid.Children.Add(star);
        }
        return grid;
    }

    private static Control Docked(Control c, Dock dock) { DockPanel.SetDock(c, dock); return c; }

    // ── details panel ───────────────────────────────────────────────────────────────────────────────────────────────

    private void ShowHelp(BiosRow? row, long? highlight = null)
    {
        _helpPanel.Children.Clear();
        if (row is null) return;
        var it = row.Item;
        SelectableTextBlock Para(IBrush? brush = null, double size = 13) => new() { TextWrapping = TextWrapping.Wrap, Foreground = brush ?? BiosTheme.Text, FontSize = size };
        if (it.T is "bootopt" or "bootnow" or "date" or "time" or "act" || (it.Q is null && it.T != "ref"))
        {
            var t = Para(size: 14);
            t.Inlines = [new Run(row.Prompt.Trim()) { FontWeight = FontWeight.Bold }];
            _helpPanel.Children.Add(t);
            if (it.H.Length > 0) { var h = Para(); h.Text = it.H; _helpPanel.Children.Add(h); }
            return;
        }
        var d = _help.Details(row.Fsid, row.Fid, it);
        var head = Para(size: 15);
        head.Inlines = [new Run(d.Title) { FontWeight = FontWeight.Bold }];
        if (d.Current is not null)
        {
            head.Inlines.Add(new Run($"    Current: ") { Foreground = BiosTheme.Dim, FontSize = 13 });
            head.Inlines.Add(new Run(d.Current) { FontWeight = FontWeight.Bold, FontSize = 13 });
            if (d.Machine is not null && d.Machine != d.Current)
                head.Inlines.Add(new Run($"  ·  This machine: {d.Machine}") { Foreground = BiosTheme.Warn, FontSize = 13 });
            head.Inlines.Add(new Run($"  ·  Default: {d.Default}") { Foreground = BiosTheme.Dim, FontSize = 13 });
        }
        if (row.State == BiosState.Gray) head.Inlines.Add(new Run("   LOCKED") { Foreground = BiosTheme.Warn, FontWeight = FontWeight.Bold, FontSize = 13 });
        else if (row.State == BiosState.Hide) head.Inlines.Add(new Run("   HIDDEN") { Foreground = BiosTheme.Warn, FontWeight = FontWeight.Bold, FontSize = 13 });
        _helpPanel.Children.Add(head);
        if (d.What.Length > 0) { var w = Para(); w.Text = d.What; _helpPanel.Children.Add(w); }
        if (d.Tip.Length > 0) { var w = Para(); w.Text = "💡 " + d.Tip; _helpPanel.Children.Add(w); }
        if (d.Choices.Count > 0)
        {
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*"), Margin = new Thickness(0, 2) };
            var r = 0;
            foreach (var c in d.Choices)
            {
                grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                var bg = highlight == c.Value ? BiosTheme.Accent : null;
                var mark = new TextBlock { Text = c.Current ? "●" : "○", Margin = new Thickness(2, 1, 8, 1) };
                var label = new TextBlock { Margin = new Thickness(0, 1, 12, 1) };
                label.Inlines = [new Run(c.Label) { FontWeight = FontWeight.Bold }];
                if (c.Default) label.Inlines.Add(new Run(" (default)") { Foreground = BiosTheme.Dim });
                var meaning = new TextBlock { Text = c.Meaning, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1) };
                foreach (var (ctl, col) in new (Control, int)[] { (mark, 0), (label, 1), (meaning, 2) })
                {
                    var cell = new Border { Background = bg, Child = ctl };
                    Grid.SetRow(cell, r);
                    Grid.SetColumn(cell, col);
                    grid.Children.Add(cell);
                }
                r++;
            }
            _helpPanel.Children.Add(grid);
        }
        if (d.Allowed.Length > 0)
        {
            var a = Para();
            a.Inlines = [new Run("Allowed values: ") { FontWeight = FontWeight.Bold }, new Run(d.Allowed)];
            _helpPanel.Children.Add(a);
        }
        foreach (var n in d.Visibility) { var v = Para(BiosTheme.Warn); v.Text = "⚠ " + n; _helpPanel.Children.Add(v); }
        if (d.Terms.Count > 0)
        {
            var t = Para(BiosTheme.Dim);
            foreach (var (term, meaning) in d.Terms)
            {
                if (t.Inlines!.Count > 0) t.Inlines.Add(new LineBreak());
                t.Inlines.Add(new Run(term) { FontWeight = FontWeight.Bold });
                t.Inlines.Add(new Run(" – " + meaning));
            }
            _helpPanel.Children.Add(t);
        }
        if (d.BiosHelp.Length > 0) { var b = Para(BiosTheme.Dim); b.Inlines = [new Run("BIOS help: ") { FontStyle = FontStyle.Italic }, new Run(d.BiosHelp)]; _helpPanel.Children.Add(b); }
        if (row.Path is not null) { var p = Para(BiosTheme.Dim); p.Text = "Location: " + row.Path; _helpPanel.Children.Add(p); }
    }

    // ── keys ────────────────────────────────────────────────────────────────────────────────────────────────────────

    private void OnKey(object? sender, KeyEventArgs e)
    {
        if (e.Source is TextBox) return;
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        switch (e.Key)
        {
            case Key.Left: SelectTab((_tab - 1 + BiosModel.Tabs.Length) % BiosModel.Tabs.Length); break;
            case Key.Right: SelectTab((_tab + 1) % BiosModel.Tabs.Length); break;
            case Key.Enter: _ = ActivateAsync(); break;
            case Key.Escape: GoBack(); break;
            case Key.OemPlus or Key.Add: Step(1); break;
            case Key.OemMinus or Key.Subtract: Step(-1); break;
            case Key.F1: _ = HotKeysAsync(); break;
            case Key.F2: _ = PreviousValuesAsync(); break;
            case Key.F3: SelectTab(0); break;
            case Key.F4: ToggleFavorite(); break;
            case Key.F5: _ = LoadDefaultsAsync(); break;
            case Key.F6: Qfan(); break;
            case Key.F9: _ = SearchAsync(); break;
            case Key.F when ctrl: _ = SearchAsync(); break;
            case Key.H when ctrl: ToggleHidden(); break;
            case Key.F10: _ = SavePlanAsync(); break;
            case Key.F12: _ = PrintScreenAsync(); break;
            default: return;
        }
        e.Handled = true;
    }

    // ── interaction ─────────────────────────────────────────────────────────────────────────────────────────────────

    public async Task ActivateAsync()
    {
        if (Current is not { } row) return;
        var it = row.Item;
        if (row.State == BiosState.Gray)
        {
            var notes = _m.VisibilityNotes(row.Fsid, row.Fid, it);
            await BiosDialog.Info(this, "Locked setting", "This setting is locked (grayed out) right now.\n\n" +
                (notes.Count > 0 ? string.Join("\n", notes) : "The BIOS only allows changing it under certain conditions."));
            return;
        }
        switch (it.T)
        {
            case "ref":
                if (_m.ResolveLink(it) is { } tgt) { if (CurrentPage?.Fsid != "fav") OpenPage(tgt.Fsid, tgt.Fid); }
                else await BiosDialog.Info(this, it.Prompt.Length > 0 ? it.Prompt : "Info", "This page is generated at runtime by a device driver and isn't part of the BIOS image.");
                break;
            case "oneof": PickOption(row); break;
            case "check": _m.Set(it, (long)_m.Get(it) != 0 ? 0L : 1L); Refresh(); break;
            case "num": await EditNumberAsync(row); break;
            case "string": await EditStringAsync(row); break;
            case "act": await DoActionAsync(row); break;
            case "password": await SetPasswordAsync(row); break;
            case "bootopt": PickBoot(row); break;
            case "bootnow": await BiosDialog.Info(this, "Boot Override", $"In the real BIOS the system would now boot from\n\n{it.P}"); break;
            case "date" or "time": await BiosDialog.Info(this, it.P, "Shows this computer's clock. Date and time aren't planned."); break;
        }
    }

    private Control Anchor(BiosRow row) => (Control?)row.ValueBox ?? _list;

    private void PickOption(BiosRow row)
    {
        var it = row.Item;
        var cur = _m.Get(it);
        var details = _help.Details(row.Fsid, row.Fid, it);
        var meanings = details.Choices.ToDictionary(c => c.Value, c => c.Meaning);
        var flyout = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedLeft };
        foreach (var o in _m.Options(row.Fsid, it))
        {
            var item = new MenuItem { Header = o.Label.Length > 0 ? o.Label : o.Value.ToString(CultureInfo.InvariantCulture), ToggleType = MenuItemToggleType.Radio, IsChecked = Equals(cur, o.Value) };
            if (meanings.TryGetValue(o.Value, out var m) && m.Length > 0) ToolTip.SetTip(item, m);
            var value = o.Value;
            item.PointerEntered += (_, _) => ShowHelp(row, value);
            item.Click += (_, _) => { _m.Set(it, value); Refresh(); };
            flyout.Items.Add(item);
        }
        flyout.Closed += (_, _) => ShowHelp(Current);
        flyout.ShowAt(Anchor(row));
    }

    private void Step(int d)
    {
        if (Current is not { State: not BiosState.Gray } row) return;
        var it = row.Item;
        switch (it.T)
        {
            case "oneof":
            {
                var opts = _m.Options(row.Fsid, it).Select(o => o.Value).ToList();
                if (opts.Count == 0) return;
                var i = opts.IndexOf((long)_m.Get(it));
                _m.Set(it, opts[((i < 0 ? 0 : i) + d + opts.Count) % opts.Count]);
                break;
            }
            case "num":
                _m.Set(it, Math.Clamp((long)_m.Get(it) + d * Math.Max(1, it.Step), it.Min, it.Max));
                break;
            case "check": _m.Set(it, (long)_m.Get(it) != 0 ? 0L : 1L); break;
            default: return;
        }
        Refresh();
    }

    /// <summary>Asks for a typed value, explaining the setting and what's allowed; asks again until it's valid.</summary>
    private async Task<object?> ValueDialogAsync(BiosRow row, string initial)
    {
        var it = row.Item;
        var d = _help.Details(row.Fsid, row.Fid, it);
        var dlg = new BiosDialog(d.Title, 600);
        if (d.What.Length > 0) dlg.Text(d.What);
        dlg.Text("Allowed values: " + d.Allowed);
        dlg.Text($"Default: {d.Default}" + (d.Machine is not null ? $"   ·   This machine: {d.Machine}" : ""), BiosTheme.Dim);
        var box = new TextBox { Text = initial, Background = BiosTheme.ValueBg, Foreground = BiosTheme.Text };
        dlg.Body.Children.Add(box);
        var err = dlg.Text("", BiosTheme.Warn);
        object? result = null;
        var msg = "";
        bool Ok()
        {
            var (good, val, m) = _help.Validate(it, box.Text ?? "");
            if (!good) { err.Text = m; box.SelectAll(); box.Focus(); return false; }
            result = val;
            msg = m;
            return true;
        }
        dlg.AddButton("OK", "ok", Ok);
        dlg.AddButton("Cancel", null);
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter && Ok()) dlg.Close("ok"); };
        dlg.Opened += (_, _) => { box.SelectAll(); box.Focus(); };
        if (await dlg.ShowDialog<object?>(this) is not "ok") return null;
        if (msg.Length > 0) await BiosDialog.Info(this, "Value adjusted", msg);
        return result;
    }

    private async Task EditNumberAsync(BiosRow row)
    {
        var it = row.Item;
        if (await ValueDialogAsync(row, _m.Get(it).ToString()!) is not { } v) return;
        if (row.Title == "ASUS User Profile" && it.P.Contains("Save to Profile", StringComparison.Ordinal)) { await SaveProfileAsync((int)(long)v); return; }
        if (row.Title == "ASUS User Profile" && it.P.Contains("Load from Profile", StringComparison.Ordinal)) { await LoadProfileAsync((int)(long)v); return; }
        _m.Set(it, v);
        Refresh();
    }

    private async Task EditStringAsync(BiosRow row)
    {
        var cur = _m.Get(row.Item) as string;
        if (await ValueDialogAsync(row, string.IsNullOrEmpty(cur) ? "Auto" : cur) is not { } v) return;
        _m.Set(row.Item, v);
        Refresh();
    }

    private void ToggleHidden()
    {
        _m.ShowHidden = !_m.ShowHidden;
        SyncHidden();
        Refresh();
    }

    private void SyncHidden()
    {
        _hiddenButton.IsChecked = _m.ShowHidden;
        _hiddenButton.Content = $"Hidden items: {(_m.ShowHidden ? "ON" : "Off")} (Ctrl+H)";
    }

    private async Task SetPasswordAsync(BiosRow row)
    {
        var key = row.Item.P.Contains("Admin", StringComparison.Ordinal) ? "admin" : "user";
        var d = new BiosDialog(row.Item.Prompt);
        d.Text("Create New Password (leave blank to clear)");
        var a = new TextBox { PasswordChar = '•' };
        d.Body.Children.Add(a);
        d.Text("Confirm New Password");
        var b = new TextBox { PasswordChar = '•' };
        d.Body.Children.Add(b);
        d.AddButton("OK", "ok");
        d.AddButton("Cancel", null);
        if (await d.ShowDialog<object?>(this) is not "ok") return;
        if (a.Text != b.Text) { await BiosDialog.Info(this, "Password", "Passwords don't match."); return; }
        _m.Passwords[key] = !string.IsNullOrEmpty(a.Text);
        _m.PersistMeta();
        await BiosDialog.Info(this, "Password", "Only \"Installed / Not Installed\" is recorded in the plan; the password itself isn't stored. Set it in the real BIOS.");
        Refresh();
    }

    private void PickBoot(BiosRow row)
    {
        var names = _m.BootNames();
        var flyout = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedLeft };
        for (var i = 0; i < names.Count; i++)
        {
            var idx = i;
            var item = new MenuItem { Header = names[i] };
            item.Click += (_, _) =>
            {
                var bo = _m.BootOrder;
                var j = bo.IndexOf(idx);
                (bo[row.Item.Slot], bo[j]) = (bo[j], bo[row.Item.Slot]);
                _m.PersistMeta();
                Refresh();
            };
            flyout.Items.Add(item);
        }
        flyout.ShowAt(Anchor(row));
    }

    private async Task DoActionAsync(BiosRow row)
    {
        var p = row.Item.Prompt;
        switch (p)
        {
            case "Save Changes" or "Save Changes & Reset": await SavePlanAsync(); break;
            case "Discard Changes": await PreviousValuesAsync(); break;
            case "Load Optimized Defaults": await LoadDefaultsAsync(); break;
            case "Discard Changes & Exit": await ExitAsync(); break;
            case "Start ASUS EZ Flash": await BiosDialog.Info(this, "ASUS EZ Flash", $"EZ Flash would open here to update the BIOS from a USB drive.\nThis screen is built from: {_m.Db.SourceFile}"); break;
            default: await BiosDialog.Info(this, p, $"This button runs firmware code ({p}); it isn't available outside the real BIOS."); break;
        }
    }

    // ── favorites / search ──────────────────────────────────────────────────────────────────────────────────────────

    private void ToggleFavorite()
    {
        if (Current is not { } row || !row.Selectable || row.Item.Id.Length == 0 || row.Item.Id.StartsWith("boot", StringComparison.Ordinal) || row.Item.Id.StartsWith("dyn:", StringComparison.Ordinal)) return;
        if (!_m.Favorites.Remove(row.Item.Id)) _m.Favorites.Add(row.Item.Id);
        _m.PersistMeta();
        Refresh();
    }

    private Task ContextMenuAsync()
    {
        if (Current is not { } row || !row.Selectable) return Task.CompletedTask;
        var it = row.Item;
        var flyout = new MenuFlyout();
        var fav = new MenuItem { Header = _m.Favorites.Contains(it.Id) ? "Remove from My Favorites (F4)" : "Add to My Favorites (F4)" };
        fav.Click += (_, _) => ToggleFavorite();
        flyout.Items.Add(fav);
        if (it.IsSetting)
        {
            var machine = new MenuItem { Header = $"Back to this machine's value ({_m.Display(it, BiosSource.Saved)})" };
            machine.Click += (_, _) => { _m.Set(it, _m.Get(it, BiosSource.Saved)); Refresh(); };
            var dflt = new MenuItem { Header = $"Reset to default ({_m.Display(it, BiosSource.Default)})" };
            dflt.Click += (_, _) => { _m.Set(it, _m.Get(it, BiosSource.Default)); Refresh(); };
            flyout.Items.Add(machine);
            flyout.Items.Add(dflt);
        }
        if (row.Path is not null && row.Fid is { } fid)
        {
            var go = new MenuItem { Header = "Go to location" };
            go.Click += (_, _) => GoTo(row.Fsid, fid, it);
            flyout.Items.Add(go);
        }
        flyout.ShowAt((Control?)_list.ContainerFromItem(row) ?? _list);
        return Task.CompletedTask;
    }

    /// <summary>Opens the page holding an item (rebuilding its breadcrumb chain) and selects it.</summary>
    public void GoTo(string fsid, long fid, BiosItem? it)
    {
        var crumbs = _m.PathOf(fsid, fid);
        var tab = Array.FindIndex(BiosModel.Tabs, t => t.Name == crumbs[0]);
        if (tab < 0) tab = 1;
        var parents = new Dictionary<string, (string, long)>();
        foreach (var (key, path) in _m.Paths) parents.TryAdd(string.Join("\u0001", path), key);
        var chain = new List<(string, long?, int)>();
        for (var n = 1; n <= crumbs.Count; n++)
            if (parents.TryGetValue(string.Join("\u0001", crumbs.Take(n)), out var key)) chain.Add((key.Item1, key.Item2, 0));
        if (chain.Count == 0 || (chain[^1].Item1, chain[^1].Item2) != (fsid, fid)) chain.Add((fsid, fid, 0));
        _stacks[tab] = chain;
        SelectTab(tab);
        if (it is null) return;
        var i = _rows.FindIndex(r => ReferenceEquals(r.Item, it));
        if (i >= 0) { _list.SelectedIndex = i; _list.ScrollIntoView(_rows[i]); }
    }

    public async Task SearchAsync()
    {
        var d = new BiosDialog("Search on the Menus", 880);
        var box = new TextBox { Watermark = "Type part of a setting name or help text…" };
        var results = new ListBox { Height = 440, Background = BiosTheme.Bg };
        d.Body.Children.Add(box);
        d.Body.Children.Add(results);
        var hits = new List<(string Fsid, long Fid, BiosItem Item)>();
        void Update()
        {
            hits.Clear();
            hits.AddRange(_m.Search(box.Text ?? ""));
            results.ItemsSource = hits.Select(h =>
            {
                var val = h.Item.IsSetting ? $"   [{_m.Display(h.Item)}]" : "";
                return new StackPanel
                {
                    Children =
                    {
                        new TextBlock { Text = h.Item.Prompt + val },
                        new TextBlock { Text = "      " + string.Join(" > ", _m.PathOf(h.Fsid, h.Fid)), Foreground = BiosTheme.Dim, FontSize = 11 },
                    },
                };
            }).ToList();
            if (hits.Count > 0) results.SelectedIndex = 0;
        }
        box.TextChanged += (_, _) => Update();
        bool Go() => results.SelectedIndex >= 0 && results.SelectedIndex < hits.Count;
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter && Go()) d.Close("go"); if (e.Key == Key.Down) { results.Focus(); } };
        results.DoubleTapped += (_, _) => { if (Go()) d.Close("go"); };
        results.KeyDown += (_, e) => { if (e.Key == Key.Enter && Go()) d.Close("go"); };
        d.AddButton("Go", "go", Go);
        d.AddButton("Close", null);
        d.Opened += (_, _) => box.Focus();
        if (await d.ShowDialog<object?>(this) is "go")
        {
            var h = hits[results.SelectedIndex];
            GoTo(h.Fsid, h.Fid, h.Item);
        }
    }

    // ── plan / exit ─────────────────────────────────────────────────────────────────────────────────────────────────

    private List<(string, string, string, string)> ChangeRows(IEnumerable<BiosChange> rows) =>
        rows.Select(c => (string.Join(" > ", _m.PathOf(c.Fsid, c.Fid)), c.Item.Prompt, c.From, c.To)).ToList();

    public async Task<bool> SavePlanAsync()
    {
        var rows = ChangeRows(_m.PendingChanges());
        var d = BiosDialog.Changes("Save the plan",
            rows.Count == 0
                ? "Nothing differs from this machine's settings. Saving empties the plan."
                : "Keep these changes as your plan? OpenHW doesn't write the BIOS: enter them in BIOS Setup (Del at boot). " +
                  "Once the machine has a planned value, it drops off the plan by itself.",
            rows, ("This machine", "Planned"));
        d.AddButton("Save the plan", true);
        d.AddButton("Cancel", false);
        if (await d.ShowDialog<object?>(this) is not true) return false;
        _m.SavePlan();
        Refresh();
        return true;
    }

    private async Task PreviousValuesAsync()
    {
        if (await BiosDialog.Confirm(this, "Load Previous Values", "Drop the changes on screen and go back to this machine's settings?"))
        {
            _m.Discard();
            Refresh();
        }
    }

    private async Task LoadDefaultsAsync()
    {
        if (await BiosDialog.Confirm(this, "Load Optimized Defaults", "Load Optimized Defaults?\n\n(They become planned changes; F10 saves the plan.)"))
        {
            _m.LoadDefaults();
            Refresh();
        }
    }

    public async Task ExitAsync()
    {
        if (_m.HasPending() && _m.PendingChanges().Count > 0)
        {
            var d = new BiosDialog("Exit");
            d.Text("You have changes that aren't saved in the plan.");
            d.AddButton("Save the plan & Exit", "save");
            d.AddButton("Discard & Exit", "discard");
            d.AddButton("Cancel", null);
            switch (await d.ShowDialog<object?>(this))
            {
                case "save": if (!await SavePlanAsync()) return; break;
                case "discard": _m.Discard(); break;
                default: return;
            }
        }
        _allowClose = true;
        Close();
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_allowClose || _m.PendingChanges().Count == 0) return;
        e.Cancel = true;
        _ = ExitAsync();
    }

    private async Task LastModifiedAsync()
    {
        var d = BiosDialog.Changes("Last Modified", $"Changes on screen (plan last saved: {_m.PlannedAt ?? "never"}):",
            ChangeRows(_m.PendingChanges()), ("This machine", "On screen"));
        d.AddButton("Close", null);
        await d.ShowDialog<object?>(this);
    }

    private async Task NonDefaultAsync()
    {
        var d = BiosDialog.Changes("Settings changed from factory defaults", $"This machine's settings (read {_m.ReadAt:yyyy-MM-dd HH:mm:ss}) that differ from the factory defaults:",
            ChangeRows(_m.NonDefault(BiosSource.Saved)), ("Default", "This machine"));
        d.AddAction("Export to text file…", () => _ = ExportAsync(d));
        d.AddButton("Close", null);
        await d.ShowDialog<object?>(this);
    }

    private async Task ExportAsync(Window owner)
    {
        var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export settings report", SuggestedFileName = "bios-changed-settings.txt", DefaultExtension = "txt",
        });
        if (file?.TryGetLocalPath() is not { } path) return;
        var n = _m.ExportReport(path);
        await BiosDialog.Info(owner, "Exported", $"Wrote {n} settings to\n{path}");
    }

    private async Task SaveProfileAsync(int n)
    {
        var nameItem = _m.Form("setup", 11300).Items.FirstOrDefault(i => i.T == "string" && i.P.Contains("Profile Name", StringComparison.Ordinal));
        var name = nameItem is not null && _m.Get(nameItem) is string s && s.Length > 0 ? s : $"Profile {n}";
        if (_m.ProfileInfo(n) is not null && !await BiosDialog.Confirm(this, "Save to Profile", $"Overwrite profile {n}?")) return;
        _m.SaveProfile(n, name);
        await BiosDialog.Info(this, "Save to Profile", $"The settings on screen are saved to profile {n} (\"{name}\").");
        Refresh();
    }

    private async Task LoadProfileAsync(int n)
    {
        if (_m.ProfileInfo(n) is null) { await BiosDialog.Info(this, "Load from Profile", $"Profile {n} is empty."); return; }
        if (await BiosDialog.Confirm(this, "Load from Profile", $"Load profile {n}? (Its differences become planned changes; F10 saves the plan.)"))
        {
            var name = _m.LoadProfile(n);
            await BiosDialog.Info(this, "Load from Profile", $"Loaded profile {n} (\"{name}\").");
            Refresh();
        }
    }

    private void Reread()
    {
        _m.Reload();
        Refresh();
    }

    private async Task MachineAsync()
    {
        var d = new BiosDialog("This machine", 640);
        d.Text($"{_m.Db.Board} · BIOS {_m.Db.BiosVersion} ({_m.Db.BuildDate}, AGESA {_m.Db.Agesa}) — menus decoded from {_m.Db.SourceFile}.");
        d.Text(BiosPlatform.Describe(_m.Platform) + $" · {_m.Db.Variants[_m.Variant].Label}", BiosTheme.Dim);
        d.Text(_m.FromMachine.Count > 0
            ? $"Settings read from this machine's UEFI variables at {_m.ReadAt:HH:mm:ss}: {_m.FromMachine.Count} variables, including Setup, Ai Tweaker, AMD CBS, AMD Overclocking, AMD PBS and Server Mgmt."
            : "This machine's UEFI variables couldn't be read: the screen shows factory defaults.");
        var notRead = _m.NotFromMachine.Where(n => !n.Contains("bytes", StringComparison.Ordinal)).ToList();
        if (notRead.Count > 0)
            d.Text("Not exposed by the firmware (factory defaults, or worked out from the hardware): " + string.Join(", ", notRead) + ".", BiosTheme.Dim);
        d.Text("OpenHW doesn't write the BIOS. Changes you make here are a plan (F10 saves it) to enter in BIOS Setup.", BiosTheme.Warn);
        var hidden = new CheckBox { Content = "Show items the BIOS hides (marked [hidden]) — also Ctrl+H", IsChecked = _m.ShowHidden };
        d.Body.Children.Add(hidden);
        d.Text("Third-party UEFI driver pages at the bottom of Advanced:");
        var tp = new Dictionary<string, CheckBox>();
        var pages = (_m.Platform["third_party_pages"] as System.Text.Json.Nodes.JsonArray)?.Select(n => n!.GetValue<string>()).ToHashSet() ?? [];
        foreach (var (k, title) in BiosModel.ThirdParty)
        {
            var cb = new CheckBox { Content = title, IsChecked = pages.Contains(k) };
            tp[k] = cb;
            d.Body.Children.Add(cb);
        }
        d.AddAction("Clear the plan…", () => _ = ClearPlanAsync(d));
        d.AddAction("Open state folder", () => OpenFolder(_m.StateDir));
        d.AddButton("Apply", true);
        d.AddButton("Cancel", false);
        if (await d.ShowDialog<object?>(this) is not true) return;
        var platform = (System.Text.Json.Nodes.JsonObject)_m.Platform.DeepClone();
        platform["third_party_pages"] = new System.Text.Json.Nodes.JsonArray(tp.Where(x => x.Value.IsChecked == true).Select(x => (System.Text.Json.Nodes.JsonNode)System.Text.Json.Nodes.JsonValue.Create(x.Key)!).ToArray());
        _m.ShowHidden = hidden.IsChecked == true;
        _m.SavePlatform(platform);
        SyncHidden();
        RefreshAll();
    }

    private async Task ClearPlanAsync(Window owner)
    {
        if (!await BiosDialog.Confirm(owner, "Clear the plan", "Drop every planned change and go back to this machine's settings? (Favorites and profiles are kept.)")) return;
        _m.Discard(persist: true);
        RefreshAll();
    }

    private static void OpenFolder(string path)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("xdg-open", path) { UseShellExecute = false })?.Dispose(); }
        catch (System.ComponentModel.Win32Exception) { }
    }

    private Task HotKeysAsync() => BiosDialog.Info(this, "Hot Keys", string.Join("\n",
        "← →    Select screen (tab)", "↑ ↓    Select item", "Enter   Select / open", "+ / -   Change option", "Esc     Back / Exit",
        "F1      General help", "F2      Previous values (this machine's)", "F3      My Favorites", "F4      Add/remove item in My Favorites",
        "F5      Optimized defaults", "F6      Q-Fan Control", "F9      Search on the menus (also Ctrl+F)", "F10     Save the plan",
        "F12     Print screen (saves a PNG)", "Ctrl+H  Show hidden items", "", "Right-click an item for more options."));

    private void Qfan()
    {
        if (_m.Paths.ContainsKey(("setup", 10107))) GoTo("setup", 10107, null);
    }

    private async Task PrintScreenAsync()
    {
        var path = System.IO.Path.Combine(_m.StateDir, $"screenshot-{DateTime.Now:yyyyMMdd-HHmmss}.png");
        var size = new PixelSize((int)Bounds.Width, (int)Bounds.Height);
        using var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(size);
        bitmap.Render(this);
        bitmap.Save(path);
        await BiosDialog.Info(this, "Print Screen", $"Saved {path}");
    }
}

internal static class BiosItemExtensions
{
    /// <summary>Whether a row has a value column at all (settings, text values, boot rows).</summary>
    public static string? Value(this BiosItem it) => it.IsSetting || it.T is "text" or "bootopt" or "date" or "time" or "password" ? "" : null;
}
