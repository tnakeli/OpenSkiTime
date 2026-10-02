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
    public async Task EditingCompetitionIntermediatesUpdatesConnectedTimingColumnsWithoutRedrawing()
    {
        var root = Path.Combine(Path.GetTempPath(), "openskitime-intermediate-ui", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var file = Path.Combine(root, "Synthetic.ost");
            var cache = new FisLocalStore(root);
            await cache.SaveListAsync(SyntheticFisArchive());
            await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore(), new AlgeDecoderFactory());
            var date = new DateOnly(2026, 9, 27);
            var series = await workspace.CreateAsync(file, new("Synthetic race", "Test slope", "Test club", date, date, "FIN", "2026/27"));
            series = await workspace.SaveCompetitionAsync(null, new("Slalom", "SL", date, Discipline.Slalom, RaceType.Fis, 2, 0, "1234"), series.Revision);
            var competition = series.Competitions[0];
            var revision = series.Revision;
            for (var i = 0; i < 12; i++)
            {
                revision = (await workspace.SaveDeskRowAsync(null, new($"TEST{i:00}", "Athlete", 2000, $"{123456 + i}", "FIN", "Test club", Gender.Female),
                    competition.Id, true, null, revision)).Revision;
            }
            using var vm = new MainViewModel(workspace, new FileDialogsStub { OpenPath = file, NewPath = file, BackupPath = file + ".backup" },
                fisStore: cache, recentSeriesStore: new RecentSeriesStore(root));
            await vm.OpenSeriesCommand.ExecuteAsync(null);
            await vm.OpenDrawRunCommand.ExecuteAsync(new DrawDestination(competition, 1));
            await vm.PrepareDrawCommand.ExecuteAsync(null);
            await vm.OpenTimingRunCommand.ExecuteAsync(new DrawDestination(competition, 1));
            Assert.False(vm.IsError, vm.StatusMessage);
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 850 };
            window.Show();
            try
            {
                var view = window.FindControl<TimingView>("TimingWorkspace")!;
                vm.TimingSource = "Simulator";
                vm.TimingIntermediateChannels = "2";
                await vm.ConnectTimingCommand.ExecuteAsync(null);
                Assert.True(vm.IsTimingConnected);
                Assert.Empty(vm.TimingCheckpoints);

                vm.ShowCompetitionsCommand.Execute(null);
                vm.SelectedCompetition = vm.Competitions.Single();
                vm.CompetitionIntermediateCount = 1;
                await vm.SaveCompetitionCommand.ExecuteAsync(null);
                Assert.False(vm.IsError, vm.StatusMessage);
                await vm.OpenTimingRunCommand.ExecuteAsync(new DrawDestination(competition, 1));
                Assert.False(vm.IsError, vm.StatusMessage);
                Assert.True(vm.IsTimingConnected);
                Assert.Single(vm.TimingCheckpoints);
                Assert.Single(vm.TimingRows[0].Result.Splits);
                Assert.True(workspace.Timing!.IsHeld(2));
                Assert.Single(view.FindControl<DataGrid>("RunningGrid")!.Columns, x => x.Tag is "intermediate" && x.IsVisible);
                Assert.Single(view.FindControl<DataGrid>("TimestampsGrid")!.Columns, x => x.Tag is "intermediate" && x.IsVisible);

                await vm.ToggleTimingChannelCommand.ExecuteAsync("intermediate:1");
                Assert.False(vm.IsError, vm.StatusMessage);
                Assert.False(workspace.Timing.IsHeld(2));

                vm.ShowCompetitionsCommand.Execute(null);
                vm.SelectedCompetition = vm.Competitions.Single();
                vm.CompetitionIntermediateCount = 0;
                await vm.SaveCompetitionCommand.ExecuteAsync(null);
                await vm.OpenTimingRunCommand.ExecuteAsync(new DrawDestination(competition, 1));
                Assert.Empty(vm.TimingCheckpoints);
                Assert.Empty(vm.TimingRows[0].Result.Splits);
                Assert.DoesNotContain(view.FindControl<DataGrid>("RunningGrid")!.Columns, x => x.Tag is "intermediate" && x.IsVisible);
                Assert.DoesNotContain(view.FindControl<DataGrid>("TimestampsGrid")!.Columns, x => x.Tag is "intermediate" && x.IsVisible);
                await vm.DisconnectTimingCommand.ExecuteAsync(null);
            }
            finally { window.Close(); }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [AvaloniaFact]
    public async Task TimingWorkspaceCapturesDragAssignmentsReopensAndFeedsRunTwoThroughVisibleCommands()
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
            await vm.OpenTimingRunCommand.ExecutionTask!;
            Assert.True(vm.IsTimingSection); // the selected race opens without choosing it again in the menu
            Dispatcher.UIThread.RunJobs();
            var menu = Assert.IsType<MenuFlyout>(timingButton.Flyout);
            var raceMenu = Assert.IsType<MenuItem>(Assert.Single(menu.Items));
            Assert.Equal("SL1  ·  ACTIVE", raceMenu.Header);
            Assert.True(raceMenu.IsSubMenuOpen);
            var runMenu = Assert.IsType<MenuItem>(Assert.Single(raceMenu.Items));
            Assert.Equal("Run 1  ·  OPEN", runMenu.Header);
            runMenu.Command!.Execute(runMenu.CommandParameter);
            await vm.OpenTimingRunCommand.ExecutionTask!;
            menu.Hide();
            Assert.True(vm.IsTimingSection);
            Assert.Contains("Run 1", vm.TimingContext, StringComparison.Ordinal);
            Assert.Contains("1234", vm.WindowTitle, StringComparison.Ordinal);
            CaptureDraw(window, output, "timing-device.png");
            vm.TimingSource = "Simulator";
            vm.TimingIntermediateChannels = "2"; // A configured Timy channel must not block a race with no intermediates.
            vm.FollowTimingOrder = false; // This reference scenario deliberately operates with manual bib selection.
            vm.ShowSettingsCommand.Execute(null);
            Click(window, "Connect");
            await vm.ConnectTimingCommand.ExecutionTask!;
            await vm.ReturnToTimingCommand.ExecuteAsync(null);
            Assert.False(vm.IsError, vm.StatusMessage);
            Assert.False(vm.StartInputOn);
            Assert.False(vm.FinishInputOn);
            Assert.Equal("HOLD · all positions", vm.TimingHoldSummary);
            CaptureDraw(window, output, "timing-hold.png");
            await vm.ToggleTimingChannelCommand.ExecuteAsync("start");
            await vm.ToggleTimingChannelCommand.ExecuteAsync("finish");
            await vm.OpenTimingRunCommand.ExecuteAsync(new DrawDestination(competition, 1));
            Assert.True(vm.StartInputOn); // reopening the current menu does not interrupt live timing
            Assert.True(vm.FinishInputOn);
            vm.SelectedTimingRow = vm.TimingRows[0];
            var firstBib = vm.SelectedTimingRow.Bib;
            var view = window.FindControl<TimingView>("TimingWorkspace")!;
            view.FindControl<DataGrid>("AtStartGrid")!.Focus();
            window.KeyPressQwerty(PhysicalKey.F5, RawInputModifiers.None);
            if (vm.ExpectSelectedCommand.ExecutionTask is { } arming) { await arming; }
            Assert.Equal(firstBib, workspace.Timing!.ArmedStart);
            vm.SimulationTime = "12:00:00.9999999";
            Click(window, "Test start"); await vm.SimulatePulseCommand.ExecutionTask!;
            await WaitTimingAsync(vm, () => vm.TimingRows[0].Status == "On course");
            vm.ShowCompetitionsCommand.Execute(null);
            vm.NewCompetitionCommand.Execute(null);
            vm.CompetitionShortLabel = "SL2";
            vm.CompetitionName = "Second slalom";
            vm.CompetitionFisCode = "1235";
            await vm.SaveCompetitionCommand.ExecuteAsync(null);
            Assert.False(vm.IsError, vm.StatusMessage);
            Assert.Equal(2, vm.Competitions.Count);
            Assert.True(vm.IsTimingConnected);
            timingButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await vm.OpenTimingRunCommand.ExecutionTask!;
            Assert.True(vm.IsTimingSection);
            Assert.True(vm.IsTimingConnected);
            Assert.False(vm.StartInputOn); // returning to Timing holds an already connected source
            Assert.False(vm.FinishInputOn);
            await vm.ToggleTimingChannelCommand.ExecuteAsync("start");
            await vm.ToggleTimingChannelCommand.ExecuteAsync("finish");
            (timingButton.Flyout as MenuFlyout)?.Hide();
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
            var unassigned = vm.TimestampRows.SelectMany(x => x.Cells).OfType<TimingTimestampCell>().Single(x => x.Review.Bib is null);
            Assert.Contains(vm.TimestampRows[0].Cells, x => x?.Key == unassigned.Key);
            await DropTimingRacer(view, vm.CreateTimingDrag(vm.TimingRows[1].Bib)!, unassigned.Key);
            Assert.Equal(vm.TimingRows[1].Bib, vm.TimestampRows[0].Bib); // latest impulse stays on top after assignment
            Assert.Contains(vm.TimestampRows[0].Cells, x => x?.Key == unassigned.Key);
            Assert.Equal("1:05.00", vm.TimingRows[1].Time);
            Assert.Equal("1:01.99", vm.TimingRows[0].Time);
            // Reassign an occupied finish through the visible drop target. The former
            // owner returns to the course and the recipient's old finish is unassigned.
            var firstFinish = vm.TimestampRows.SelectMany(x => x.Cells).OfType<TimingTimestampCell>()
                .Single(x => x.Review.Bib == firstBib && x.Channel == 1);
            await DropTimingRacer(view, vm.CreateTimingDrag(vm.TimingRows[1].Bib)!, firstFinish.Key);
            Assert.Equal("On course", vm.TimingRows[0].Status);
            Assert.Equal("0:32.99", vm.TimingRows[1].Time);
            Assert.Contains(vm.TimestampRows, x => x.Cells.Any(c => c?.Key == unassigned.Key && c.Review.Bib is null));
            await DropTimingRacer(view, vm.CreateTimingDrag(firstBib)!, firstFinish.Key);
            Assert.Equal("On course", vm.TimingRows[1].Status);
            await DropTimingRacer(view, vm.CreateTimingDrag(vm.TimingRows[1].Bib)!, unassigned.Key);
            Assert.Equal("1:01.99", vm.TimingRows[0].Time);
            Assert.Equal("1:05.00", vm.TimingRows[1].Time);
            vm.ShowAllTimingObservations = true;
            Assert.Contains(vm.TimingObservations, x => x.Time == "12:00:00.9999999");
            Assert.Contains(vm.TimingObservations, x => x.Time == "12:01:02.9999998");
            CaptureDraw(window, output, "timing-live.png");
            window.Width = 980; window.Height = 680;
            CaptureDraw(window, output, "timing-compact.png");
            Assert.True(view.FindControl<DataGrid>("AtStartGrid")!.Bounds.Height > 100);
            for (var i = 2; i < vm.TimingRows.Count; i++)
            {
                vm.SelectedTimingRow = vm.TimingRows[i]; vm.TimingReason = "Synthetic no start";
                TimingMenu(view, "AtStartGrid", "DNS · did not start"); await vm.ClassifyTimingCommand.ExecutionTask!;
            }
            Assert.True(vm.IsTimingConnected);
            Assert.True(vm.CanPrepareNextTimedRun);
            // Capture intentionally remains connected through Run 2 preparation.
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
            Assert.Collection(vm.AtStartRows.Select(x => x.PreviousRunTime).Order(),
                first => Assert.Equal("1:01.99", first), secondTime => Assert.Equal("1:05.00", secondTime));
            Assert.True(view.FindControl<DataGrid>("AtStartGrid")!.Columns.Single(x => Equals(x.Header, "RUN 1")).IsVisible);
            Assert.True(vm.IsTimingConnected);
            Assert.False(vm.IsError, vm.StatusMessage);
            Assert.False(vm.StartInputOn); // changing runs requires explicit resume
            Assert.False(vm.FinishInputOn);
            await vm.ToggleTimingChannelCommand.ExecuteAsync("start");
            await vm.ToggleTimingChannelCommand.ExecuteAsync("finish");
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
            Assert.True(view.FindControl<DataGrid>("RankingGrid")!.Columns.Single(x => Equals(x.Header, "TOTAL")).IsVisible);
            vm.ShowSettingsCommand.Execute(null);
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
