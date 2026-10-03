using Avalonia.Controls;
using Avalonia.Markup.Xaml;
namespace OpenSkiTime.Desktop;
public sealed partial class TimingSettingsView : UserControl
{
    public TimingSettingsView() => AvaloniaXamlLoader.Load(this);
}
