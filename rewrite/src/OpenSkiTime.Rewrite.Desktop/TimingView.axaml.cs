using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;

namespace OpenSkiTime.Rewrite.Desktop;

public sealed partial class TimingView : UserControl
{
    public TimingView()
    {
        AvaloniaXamlLoader.Load(this);
        KeyDown += (_, e) =>
        {
            if (DataContext is not MainViewModel vm) { return; }
            if (e.Key == Key.F5) { vm.ArmStartCommand.Execute(null); e.Handled = true; }
            if (e.Key == Key.F6) { vm.ArmFinishCommand.Execute(null); e.Handled = true; }
        };
    }
}
