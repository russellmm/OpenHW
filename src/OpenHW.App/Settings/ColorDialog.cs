using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using OpenHW.App.SensorWindow;

namespace OpenHW.App.Settings;

/// <summary>Pick any tray tile color: spectrum, sliders, hex entry, and the presets as a palette. Returns "#RRGGBB" or null.</summary>
public sealed class ColorDialog : Window
{
    private readonly ColorView _picker;
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
            var color = _picker.Color;
            _result = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
            Close();
        };
        var cancel = new Button { Content = "Cancel", Padding = new Thickness(12, 4), IsCancel = true };
        cancel.Click += (_, _) => Close();
        Content = new StackPanel
        {
            Margin = new Thickness(14), Spacing = 10,
            Children =
            {
                _picker,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, ok } },
            },
        };
    }

    public static async Task<string?> PickAsync(Window owner, string current)
    {
        var dialog = new ColorDialog(current);
        await dialog.ShowDialog(owner);
        return dialog._result;
    }
}
