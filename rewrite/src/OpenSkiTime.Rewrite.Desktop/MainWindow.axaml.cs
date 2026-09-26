using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace OpenSkiTime.Rewrite.Desktop;

public partial class MainWindow : Window
{
    public MainWindow() => AvaloniaXamlLoader.Load(this);
}
