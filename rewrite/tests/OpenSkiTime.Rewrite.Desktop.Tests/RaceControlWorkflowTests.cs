using Avalonia.Controls;
using Avalonia.Headless.XUnit;
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
            series = await workspace.SaveCompetitionAsync(null, new("Synthetic Slalom", "SL1", date, Discipline.Slalom, RaceType.Fis, 2, 1, "1234"), series.Revision);
            var competition = series.Competitions[0];
            var revision = series.Revision;
            for (var i = 0; i < 12; i++)
            {
                var saved = await workspace.SaveDeskRowAsync(null, new($"TEST{i:00}", "Athlete", 2000, $"{123456+i}", "FIN", "Synthetic club", Gender.Female), competition.Id, true, null, revision);
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
            vm.TimingSource = "Simulator"; vm.TimingIntermediateChannels = "2";
            Click(window, "Save timing settings"); await vm.SaveTimingPreferencesCommand.ExecutionTask!;
            Assert.Equal("2", preferences.Load()!.IntermediateChannels);
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
            Click(window, "Absent · DNS"); await vm.NextStartDnsCommand.ExecutionTask!;
            Assert.Equal("DNS", vm.TimingRows.Single(x => x.Bib == b).Status);
            Assert.StartsWith(c + " ·", vm.NextStartLabel, StringComparison.Ordinal);
            view.FindControl<DataGrid>("TimingResultsGrid")!.SelectedItem = vm.TimingRows.Single(x => x.Bib == d);
            Click(window, "Start next · F5"); await vm.ExpectSelectedCommand.ExecutionTask!;
            Assert.StartsWith(d + " ·", vm.NextStartLabel, StringComparison.Ordinal);
            vm.SimulationTime = "12:00:05.0000";
            Click(window, "Test start"); await vm.SimulatePulseCommand.ExecutionTask!;
            await WaitTimingAsync(vm, () => vm.OnCourseRows.Count == 2);
            view.FindControl<DataGrid>("OnCourseGrid")!.SelectedItem = vm.OnCourseRows.Single(x => x.Bib == d);
            Assert.True(vm.ReturnToStartCommand.CanExecute(null));
            CaptureDraw(window, output, "race-false-start-selected.png");
            Click(window, "Back to start"); await vm.ReturnToStartCommand.ExecutionTask!;
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
            Click(window, "Test intermediate"); await vm.SimulatePulseCommand.ExecutionTask!;
            await WaitTimingAsync(vm, () => vm.IntermediateTimingRows.Count == 1);
            Assert.Equal("I1 0:20.00", vm.OnCourseRows.Single(x => x.Bib == a).SplitTimes);
            // Selection in a different visual pane must target that competitor's quick actions.
            view.FindControl<DataGrid>("OnCourseGrid")!.SelectedItem = vm.OnCourseRows.Single(x => x.Bib == d);
            Click(window, "DNF"); await vm.ClassifyTimingCommand.ExecutionTask!;
            Assert.Equal("DNF", vm.TimingRows.Single(x => x.Bib == d).Status);
            Assert.Single(vm.OnCourseRows);
            vm.SimulationTime = "12:00:30.0000";
            Click(window, "Test finish"); await vm.SimulatePulseCommand.ExecutionTask!;
            await WaitTimingAsync(vm, () => vm.FinishedTimingRows.Count == 1);
            Assert.Equal(d, vm.SelectedTimingRow!.Bib); // background queue changes must not retarget quick actions
            Assert.Equal(d, Assert.IsType<TimingGridRow>(view.FindControl<DataGrid>("TimingResultsGrid")!.SelectedItem).Bib);
            Click(window, "False finish · not a racer"); await vm.IgnoreLastFinishCommand.ExecutionTask!;
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
            Click(window, "False finish · not a racer"); await vm.IgnoreLastFinishCommand.ExecutionTask!;
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
            Assert.True(view.FindControl<DataGrid>("TimingResultsGrid")!.Bounds.Height > 100);
            Assert.True(view.FindControl<DataGrid>("OnCourseGrid")!.Bounds.Height > 80);
            Assert.True(view.FindControl<DataGrid>("IntermediateGrid")!.Bounds.Height >= 60);
            Assert.True(view.FindControl<DataGrid>("FinishedGrid")!.Bounds.Height >= 65);
            vm.CorrectLastFinishCommand.Execute(null); vm.ShowAllTimingObservations = true;
            CaptureDraw(window, output, "race-correction-980.png");
            Assert.Contains(vm.TimingObservations, x => x.State == "Ignored");
            vm.ShowTimingCorrection = false;
            // Each queue exposes the same contextual classification actions.
            foreach (var name in new[] { "TimingResultsGrid", "OnCourseGrid", "IntermediateGrid", "FinishedGrid" })
            {
                var grid = view.FindControl<DataGrid>(name)!;
                grid.ContextMenu!.Open(grid);
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                Assert.Contains(grid.ContextMenu!.Items.OfType<MenuItem>(), x => Equals(x.Header, "DSQ · disqualified"));
                Assert.Contains(grid.ContextMenu.Items.OfType<MenuItem>(), x => Equals(x.Header, "Back to start") && x.Command == vm.ReturnToStartCommand);
                grid.ContextMenu.Close();
            }
            vm.ShowSettingsCommand.Execute(null);
            Click(window, "Disconnect"); await vm.DisconnectTimingCommand.ExecutionTask!;
            if (!string.IsNullOrEmpty(output))
            {
                Directory.CreateDirectory(output);
                await workspace.BackupAsync(Path.Combine(output, "Synthetic-Race-Control-" + Guid.NewGuid().ToString("N") + ".ost"));
            }
            await vm.OpenSeriesCommand.ExecuteAsync(null);
            await vm.OpenTimingRunCommand.ExecuteAsync(new DrawDestination(competition, 1));
            Assert.Equal("0:42.12", vm.FinishedTimingRows.Single().Time);
            Assert.Equal(2, vm.OnCourseRows.Count);
            Assert.Equal(1, Assert.IsType<int>(view.GetVisualDescendants().OfType<ComboBox>().Single().SelectedItem));
            window.Close();
        }
        finally
        {
            if (Path.GetFullPath(root).StartsWith(Path.Combine(Path.GetTempPath(), "openskitime-race-ui") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            { Directory.Delete(root, true); }
        }
    }
}
