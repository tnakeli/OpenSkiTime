using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OpenSkiTime.Application;
using OpenSkiTime.Desktop;
using OpenSkiTime.Persistence;
using Xunit;

namespace OpenSkiTime.Tests;

public partial class DesktopWorkflowTests
{
    [AvaloniaFact]
    public void TimingReceiptHeadersFitAndColumnsAndPanelsResizeByDragging()
    {
        using var vm = new MainViewModel(new SeriesWorkspace(new SqliteSeriesFileStore()),
            new FileDialogsStub { NewPath = "unused", OpenPath = "unused", BackupPath = "unused" });
        vm.ReportImportPreview.Add(new() { Target = new() { Run = 1, Bib = 1, Sample = "First" },
            Stamp = new(new DateOnly(2026, 10, 3).ToDateTime(TimeOnly.MinValue).Ticks, 2), State = "Proposed - verify" });
        vm.ReportImportPreview.Add(new() { Target = new() { Run = 1, Bib = 2, Sample = "Last" }, State = "Not found" });
        var window = new TimingReceiptDialog { DataContext = vm };
        window.Show(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
        try
        {
            var grid = window.FindControl<DataGrid>("ReportImportGrid")!;
            var all = window.FindControl<CheckBox>("SelectAllReceiptMatches")!;
            all.IsChecked = true; all.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(CheckBox.ClickEvent));
            Assert.True(vm.ReportImportPreview[0].Accept); Assert.False(vm.ReportImportPreview[1].Accept);
            var headers = grid.GetVisualDescendants().OfType<DataGridColumnHeader>()
                .Where(x => x.Content is string).ToArray();
            foreach (var column in grid.Columns)
            {
                var header = Assert.Single(headers, x => Equals(x.Content, column.Header));
                var text = Assert.Single(header.GetVisualDescendants().OfType<TextBlock>(), x => x.Text == (string)column.Header);
                var expected = new TextBlock { Text = text.Text, FontFamily = text.FontFamily,
                    FontSize = text.FontSize, FontWeight = text.FontWeight };
                expected.Measure(Size.Infinity);
                Assert.True(text.Bounds.Width >= expected.DesiredSize.Width - 0.5,
                    $"{column.Header} header is clipped: {text.Bounds.Width} < {expected.DesiredSize.Width}.");
            }
            var bib = headers.Single(x => Equals(x.Content, "SAMPLE"));
            var edge = bib.TranslatePoint(new Point(bib.Bounds.Width - 1, bib.Bounds.Height / 2), window)!.Value;
            var columnWidth = grid.Columns[1].ActualWidth;
            Drag(edge, 35);
            Assert.True(grid.Columns[1].ActualWidth > columnWidth + 20,
                $"SAMPLE column did not widen: {columnWidth} -> {grid.Columns[1].ActualWidth}.");

            var splitter = window.FindControl<GridSplitter>("ReceiptPanelSplitter")!;
            var point = splitter.TranslatePoint(new Point(splitter.Bounds.Width / 2, splitter.Bounds.Height / 2), window)!.Value;
            var panelWidth = grid.Bounds.Width;
            Drag(point, 80);
            Assert.True(grid.Bounds.Width > panelWidth + 50);
            Assert.True(window.CanResize);
            window.Width += 200; window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            Assert.True(grid.Bounds.Width > panelWidth + 100);

            var output = Environment.GetEnvironmentVariable("OPENSKITIME_REPORT_VISUAL_DIR");
            if (!string.IsNullOrWhiteSpace(output))
            {
                Directory.CreateDirectory(output);
                using var bitmap = new RenderTargetBitmap(new PixelSize((int)window.Width, (int)window.Height), new Vector(96, 96));
                bitmap.Render(window); bitmap.Save(Path.Combine(output, "timing-receipt-dialog.png"));
            }

            void Drag(Point start, double distance)
            {
                window.MouseMove(start);
                window.MouseDown(start, MouseButton.Left);
                window.MouseMove(start + new Vector(distance, 0));
                window.MouseUp(start + new Vector(distance, 0), MouseButton.Left);
                window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            }
        }
        finally { window.Close(); }
    }
}
