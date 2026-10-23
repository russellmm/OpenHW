using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using OpenHW.App.SensorWindow;

namespace OpenHW.App;

/// <summary>Help → About: version, revision and where the project lives.</summary>
public sealed class AboutWindow : Window
{
    public AboutWindow()
    {
        Title = "About OpenHW";
        SizeToContent = SizeToContent.WidthAndHeight;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Palette.Window;
        Foreground = Palette.Text;
        var close = new Button { Content = "Close", HorizontalAlignment = HorizontalAlignment.Right, Padding = new Thickness(14, 4) };
        close.Click += (_, _) => Close();
        Content = new StackPanel
        {
            Margin = new Thickness(20, 16), Spacing = 6, MinWidth = 340,
            Children =
            {
                new TextBlock { Text = "OpenHW", FontSize = 20, FontWeight = FontWeight.SemiBold },
                new TextBlock { Text = MainWindow.VersionText(), Foreground = Palette.Footnote },
                new TextBlock { Text = "HWiNFO-style hardware monitor for Linux.", Margin = new Thickness(0, 6, 0, 0) },
                new TextBlock { Text = "github.com/russellmm/OpenHW", Foreground = Palette.Footnote },
                close,
            },
        };
    }
}

/// <summary>An <see cref="ICommand"/> around an action, for key bindings.</summary>
public sealed class RelayCommand(Action run) : ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => run();
}
