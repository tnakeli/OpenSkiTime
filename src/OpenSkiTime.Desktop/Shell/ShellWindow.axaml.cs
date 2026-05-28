using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace OpenSkiTime.Desktop.Shell;

public partial class ShellWindow : Window
{
    public ShellWindow()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
