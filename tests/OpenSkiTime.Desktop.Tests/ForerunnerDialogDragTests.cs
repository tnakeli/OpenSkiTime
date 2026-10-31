using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OpenSkiTime.Application;
using OpenSkiTime.Desktop;
using OpenSkiTime.Persistence;
using Xunit;

namespace OpenSkiTime.Tests;

public sealed class ForerunnerDialogDragTests
{
    // A row dropped on another reorders the draft and the rows the grid shows (a Move notification is not redrawn).
    [AvaloniaFact]
    public async Task DroppingAForerunnerRowReordersTheDraftAndTheShownRows()
    {
        await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore());
        var dialog = new TimingForerunnerDialog { Width = 760, Height = 400 };
        using var vm = new MainViewModel(workspace, new AvaloniaFileDialogs(dialog));
        foreach (var letter in new[] { "A", "B", "C" }) { vm.TimingForerunners.Add(new ForerunnerEditor { Letter = letter, LastName = "Synth" + letter, FirstName = "Runner" }); }
        dialog.DataContext = vm;
        dialog.Show();
        try
        {
            Dispatcher.UIThread.RunJobs(); dialog.UpdateLayout();
            DataGridRow Row(string letter) => dialog.FindControl<DataGrid>("ForerunnersGrid")!.GetVisualDescendants().OfType<DataGridRow>()
                .Single(x => x.DataContext is ForerunnerEditor runner && runner.Letter == letter && x.Bounds.Height > 0);
            // C dropped onto the upper half of A starts first; the grid shows the new order.
            var rowA = Row("A");
            using (var data = TimingForerunnerDialog.CreateRunnerDragData(vm.TimingForerunners[2]))
            {
                rowA.RaiseEvent(new DragEventArgs(DragDrop.DragOverEvent, data, rowA, new Point(40, 2), KeyModifiers.None) { Source = rowA });
                rowA.RaiseEvent(new DragEventArgs(DragDrop.DropEvent, data, rowA, new Point(40, 2), KeyModifiers.None) { Source = rowA });
            }
            Dispatcher.UIThread.RunJobs(); dialog.UpdateLayout();
            Assert.Equal(["C", "A", "B"], vm.TimingForerunners.Select(x => x.Letter));
            var shown = dialog.FindControl<DataGrid>("ForerunnersGrid")!.GetVisualDescendants().OfType<DataGridRow>().Where(x => x.Bounds.Height > 0)
                .OrderBy(x => x.TranslatePoint(default, dialog)!.Value.Y).Select(x => ((ForerunnerEditor)x.DataContext!).Letter).ToArray();
            Assert.Equal(["C", "A", "B"], shown);
        }
        finally { dialog.Close(); }
    }
}
