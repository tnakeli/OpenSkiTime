using Avalonia.Controls;
using Avalonia.Markup.Xaml;
namespace OpenSkiTime.Rewrite.Desktop;
public sealed partial class SettingsView : UserControl
{
    public SettingsView() => AvaloniaXamlLoader.Load(this);
}
