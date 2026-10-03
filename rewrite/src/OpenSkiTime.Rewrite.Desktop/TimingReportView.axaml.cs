using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using OpenSkiTime.Rewrite.Application;

namespace OpenSkiTime.Rewrite.Desktop;

public partial class TimingReportView : UserControl
{
    public TimingReportView() => AvaloniaXamlLoader.Load(this);

    private void ReplacementReason_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox { DataContext: TimingReportMissedEditor row, SelectedItem: string reason })
        { row.Reason = reason; }
    }

    private void RemoveReplacement_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: TimingReportMissedEditor row }
            && DataContext is MainViewModel vm)
        { vm.RemoveReportReplacementCommand.Execute(row); }
    }

    private async void ChooseImages_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag, CommandParameter: int run }
            || DataContext is not MainViewModel vm || vm.IsReportBusy
            || TopLevel.GetTopLevel(this) is not Window owner) { return; }
        vm.ResetReportImport();
        vm.ReportImportRun = run;
        vm.ReportImageRole = Enum.Parse<TimingReportImageRole>(tag);
        vm.ReportImportStatus = "Open images, drop images here, or paste an image (Ctrl+V / Ctrl+C). Review the matched timestamps, then press OK.";
        var dialog = new TimingReceiptDialog { DataContext = vm };
        try { await dialog.ShowDialog(owner); }
        finally { vm.ResetReportImport(); }
    }
}
