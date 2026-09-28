using Avalonia.Controls;
using Avalonia.Collections;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using OpenSkiTime.Rewrite.Application;
using OpenSkiTime.Rewrite.Desktop;
using OpenSkiTime.Rewrite.Devices;
using OpenSkiTime.Rewrite.Domain;
using OpenSkiTime.Rewrite.Persistence;
using Xunit;

namespace OpenSkiTime.Rewrite.Tests;

public partial class DesktopWorkflowTests
{
    private static void TimingMenu(TimingView view, string gridName, string label)
    {
        var grid = view.FindControl<DataGrid>(gridName)!;
        grid.ContextMenu!.Open(grid);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        var item = AllTimingMenuItems(grid.ContextMenu.Items).Single(x => Equals(x.Header, label));
        Assert.True(item.IsEnabled);
        Assert.True(item.IsVisible);
        item.Command!.Execute(item.CommandParameter);
        grid.ContextMenu.Close();
    }

    private static IEnumerable<MenuItem> AllTimingMenuItems(IEnumerable<object?> items) => items.OfType<MenuItem>()
        .SelectMany(x => new[] { x }.Concat(AllTimingMenuItems(x.Items)));

    private static async Task DropTimingRacer(TimingView view, TimingDragCompetitor racer, string? timestampKey = null)
    {
        var vm = Assert.IsType<MainViewModel>(view.DataContext);
        if (timestampKey is not null)
        {
            var row = vm.TimestampRows.Single(x => x.Cells.Any(c => c?.Key == timestampKey));
            view.FindControl<DataGrid>("TimestampsGrid")!.ScrollIntoView(row, null);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            ((Window)view.GetVisualRoot()!).UpdateLayout();
        }
        var target = timestampKey is null ? view.FindControl<Border>("StartDropTarget")!
            : view.GetVisualDescendants().OfType<Border>().Single(x => x.Tag is TimingTimestampCell cell && cell.Key == timestampKey);
        var data = new DataObject(); data.Set(TimingView.CompetitorDragFormat, racer);
        var over = new DragEventArgs(DragDrop.DragOverEvent, data, target, new Avalonia.Point(2, 2), KeyModifiers.None) { Source = target };
        target.RaiseEvent(over);
        Assert.Equal(DragDropEffects.Move, over.DragEffects);
        Assert.Contains("timingDropTarget", target.Classes);
        var drop = new DragEventArgs(DragDrop.DropEvent, data, target, new Avalonia.Point(2, 2), KeyModifiers.None) { Source = target };
        target.RaiseEvent(drop);
        await view.PendingTimingDrop;
        Assert.False(vm.IsError, vm.StatusMessage);
        Assert.DoesNotContain("timingDropTarget", target.Classes);
    }

    [AvaloniaFact]
    public async Task RaceControlKeepsQueuesVisibleAndSupportsFastClassificationAndFalseFinishRecovery()
    {
        var root = Path.Combine(Path.GetTempPath(), "openskitime-race-ui", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var file = Path.Combine(root, "Synthetic-Race-Control.ost");
            var cache = new FisLocalStore(root);
            await cache.SaveListAsync(SyntheticFisArchive());
            await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore(), new AlgeDecoderFactory());
            var date = new DateOnly(2026, 9, 27);
            var series = await workspace.CreateAsync(file, new("Synthetic race weekend", "Test slope", "Test club", date, date, "FIN", "2026/27"));
            series = await workspace.SaveCompetitionAsync(null, new("Synthetic Slalom", "SL1", date, Discipline.Slalom, RaceType.Fis, 2, 2, "1234"), series.Revision);
            var competition = series.Competitions[0];
            var revision = series.Revision;
            revision = (await workspace.SaveCategoryRuleAsync(null, new("Test 2000", 2000, 2000, null, 0), revision)).Revision;
            revision = (await workspace.SaveCategoryRuleAsync(null, new("Test 2001", 2001, 2001, null, 1), revision)).Revision;
            for (var i = 0; i < 12; i++)
            {
                var saved = await workspace.SaveDeskRowAsync(null, new($"TEST{i:00}", "Athlete", 2000 + i % 2, $"{123456+i}", "FIN", "Synthetic club", Gender.Female), competition.Id, true, null, revision);
                revision = saved.Revision;
            }
            var preferences = new TimingPreferencesStore(root);
            using var vm = new MainViewModel(workspace, new FileDialogsStub { OpenPath = file, NewPath = file, BackupPath = file + ".backup" }, fisStore: cache,
                recentSeriesStore: new RecentSeriesStore(root), timingPreferencesStore: preferences);
            await vm.OpenSeriesCommand.ExecuteAsync(null);
            await vm.OpenDrawRunCommand.ExecuteAsync(new DrawDestination(competition, 1));
            await vm.PrepareDrawCommand.ExecuteAsync(null);
            await vm.OpenTimingRunCommand.ExecuteAsync(new DrawDestination(competition, 1));
            Assert.False(vm.IsError, vm.StatusMessage);
            var window = new MainWindow { DataContext = vm, WindowState = WindowState.Normal, Width = 1366, Height = 850 };
            window.Show();
            var view = window.FindControl<TimingView>("TimingWorkspace")!;
            vm.ShowSettingsCommand.Execute(null);
            vm.TimingSource = "Simulator"; vm.TimingIntermediateChannels = "2,3";
            Click(window, "Save timing settings"); await vm.SaveTimingPreferencesCommand.ExecutionTask!;
            Assert.Equal("2,3", preferences.Load()!.IntermediateChannels);
            var output = Environment.GetEnvironmentVariable("OPENSKITIME_RACE_VISUAL_DIR");
            CaptureDraw(window, output, "race-settings.png");
            Click(window, "Connect"); await vm.ConnectTimingCommand.ExecutionTask!;
            Click(window, "Back to timing");
            Assert.False(vm.IsError, vm.StatusMessage);
            var a = vm.TimingRows[0].Bib; var b = vm.TimingRows[1].Bib; var c = vm.TimingRows[2].Bib; var d = vm.TimingRows[3].Bib;
            Assert.StartsWith(a + " ·", vm.NextStartLabel, StringComparison.Ordinal);
            vm.SimulationTime = "12:00:00.0000";
            Click(window, "Test start"); await vm.SimulatePulseCommand.ExecutionTask!;
            await WaitTimingAsync(vm, () => vm.OnCourseRows.Count == 1);
            var runningRow = Assert.Single(vm.OnCourseRows);
            var initialRunningTime = runningRow.Clock.Time;
            await WaitTimingAsync(vm, () => runningRow.Clock.Time != initialRunningTime);
            Assert.Same(runningRow, Assert.Single(vm.OnCourseRows)); // clock ticks must not replace/select grid rows
            Assert.Equal(runningRow.Clock.Time, vm.ExpectedFinishTime);
            Assert.DoesNotContain(view.GetVisualDescendants().OfType<Button>(), x => Equals(x.Content, "Settings") || Equals(x.Content, "Connect") || Equals(x.Content, "Disconnect"));
            Assert.DoesNotContain(view.GetVisualDescendants().OfType<DataGrid>(), x => x.Name == "TimingObservationsGrid");
            vm.ShowSettingsCommand.Execute(null);
            Assert.True(vm.IsTimingConnected);
            vm.ReturnToTimingCommand.Execute(null);
            Assert.StartsWith(b + " ·", vm.NextStartLabel, StringComparison.Ordinal);
            TimingMenu(view, "AtStartGrid", "Absent next starter · DNS"); await vm.NextStartDnsCommand.ExecutionTask!;
            Assert.Equal("DNS", vm.TimingRows.Single(x => x.Bib == b).Status);
            Assert.StartsWith(c + " ·", vm.NextStartLabel, StringComparison.Ordinal);
            view.FindControl<DataGrid>("AtStartGrid")!.SelectedItem = vm.AtStartRows.Single(x => x.Bib == d);
            TimingMenu(view, "AtStartGrid", "Next at start"); await vm.ExpectSelectedCommand.ExecutionTask!;
            Assert.StartsWith(d + " ·", vm.NextStartLabel, StringComparison.Ordinal);
            vm.SimulationTime = "12:00:05.0000";
            Click(window, "Test start"); await vm.SimulatePulseCommand.ExecutionTask!;
            await WaitTimingAsync(vm, () => vm.OnCourseRows.Count == 2);
            view.FindControl<DataGrid>("RunningGrid")!.SelectedItem = vm.RunningRows.Single(x => x.Bib == d);
            Assert.True(vm.ReturnToStartCommand.CanExecute(null));
            CaptureDraw(window, output, "race-false-start-selected.png");
            await DropTimingRacer(view, vm.CreateTimingDrag(d)!);
            Assert.False(vm.IsError, vm.StatusMessage);
            Assert.Equal(d, vm.SelectedTimingRow!.Bib);
            Assert.Equal("Ready", vm.SelectedTimingRow.Status);
            Assert.False(vm.ReturnToStartCommand.CanExecute(null));
            Assert.Equal(a, Assert.Single(vm.OnCourseRows).Bib);
            Assert.StartsWith(d + " ·", vm.NextStartLabel, StringComparison.Ordinal);
            CaptureDraw(window, output, "race-returned-to-start.png");
            vm.SimulationTime = "12:00:06.0000";
            Click(window, "Test start"); await vm.SimulatePulseCommand.ExecutionTask!;
            await WaitTimingAsync(vm, () => vm.OnCourseRows.Count == 2);
            Assert.True(vm.ReturnToStartCommand.CanExecute(null));
            vm.SimulationTime = "12:00:20.0000";
            Click(window, "Test I1"); await vm.SimulatePulseCommand.ExecutionTask!;
            await WaitTimingAsync(vm, () => vm.OnCourseRows.Single(x => x.Bib == a).SplitTimes.Contains("I1 0:20.00", StringComparison.Ordinal));
            Assert.Equal("I1 0:20.00", vm.OnCourseRows.Single(x => x.Bib == a).SplitTimes);
            vm.SimulationTime = "12:00:25.0000";
            Click(window, "Test I2"); await vm.SimulatePulseCommand.ExecutionTask!;
            await WaitTimingAsync(vm, () => vm.OnCourseRows.Single(x => x.Bib == a).SplitTimes.Contains("I2 0:25.00", StringComparison.Ordinal));
            // Selection in a different visual pane must target that competitor's quick actions.
            view.FindControl<DataGrid>("RunningGrid")!.SelectedItem = vm.RunningRows.Single(x => x.Bib == d);
            view.FindControl<DataGrid>("RunningGrid")!.Focus();
            window.KeyPressQwerty(PhysicalKey.F, RawInputModifiers.Control);
            await vm.ClassifyTimingCommand.ExecutionTask!;
            Assert.Equal("DNF", vm.TimingRows.Single(x => x.Bib == d).Status);
            Assert.Single(vm.OnCourseRows);
            vm.SimulationTime = "12:00:30.0000";
            Click(window, "Test finish"); await vm.SimulatePulseCommand.ExecutionTask!;
            await WaitTimingAsync(vm, () => vm.FinishedTimingRows.Count == 1);
            Assert.Equal(d, vm.SelectedTimingRow!.Bib); // background queue changes must not retarget quick actions
            Assert.Equal(d, Assert.IsType<TimingGridRow>(view.FindControl<DataGrid>("RankingGrid")!.SelectedItem).Bib);
            TimingMenu(view, "RunningGrid", "False finish · not a racer"); await vm.IgnoreLastFinishCommand.ExecutionTask!;
            Assert.Single(vm.OnCourseRows); Assert.Empty(vm.FinishedTimingRows);
            Assert.StartsWith(a + " ·", vm.ExpectedFinishLabel, StringComparison.Ordinal);
            vm.SimulationTime = "12:00:42.1234";
            Click(window, "Test finish"); await vm.SimulatePulseCommand.ExecutionTask!;
            await WaitTimingAsync(vm, () => vm.FinishedTimingRows.Count == 1);
            Assert.Equal("0:42.12", vm.FinishedTimingRows[0].Time);
            Assert.False(vm.ShowTimingCorrection); // a valid finish is displayed immediately without an approval step
            vm.SimulationTime = "12:00:44.0000";
            Click(window, "Test finish"); await vm.SimulatePulseCommand.ExecutionTask!;
            await WaitTimingAsync(vm, () => vm.TimingObservations.Any(x => x.State == "Unassigned"));
            TimingMenu(view, "RunningGrid", "False finish · not a racer"); await vm.IgnoreLastFinishCommand.ExecutionTask!;
            Assert.Equal("0:42.12", vm.FinishedTimingRows[0].Time); // a stray unassigned pulse must not erase the previous racer's finish
            vm.SimulationTime = "12:00:45.0000";
            Click(window, "Test start"); await vm.SimulatePulseCommand.ExecutionTask!;
            await WaitTimingAsync(vm, () => vm.OnCourseRows.Count == 1);
            vm.SimulationTime = "12:00:50.0000";
            Click(window, "Test start"); await vm.SimulatePulseCommand.ExecutionTask!;
            await WaitTimingAsync(vm, () => vm.OnCourseRows.Count == 2);
            CaptureDraw(window, output, "race-live-1366.png");
            window.Width = 980; window.Height = 680;
            CaptureDraw(window, output, "race-live-980.png");
            Assert.True(view.FindControl<DataGrid>("AtStartGrid")!.Bounds.Height > 100);
            Assert.True(view.FindControl<DataGrid>("RunningGrid")!.Bounds.Height > 80);
            Assert.Equal(4, view.GetVisualDescendants().OfType<DataGrid>().Count());
            Assert.True(view.FindControl<DataGrid>("RankingGrid")!.Bounds.Height >= 65);
            vm.ShowAllTimingObservations = true;
            Assert.Contains(vm.TimingObservations, x => x.State == "Ignored");
            Assert.DoesNotContain(view.GetVisualDescendants().OfType<Button>(), x => Equals(x.Content, "Correct time") || Equals(x.Content, "Assign finish"));
            // Each queue exposes the same contextual classification actions.
            foreach (var name in new[] { "AtStartGrid", "RunningGrid", "TimestampsGrid", "RankingGrid" })
            {
                var grid = view.FindControl<DataGrid>(name)!;
                grid.ContextMenu!.Open(grid);
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                Assert.Contains(AllTimingMenuItems(grid.ContextMenu!.Items), x => Equals(x.Header, "DSQ · disqualified"));
                Assert.Contains(grid.ContextMenu.Items.OfType<MenuItem>(), x => Equals(x.Header, "Back to start") && x.Command == vm.ReturnToStartCommand);
                Assert.All(AllTimingMenuItems(grid.ContextMenu.Items).Where(x => x.Command is not null), x => Assert.NotNull(x.InputGesture));
                grid.ContextMenu.Close();
            }
            // More than three arrivals: Running retains the latest three, Ranking retains everyone.
            async Task Finish(string at, int count)
            {
                vm.SimulationTime = at;
                Click(window, "Test finish"); await vm.SimulatePulseCommand.ExecutionTask!;
                await WaitTimingAsync(vm, () => vm.FinishedTimingRows.Count == count);
            }
            await Finish("12:00:55.0000", 2);
            await Finish("12:01:00.0000", 3);
            vm.SimulationTime = "12:01:05.0000";
            Click(window, "Test start"); await vm.SimulatePulseCommand.ExecutionTask!;
            await WaitTimingAsync(vm, () => vm.OnCourseRows.Count == 1);
            await Finish("12:01:20.0000", 4);
            vm.SimulationTime = "12:01:25.0000";
            Click(window, "Test start"); await vm.SimulatePulseCommand.ExecutionTask!;
            await WaitTimingAsync(vm, () => vm.OnCourseRows.Count == 1);
            await Finish("12:01:40.0000", 5);
            Assert.Equal(3, vm.RunningRows.Count);
            Assert.Equal(vm.FinishedTimingRows.Take(3).Select(x => x.Bib), vm.RunningRows.Select(x => x.Bib));
            Assert.Equal(7, vm.RankingRows.Count); // five finishers plus DNS and DNF
            Assert.Equal(vm.FinishedTimingRows[0].Bib, Assert.Single(vm.RankingRows, x => x.IsLatestFinish).Bib);
            Assert.Equal(2, vm.RankingView.Groups!.OfType<DataGridCollectionViewGroup>().Count());
            foreach (var group in vm.RankingRows.GroupBy(x => x.Category))
            {
                var finished = group.Where(x => x.Result.Status == Timing.TimingStatus.Finished).ToArray();
                Assert.Equal(finished.OrderBy(x => x.Result.Hundredths), finished);
                foreach (var row in finished)
                { Assert.Equal(finished.Count(x => x.Result.Hundredths < row.Result.Hundredths) + 1, row.DisplayRank); }
            }
            window.Width = 1366; window.Height = 850;
            CaptureDraw(window, output, "race-grouped-ranking.png");
            vm.ShowSettingsCommand.Execute(null);
            Click(window, "Disconnect"); await vm.DisconnectTimingCommand.ExecutionTask!;
            if (!string.IsNullOrEmpty(output))
            {
                Directory.CreateDirectory(output);
                await workspace.BackupAsync(Path.Combine(output, "Synthetic-Race-Control-" + Guid.NewGuid().ToString("N") + ".ost"));
            }
            await vm.OpenSeriesCommand.ExecuteAsync(null);
            await vm.OpenTimingRunCommand.ExecuteAsync(new DrawDestination(competition, 1));
            Assert.Equal("0:42.12", vm.FinishedTimingRows.Single(x => x.Bib == a).Time);
            Assert.Equal(5, vm.FinishedTimingRows.Count);
            Assert.Empty(vm.OnCourseRows);
            Assert.Equal(3, vm.RunningRows.Count);
            Assert.Empty(view.GetVisualDescendants().OfType<ComboBox>());
            window.Close();
        }
        finally
        {
            if (Path.GetFullPath(root).StartsWith(Path.Combine(Path.GetTempPath(), "openskitime-race-ui") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            { Directory.Delete(root, true); }
        }
    }
}
