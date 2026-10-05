using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OpenSkiTime.Application;
using OpenSkiTime.Desktop;
using OpenSkiTime.Devices;
using OpenSkiTime.Domain;
using OpenSkiTime.Persistence;
using OpenSkiTime.Timing;
using Xunit;

namespace OpenSkiTime.Tests;

public partial class DesktopWorkflowTests
{
    private static void DoubleClick(Window window, Visual target, Point at)
    {
        window.UpdateLayout();
        var point = target.TranslatePoint(at, window)!.Value;
        for (var i = 0; i < 2; i++) { window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left); }
        Dispatcher.UIThread.RunJobs();
    }

    private static DataGridColumnHeader TimestampHeader(TimingView view, string header) => view.FindControl<DataGrid>("TimestampsGrid")!
        .GetVisualDescendants().OfType<DataGridColumnHeader>().Single(x => Equals(x.Content, header) && x.IsVisible);

    private static async Task TypeManualTimestampAsync(Window window, TimingView view, string time)
    {
        var box = view.FindControl<TextBox>("ManualTimestampTime")!;
        Assert.True(box.IsFocused); // typing starts immediately after the double-click
        box.Text = time;
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        var vm = Assert.IsType<MainViewModel>(view.DataContext);
        if (vm.SaveManualTimestampCommand.ExecutionTask is { } saving) { await saving; }
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task DoubleClickingATimestampColumnAddsAnAuditedManualTimeMarkedWithM()
    {
        var root = Path.Combine(Path.GetTempPath(), "openskitime-manual-timestamp-ui", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var file = Path.Combine(root, "Synthetic-Manual-Time.ost");
            var cache = new FisLocalStore(root);
            await cache.SaveListAsync(SyntheticFisArchive());
            await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore(), new AlgeDecoderFactory());
            var date = new DateOnly(2026, 9, 27);
            var series = await workspace.CreateAsync(file, new("Synthetic race weekend", "Test slope", "Test club", date, date, "FIN", "2026/27"));
            series = await workspace.SaveCompetitionAsync(null, new("Synthetic Slalom", "SL1", date, Discipline.Slalom, RaceType.Fis, 2, 1, "1234"), series.Revision);
            var competition = series.Competitions[0];
            var revision = series.Revision;
            revision = (await workspace.SaveCategoryRuleAsync(null, new("Test 2000", 2000, 2000, null, 0), revision)).Revision;
            for (var i = 0; i < 2; i++)
            {
                revision = (await workspace.SaveDeskRowAsync(null, new($"TEST{i:00}", "Athlete", 2000, $"{123456 + i}", "FIN", "Synthetic club", Gender.Female),
                    competition.Id, true, null, revision)).Revision;
            }
            using var vm = new MainViewModel(workspace, new FileDialogsStub { OpenPath = file, NewPath = file, BackupPath = file + ".backup" }, fisStore: cache,
                recentSeriesStore: new RecentSeriesStore(root), timingPreferencesStore: new TimingPreferencesStore(root));
            await vm.OpenSeriesCommand.ExecuteAsync(null);
            await vm.OpenDrawRunCommand.ExecuteAsync(new DrawDestination(competition, 1));
            await vm.PrepareDrawCommand.ExecuteAsync(null);
            await vm.OpenTimingRunCommand.ExecuteAsync(new DrawDestination(competition, 1));
            Assert.False(vm.IsError, vm.StatusMessage);
            var window = new MainWindow { DataContext = vm, WindowState = WindowState.Normal, Width = 1366, Height = 850 };
            window.Show();
            var view = window.FindControl<TimingView>("TimingWorkspace")!;
            var grid = view.FindControl<DataGrid>("TimestampsGrid")!;
            var editor = view.FindControl<Border>("ManualTimestampEditor")!;
            Assert.Empty(vm.TimestampRows);
            Assert.False(editor.IsVisible);
            var bib = vm.TimingRows[0].Bib;

            // No device and no timestamps yet: double-click the empty START column.
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            var startHeader = TimestampHeader(view, "START");
            var x = startHeader.TranslatePoint(new Point(startHeader.Bounds.Width / 2, 0), grid)!.Value.X;
            DoubleClick(window, grid, new Point(x, grid.Bounds.Height - 8));
            Assert.True(editor.IsVisible, vm.StatusMessage);
            Assert.Equal("Start", view.FindControl<TextBlock>("ManualTimestampPositionText")!.Text);
            Assert.Equal(MainViewModel.DefaultManualTimestampReason, vm.ManualTimestampReason);
            await TypeManualTimestampAsync(window, view, "12:00:00.00");
            Assert.False(editor.IsVisible);
            Assert.False(vm.IsError, vm.StatusMessage);
            var startRow = Assert.Single(vm.TimestampRows);
            Assert.Equal("Unassigned manual time", startRow.Name);
            var startCell = Assert.IsType<TimingTimestampCell>(startRow.Cells[0]);
            Assert.True(startCell.IsManual);
            Assert.Equal("12:00:00.0000000", startCell.Time);
            Assert.True(grid.IsFocused || grid.IsKeyboardFocusWithin);

            // The row's empty FINISH cell opens the editor for the finish; invalid input keeps the editor and saves nothing.
            window.UpdateLayout();
            var finishCell = grid.GetVisualDescendants().OfType<Border>()
                .First(b => TimingView.GetTimestampChannel(b) == 1 && b.Bounds.Width > 0);
            DoubleClick(window, finishCell, new Point(4, 4));
            Assert.True(editor.IsVisible);
            Assert.Equal("Finish", vm.ManualTimestampPosition);
            var audit = workspace.Timing!.Snapshot!.AuditVersion;
            await TypeManualTimestampAsync(window, view, "12:00:41");
            Assert.True(editor.IsVisible);
            Assert.True(vm.HasManualTimestampError);
            Assert.Equal(audit, workspace.Timing.Snapshot!.AuditVersion);
            await TypeManualTimestampAsync(window, view, "12:00:41.2345");
            Assert.False(editor.IsVisible);
            Assert.Equal(2, vm.TimestampRows.Count);

            // Escape cancels without saving; the column header also opens the editor.
            DoubleClick(window, TimestampHeader(view, "FINISH"), new Point(10, 5));
            Assert.True(editor.IsVisible);
            Assert.Equal("Finish", vm.ManualTimestampPosition);
            window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            Assert.False(editor.IsVisible);
            Assert.Equal(2, vm.TimestampRows.Count);

            // Keyboard: F2 opens the editor for the grid's current column.
            grid.SelectedItem = vm.TimestampRows[0];
            grid.CurrentColumn = grid.Columns.Single(c => Equals(c.Header, "START"));
            grid.Focus();
            window.KeyPressQwerty(PhysicalKey.F2, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            Assert.True(editor.IsVisible);
            Assert.Equal("Start", vm.ManualTimestampPosition);
            window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            Assert.False(editor.IsVisible);

            // A manual timestamp is rendered with a small m and assigned like any other timestamp.
            window.UpdateLayout();
            Assert.Contains(grid.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "m" && t.Classes.Contains("manualTimestampMark"));
            var keys = workspace.Timing.Snapshot!.Observations.Select(o => o.Observation.Key).ToArray();
            await DropTimingRacer(view, vm.CreateTimingDrag(bib)!, keys[0]);
            await DropTimingRacer(view, vm.CreateTimingDrag(bib)!, keys[1]);
            Assert.False(vm.IsError, vm.StatusMessage);
            var row = vm.TimingRows.Single(r => r.Bib == bib);
            Assert.Equal("0:41.23 m", row.DisplayTime);
            Assert.Equal("0:41.23 m", row.Clock.Time);
            Assert.Contains(vm.TimestampRows, r => r.Bib == bib && r.Cells[0]!.IsManual && r.Cells[21]!.IsManual);

            var saved = await workspace.ReadTimingAsync(workspace.Timing.ListId!.Value);
            Assert.Empty(saved.Packets);
            var entries = saved.Audit.Where(a => a.After.Kind == DecisionKind.ManualTime).ToArray();
            Assert.Equal(2, entries.Length);
            Assert.All(entries, a => Assert.Equal((vm.TimingOperator, MainViewModel.DefaultManualTimestampReason), (a.Operator, a.Reason)));
            vm.ShowTimingHistory = true;
            Assert.Contains(vm.TimingHistory, h => h.Summary.Contains("Manual Finish 12:00:41.2345000", StringComparison.Ordinal));
            window.Close();
        }
        finally
        {
            if (Path.GetFullPath(root).StartsWith(Path.Combine(Path.GetTempPath(), "openskitime-manual-timestamp-ui") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            { Directory.Delete(root, true); }
        }
    }
}
