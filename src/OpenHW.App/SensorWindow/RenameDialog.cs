using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;

namespace OpenHW.App.SensorWindow;

/// <summary>Sensor menu → Rename (F2). Returns the new name, "" to go back to OpenHW's own, or null if cancelled.</summary>
public sealed class RenameDialog : Window
{
    private string? _result;

    private RenameDialog(string current, string builtIn)
    {
        Title = "Rename Sensor";
        SizeToContent = SizeToContent.WidthAndHeight;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Palette.Window;
        Foreground = Palette.Text;

        var box = new TextBox { Text = current, Width = 380 };
        var ok = new Button { Content = "OK", Padding = new Thickness(18, 4), IsDefault = true };
        ok.Click += (_, _) => { _result = box.Text ?? ""; Close(); };
        var reset = new Button { Content = "Original name", Padding = new Thickness(10, 4), IsEnabled = current != builtIn };
        ToolTip.SetTip(reset, builtIn);
        reset.Click += (_, _) => { _result = ""; Close(); };
        var cancel = new Button { Content = "Cancel", Padding = new Thickness(10, 4), IsCancel = true };
        cancel.Click += (_, _) => Close();
        Content = new StackPanel
        {
            Margin = new Thickness(16), Spacing = 8,
            Children =
            {
                new TextBlock { Text = "Name shown for this sensor (in the window, tray, Settings and logs):" },
                box,
                new TextBlock { Text = $"OpenHW's name: {builtIn}", Foreground = Palette.Footnote },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { reset, cancel, ok } },
            },
        };
        Opened += (_, _) => { box.Focus(); box.SelectAll(); };
    }

    public static async Task<string?> AskAsync(Window owner, string current, string builtIn)
    {
        var dialog = new RenameDialog(current, builtIn);
        await dialog.ShowDialog(owner);
        return dialog._result;
    }
}
