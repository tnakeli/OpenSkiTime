using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace OpenSkiTime.Desktop;

public sealed partial class PdfFactoryView : UserControl
{
    public PdfFactoryView() { InitializeComponent(); }
    private async void ChooseBackground_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm || TopLevel.GetTopLevel(this) is not { } top) { return; }
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        { Title = "Choose A4 PDF background", AllowMultiple = false, FileTypeFilter = [new("PDF") { Patterns = ["*.pdf"] }] });
        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path) { await vm.SetPdfTemplateAsync(path); }
    }
}
