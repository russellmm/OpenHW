using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace OpenHW.App.SensorWindow;

/// <summary>A short message with OK, in OpenHW's style.</summary>
public sealed class InfoDialog : Window
{
    private InfoDialog(string title, string message)
    {
        Title = title;
        SizeToContent = SizeToContent.WidthAndHeight;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Palette.Window;
        Foreground = Palette.Text;
        var ok = new Button { Content = "OK", Padding = new Thickness(18, 4), IsDefault = true, IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right };
        ok.Click += (_, _) => Close();
        Content = new StackPanel
        {
            Margin = new Thickness(18, 14), Spacing = 12, MaxWidth = 460,
            Children = { new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap }, ok },
        };
    }

    public static Task ShowAsync(Window owner, string title, string message) => new InfoDialog(title, message).ShowDialog(owner);
}
