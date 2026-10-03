using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using OpenSkiTime.Rewrite.Application;

namespace OpenSkiTime.Rewrite.Desktop;

public partial class TimingReportView : UserControl
{
    public TimingReportView()
    {
        AvaloniaXamlLoader.Load(this);
        foreach (var (name, role) in new[] { ("BackupImageDrop", TimingReportImageRole.B), ("HandStartImageDrop", TimingReportImageRole.HandStart), ("HandFinishImageDrop", TimingReportImageRole.HandFinish) })
        {
            var area = this.FindControl<Border>(name)!;
            area.AddHandler(DragDrop.DragOverEvent, (_, e) => { e.DragEffects = e.Data.Contains(DataFormats.Files) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; });
            area.AddHandler(DragDrop.DropEvent, async (_, e) =>
            {
                e.Handled = true;
                if (DataContext is MainViewModel vm)
                {
                    vm.ReportImageRole = role;
                    this.FindControl<TabControl>("TimingReportTabs")!.SelectedIndex = 3;
                    await vm.ImportReportImagesAsync(e.Data.GetFiles()?.Select(x => x.TryGetLocalPath()).OfType<string>().ToArray() ?? []);
                }
            });
        }
    }
    private async void ChooseImages_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag } || DataContext is not MainViewModel vm || TopLevel.GetTopLevel(this) is not { } top) { return; }
        var images = await top.StorageProvider.OpenFilePickerAsync(new() { Title = "Timing receipt / screen images", AllowMultiple = true,
            FileTypeFilter = [new("Images") { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.bmp", "*.tif", "*.tiff"] }] });
        if (images.Count == 0) { return; }
        vm.ReportImageRole = Enum.Parse<TimingReportImageRole>(tag);
        this.FindControl<TabControl>("TimingReportTabs")!.SelectedIndex = 3;
        await vm.ImportReportImagesAsync(images.Select(x => x.TryGetLocalPath()).OfType<string>().ToArray());
    }
}
