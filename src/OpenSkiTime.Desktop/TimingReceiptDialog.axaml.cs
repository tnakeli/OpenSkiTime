using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;

namespace OpenSkiTime.Desktop;

public partial class TimingReceiptDialog : Window
{
    private bool _applying;
    private bool _closed;

    public TimingReceiptDialog()
    {
        AvaloniaXamlLoader.Load(this);
        var panels = this.FindControl<Grid>("ReceiptPanels")!;
        panels.AddHandler(DragDrop.DragOverEvent, (_, e) =>
        {
            e.DragEffects = DataContext is MainViewModel { IsReportBusy: false } && e.Data.Contains(DataFormats.Files)
                ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        });
        panels.AddHandler(DragDrop.DropEvent, async (_, e) =>
        {
            e.Handled = true;
            if (DataContext is MainViewModel vm)
            { await vm.ImportReportImagesAsync(e.Data.GetFiles()?.Select(x => x.TryGetLocalPath()).OfType<string>().ToArray() ?? []); }
        });
        AddHandler(KeyDownEvent, async (_, e) =>
        {
            if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key is Key.V or Key.C
                && (e.Key == Key.V || e.Source is not TextBox))
            { e.Handled = true; await PasteImageAsync(); }
        }, RoutingStrategies.Tunnel);
        Closing += (_, e) =>
        {
            if (_applying) { e.Cancel = true; return; }
            if (DataContext is MainViewModel vm) { vm.CancelReportImagesCommand.Execute(null); }
        };
        Closed += (_, _) =>
        { _closed = true; if (DataContext is MainViewModel vm) { vm.ResetReportImport(); } };
    }

    private async void OpenImages_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel { IsReportBusy: false } vm) { return; }
        var images = await StorageProvider.OpenFilePickerAsync(new()
        {
            Title = "Timing receipt / screen images", AllowMultiple = true,
            FileTypeFilter = [new("Images") { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.bmp", "*.tif", "*.tiff"] }]
        });
        if (!_closed && images.Count > 0)
        { await vm.ImportReportImagesAsync(images.Select(x => x.TryGetLocalPath()).OfType<string>().ToArray()); }
    }

    private async void PasteImage_Click(object? sender, RoutedEventArgs e) => await PasteImageAsync();
    private async Task PasteImageAsync()
    {
        if (_closed || DataContext is not MainViewModel { IsReportBusy: false } vm) { return; }
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var image = WindowsReceiptClipboard.ReadImage();
                if (image is not null) { await vm.ImportReportImageBytesAsync(image.Value.Bytes, image.Value.FileName); return; }
            }
            else if (Clipboard is { } clipboard && await clipboard.GetDataAsync("image/png") is byte[] png)
            { if (!_closed) { await vm.ImportReportImageBytesAsync(png, "Clipboard.png"); } return; }
            vm.ReportImportStatus = "The clipboard has no supported image. Copy an image or use Open images.";
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException)
        { vm.ReportImportStatus = "Could not read the clipboard image: " + ex.Message; }
    }

    private void SelectAll_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm || sender is not CheckBox check) { return; }
        var selected = check.IsChecked == true;
        foreach (var row in vm.ReportImportPreview) { row.Accept = selected && row.CanAccept; }
    }

    private async void Accept_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) { return; }
        vm.ReportImportVerified = true;
        _applying = true;
        try { await vm.AcceptReportImageMatchesCommand.ExecuteAsync(null); }
        finally { _applying = false; }
        if (vm.IsError) { vm.ReportImportStatus = vm.StatusMessage; }
        if (!vm.IsError && vm.ReportImportPreview.Count == 0) { Close(); }
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close();
}
