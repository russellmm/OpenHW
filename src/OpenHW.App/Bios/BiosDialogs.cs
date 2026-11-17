using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace OpenHW.App.Bios;

/// <summary>The BIOS screen's colours (ASUS Advanced Mode: blue background, white text).</summary>
internal static class BiosTheme
{
    public static readonly IBrush Bg = Brush("#0b3478");
    public static readonly IBrush Panel = Brush("#0e3f8c");
    public static readonly IBrush Line = Brush("#4a78bf");
    public static readonly IBrush Text = Brush("#ffffff");
    public static readonly IBrush Dim = Brush("#a8c2e8");
    public static readonly IBrush Accent = Brush("#2f86e8");
    public static readonly IBrush ValueBg = Brush("#164c9e");
    public static readonly IBrush ValueBgSelected = Brush("#1d5fb8");
    public static readonly IBrush Warn = Brush("#f0b429");
    public static readonly IBrush HiddenTag = Brush("#e08080");
    public static readonly IBrush Selected = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
        GradientStops = { new GradientStop(Color.Parse("#3b93f0"), 0), new GradientStop(Color.Parse("#2a74cc"), 1) },
    };

    public static IBrush Brush(string hex) => new SolidColorBrush(Color.Parse(hex));

    public static Button Button(string text, Action onClick)
    {
        var b = new Button { Content = text, Background = ValueBg, Foreground = Text, BorderBrush = Line, BorderThickness = new Thickness(1), Padding = new Thickness(14, 5) };
        b.Click += (_, _) => onClick();
        return b;
    }
}

/// <summary>A dialog in the BIOS screen's style: a title, content, and a row of buttons that close it with a result.</summary>
internal class BiosDialog : Window
{
    public StackPanel Body { get; } = new() { Spacing = 8, Margin = new Thickness(16, 12) };
    private readonly StackPanel _buttons = new() { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 6, 0, 0) };

    public BiosDialog(string title, double width = 520)
    {
        Title = title;
        Width = width;
        SizeToContent = SizeToContent.Height;
        MaxHeight = 860;
        Background = BiosTheme.Panel;
        Foreground = BiosTheme.Text;
        FontSize = 13;
        CanResize = true;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Body.Children.Add(new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeight.Bold });
        Content = new ScrollViewer { Content = new StackPanel { Children = { Body, _buttons } } };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) { Close(null); e.Handled = true; } };
    }

    public TextBlock Text(string text, IBrush? brush = null)
    {
        var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = brush ?? BiosTheme.Text };
        Body.Children.Add(t);
        return t;
    }

    public Button AddButton(string text, object? result, Func<bool>? canClose = null)
    {
        var b = BiosTheme.Button(text, () => { if (canClose?.Invoke() ?? true) Close(result); });
        _buttons.Children.Add(b);
        return b;
    }

    public void AddAction(string text, Action onClick) => _buttons.Children.Insert(0, BiosTheme.Button(text, onClick));

    public static async Task<bool> Confirm(Window owner, string title, string text, string ok = "OK", string? cancel = "Cancel")
    {
        var d = new BiosDialog(title);
        d.Text(text);
        d.AddButton(ok, true);
        if (cancel is not null) d.AddButton(cancel, false);
        return await d.ShowDialog<object?>(owner) is true;
    }

    public static Task Info(Window owner, string title, string text) => Confirm(owner, title, text, "OK", null);

    /// <summary>A list of settings changes (Save, Last Modified, changed from defaults).</summary>
    public static BiosDialog Changes(string title, string? question, IReadOnlyList<(string Path, string Name, string A, string B)> rows,
        (string A, string B) heads)
    {
        var d = new BiosDialog(title, 900);
        if (question is not null) d.Text(question);
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,200,200") };
        void Put(Control c, int row, int col) { Grid.SetRow(c, row); Grid.SetColumn(c, col); grid.Children.Add(c); }
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        Put(new TextBlock { Text = "Setting", Foreground = BiosTheme.Dim, FontWeight = FontWeight.SemiBold }, 0, 0);
        Put(new TextBlock { Text = heads.A, Foreground = BiosTheme.Dim, FontWeight = FontWeight.SemiBold }, 0, 1);
        Put(new TextBlock { Text = heads.B, Foreground = BiosTheme.Dim, FontWeight = FontWeight.SemiBold }, 0, 2);
        var r = 1;
        foreach (var (path, name, a, b) in rows)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            Put(new StackPanel
            {
                Margin = new Thickness(0, 3),
                Children =
                {
                    new SelectableTextBlock { Text = name, TextWrapping = TextWrapping.Wrap },
                    new TextBlock { Text = path, Foreground = BiosTheme.Dim, FontSize = 11, TextWrapping = TextWrapping.Wrap },
                },
            }, r, 0);
            Put(new SelectableTextBlock { Text = a, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(6, 3) }, r, 1);
            Put(new SelectableTextBlock { Text = b, TextWrapping = TextWrapping.Wrap, Foreground = BiosTheme.Warn, Margin = new Thickness(6, 3) }, r, 2);
            r++;
        }
        if (rows.Count == 0) d.Body.Children.Add(new TextBlock { Text = "(no changes)", Foreground = BiosTheme.Dim });
        else d.Body.Children.Add(new Border { BorderBrush = BiosTheme.Line, BorderThickness = new Thickness(1), Padding = new Thickness(8), Child = grid });
        return d;
    }
}
