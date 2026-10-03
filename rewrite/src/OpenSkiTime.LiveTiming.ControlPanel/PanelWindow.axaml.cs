using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace OpenSkiTime.LiveTiming.ControlPanel;

public sealed partial class PanelWindow : Window
{
    public PanelWindow() => AvaloniaXamlLoader.Load(this);
}
