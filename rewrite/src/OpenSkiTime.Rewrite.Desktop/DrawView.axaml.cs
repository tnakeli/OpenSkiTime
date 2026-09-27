using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace OpenSkiTime.Rewrite.Desktop;

public partial class DrawView : UserControl
{
    public DrawView() => AvaloniaXamlLoader.Load(this);
}
