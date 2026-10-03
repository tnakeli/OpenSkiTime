using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace OpenSkiTime.Desktop;

public partial class DrawView : UserControl
{
    public DrawView() => AvaloniaXamlLoader.Load(this);
}
