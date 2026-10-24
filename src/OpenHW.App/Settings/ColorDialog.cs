using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using OpenHW.App.SensorWindow;

namespace OpenHW.App.Settings;

/// <summary>
/// Pick any tray tile color: a hex field at the top (type "#1E90FF" or "1e90ff"), kept in step with the spectrum,
/// sliders and palette below. Returns "#RRGGBB" or null if cancelled.
/// </summary>
public sealed class ColorDialog : Window
{
    private readonly ColorView _picker;
    private readonly TextBox _hex = new() { Width = 110, FontFamily = new FontFamily("monospace") };
    private readonly Border _swatch = new() { Width = 28, Height = 22, CornerRadius = new CornerRadius(3), BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1) };
    private bool _syncing; // one side is updating the other
    private string? _result;

    public ColorDialog(string current)
    {
        Title = "Custom Color";
        SizeToContent = SizeToContent.WidthAndHeight;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Palette.Window;
        Foreground = Palette.Text;

        _picker = new ColorView
        {
            Color = Color.TryParse(current, out var c) ? c : Colors.Red,
            IsAlphaEnabled = false, IsAlphaVisible = false, // tray tiles are opaque
            PaletteColors = AppSettings.Colours.Select(p => Color.Parse(p.Hex)).ToArray(),
            Width = 360,
        };
        var ok = new Button { Content = "OK", Padding = new Thickness(18, 4), IsDefault = true };
        ok.Click += (_, _) =>
        {
            if (Parse(_hex.Text) is not { } hex) return;
            _result = hex;
            Close();
        };
        _picker.ColorChanged += (_, _) =>
        {
            if (_syncing) return;
            _syncing = true;
            _hex.Text = Hex(_picker.Color);
            _syncing = false;
            ShowSwatch(ok);
        };
        _hex.TextChanged += (_, _) =>
        {
            ShowSwatch(ok);
            if (_syncing || Parse(_hex.Text) is not { } hex) return;
            _syncing = true;
            _picker.Color = Color.Parse(hex);
            _syncing = false;
        };
        _hex.Text = Hex(_picker.Color);
        var cancel = new Button { Content = "Cancel", Padding = new Thickness(12, 4), IsCancel = true };
        cancel.Click += (_, _) => Close();
        Content = new StackPanel
        {
            Margin = new Thickness(14), Spacing = 10,
            Children =
            {
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 8,
                    Children = { new TextBlock { Text = "Hex", VerticalAlignment = VerticalAlignment.Center }, _hex, _swatch },
                },
                _picker,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, ok } },
            },
        };
    }

    /// <summary>"#RRGGBB" from what was typed ("1e90ff", "#1E90FF", spaces allowed), or null if it isn't a color.</summary>
    public static string? Parse(string? text)
    {
        var t = (text ?? "").Trim();
        if (!t.StartsWith('#')) t = "#" + t;
        return AppSettings.IsColor(t) ? t.ToUpperInvariant() : null;
    }

    private static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    // The swatch shows what OK would give; OK is off while the text isn't a color.
    private void ShowSwatch(Button ok)
    {
        var hex = Parse(_hex.Text);
        _swatch.Background = hex is null ? Brushes.Transparent : new SolidColorBrush(Color.Parse(hex));
        ok.IsEnabled = hex is not null;
    }

    public static async Task<string?> PickAsync(Window owner, string current)
    {
        var dialog = new ColorDialog(current);
        await dialog.ShowDialog(owner);
        return dialog._result;
    }
}
