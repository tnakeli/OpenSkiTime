using Avalonia.Controls;
using Avalonia.Markup.Xaml;
namespace OpenSkiTime.Rewrite.Desktop;
public sealed partial class TimingSettingsView : UserControl
{
    public TimingSettingsView() => AvaloniaXamlLoader.Load(this);
}
