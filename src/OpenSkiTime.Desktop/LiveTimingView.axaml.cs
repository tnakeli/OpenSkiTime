using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace OpenSkiTime.Desktop;

public sealed partial class LiveTimingView : UserControl
{
    public LiveTimingView() => AvaloniaXamlLoader.Load(this);
}
