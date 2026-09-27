using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
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
    public async Task TimingWorkspaceCapturesCorrectsUndoesReopensAndFeedsRunTwoThroughVisibleCommands()
    {
        var root = Path.Combine(Path.GetTempPath(), "openskitime-timing-ui", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var file = Path.Combine(root, "Synthetic-M5.ost");
            var cache = new FisLocalStore(root);
            await cache.SaveListAsync(SyntheticFisArchive());
            await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore(), new AlgeDecoderFactory());
            var date = new DateOnly(2026, 9, 27);
            var series = await workspace.CreateAsync(file, new("Synthetic race weekend", "Test slope", "Test club", date, date, "FIN", "2026/27"));
            series = await workspace.SaveCompetitionAsync(null, new("Synthetic Slalom", "SL1", date, Discipline.Slalom, RaceType.Fis, 2, 0, "1234"), series.Revision);
            var competition = series.Competitions[0];
            var revision = series.Revision;
            for (var i = 0; i < 12; i++)
            {
                var saved = await workspace.SaveDeskRowAsync(null, new($"TEST{i:00}", "Athlete", 2000, $"{123456+i}", "FIN", "Synthetic club", Gender.Female),
                    competition.Id, true, null, revision);
                revision = saved.Revision;
            }
            var output = Environment.GetEnvironmentVariable("OPENSKITIME_M5_VISUAL_DIR");
            var dialogs = new FileDialogsStub { NewPath = file, OpenPath = file, BackupPath = file + ".backup" };
            using var vm = new MainViewModel(workspace, dialogs, fisStore: cache, recentSeriesStore: new RecentSeriesStore(root));
            await vm.OpenSeriesCommand.ExecuteAsync(null);
            await vm.OpenDrawRunCommand.ExecuteAsync(new DrawDestination(competition, 1));
            await vm.PrepareDrawCommand.ExecuteAsync(null);
            Assert.False(vm.IsError, vm.StatusMessage);
            var window = new MainWindow { DataContext = vm, WindowState = WindowState.Normal, Width = 1280, Height = 800 };
            window.Show();
            var timingButton = window.FindControl<Button>("TimingMenuButton")!;
            timingButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await vm.RefreshTimingMenuCommand.ExecutionTask!;
            Dispatcher.UIThread.RunJobs();
            var menu = Assert.IsType<MenuFlyout>(timingButton.Flyout);
            var raceMenu = Assert.IsType<MenuItem>(Assert.Single(menu.Items));
            Assert.Equal("SL1", raceMenu.Header);
            var runMenu = Assert.IsType<MenuItem>(Assert.Single(raceMenu.Items));
            runMenu.Command!.Execute(runMenu.CommandParameter);
            await vm.OpenTimingRunCommand.ExecutionTask!;
            menu.Hide();
            Assert.True(vm.IsTimingSection);
            Assert.Contains("Run 1", vm.TimingContext, StringComparison.Ordinal);
            Assert.Contains("1234", vm.WindowTitle, StringComparison.Ordinal);
            CaptureDraw(window, output, "timing-device.png");
            vm.TimingSource = "Simulator";
            vm.FollowTimingOrder = false; // This reference scenario deliberately operates with manual bib selection.
            Click(window, "Connect");
            await vm.ConnectTimingCommand.ExecutionTask!;
            Assert.False(vm.IsError, vm.StatusMessage);
            vm.SelectedTimingRow = vm.TimingRows[0];
            var firstBib = vm.SelectedTimingRow.Bib;
            var view = window.FindControl<TimingView>("TimingWorkspace")!;
            view.FindControl<DataGrid>("TimingResultsGrid")!.Focus();
            window.KeyPressQwerty(PhysicalKey.F5, RawInputModifiers.None);
            if (vm.ExpectSelectedCommand.ExecutionTask is { } arming) { await arming; }
            Assert.Equal(firstBib, workspace.Timing!.ArmedStart);
            vm.SimulationTime = "12:00:00.9999999";
            Click(window, "Test start"); await vm.SimulatePulseCommand.ExecutionTask!;
            await WaitTimingAsync(vm, () => vm.TimingRows[0].Status == "On course");
            vm.SelectedTimingRow = vm.TimingRows[1];
            await vm.ArmStartCommand.ExecuteAsync(null);
            vm.SimulationTime = "12:00:30.0000";
            Click(window, "Test start"); await vm.SimulatePulseCommand.ExecutionTask!;
            await WaitTimingAsync(vm, () => vm.TimingRows[1].Status == "On course");
            vm.SelectedTimingRow = vm.TimingRows[0];
            await vm.ArmFinishCommand.ExecuteAsync(null);
            vm.SimulationTime = "12:01:02.9999998";
            Click(window, "Test finish"); await vm.SimulatePulseCommand.ExecutionTask!;
            await WaitTimingAsync(vm, () => vm.TimingRows[0].Status == "Finished");
            Assert.Equal("1:01.99", vm.TimingRows[0].Time);
            vm.SimulationTime = "12:01:35.0000";
            Click(window, "Test finish"); await vm.SimulatePulseCommand.ExecutionTask!;
            await WaitTimingAsync(vm, () => vm.TimingObservations.Any(x => x.State == "Unassigned"));
            vm.SelectedTimingObservation = vm.TimingObservations.Single(x => x.State == "Unassigned");
            vm.ShowTimingReview = true;
            vm.ObservationBibText = vm.TimingRows[1].Bib.ToString(System.Globalization.CultureInfo.InvariantCulture);
            Click(window, "Assign"); await vm.ChangeObservationCommand.ExecutionTask!;
            Assert.Equal("1:05.00", vm.TimingRows[1].Time);
            vm.SelectedTimingRow = vm.TimingRows[0];
            vm.CorrectedTimeText = "1:02.00"; vm.TimingReason = "Verified backup timing";
            Click(window, "Correct time"); await vm.CorrectTimingTimeCommand.ExecutionTask!;
            Assert.Equal("1:02.00", vm.TimingRows[0].Time);
            vm.ShowTimingHistory = true;
            vm.SelectedTimingHistory = vm.TimingHistory[0];
            CaptureDraw(window, output, "timing-history.png");
            Click(window, "Undo selected change"); await vm.UndoTimingChangeCommand.ExecutionTask!;
            Assert.Equal("1:01.99", vm.TimingRows[0].Time);
            vm.ShowTimingHistory = false;
            vm.ShowTimingReview = false;
            vm.ShowAllTimingObservations = true;
            Assert.Contains(vm.TimingObservations, x => x.Time == "12:00:00.9999999");
            Assert.Contains(vm.TimingObservations, x => x.Time == "12:01:02.9999998");
            CaptureDraw(window, output, "timing-live.png");
            window.Width = 980; window.Height = 680;
            CaptureDraw(window, output, "timing-compact.png");
            Assert.True(view.FindControl<DataGrid>("TimingResultsGrid")!.Bounds.Height > 100);
            for (var i = 2; i < vm.TimingRows.Count; i++)
            {
                vm.SelectedTimingRow = vm.TimingRows[i]; vm.TimingReason = "Synthetic no start";
                Click(window, "DNS"); await vm.ClassifyTimingCommand.ExecutionTask!;
            }
            Click(window, "Disconnect"); await vm.DisconnectTimingCommand.ExecutionTask!;
            Assert.False(vm.IsTimingConnected);
            Assert.True(vm.CanPrepareNextTimedRun);
            if (!string.IsNullOrEmpty(output))
            {
                Directory.CreateDirectory(output);
                await workspace.BackupAsync(Path.Combine(output, "Synthetic-M5-" + Guid.NewGuid().ToString("N") + ".ost"));
            }
            Click(window, "Prepare Run 2"); await vm.PrepareNextTimedRunCommand.ExecutionTask!;
            Assert.True(vm.HasCapturedRunInput);
            Assert.True(vm.CanPrepareDraw);
            Click(window, "Create start list"); await vm.PrepareDrawCommand.ExecutionTask!;
            Assert.False(vm.IsError, vm.StatusMessage);
            Assert.Equal(2, vm.DrawEntries.Count);
            var second = vm.DrawRevision!;
            Assert.NotNull(second.SourceTimingVersion);
            await vm.OpenTimingRunCommand.ExecuteAsync(new DrawDestination(competition, 2));
            Assert.Contains("Run 2", vm.TimingContext, StringComparison.Ordinal);
            Click(window, "Connect"); await vm.ConnectTimingCommand.ExecutionTask!;
            Assert.False(vm.IsError, vm.StatusMessage);
            var bib = vm.TimingRows[0].Bib;
            vm.StartBibText = bib.ToString(System.Globalization.CultureInfo.InvariantCulture);
            await vm.ArmStartCommand.ExecuteAsync(null);
            vm.SimulationTime = "13:00:00.0000";
            Click(window, "Test start"); await vm.SimulatePulseCommand.ExecutionTask!;
            await WaitTimingAsync(vm, () => vm.TimingRows[0].Status == "On course");
            vm.FinishBibText = bib.ToString(System.Globalization.CultureInfo.InvariantCulture);
            await vm.ArmFinishCommand.ExecuteAsync(null);
            vm.SimulationTime = "13:01:03.0000";
            Click(window, "Test finish"); await vm.SimulatePulseCommand.ExecutionTask!;
            await WaitTimingAsync(vm, () => vm.TimingRows[0].Status == "Finished");
            Assert.Equal("2:08.00", vm.TimingRows[0].TotalTime);
            Assert.True(view.FindControl<DataGrid>("FinishedGrid")!.Columns.Single(x => Equals(x.Header, "TOTAL")).IsVisible);
            Click(window, "Disconnect"); await vm.DisconnectTimingCommand.ExecutionTask!;
            await vm.OpenSeriesCommand.ExecuteAsync(null);
            Assert.False(vm.HasTimingRun);
            await vm.OpenTimingRunCommand.ExecuteAsync(new DrawDestination(competition, 2));
            Assert.Equal("2:08.00", vm.TimingRows[0].TotalTime);
            window.Width = 1280; window.Height = 800;
            CaptureDraw(window, output, "timing-run2.png");
            window.Close();
        }
        finally
        {
            if (Path.GetFullPath(root).StartsWith(Path.Combine(Path.GetTempPath(), "openskitime-timing-ui") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            { Directory.Delete(root, true); }
        }
    }

    private static async Task WaitTimingAsync(MainViewModel vm, Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        do { await Task.Delay(10, timeout.Token); vm.RefreshTiming(); Dispatcher.UIThread.RunJobs(); } while (!condition());
        Assert.False(vm.IsError, vm.StatusMessage);
    }
}
