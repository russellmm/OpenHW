using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Path = Avalonia.Controls.Shapes.Path;
using OpenHW.App.SensorWindow;

namespace OpenHW.App.Settings;

/// <summary>Colors of HWiNFO's settings dialogs, sampled from the reference/settings_menu captures.</summary>
public static class ClassicColors
{
    public static readonly IBrush Window = Palette.Window;
    public static readonly IBrush Text = new SolidColorBrush(Color.Parse("#F0F0F0"));
    public static readonly IBrush Disabled = new SolidColorBrush(Color.Parse("#6E6E6E"));
    public static readonly IBrush PageFrame = new SolidColorBrush(Color.Parse("#525252"));
    public static readonly IBrush GroupLine = new SolidColorBrush(Color.Parse("#333333"));
    public static readonly IBrush FieldFill = new SolidColorBrush(Color.Parse("#141414"));
    public static readonly IBrush FieldEdge = new SolidColorBrush(Color.Parse("#323232"));
    public static readonly IBrush ComboEdge = new SolidColorBrush(Color.Parse("#4A4A4A"));
    public static readonly IBrush ButtonEdge = new SolidColorBrush(Color.Parse("#9A9A9A"));
    public static readonly IBrush ButtonFace = Vertical("#444444", "#040404");
    public static readonly IBrush ButtonHover = Vertical("#5A5A5A", "#141414");
    public static readonly IBrush ButtonPressed = Vertical("#101010", "#303030");
    public static readonly IBrush ListHeader = new SolidColorBrush(Color.Parse("#141414"));
    public static readonly IBrush ListEdge = new SolidColorBrush(Color.Parse("#313131"));
    public static readonly IBrush ListDivider = new SolidColorBrush(Color.Parse("#262626"));
    public static readonly IBrush Link = new SolidColorBrush(Color.Parse("#4C86F0"));
    public static readonly IBrush Swatch = new SolidColorBrush(Color.Parse("#8C8C8C"));
    public static readonly IBrush ArrowBlue = Vertical("#5BA8E0", "#1E5E96");

    private static IBrush Vertical(string top, string bottom) => new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
        GradientStops = { new GradientStop(Color.Parse(top), 0), new GradientStop(Color.Parse(bottom), 1) },
    };
}

/// <summary>
/// One tab page of a HWiNFO-style dialog, laid out like a Win32 dialog resource: every control at a fixed place.
/// Places are given in the pixels of the owner's capture of that tab (reference/settings_menu/, taken at 125 %),
/// so the code reads straight off the screenshot; <see cref="Origin"/> is where that capture's page frame starts.
/// "Centre y" arguments put a one-line control's middle on that row, as the captures' rows line up.
/// </summary>
public sealed class ClassicPage : Canvas
{
    public const double CaptureScale = 1.25;
    private readonly double _x0, _y0;

    /// <summary>Controls HWiNFO grays until an entry in the list is selected (Layout, Customize, Tray Icon, Alerts).</summary>
    public List<Control> NeedSelection { get; } = [];

    /// <param name="frameX">Left of the page frame's line in the capture.</param>
    /// <param name="frameY">Top of the page frame's line in the capture.</param>
    public ClassicPage(double frameX, double frameY)
    {
        // The page sits inside the frame's 1 px line (1.25 capture px).
        (_x0, _y0) = (frameX + CaptureScale, frameY + CaptureScale);
        Background = Brushes.Transparent;
        ClipToBounds = true;
    }

    public Point Origin => new(_x0, _y0);

    private double X(double x) => (x - _x0) / CaptureScale;
    private double Y(double y) => (y - _y0) / CaptureScale;
    private static double L(double length) => length / CaptureScale;

    private T Put<T>(T control, double x, double y) where T : Control
    {
        SetLeft(control, X(x));
        SetTop(control, Y(y));
        Children.Add(control);
        return control;
    }

    private T PutBox<T>(T control, double x1, double y1, double x2, double y2) where T : Control
    {
        control.Width = L(x2 - x1);
        control.Height = L(y2 - y1);
        return Put(control, x1, y1);
    }

    private T Grayable<T>(T control, bool needsSelection) where T : Control
    {
        if (needsSelection)
        {
            control.IsEnabled = false;
            NeedSelection.Add(control);
        }
        return control;
    }

    public const double RowHeight = 20; // logical px of a one-line label/check/radio

    public TextBlock Label(string text, double x, double cy, bool needsSelection = false, bool bold = false)
    {
        var label = new TextBlock { Text = text, Height = RowHeight, LineHeight = RowHeight, FontWeight = bold ? FontWeight.Bold : FontWeight.Normal };
        label.Classes.Add("classic");
        Put(label, x, cy - RowHeight * CaptureScale / 2);
        return Grayable(label, needsSelection);
    }

    /// <summary>A label whose text ends at <paramref name="right"/> (where a wider font must not run into the next control).</summary>
    public TextBlock LabelRight(string text, double right, double cy)
    {
        const double width = 200;
        var label = new TextBlock { Text = text, Width = width, TextAlignment = TextAlignment.Right, Height = RowHeight, LineHeight = RowHeight };
        label.Classes.Add("classic");
        Put(label, right - width * CaptureScale, cy - RowHeight * CaptureScale / 2);
        return label;
    }

    public CheckBox Check(string text, double x, double cy, bool isChecked = false, bool needsSelection = false)
    {
        var check = new CheckBox { Content = text, IsChecked = isChecked, Height = RowHeight };
        Put(check, x, cy - RowHeight * CaptureScale / 2);
        return Grayable(check, needsSelection);
    }

    public RadioButton Radio(string text, string group, double x, double cy, bool isChecked = false, bool needsSelection = false)
    {
        var radio = new RadioButton { Content = text, GroupName = group, IsChecked = isChecked, Height = RowHeight };
        Put(radio, x, cy - RowHeight * CaptureScale / 2);
        return Grayable(radio, needsSelection);
    }

    public TextBox Field(string text, double x1, double y1, double x2, double y2, bool alignRight = true, bool needsSelection = false)
    {
        var box = new TextBox { Text = text, TextAlignment = alignRight ? TextAlignment.Right : TextAlignment.Left };
        return Grayable(PutBox(box, x1, y1, x2, y2), needsSelection);
    }

    /// <summary>
    /// A read-only framed value (Driver Management's "Current Status"). The capture's width is its minimum: the box
    /// grows to fit a longer value, or the desktop's wider font, instead of cutting it off.
    /// </summary>
    public Border Readout(string text, double x1, double y1, double x2, double y2)
    {
        var box = new Border
        {
            BorderBrush = ClassicColors.FieldEdge, BorderThickness = new Thickness(1), Background = ClassicColors.Window,
            MinWidth = L(x2 - x1), Height = L(y2 - y1), Padding = new Thickness(6, 0),
            Child = new TextBlock { Text = text, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        return Put(box, x1, y1);
    }

    public Button Button(string text, double x1, double y1, double x2, double y2, bool needsSelection = false) =>
        Grayable(PutBox(new Button { Content = text }, x1, y1, x2, y2), needsSelection);

    /// <summary>A button showing a color (Customize's font color, Tray Icon's background/text).</summary>
    public Button Swatch(double x1, double y1, double x2, double y2, IBrush? fill, bool needsSelection = false)
    {
        var button = new Button
        {
            Content = new Border { Background = fill ?? Brushes.Transparent, Margin = new Thickness(3) },
            HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch,
        };
        button.Classes.Add("swatch");
        return Grayable(PutBox(button, x1, y1, x2, y2), needsSelection);
    }

    public ComboBox Combo(IEnumerable<string> items, int selected, double x1, double y1, double x2, double y2, bool needsSelection = false)
    {
        var combo = new ComboBox();
        // HWiNFO's small filled triangle instead of Fluent's chevron (set by the template, so not stylable).
        combo.TemplateApplied += (_, e) =>
        {
            if (e.NameScope.Find<PathIcon>("DropDownGlyph") is not { } glyph) return;
            glyph.Data = Geometry.Parse("M0,0 L8,0 L4,4.5 Z");
            (glyph.Width, glyph.Height) = (8, 4.5);
        };
        foreach (var item in items) combo.Items.Add(new ComboBoxItem { Content = item });
        combo.SelectedIndex = selected;
        return Grayable(PutBox(combo, x1, y1, x2, y2), needsSelection);
    }

    /// <summary>A Win32 group box: a thin frame whose title sits on (and breaks) its top line.</summary>
    public void Group(string title, double x1, double top, double x2, double bottom, double titleX)
    {
        var frame = new Border { BorderBrush = ClassicColors.GroupLine, BorderThickness = new Thickness(1) };
        PutBox(frame, x1, top, x2, bottom);
        var label = new TextBlock { Text = title, Background = ClassicColors.Window, Padding = new Thickness(2, 0), Height = RowHeight, LineHeight = RowHeight };
        Put(label, titleX - 2 * CaptureScale, top - RowHeight * CaptureScale / 2);
    }

    /// <summary>A blue underlined link (General's "Check online…"); does nothing yet.</summary>
    public TextBlock Link(string text, double x, double cy)
    {
        var link = new TextBlock
        {
            Text = text, Foreground = ClassicColors.Link, FontWeight = FontWeight.Bold, TextDecorations = TextDecorations.Underline,
            Height = RowHeight, LineHeight = RowHeight, Cursor = new Cursor(StandardCursorType.Hand),
        };
        return Put(link, x, cy - RowHeight * CaptureScale / 2);
    }

    /// <summary>A small square button with a blue arrow glyph (Layout's Order box).</summary>
    public Button Arrow(string glyph, string tip, double x1, double y1, double x2, double y2)
    {
        var button = new Button
        {
            Content = new TextBlock { Text = glyph, Foreground = ClassicColors.ArrowBlue, FontWeight = FontWeight.Bold, FontSize = 15 },
            Padding = new Thickness(0),
        };
        ToolTip.SetTip(button, tip);
        return PutBox(button, x1, y1, x2, y2);
    }

    public T Place<T>(T control, double x1, double y1, double x2, double y2) where T : Control => PutBox(control, x1, y1, x2, y2);
}

/// <summary>
/// HWiNFO's sensor list on the Layout, Customize, Tray Icon and Alerts tabs: an "Entry" column, one value column
/// (Monitoring / Logged / In Tray / Alert) and an empty strip on the right, with group headings in brackets and a
/// blank line between groups.
/// </summary>
public sealed class ClassicEntryList : Border
{
    /// <summary>A line of the list: a group heading, a sensor, or the blank line between groups.</summary>
    public sealed record Entry(string Key, bool IsGroup, string Label, string Value, string GroupKey = "")
    {
        public bool IsSpacer => Key.Length == 0;
    }

    private readonly ListBox _list;

    public event Action<Entry?>? SelectionChanged;

    /// <param name="valueLeft">The first divider, as a distance from the list's left edge (logical px).</param>
    /// <param name="valueWidth">The value column's width (logical px).</param>
    public ClassicEntryList(string valueTitle, double valueLeft, double valueWidth, bool showValueTitle = true)
    {
        BorderBrush = ClassicColors.ListEdge;
        BorderThickness = new Thickness(1);
        Background = ClassicColors.Window;
        var columns = $"{valueLeft},{valueWidth},*";
        _list = new ListBox
        {
            Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0),
            ItemTemplate = new FuncDataTemplate<Entry>((entry, _) => entry is null ? new Panel() : new Grid
            {
                ColumnDefinitions = new ColumnDefinitions(columns), Height = 18.4,
                Children =
                {
                    new TextBlock { Text = entry.Label, Margin = new Thickness(5, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis },
                    new TextBlock { Text = entry.Value, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, [Grid.ColumnProperty] = 1 },
                },
            }),
        };
        _list.Styles.Add(new Style(x => x.OfType<ListBoxItem>())
        {
            Setters = { new Setter(ListBoxItem.PaddingProperty, new Thickness(0)), new Setter(MinHeightProperty, 0.0) },
        });
        _list.SelectionChanged += (_, _) =>
        {
            if (_list.SelectedItem is Entry { IsSpacer: true }) _list.SelectedItem = null;
            SelectionChanged?.Invoke(_list.SelectedItem as Entry);
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled);

        var header = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions(columns), Height = 21, Background = ClassicColors.ListHeader,
            Children =
            {
                new TextBlock { Text = "Entry", Margin = new Thickness(7, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center },
                new TextBlock { Text = showValueTitle ? valueTitle : "", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, [Grid.ColumnProperty] = 1 },
            },
        };
        var dividers = new Grid { ColumnDefinitions = new ColumnDefinitions(columns), IsHitTestVisible = false };
        dividers.Children.Add(new Border { BorderBrush = ClassicColors.ListDivider, BorderThickness = new Thickness(1, 0, 0, 0), [Grid.ColumnProperty] = 1 });
        dividers.Children.Add(new Border { BorderBrush = ClassicColors.ListDivider, BorderThickness = new Thickness(1, 0, 0, 0), [Grid.ColumnProperty] = 2 });
        var body = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(header, Dock.Top);
        body.Children.Add(header);
        body.Children.Add(_list);
        Child = new Panel { Children = { body, dividers } };
    }

    public IReadOnlyList<Entry> Entries => _list.Items.OfType<Entry>().ToList();
    public Entry? Selected { get => _list.SelectedItem as Entry; set => _list.SelectedItem = value; }
    /// <summary>Replaces the rows, keeping the selected one selected (by key) when it is still there.</summary>
    public void Fill(IReadOnlyList<Entry> entries)
    {
        var selected = Selected;
        _list.ItemsSource = entries;
        if (selected is not null)
            _list.SelectedItem = entries.FirstOrDefault(e => !e.IsSpacer && e.Key == selected.Key && e.IsGroup == selected.IsGroup);
    }

    /// <summary>The rows as (key, group?, group key), for <see cref="SensorWindow.LayoutWindow.MoveShown"/>.</summary>
    public IReadOnlyList<(string Key, bool IsGroup, string GroupKey)> Shown =>
        Entries.Where(e => !e.IsSpacer).Select(e => (e.Key, e.IsGroup, e.GroupKey)).ToList();

    /// <summary>OpenHW's real groups and sensors as HWiNFO lists them; <paramref name="value"/> gives each sensor's column.</summary>
    public static IReadOnlyList<Entry> FromHub(Sensors.SensorHub hub, Func<string, string> value, string groupValue = "-",
        Func<Sensors.SensorGroupView, string>? groupValueOf = null)
    {
        var entries = new List<Entry>();
        foreach (var group in Sensors.SensorPresentation.Build(hub.Inventory, hub.Children, hub.Machine))
        {
            if (entries.Count > 0) entries.Add(new Entry("", false, "", ""));
            entries.Add(new Entry(group.Key, true, $"[{group.Title}]", groupValueOf?.Invoke(group) ?? groupValue, group.Key));
            entries.AddRange(group.Sensors.Select(s => new Entry(s.Id, false, s.Name, value(s.Id), group.Key)));
        }
        return entries;
    }
}

/// <summary>
/// A fixed-size dialog in HWiNFO's settings style: a row of Win32 tabs over a framed page, OK and Cancel at the
/// bottom right, everything black with light text, X check boxes and dark gradient buttons. Tabs that OpenHW
/// doesn't have yet are shown grayed with "To be implemented".
/// </summary>
public abstract class ClassicDialog : Window
{
    private const string NotYet = "To be implemented";
    private readonly StackPanel _tabs = new() { Orientation = Orientation.Horizontal };
    private readonly Border _page;
    private readonly List<(Border Tab, ClassicPage? Page)> _entries = [];

    public Button Ok { get; }
    public Button CancelButton { get; }

    /// <param name="client">Client size in logical px.</param>
    /// <param name="page">The page frame (left, top, width, height) in logical px.</param>
    /// <param name="okLeft">OK's left edge; Cancel follows at HWiNFO's spacing.</param>
    protected ClassicDialog(string title, Size client, Rect page, Point okLeft)
    {
        Title = title;
        Width = client.Width;
        Height = client.Height;
        CanResize = false;
        Background = ClassicColors.Window;
        Foreground = ClassicColors.Text;
        FontSize = 12; // HWiNFO's 9 pt at the captures' 125 %, matched by width
        ApplyStyles(Styles);

        _page = new Border
        {
            Width = page.Width, Height = page.Height, BorderBrush = ClassicColors.PageFrame, BorderThickness = new Thickness(1),
            Background = ClassicColors.Window,
        };
        Canvas.SetLeft(_page, page.X);
        Canvas.SetTop(_page, page.Y);
        const double tabHeight = 19.2;
        Canvas.SetLeft(_tabs, page.X);
        Canvas.SetTop(_tabs, page.Y - tabHeight + 1);
        _tabs.Height = tabHeight;

        Ok = DialogButton("OK", okLeft);
        CancelButton = DialogButton("Cancel", new Point(okLeft.X + 97.6, okLeft.Y));
        Ok.Click += (_, _) => Close(true);
        CancelButton.Click += (_, _) => Close(false);
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(false); };

        // The page goes first so the selected tab, drawn over its top line, looks joined to it.
        Content = new Canvas { Children = { _page, _tabs, Ok, CancelButton } };
    }

    private static Button DialogButton(string text, Point at)
    {
        var button = new Button { Content = text, Width = 88, Height = 24 };
        Canvas.SetLeft(button, at.X);
        Canvas.SetTop(button, at.Y);
        return button;
    }

    /// <summary>Adds a tab; a null page is a HWiNFO tab OpenHW doesn't have yet (grayed, "To be implemented").</summary>
    protected void AddTab(string title, ClassicPage? page)
    {
        var tab = new Border
        {
            BorderBrush = ClassicColors.PageFrame, BorderThickness = new Thickness(1, 1, 1, 0), Background = ClassicColors.Window,
            Padding = new Thickness(4.5, 0), Margin = new Thickness(_entries.Count == 0 ? 0 : -1, 0, 0, 0),
            Child = new TextBlock
            {
                Text = title, VerticalAlignment = VerticalAlignment.Center,
                Foreground = page is null ? ClassicColors.Disabled : ClassicColors.Text,
            },
            Cursor = page is null ? null : new Cursor(StandardCursorType.Hand),
        };
        if (page is null) ToolTip.SetTip(tab, NotYet);
        else tab.PointerPressed += (_, _) => Select(page);
        _tabs.Children.Add(tab);
        _entries.Add((tab, page));
        if (page is not null && _page.Child is null) Select(page);
    }

    public IReadOnlyList<string> TabTitles => _entries.Select(e => ((TextBlock)e.Tab.Child!).Text ?? "").ToList();
    public ClassicPage? SelectedPage => _page.Child as ClassicPage;

    /// <summary>Shows a tab by its title; false for a grayed or unknown tab.</summary>
    public bool SelectTab(string title)
    {
        var entry = _entries.FirstOrDefault(e => ((TextBlock)e.Tab.Child!).Text == title);
        if (entry.Page is null) return false;
        Select(entry.Page);
        return true;
    }

    private void Select(ClassicPage page)
    {
        _page.Child = page;
        foreach (var (tab, p) in _entries)
        {
            var selected = p == page;
            // Win32's selected tab stands 2 px taller and wider, over the page's top line.
            tab.Margin = new Thickness((tab == _entries[0].Tab ? 0 : -1) - (selected ? 1.5 : 0), selected ? -1.5 : 0, selected ? -1.5 : 0, selected ? -1 : 0);
            tab.ZIndex = selected ? 1 : 0;
        }
    }

    // ── HWiNFO look: X check boxes, ring radios, framed gradient buttons, flat dark fields ─────────────────────

    private static void ApplyStyles(Styles styles)
    {
        styles.Add(new Style(x => x.OfType<CheckBox>())
        {
            Setters = { new Setter(TemplatedControl.TemplateProperty, CheckTemplate()), new Setter(Layoutable.MinHeightProperty, 0.0), new Setter(Layoutable.MinWidthProperty, 0.0) },
        });
        // Shown by style, not a local value in the template, so the :checked style can win.
        styles.Add(new Style(x => x.OfType<CheckBox>().Template().OfType<Path>().Name("Mark")) { Setters = { new Setter(Visual.IsVisibleProperty, false) } });
        styles.Add(new Style(x => x.OfType<RadioButton>().Template().OfType<Ellipse>().Name("Dot")) { Setters = { new Setter(Visual.IsVisibleProperty, false) } });
        styles.Add(new Style(x => x.OfType<CheckBox>().Class(":checked").Template().OfType<Path>().Name("Mark"))
        {
            Setters = { new Setter(Visual.IsVisibleProperty, true) },
        });
        styles.Add(new Style(x => x.OfType<RadioButton>())
        {
            Setters = { new Setter(TemplatedControl.TemplateProperty, RadioTemplate()), new Setter(Layoutable.MinHeightProperty, 0.0), new Setter(Layoutable.MinWidthProperty, 0.0) },
        });
        styles.Add(new Style(x => x.OfType<RadioButton>().Class(":checked").Template().OfType<Ellipse>().Name("Dot"))
        {
            Setters = { new Setter(Visual.IsVisibleProperty, true) },
        });
        foreach (var type in new Func<Selector?, Selector>[] { x => x.OfType<CheckBox>(), x => x.OfType<RadioButton>() })
            styles.Add(new Style(x => type(x).Class(":disabled")) { Setters = { new Setter(TemplatedControl.ForegroundProperty, ClassicColors.Disabled) } });
        styles.Add(new Style(x => x.OfType<TextBlock>().Class("classic").Class(":disabled"))
        {
            Setters = { new Setter(TextBlock.ForegroundProperty, ClassicColors.Disabled) },
        });

        styles.Add(new Style(x => x.OfType<Button>())
        {
            Setters =
            {
                new Setter(TemplatedControl.TemplateProperty, ButtonTemplate()),
                new Setter(ContentControl.HorizontalContentAlignmentProperty, HorizontalAlignment.Center),
                new Setter(ContentControl.VerticalContentAlignmentProperty, VerticalAlignment.Center),
            },
        });
        foreach (var (state, face) in new[] { (":pointerover", ClassicColors.ButtonHover), (":pressed", ClassicColors.ButtonPressed) })
            styles.Add(new Style(x => x.OfType<Button>().Class(state).Template().OfType<Border>().Name("Face"))
            {
                Setters = { new Setter(Border.BackgroundProperty, face) },
            });
        styles.Add(new Style(x => x.OfType<Button>().Class(":disabled").Template().OfType<Border>().Name("Face"))
        {
            Setters = { new Setter(Border.BorderBrushProperty, ClassicColors.ListEdge) },
        });
        styles.Add(new Style(x => x.OfType<Button>().Class(":disabled")) { Setters = { new Setter(TemplatedControl.ForegroundProperty, ClassicColors.Disabled) } });

        // Fluent's field: rounded, 32 px tall, an accent underline on focus. HWiNFO's: flat, square, dark gray.
        styles.Add(new Style(x => x.OfType<TextBox>())
        {
            Setters =
            {
                new Setter(TemplatedControl.BackgroundProperty, ClassicColors.FieldFill),
                new Setter(TemplatedControl.BorderBrushProperty, ClassicColors.FieldEdge),
                new Setter(TemplatedControl.BorderThicknessProperty, new Thickness(1)),
                new Setter(TemplatedControl.CornerRadiusProperty, new CornerRadius(0)),
                new Setter(TemplatedControl.PaddingProperty, new Thickness(4, 0)),
                new Setter(Layoutable.MinHeightProperty, 0.0),
                new Setter(Layoutable.MinWidthProperty, 0.0),
                new Setter(TextBox.VerticalContentAlignmentProperty, VerticalAlignment.Center),
                new Setter(TemplatedControl.ForegroundProperty, ClassicColors.Text),
            },
        });
        foreach (var state in new[] { ":pointerover", ":focus", ":disabled" })
            styles.Add(new Style(x => x.OfType<TextBox>().Class(state).Template().OfType<Border>().Name("PART_BorderElement"))
            {
                Setters =
                {
                    new Setter(Border.BackgroundProperty, ClassicColors.FieldFill),
                    new Setter(Border.BorderBrushProperty, state == ":focus" ? ClassicColors.ButtonEdge : ClassicColors.FieldEdge),
                    new Setter(Border.BorderThicknessProperty, new Thickness(1)),
                },
            });
        styles.Add(new Style(x => x.OfType<TextBox>().Class(":disabled")) { Setters = { new Setter(TemplatedControl.ForegroundProperty, ClassicColors.Disabled) } });

        styles.Add(new Style(x => x.OfType<ComboBox>())
        {
            Setters =
            {
                new Setter(TemplatedControl.BackgroundProperty, ClassicColors.Window),
                new Setter(TemplatedControl.BorderBrushProperty, ClassicColors.ComboEdge),
                new Setter(TemplatedControl.BorderThicknessProperty, new Thickness(1)),
                new Setter(TemplatedControl.CornerRadiusProperty, new CornerRadius(0)),
                new Setter(TemplatedControl.PaddingProperty, new Thickness(4, 0, 0, 0)),
                new Setter(Layoutable.MinHeightProperty, 0.0),
                new Setter(Layoutable.MinWidthProperty, 0.0),
                new Setter(TemplatedControl.ForegroundProperty, ClassicColors.Text),
            },
        });
        foreach (var state in new[] { ":pointerover", ":pressed", ":disabled" })
            styles.Add(new Style(x => x.OfType<ComboBox>().Class(state).Template().OfType<Border>().Name("Background"))
            {
                Setters = { new Setter(Border.BackgroundProperty, ClassicColors.Window), new Setter(Border.BorderBrushProperty, ClassicColors.ComboEdge) },
            });
    }

    // A white 14 px square with an X when checked, then the text.
    private static FuncControlTemplate<CheckBox> CheckTemplate() => new((check, scope) => new Grid
    {
        ColumnDefinitions = new ColumnDefinitions("Auto,*"), Background = Brushes.Transparent,
        Children =
        {
            new Border
            {
                Width = 14.4, Height = 14.4, VerticalAlignment = VerticalAlignment.Center,
                [!Border.BorderBrushProperty] = check[!TemplatedControl.ForegroundProperty], BorderThickness = new Thickness(1.2),
                Child = new Path
                {
                    Name = "Mark", Data = Geometry.Parse("M2.6,2.6 L9.4,9.4 M9.4,2.6 L2.6,9.4"),
                    [!Shape.StrokeProperty] = check[!TemplatedControl.ForegroundProperty], StrokeThickness = 1.8,
                    Stretch = Stretch.None, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                }.RegisterInNameScope(scope),
            },
            new ContentPresenter
            {
                Name = "Content", Margin = new Thickness(6.4, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                [!ContentPresenter.ContentProperty] = check[!ContentControl.ContentProperty],
                [!ContentPresenter.ForegroundProperty] = check[!TemplatedControl.ForegroundProperty],
                [Grid.ColumnProperty] = 1,
            },
        },
    });

    // A white ring with a dot when chosen, then the text.
    private static FuncControlTemplate<RadioButton> RadioTemplate() => new((radio, scope) => new Grid
    {
        ColumnDefinitions = new ColumnDefinitions("Auto,*"), Background = Brushes.Transparent,
        Children =
        {
            new Panel
            {
                Width = 14.4, Height = 14.4, VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    new Ellipse { [!Shape.StrokeProperty] = radio[!TemplatedControl.ForegroundProperty], StrokeThickness = 1.2 },
                    new Ellipse { Name = "Dot", Width = 7, Height = 7, [!Shape.FillProperty] = radio[!TemplatedControl.ForegroundProperty] }.RegisterInNameScope(scope),
                },
            },
            new ContentPresenter
            {
                Name = "Content", Margin = new Thickness(6.4, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                [!ContentPresenter.ContentProperty] = radio[!ContentControl.ContentProperty],
                [!ContentPresenter.ForegroundProperty] = radio[!TemplatedControl.ForegroundProperty],
                [Grid.ColumnProperty] = 1,
            },
        },
    });

    // HWiNFO's button: a light gray frame around a near-black gradient, centred text.
    private static FuncControlTemplate<Button> ButtonTemplate() => new((button, _) => new Border
    {
        Name = "Face", Background = ClassicColors.ButtonFace, BorderBrush = ClassicColors.ButtonEdge, BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(1.5),
        Child = new ContentPresenter
        {
            Name = "Content",
            [!ContentPresenter.HorizontalAlignmentProperty] = button[!ContentControl.HorizontalContentAlignmentProperty],
            [!ContentPresenter.VerticalAlignmentProperty] = button[!ContentControl.VerticalContentAlignmentProperty],
            [!ContentPresenter.ContentProperty] = button[!ContentControl.ContentProperty],
            [!ContentPresenter.ForegroundProperty] = button[!TemplatedControl.ForegroundProperty],
        },
    });

    private readonly List<(CheckBox Box, Func<bool> Get)> _bound = [];
    private bool _showingBound;

    /// <summary>
    /// Connects a check box to one of OpenHW's settings: ticking saves at once and applies it (as every OpenHW
    /// settings window does), and <see cref="ShowBound"/> shows the saved value again after any change.
    /// </summary>
    protected void Bind(CheckBox box, AppSettings settings, Action applied, Func<bool> get, Action<bool> set, string tip)
    {
        _bound.Add((box, get));
        ToolTip.SetTip(box, tip);
        box.IsChecked = get();
        box.IsCheckedChanged += (_, _) =>
        {
            if (_showingBound || (box.IsChecked == true) == get()) return;
            set(box.IsChecked == true);
            settings.Save();
            applied();
        };
    }

    protected void ShowBound()
    {
        _showingBound = true;
        try { foreach (var (box, get) in _bound) box.IsChecked = get(); }
        finally { _showingBound = false; }
    }

    /// <summary>Enables or grays the controls HWiNFO grays until a sensor is selected.</summary>
    protected static void EnableSelectionControls(ClassicPage page, bool enabled)
    {
        foreach (var control in page.NeedSelection) control.IsEnabled = enabled;
    }
}
