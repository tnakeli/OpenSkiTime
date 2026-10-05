using Avalonia.Controls;
using Avalonia.Collections;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
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
            : view.FindControl<DataGrid>("TimestampsGrid")!.GetVisualDescendants().OfType<Border>()
                .First(x => x.Tag is TimingTimestampCell cell && cell.Key == timestampKey && x.Bounds.Width > 0);
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

    private static async Task DropStartQueueRow(TimingView view, TimingDragCompetitor racer, int targetBib)
    {
        var grid = view.FindControl<DataGrid>("AtStartGrid")!;
        var vm = (MainViewModel)view.DataContext!;
        var targetItem = vm.AtStartRows.Single(x => x.Bib == targetBib);
        grid.ScrollIntoView(targetItem, null);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        ((Window)view.GetVisualRoot()!).UpdateLayout();
        var row = grid.GetVisualDescendants().OfType<DataGridRow>()
            .Single(x => x.DataContext is TimingGridRow racerRow && racerRow.Bib == targetBib);
        Assert.True(DragDrop.GetAllowDrop(row));
        var data = new DataObject(); data.Set(TimingView.CompetitorDragFormat, racer);
        vm.IsTimingDragging = true;
        var lower = new DragEventArgs(DragDrop.DragOverEvent, data, row,
            new Avalonia.Point(2, row.Bounds.Height - 2), KeyModifiers.None) { Source = row };
        row.RaiseEvent(lower);
        Assert.Equal(DragDropEffects.Move, lower.DragEffects);
        Assert.Contains("timingDropBelow", row.Classes);
        var over = new DragEventArgs(DragDrop.DragOverEvent, data, row, new Avalonia.Point(2, 2), KeyModifiers.None) { Source = row };
        row.RaiseEvent(over);
        Assert.Equal(DragDropEffects.Move, over.DragEffects);
        Assert.Contains("timingDropAbove", row.Classes);
        Assert.DoesNotContain("timingDropBelow", row.Classes);
        CaptureDraw((Window)view.GetVisualRoot()!, Environment.GetEnvironmentVariable("OPENSKITIME_RACE_VISUAL_DIR"), "race-start-reorder-hover.png");
        var drop = new DragEventArgs(DragDrop.DropEvent, data, row, new Avalonia.Point(2, 2), KeyModifiers.None) { Source = row };
        row.RaiseEvent(drop);
        await view.PendingTimingDrop;
        vm.IsTimingDragging = false;
        Assert.DoesNotContain("timingDropAbove", row.Classes);
    }

    private static async Task DropTimingStatus(TimingView view, TimingDragCompetitor racer, string status)
    {
        var button = view.FindControl<Button>("StatusDrop" + status)!;
        Assert.True(DragDrop.GetAllowDrop(button));
        var data = new DataObject(); data.Set(TimingView.CompetitorDragFormat, racer);
        var over = new DragEventArgs(DragDrop.DragOverEvent, data, button, new Avalonia.Point(2, 2), KeyModifiers.None) { Source = button };
        button.RaiseEvent(over);
        Assert.Equal(DragDropEffects.Move, over.DragEffects);
        Assert.Contains("timingStatusDropTarget", button.Classes);
        CaptureDraw((Window)view.GetVisualRoot()!, Environment.GetEnvironmentVariable("OPENSKITIME_RACE_VISUAL_DIR"), "race-status-drop-hover.png");
        var drop = new DragEventArgs(DragDrop.DropEvent, data, button, new Avalonia.Point(2, 2), KeyModifiers.None) { Source = button };
        button.RaiseEvent(drop);
        await view.PendingTimingDrop;
        Assert.DoesNotContain("timingStatusDropTarget", button.Classes);
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
            SelectTimingSettingsTab(window);
            UseSimulatorTiming(vm, 2, 3);
            Click(window, "Save timing settings"); await vm.SaveTimingPreferencesCommand.ExecutionTask!;
            Assert.Equal([2, 3], preferences.Load()!.Intermediates.Select(x => x.Channel));
            var output = Environment.GetEnvironmentVariable("OPENSKITIME_RACE_VISUAL_DIR");
            CaptureDraw(window, output, "race-settings.png");
            Click(window, "Connect"); await vm.ConnectTimingCommand.ExecutionTask!;
            Click(window, "Back to timing"); await vm.ReturnToTimingCommand.ExecutionTask!;
            Assert.False(vm.IsError, vm.StatusMessage);
            Assert.Equal(4, vm.TimingChannelStates.Count);
            Assert.All(vm.TimingChannelStates, state => Assert.False(state));
            await vm.ToggleTimingChannelCommand.ExecuteAsync("start");
            await vm.ToggleTimingChannelCommand.ExecuteAsync("finish");
            await vm.ToggleTimingChannelCommand.ExecuteAsync("intermediate:1");
            await vm.ToggleTimingChannelCommand.ExecuteAsync("intermediate:2");
            Assert.Equal(2, view.FindControl<DataGrid>("RunningGrid")!.Columns.Count(x => x.Tag is "intermediate" && x.IsVisible));
            await vm.ToggleTimingChannelCommand.ExecuteAsync("intermediate:2");
            Assert.Equal(2, view.FindControl<DataGrid>("RunningGrid")!.Columns.Count(x => x.Tag is "intermediate" && x.IsVisible));
            Assert.Equal(2, view.FindControl<DataGrid>("TimestampsGrid")!.Columns.Count(x => x.Tag is "intermediate" && x.IsVisible));
            Assert.True(workspace.Timing!.IsHeld(3));
            await vm.ToggleTimingChannelCommand.ExecuteAsync("intermediate:2");
            var a = vm.TimingRows[0].Bib; var b = vm.TimingRows[1].Bib; var c = vm.TimingRows[2].Bib; var d = vm.TimingRows[3].Bib;
            Assert.Equal(a, vm.AtStartRows[^1].Bib);
            Assert.StartsWith(a + " ·", vm.NextStartLabel, StringComparison.Ordinal);
            view.FindControl<DataGrid>("AtStartGrid")!.SelectedItem = vm.AtStartRows[^1];
            view.FindControl<DataGrid>("AtStartGrid")!.Focus();
            window.KeyPressQwerty(PhysicalKey.ArrowUp, RawInputModifiers.Control); await vm.MoveStartRowCommand.ExecutionTask!;
            Assert.Equal(a, vm.AtStartRows[^2].Bib);
            Assert.Equal(b, vm.AtStartRows[^1].Bib);
            Assert.StartsWith(b + " ·", vm.NextStartLabel, StringComparison.Ordinal);
            view.FindControl<DataGrid>("AtStartGrid")!.SelectedItem = vm.AtStartRows[^2];
            window.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.Control); await vm.MoveStartRowCommand.ExecutionTask!;
            Assert.Equal(a, vm.AtStartRows[^1].Bib);
            await DropStartQueueRow(view, vm.CreateTimingDrag(a)!, c);
            Assert.False(vm.IsError, vm.StatusMessage);
            Assert.Equal(new[] { b, c, a }, vm.AtStartRows.Reverse().Take(3).Select(x => x.Bib));
            view.FindControl<DataGrid>("AtStartGrid")!.SelectedItem = vm.AtStartRows.Single(x => x.Bib == a);
            window.KeyPressQwerty(PhysicalKey.F5, RawInputModifiers.None); await vm.ExpectSelectedCommand.ExecutionTask!;
            Assert.Equal(a, vm.AtStartRows[^1].Bib);
            vm.SelectedTimingRow = vm.TimingRows.Single(x => x.Bib == a);
            await DropTimingStatus(view, vm.CreateTimingDrag(c)!, "DSQ");
            Assert.Equal("DSQ", vm.TimingRows.Single(x => x.Bib == c).Status);
            await DropTimingStatus(view, vm.CreateTimingDrag(c)!, "Clear");
            Assert.Equal("Ready", vm.TimingRows.Single(x => x.Bib == c).Status);
            view.FindControl<DataGrid>("AtStartGrid")!.SelectedItem = vm.AtStartRows.Single(x => x.Bib == c);
            Click(window, "DNS"); await vm.ClassifyTimingCommand.ExecutionTask!;
            Assert.Equal("DNS", vm.TimingRows.Single(x => x.Bib == c).Status);
            Click(window, "Clear status"); await vm.ClassifyTimingCommand.ExecutionTask!;
            Assert.Equal("Ready", vm.TimingRows.Single(x => x.Bib == c).Status);
            var startGrid = view.FindControl<DataGrid>("AtStartGrid")!;
            startGrid.SelectedItems.Clear();
            startGrid.SelectedItems.Add(vm.AtStartRows.Single(x => x.Bib == b));
            startGrid.SelectedItems.Add(vm.AtStartRows.Single(x => x.Bib == c));
            Assert.Equal(new[] { b, c }, vm.SelectedTimingBibs.Order());
            Assert.Equal(2, startGrid.SelectedItems.Count);
            Assert.Equal("2 competitors selected", vm.SelectedTimingIdentity);
            CaptureDraw(window, Environment.GetEnvironmentVariable("OPENSKITIME_RACE_VISUAL_DIR"), "race-multi-selected.png");
            TimingMenu(view, "AtStartGrid", "DNS · did not start"); await vm.ClassifyTimingCommand.ExecutionTask!;
            Assert.Equal("DNS", vm.TimingRows.Single(x => x.Bib == b).Status);
            Assert.Equal("DNS", vm.TimingRows.Single(x => x.Bib == c).Status);
            Assert.Equal("Ready", vm.TimingRows.Single(x => x.Bib == d).Status);
            Assert.Equal(2, vm.SelectedTimingBibs.Count);
            Assert.Equal(2, view.FindControl<DataGrid>("RankingGrid")!.SelectedItems.Count);
            Click(window, "Clear status"); await vm.ClassifyTimingCommand.ExecutionTask!;
            Assert.Equal("Ready", vm.TimingRows.Single(x => x.Bib == b).Status);
            Assert.Equal("Ready", vm.TimingRows.Single(x => x.Bib == c).Status);
            await vm.ToggleTimingChannelCommand.ExecuteAsync("start");
            vm.SimulationTime = "11:59:50.0000";
            Click(window, "Test start"); await vm.SimulatePulseCommand.ExecutionTask!;
            await WaitTimingAsync(vm, () => vm.TimingObservations.Any(x => x.State == "Unassigned"));
            Assert.Equal("Ready", vm.TimingRows[0].Status); // OFF still journals the raw start pulse
            await vm.ToggleTimingChannelCommand.ExecuteAsync("start");
            vm.SimulationTime = "12:00:00.0000";
            Click(window, "Test start"); await vm.SimulatePulseCommand.ExecutionTask!;
            await WaitTimingAsync(vm, () => vm.OnCourseRows.Count == 1);
            Assert.Equal("12:00:00", vm.TimingDeviceClock);
            var runningRow = Assert.Single(vm.OnCourseRows);
            var initialRunningTime = runningRow.Clock.Time;
            await WaitTimingAsync(vm, () => runningRow.Clock.Time != initialRunningTime);
            Assert.Same(runningRow, Assert.Single(vm.OnCourseRows)); // clock ticks must not replace/select grid rows
            Assert.Equal(runningRow.Clock.Time, vm.ExpectedFinishTime);
            Assert.DoesNotContain(view.GetVisualDescendants().OfType<Button>(), x => Equals(x.Content, "Settings") || Equals(x.Content, "Connect") || Equals(x.Content, "Disconnect"));
            Assert.DoesNotContain(view.GetVisualDescendants().OfType<DataGrid>(), x => x.Name == "TimingObservationsGrid");
            vm.ShowSettingsCommand.Execute(null);
            Assert.True(vm.IsTimingConnected);
            await vm.ReturnToTimingCommand.ExecuteAsync(null);
            Assert.False(vm.StartInputOn);
            Assert.False(vm.FinishInputOn);
            await vm.ToggleTimingChannelCommand.ExecuteAsync("start");
            await vm.ToggleTimingChannelCommand.ExecuteAsync("finish");
            await vm.ToggleTimingChannelCommand.ExecuteAsync("intermediate:1");
            await vm.ToggleTimingChannelCommand.ExecuteAsync("intermediate:2");
            Assert.StartsWith(b + " ·", vm.NextStartLabel, StringComparison.Ordinal);
            view.FindControl<DataGrid>("AtStartGrid")!.Focus();
            window.KeyPressQwerty(PhysicalKey.F5, RawInputModifiers.Shift); await vm.NextStartDnsCommand.ExecutionTask!;
            Assert.Equal("DNS", vm.TimingRows.Single(x => x.Bib == b).Status);
            Assert.StartsWith(c + " ·", vm.NextStartLabel, StringComparison.Ordinal);
            view.FindControl<DataGrid>("AtStartGrid")!.SelectedItem = vm.AtStartRows.Single(x => x.Bib == d);
            window.KeyPressQwerty(PhysicalKey.F5, RawInputModifiers.None); await vm.ExpectSelectedCommand.ExecutionTask!;
            Assert.Equal(d, vm.AtStartRows[^1].Bib); // changing the next starter also moves that racer in the visible queue
            Assert.StartsWith(d + " ·", vm.NextStartLabel, StringComparison.Ordinal);
            vm.SimulationTime = "12:00:05.0000";
            Click(window, "Test start"); await vm.SimulatePulseCommand.ExecutionTask!;
            await WaitTimingAsync(vm, () => vm.OnCourseRows.Count == 2);
            Assert.Equal(a, vm.RunningRows[^1].Bib); // earliest expected finish stays at the bottom
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
            // Capture can advance between queue and selected-row projections. Wait for the
            // selected competitor's displayed state before checking its contextual action.
            await WaitTimingAsync(vm, () => vm.OnCourseRows.Count == 2
                && vm.SelectedTimingRow is { Bib: var selected, Result.Status: TimingStatus.OnCourse } && selected == d);
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
            view.FindControl<DataGrid>("RunningGrid")!.Focus();
            window.KeyPressQwerty(PhysicalKey.Backspace, RawInputModifiers.Control); await vm.IgnoreLastFinishCommand.ExecutionTask!;
            Assert.Single(vm.OnCourseRows); Assert.Empty(vm.FinishedTimingRows);
            Assert.StartsWith(a + " ·", vm.ExpectedFinishLabel, StringComparison.Ordinal);
            vm.SimulationTime = "12:00:42.1234";
            Click(window, "Test finish"); await vm.SimulatePulseCommand.ExecutionTask!;
            await WaitTimingAsync(vm, () => vm.FinishedTimingRows.Count == 1);
            Assert.Equal("0:42.12", vm.FinishedTimingRows[0].Time);
            Assert.False(vm.ShowTimingCorrection); // a valid finish is displayed immediately without an approval step
            vm.SimulationTime = "12:00:44.0000";
            Click(window, "Test finish"); await vm.SimulatePulseCommand.ExecutionTask!;
            await WaitTimingAsync(vm, () => vm.TimingObservations.Any(x => x.State == "Unassigned" && x.Channel == "Finish" && x.Time.StartsWith("12:00:44", StringComparison.Ordinal)));
            window.KeyPressQwerty(PhysicalKey.Backspace, RawInputModifiers.Control); await vm.IgnoreLastFinishCommand.ExecutionTask!;
            Assert.Equal("0:42.12", vm.FinishedTimingRows[0].Time); // a stray unassigned pulse must not erase the previous racer's finish
            Assert.Contains(vm.TimestampRows[0].Cells, x => x?.Review.Ignored == true); // latest ignored pulse remains visible
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
            Assert.Equal(4, view.GetVisualDescendants().OfType<DataGrid>().Count(x => x.IsEffectivelyVisible));
            Assert.True(view.FindControl<DataGrid>("RankingGrid")!.Bounds.Height >= 65);
            Assert.Contains(vm.TimestampRows, x => x.Cells.Any(cell => cell?.Review.Ignored == true));
            Assert.DoesNotContain(view.GetVisualDescendants().OfType<CheckBox>(), x => Equals(x.Content, "Show ignored"));
            Assert.DoesNotContain(view.GetVisualDescendants().OfType<Button>(), x => Equals(x.Content, "Correct time") || Equals(x.Content, "Assign finish"));
            // Each queue exposes only contextual classification actions.
            foreach (var name in new[] { "AtStartGrid", "RunningGrid", "TimestampsGrid", "RankingGrid" })
            {
                var grid = view.FindControl<DataGrid>(name)!;
                grid.ContextMenu!.Open(grid);
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                Assert.Contains(AllTimingMenuItems(grid.ContextMenu!.Items), x => Equals(x.Header, "DSQ · disqualified"));
                Assert.Equal(5, grid.ContextMenu.Items.OfType<MenuItem>().Count());
                Assert.All(grid.ContextMenu.Items.OfType<MenuItem>(), x => Assert.Equal(vm.ClassifyTimingCommand, x.Command));
                Assert.All(AllTimingMenuItems(grid.ContextMenu.Items).Where(x => x.Command is not null), x => Assert.NotNull(x.InputGesture));
                grid.ContextMenu.Close();
            }
            // Completed racers appear in Ranking, never in the on-course queue.
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
            Assert.Empty(vm.RunningRows);
            Assert.Equal(5, vm.FinishedTimingRows.Count);
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
            SelectTimingSettingsTab(window);
            Click(window, "Disconnect"); await vm.DisconnectTimingCommand.ExecutionTask!;
            UseSimulatorTiming(vm, 2);
            await vm.SaveTimingPreferencesCommand.ExecuteAsync(null);
            await vm.ReturnToTimingCommand.ExecuteAsync(null);
            Assert.True(vm.IsTimingConnected);
            Assert.Single(vm.TimingCheckpoints);
            Assert.All(vm.TimingChannelStates, state => Assert.False(state));
            await vm.ToggleTimingChannelCommand.ExecuteAsync("intermediate:1");
            Assert.Single(view.FindControl<DataGrid>("RunningGrid")!.Columns, x => x.Tag is "intermediate" && x.IsVisible);
            Assert.Single(view.FindControl<DataGrid>("TimestampsGrid")!.Columns, x => x.Tag is "intermediate" && x.IsVisible);
            Assert.Equal(5, vm.FinishedTimingRows.Count); // changing active channels preserves earlier results
            CaptureDraw(window, output, "race-intermediate-removed.png");
            await vm.DisconnectTimingCommand.ExecuteAsync(null);
            if (!string.IsNullOrEmpty(output))
            {
                Directory.CreateDirectory(output);
                await workspace.BackupAsync(Path.Combine(output, "Synthetic-Race-Control-" + Guid.NewGuid().ToString("N") + ".ost"));
            }
            await vm.OpenSeriesCommand.ExecuteAsync(null);
            await vm.OpenTimingRunCommand.ExecuteAsync(new DrawDestination(competition, 1));
            Assert.True(vm.IsTimingConnected); // remembered source reconnects when returning to Timing
            Assert.Equal("0:42.12", vm.FinishedTimingRows.Single(x => x.Bib == a).Time);
            Assert.Equal(5, vm.FinishedTimingRows.Count);
            Assert.Empty(vm.OnCourseRows);
            Assert.Empty(vm.RunningRows);
            Assert.False(vm.ShowTimingClassificationEditor);
            Assert.All(view.GetVisualDescendants().OfType<ComboBox>(), choice => Assert.Equal("TimingClassificationChoice", choice.Name));
            await DropTimingRacer(view, vm.CreateTimingDrag(a)!);
            Assert.Equal("Ready", vm.TimingRows.Single(x => x.Bib == a).Status);
            await vm.DisconnectTimingCommand.ExecuteAsync(null);
            window.Close();
        }
        finally
        {
            if (Path.GetFullPath(root).StartsWith(Path.Combine(Path.GetTempPath(), "openskitime-race-ui") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            { Directory.Delete(root, true); }
        }
    }
}
