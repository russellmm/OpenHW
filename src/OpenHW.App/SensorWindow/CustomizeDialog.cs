using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using OpenHW.App.Sensors;
using OpenHW.Hardware;

namespace OpenHW.App.SensorWindow;

/// <summary>
/// Sensor menu → Customize values… (HWiNFO's Customize tab, for one sensor): the label, a unit of the user's, the
/// value × multiplier + offset, and decimal places. OK saves both the label (as Rename does) and the values.
/// Unlike HWiNFO, "Multiply by" multiplies (HWiNFO's field takes the inverse) and accepts either decimal separator.
/// </summary>
public sealed class CustomizeDialog : Window
{
    public sealed record Result(string Name, ValueCustomization? Values);

    private Result? _result;

    /// <summary>What OK returned; null while open or after Cancel.</summary>
    internal Result? Outcome => _result;
    private readonly HardwareItem _item;
    private readonly double? _reading; // the current reading in OpenHW's own unit, for the preview
    private readonly TextBox _name = new() { Width = 300 };
    private readonly TextBox _unit = new() { Width = 120 };
    private readonly TextBox _multiply = new() { Width = 120 };
    private readonly TextBox _offset = new() { Width = 120 };
    private readonly ComboBox _decimals = new() { Width = 120 };
    private readonly TextBlock _preview = new() { Foreground = Palette.Footnote };
    private readonly Button _ok = new() { Content = "OK", Padding = new Thickness(18, 4), IsDefault = true };

    internal CustomizeDialog(HardwareItem item, double? reading)
    {
        _item = item;
        _reading = reading;
        Title = "Customize Values";
        SizeToContent = SizeToContent.WidthAndHeight;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Palette.Window;
        Foreground = Palette.Text;
        FontSize = 12.5;

        var builtInName = SensorPresentation.BuiltInName(item);
        var builtInUnit = SensorPresentation.BuiltInUnit(item);
        var current = SensorCustomizations.Current.ValuesOf(item.Id) ?? new ValueCustomization();
        _name.Text = SensorPresentation.DisplayName(item);
        _name.Watermark = builtInName;
        _unit.Text = current.Unit ?? builtInUnit;
        _unit.Watermark = builtInUnit;
        _multiply.Text = Number(current.Multiply);
        _offset.Text = Number(current.Offset);
        _decimals.Items.Add(new ComboBoxItem { Content = "Automatic", Tag = -1 });
        for (var d = 0; d <= ValueCustomization.MaxDecimals; d++) _decimals.Items.Add(new ComboBoxItem { Content = d.ToString(CultureInfo.InvariantCulture), Tag = d });
        _decimals.SelectedIndex = (current.Decimals ?? -1) + 1;

        foreach (var box in new[] { _name, _unit, _multiply, _offset }) box.TextChanged += (_, _) => UpdatePreview();
        _decimals.SelectionChanged += (_, _) => UpdatePreview();

        _ok.Click += (_, _) =>
        {
            if (Read() is not { } values) return;
            _result = new Result(_name.Text?.Trim() is { Length: > 0 } n && n != builtInName ? n : "", values.IsDefault ? null : values);
            Close();
        };
        var reset = new Button { Content = "OpenHW's values", Padding = new Thickness(10, 4) };
        ToolTip.SetTip(reset, $"Name {builtInName}, unit {(builtInUnit.Length > 0 ? builtInUnit : "none")}, multiply 1, add 0, automatic decimals");
        reset.Click += (_, _) =>
        {
            _name.Text = builtInName;
            _unit.Text = builtInUnit;
            _multiply.Text = "1";
            _offset.Text = "0";
            _decimals.SelectedIndex = 0;
        };
        var cancel = new Button { Content = "Cancel", Padding = new Thickness(10, 4), IsCancel = true };
        cancel.Click += (_, _) => Close();

        var form = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto"), RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,Auto") };
        void Row(int r, string label, Control field)
        {
            var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetRow(text, r);
            Grid.SetRow(field, r);
            Grid.SetColumn(field, 1);
            field.Margin = new Thickness(10, 3, 0, 3); // Avalonia 11.2's Grid has no row/column spacing
            field.HorizontalAlignment = HorizontalAlignment.Left;
            form.Children.Add(text);
            form.Children.Add(field);
        }
        Row(0, "Label", _name);
        Row(1, "Unit", _unit);
        Row(2, "Multiply by", _multiply);
        Row(3, "Then add", _offset);
        Row(4, "Decimal places", _decimals);

        Content = new StackPanel
        {
            Margin = new Thickness(16), Spacing = 10, Width = 440,
            Children =
            {
                new TextBlock { Text = builtInName, FontWeight = FontWeight.SemiBold },
                form,
                new TextBlock
                {
                    Text = "Shown value = reading × multiplier + offset, everywhere the sensor appears: this window, graphs, " +
                           "tray icons, logs and alert thresholds. Example, °C to °F: unit °F, multiply by 1.8, then add 32.",
                    Foreground = Palette.Footnote, TextWrapping = TextWrapping.Wrap,
                },
                _preview,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { reset, cancel, _ok } },
            },
        };
        UpdatePreview();
        Opened += (_, _) => { _multiply.Focus(); _multiply.SelectAll(); };
    }

    private static string Number(double value) => value.ToString("0.##########", CultureInfo.CurrentCulture);

    // Either separator works: "1.8" and "1,8" both mean 1.8 (HWiNFO insists on "."). Thousands separators aren't accepted.
    internal static double? ParseNumber(string? text)
    {
        var t = (text ?? "").Trim().Replace(',', '.');
        return t.Length > 0 && double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v) ? v : null;
    }

    /// <summary>The form's values, or null (with the reason shown) when a number doesn't parse.</summary>
    private ValueCustomization? Read()
    {
        var multiply = ParseNumber(_multiply.Text);
        var offset = ParseNumber(_offset.Text);
        string? problem = multiply is null ? "“Multiply by” needs a number." : multiply == 0 ? "“Multiply by” can't be 0." : offset is null ? "“Then add” needs a number." : null;
        _ok.IsEnabled = problem is null;
        if (problem is not null)
        {
            _preview.Text = problem;
            _preview.Foreground = Brushes.IndianRed;
            return null;
        }
        // A unit equal to OpenHW's is no customization; an emptied box means "show no unit".
        var unit = _unit.Text?.Trim() ?? "";
        return new ValueCustomization
        {
            Multiply = multiply!.Value, Offset = offset!.Value,
            Unit = unit == SensorPresentation.BuiltInUnit(_item) ? null : unit,
            Decimals = _decimals.SelectedItem is ComboBoxItem { Tag: int d } && d >= 0 ? d : null,
        };
    }

    private void UpdatePreview()
    {
        if (Read() is not { } values) return;
        _preview.Foreground = Palette.Footnote;
        if (_reading is not { } raw) { _preview.Text = "No reading right now."; return; }
        var builtInUnit = SensorPresentation.BuiltInUnit(_item);
        var shown = SensorPresentation.Format(values.Apply(raw), values.Unit ?? builtInUnit, values.Decimals, builtInUnit);
        _preview.Text = $"Now: {SensorPresentation.Format(raw, builtInUnit)} → {shown}";
    }

    /// <summary>Opens the dialog; null if cancelled.</summary>
    public static async Task<Result?> AskAsync(Window owner, HardwareItem item, double? reading)
    {
        var dialog = new CustomizeDialog(item, reading);
        await dialog.ShowDialog(owner);
        return dialog._result;
    }
}
