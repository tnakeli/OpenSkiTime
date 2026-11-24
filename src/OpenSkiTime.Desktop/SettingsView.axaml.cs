using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
namespace OpenSkiTime.Desktop;
public sealed partial class SettingsView : UserControl
{
    private bool _openingSkiAlpPro;
    public SettingsView() => AvaloniaXamlLoader.Load(this);

    private async void UpdateFromSkiAlpPro_Click(object? sender, RoutedEventArgs e)
    {
        if (_openingSkiAlpPro || DataContext is not MainViewModel vm || TopLevel.GetTopLevel(this) is not Window owner) { return; }
        _openingSkiAlpPro = true;
        try
        {
            if (await vm.OpenSkiAlpProAsync() is { } import) { await new SkiAlpProImportDialog { DataContext = import }.ShowDialog(owner); }
        }
        finally { _openingSkiAlpPro = false; }
    }
}
