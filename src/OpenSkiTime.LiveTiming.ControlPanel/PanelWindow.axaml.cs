using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;

namespace OpenSkiTime.LiveTiming.ControlPanel;

public sealed partial class PanelWindow : Window
{
    public PanelWindow() => AvaloniaXamlLoader.Load(this);

    private async void BrowseMusic(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not PanelViewModel model) { return; }
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Background music folder", AllowMultiple = false });
        // Takes effect with Save radio settings, like the other radio fields.
        if (folders.Count == 1 && folders[0].TryGetLocalPath() is { } path) { model.RadioMusicFolder = path; }
    }
}
