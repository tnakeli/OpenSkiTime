using Avalonia.Controls;
using Avalonia.Headless.XUnit;
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
    // Primary timing on the simulator with one intermediate role per channel, all on the Start connection.
    private static void UseSimulatorTiming(MainViewModel vm, params int[] intermediateChannels)
    {
        vm.TimingStartRole.Source = TimingSourceTypes.SimulatorLabel;
        while (vm.HasTimingIntermediateRoles) { vm.RemoveTimingIntermediateCommand.Execute(null); }
        foreach (var channel in intermediateChannels)
        {
            vm.AddTimingIntermediateCommand.Execute(null);
            vm.TimingIntermediateRoles[^1].Channel = channel;
        }
        Assert.Equal(intermediateChannels.Length, vm.TimingIntermediateRoles.Count);
    }

    [Fact]
    public void TimingRoleSourceListsKeepAllPrimarySourcesAndExcludeSimulatorFromBClock()
    {
        string[] primary = ["Timy 2/3 · USB", "MT1 · USB / serial", "MT1 · ALGE Results", "Simulator", "Replay file"];
        Assert.Equal(primary, new TimingRoleEditor(TimingRole.Start).Sources);
        var start = new TimingRoleEditor(TimingRole.Start);
        Assert.Equal(primary, new TimingRoleEditor(TimingRole.Intermediate(1), start).Sources);
        Assert.Equal(primary.Where(x => x != "Simulator"), new TimingRoleEditor(TimingRole.BackupStart).Sources);
        Assert.Equal(primary.Where(x => x != "Simulator"), new TimingRoleEditor(TimingRole.BackupFinish).Sources);
    }

    [AvaloniaFact]
    public async Task TimingSettingsSaveAndReloadRolesIntermediatesAndBClockThroughTheUi()
    {
        var root = Path.Combine(Path.GetTempPath(), "openskitime-timing-roles", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore());
            var preferences = new TimingPreferencesStore(root);
            using var vm = new MainViewModel(workspace, new FileDialogsStub { NewPath = "", OpenPath = "", BackupPath = "" },
                fisStore: new FisLocalStore(root, new ReportSettingsCredential()), recentSeriesStore: new(root), timingPreferencesStore: preferences);
            vm.LoadTimingPreferences();
            Assert.False(vm.HasTimingMigrationNotes);
            Assert.Equal(TimingSourceTypes.TimyUsbLabel, vm.TimingStartRole.Source);
            Assert.True(vm.TimingFinishRole.UsesLeaderConnection);
            Assert.False(vm.HasBackupClockRoles);
            vm.ShowSettingsCommand.Execute(null);
            var window = new Window { Content = new ScrollViewer { Content = new SettingsView { DataContext = vm } }, Width = 1100, Height = 900 };
            window.Show();
            try
            {
                SelectTimingSettingsTab(window);
                vm.TimingStartRole.Source = TimingSourceTypes.Mt1SerialLabel;
                vm.TimingStartRole.Port = "COM7"; vm.TimingStartRole.BaudRate = 19200;
                PressSettingsControl(window, VisibleButton(window, "Add intermediate"));
                PressSettingsControl(window, VisibleButton(window, "Add intermediate"));
                Assert.Equal([2, 3], vm.TimingIntermediateRoles.Select(x => x.Channel));
                vm.TimingIntermediateRoles[1].Channel = 4;
                PressSettingsControl(window, VisibleButton(window, "Add B Clock"));
                Assert.Equal(2, vm.BackupTimingRoles.Count);
                Assert.DoesNotContain(TimingSourceTypes.SimulatorLabel, vm.BackupTimingRoles[0].Sources);
                vm.BackupTimingRoles[0].UsbId = "SYNTHETIC-B";
                vm.BackupStartWarningMilliseconds = 2; vm.BackupFinishWarningMilliseconds = 20; vm.BackupMissingGraceSeconds = 7;
                window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
                var rows = window.GetVisualDescendants().OfType<ItemsControl>().Single(x => x.Name == "PrimaryTimingRolesList");
                Assert.Equal(4, rows.GetVisualDescendants().OfType<NumericUpDown>().Count(x => x.IsEffectivelyVisible && x.Maximum == 8));
                PressSettingsControl(window, VisibleButton(window, "Save timing settings"));
                Assert.False(vm.IsError, vm.StatusMessage);
                CaptureSettingsWindow(window, 1100, 900, "timing-roles-b-clock");

                var saved = preferences.Load()!;
                Assert.Equal(TimingSourceType.Mt1Serial, saved.Start!.Connection.Source);
                Assert.Equal("COM7", saved.Start.Connection.Port);
                Assert.Equal(19200, saved.Start.Connection.BaudRate);
                Assert.Equal((0, 1), (saved.Start.Channel, saved.Finish!.Channel));
                Assert.Equal([2, 4], saved.Intermediates.Select(x => x.Channel));
                Assert.All(saved.Assignments.Where(x => x.Role.IsPrimary), x => Assert.Equal(saved.Start.Connection, x.Connection));
                Assert.Equal("SYNTHETIC-B", saved.BackupStart!.Connection.UsbId);
                Assert.Equal(saved.BackupStart.Connection, saved.BackupFinish!.Connection);
                Assert.Equal(new BackupClockWarnings(2, 20, 7), saved.BackupWarnings);
                var compiled = Assert.Single(saved.PrimaryCapture(new DateOnly(2026, 10, 4))).Options;
                Assert.Equal(("MT1 · USB / serial", "COM7", 0, 1), (compiled.Device, compiled.Endpoint, compiled.StartChannel, compiled.FinishChannel));
                Assert.Equal([2, 4], compiled.IntermediateChannels);

                // Two roles on the same device cannot share one channel; the saved file stays unchanged.
                vm.TimingIntermediateRoles[1].Channel = 2;
                PressSettingsControl(window, VisibleButton(window, "Save timing settings"));
                Assert.True(vm.IsError);
                Assert.Equal([2, 4], preferences.Load()!.Intermediates.Select(x => x.Channel));
                vm.TimingIntermediateRoles[1].Channel = 4;
                // Any device can serve any role: Intermediate 1 on its own Timy alongside the serial MT1.
                vm.TimingIntermediateRoles[0].UsesLeaderConnection = false;
                vm.TimingIntermediateRoles[0].Source = TimingSourceTypes.TimyUsbLabel;
                PressSettingsControl(window, VisibleButton(window, "Save timing settings"));
                Assert.True(vm.IsError); // A Timy without an explicit ID is ambiguous next to the B Clock Timy.
                vm.TimingIntermediateRoles[0].UsbId = "SPLIT-1";
                PressSettingsControl(window, VisibleButton(window, "Save timing settings"));
                Assert.False(vm.IsError, vm.StatusMessage);
                var split = preferences.Load()!;
                Assert.Equal("SPLIT-1", split.Intermediates[0].Connection.UsbId);
                Assert.Equal(2, split.PrimaryCapture(new DateOnly(2026, 10, 4)).Count);
                vm.TimingIntermediateRoles[0].UsesLeaderConnection = true;
                PressSettingsControl(window, VisibleButton(window, "Save timing settings"));
                Assert.False(vm.IsError, vm.StatusMessage);
            }
            finally { window.Close(); }

            using var reloaded = new MainViewModel(workspace, new FileDialogsStub { NewPath = "", OpenPath = "", BackupPath = "" },
                fisStore: new FisLocalStore(root, new ReportSettingsCredential()), recentSeriesStore: new(root), timingPreferencesStore: preferences);
            reloaded.LoadTimingPreferences();
            Assert.Equal(TimingSourceTypes.Mt1SerialLabel, reloaded.TimingStartRole.Source);
            Assert.Equal("COM7", reloaded.TimingStartRole.Port);
            Assert.True(reloaded.TimingFinishRole.UsesLeaderConnection);
            Assert.All(reloaded.TimingIntermediateRoles, x => Assert.True(x.UsesLeaderConnection));
            Assert.Equal([2, 4], reloaded.TimingIntermediateRoles.Select(x => x.Channel));
            Assert.Equal("SYNTHETIC-B", reloaded.BackupTimingRoles[0].UsbId);
            Assert.True(reloaded.BackupTimingRoles[1].UsesLeaderConnection);
            Assert.Equal((2, 20, 7), (reloaded.BackupStartWarningMilliseconds, reloaded.BackupFinishWarningMilliseconds,
                reloaded.BackupMissingGraceSeconds));

            // B Clock is optional and can be cleared.
            await reloaded.ClearBackupClockCommand.ExecuteAsync(null);
            await reloaded.RemoveTimingIntermediateCommand.ExecuteAsync(null);
            await reloaded.SaveTimingPreferencesCommand.ExecuteAsync(null);
            Assert.False(reloaded.IsError, reloaded.StatusMessage);
            var cleared = preferences.Load()!;
            Assert.False(cleared.HasBackupClock);
            Assert.Single(cleared.Intermediates);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [AvaloniaFact]
    public async Task MigratedFormerTimingSettingsShowNotesUntilSaved()
    {
        var root = Path.Combine(Path.GetTempPath(), "openskitime-timing-migration", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "timing-settings.json"),
                """{"Source":"MT1 · USB / serial","Port":"COM3","UsbId":"","Baud":38400,"StartChannel":0,"FinishChannel":1,"IntermediateChannels":"2","Firmware":"Not queried"}""");
            await File.WriteAllTextAsync(Path.Combine(root, "auxiliary-settings.json"),
                """{"Source":"MT1 · USB / serial","Port":"COM4","UsbId":"","Baud":38400,"StartChannel":0,"FinishChannel":1,"Firmware":"Not queried","StartDevice":"","FinishDevice":"","Username":"","BackupStartWarningMilliseconds":3}""");
            await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore());
            var preferences = new TimingPreferencesStore(root);
            using var vm = new MainViewModel(workspace, new FileDialogsStub { NewPath = "", OpenPath = "", BackupPath = "" },
                fisStore: new FisLocalStore(root, new ReportSettingsCredential()), recentSeriesStore: new(root), timingPreferencesStore: preferences);
            vm.LoadTimingPreferences();
            Assert.True(vm.HasTimingMigrationNotes);
            Assert.Contains("B Clock", vm.TimingMigrationNotes, StringComparison.Ordinal);
            Assert.Equal("COM3", vm.TimingStartRole.Port);
            Assert.Equal(2, Assert.Single(vm.TimingIntermediateRoles).Channel);
            Assert.Equal("COM4", vm.BackupTimingRoles[0].Port);
            Assert.Equal(3, vm.BackupStartWarningMilliseconds);
            vm.ShowSettingsCommand.Execute(null);
            var window = new Window { Content = new ScrollViewer { Content = new SettingsView { DataContext = vm } }, Width = 1100, Height = 900 };
            window.Show();
            try
            {
                SelectTimingSettingsTab(window);
                var notes = window.GetVisualDescendants().OfType<TextBlock>().Single(x => x.Name == "TimingMigrationNotesText");
                Assert.True(notes.IsEffectivelyVisible);
                PressSettingsControl(window, VisibleButton(window, "Save timing settings"));
                Assert.False(vm.IsError, vm.StatusMessage);
                Assert.False(vm.HasTimingMigrationNotes);
                window.UpdateLayout();
                Assert.False(notes.IsEffectivelyVisible);
                Assert.True(File.Exists(Path.Combine(root, "timing-roles.json")));
                Assert.True(File.Exists(Path.Combine(root, "timing-settings.json"))); // former files are left unchanged
            }
            finally { window.Close(); }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [AvaloniaFact]
    public async Task BClockIsOptionalAndItsFailureNeverPreventsPrimaryCapture()
    {
        var root = Path.Combine(Path.GetTempPath(), "openskitime-bclock-ui", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var (workspace, vm) = await CreateTimingRunAsync(root);
            await using var ownedWorkspace = workspace;
            using var ownedVm = vm;
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 850 };
            window.Show();
            try
            {
                var view = window.FindControl<TimingView>("TimingWorkspace")!;
                // No B Clock: neutral status, no warnings, A connects and captures.
                UseSimulatorTiming(vm);
                await vm.ConnectTimingCommand.ExecuteAsync(null);
                Assert.True(vm.IsTimingConnected, vm.StatusMessage);
                await vm.BackupClockTask;
                Assert.Equal(BackupClockHealth.NotConfigured, vm.BackupClockHealth);
                Assert.Equal("B Clock · not configured", vm.BackupClockStatusText);
                Assert.False(vm.HasBackupClockProblem);
                Assert.Equal("", vm.BackupWarning);
                window.UpdateLayout();
                var badge = view.FindControl<Border>("BackupClockStatusBadge")!;
                Assert.Equal("B Clock · not configured", view.FindControl<TextBlock>("BackupClockStatusText")!.Text);
                Assert.DoesNotContain("backupClockProblem", badge.Classes);
                await PulseStartAsync(vm, workspace, vm.TimingRows[0].Bib, "12:00:00.0000");
                await vm.DisconnectTimingCommand.ExecuteAsync(null);

                // B configured as a replay file that does not exist: A still connects and captures; B reports the failure only.
                await vm.AddBackupClockCommand.ExecuteAsync(null);
                vm.BackupTimingRoles[0].Source = TimingSourceTypes.ReplayFileLabel;
                vm.BackupTimingRoles[0].ReplayPath = Path.Combine(root, "missing-b.txt");
                await vm.ConnectTimingCommand.ExecuteAsync(null);
                Assert.True(vm.IsTimingConnected, vm.StatusMessage);
                Assert.False(vm.IsError, vm.StatusMessage);
                await vm.BackupClockTask;
                vm.RefreshTiming();
                Assert.Equal(BackupClockHealth.DeviceUnavailable, vm.BackupClockHealth);
                Assert.Contains("connect failed", vm.BackupClockStatusText, StringComparison.Ordinal);
                Assert.True(vm.HasBackupClockProblem);
                Assert.False(workspace.Auxiliary!.State(AuxiliaryTimingRole.B).IsActive);
                window.UpdateLayout();
                Assert.Contains("backupClockProblem", badge.Classes);
                CaptureDraw(window, Environment.GetEnvironmentVariable("OPENSKITIME_RACE_VISUAL_DIR"), "race-b-clock-problem.png");
                await PulseStartAsync(vm, workspace, vm.TimingRows[1].Bib, "12:00:30.0000");
                await vm.DisconnectTimingCommand.ExecuteAsync(null);
                await vm.BackupClockTask;

                // An invalid B configuration (same channel twice) is also isolated from A.
                vm.BackupTimingRoles[0].ReplayPath = Path.Combine(root, "b.txt");
                await File.WriteAllTextAsync(vm.BackupTimingRoles[0].ReplayPath, " 0001 C0 12:00:00.0002\r");
                vm.BackupTimingRoles[1].Channel = 0;
                await vm.ConnectTimingCommand.ExecuteAsync(null);
                Assert.True(vm.IsTimingConnected, vm.StatusMessage);
                await vm.BackupClockTask;
                Assert.Equal(BackupClockHealth.DeviceUnavailable, vm.BackupClockHealth);
                await vm.DisconnectTimingCommand.ExecuteAsync(null);
                await vm.BackupClockTask;

                // A working B replay connects live alongside A, follows the active run and stops with A.
                vm.BackupTimingRoles[1].Channel = 1;
                await vm.ConnectTimingCommand.ExecuteAsync(null);
                Assert.True(vm.IsTimingConnected, vm.StatusMessage);
                var aObservations = workspace.Timing!.Snapshot!.Observations.Count;
                await vm.BackupClockTask;
                await WaitTimingAsync(vm, () => workspace.Auxiliary.State(AuxiliaryTimingRole.B).SavedPackets >= 1);
                var state = workspace.Auxiliary.State(AuxiliaryTimingRole.B);
                Assert.True(state.IsActive);
                Assert.True(state.Live);
                Assert.Equal(workspace.Timing.ListId, state.ListId);
                Assert.True(vm.IsBackupClockActive);
                Assert.True(vm.CanShowBackup);
                Assert.NotEqual(BackupClockHealth.DeviceUnavailable, vm.BackupClockHealth);
                Assert.False(vm.HasBackupClockProblem, vm.BackupClockStatusText);
                Assert.Equal(aObservations, workspace.Timing.Snapshot!.Observations.Count); // B input never enters A timing
                await vm.DisconnectTimingCommand.ExecuteAsync(null);
                await vm.BackupClockTask;
                Assert.False(workspace.Auxiliary.State(AuxiliaryTimingRole.B).IsActive);
                Assert.Equal("B Clock · connects with timing", vm.BackupClockStatusText);
                Assert.False(vm.HasBackupClockProblem);
            }
            finally { window.Close(); }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [AvaloniaFact]
    public async Task PrimaryReplaySourceStillCapturesThroughRoleSettings()
    {
        var root = Path.Combine(Path.GetTempPath(), "openskitime-replay-ui", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var (workspace, vm) = await CreateTimingRunAsync(root);
            await using var ownedWorkspace = workspace;
            using var ownedVm = vm;
            var replay = Path.Combine(root, "a.txt");
            await File.WriteAllTextAsync(replay, " 0001 C0 12:00:00.0000\r 0002 C1 12:01:00.5000\r");
            vm.TimingStartRole.Source = TimingSourceTypes.ReplayFileLabel;
            vm.TimingStartRole.ReplayPath = replay;
            await vm.ConnectTimingCommand.ExecuteAsync(null);
            Assert.True(vm.IsTimingConnected, vm.StatusMessage);
            Assert.Equal(TimingSourceTypes.ReplayFileLabel, workspace.Timing!.LastCaptureOptions!.Device);
            Assert.True(workspace.Timing.IsSimulation);
            await WaitTimingAsync(vm, () => workspace.Timing.Snapshot!.Observations.Count == 2);
            Assert.Equal([0, 1], workspace.Timing.Snapshot!.Observations.Select(x => x.Observation.Channel ?? -1));
            await vm.DisconnectTimingCommand.ExecuteAsync(null);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static Button VisibleButton(Window window, string label) => window.GetVisualDescendants().OfType<Button>()
        .Single(x => Equals(x.Content, label) && x.IsEffectivelyVisible);

    private static async Task PulseStartAsync(MainViewModel vm, SeriesWorkspace workspace, int bib, string time)
    {
        await vm.ToggleTimingChannelCommand.ExecuteAsync("start");
        if (workspace.Timing!.IsHeld(0)) { await vm.ToggleTimingChannelCommand.ExecuteAsync("start"); }
        await workspace.Timing.ArmAsync(bib, workspace.Timing.ArmedFinish);
        vm.SimulationTime = time;
        await vm.SimulatePulseCommand.ExecuteAsync("start");
        await WaitTimingAsync(vm, () => vm.TimingRows.Single(x => x.Bib == bib).Status == "On course");
    }

    private static async Task<(SeriesWorkspace Workspace, MainViewModel Vm)> CreateTimingRunAsync(string root, TimeProvider? timeProvider = null)
    {
        var file = Path.Combine(root, "Synthetic-BClock.ost");
        var cache = new FisLocalStore(root);
        await cache.SaveListAsync(SyntheticFisArchive());
        var workspace = new SeriesWorkspace(new SqliteSeriesFileStore(), new AlgeDecoderFactory());
        var date = new DateOnly(2026, 9, 27);
        var series = await workspace.CreateAsync(file, new("Synthetic B race", "Test slope", "Test club", date, date, "FIN", "2026/27"));
        series = await workspace.SaveCompetitionAsync(null, new("Slalom", "SL", date, Discipline.Slalom, RaceType.Fis, 2, 0, "1234"), series.Revision);
        var competition = series.Competitions[0];
        var revision = series.Revision;
        for (var i = 0; i < 6; i++)
        {
            revision = (await workspace.SaveDeskRowAsync(null, new($"TEST{i:00}", "Athlete", 2000, $"{123456 + i}", "FIN", "Test club", Gender.Female),
                competition.Id, true, null, revision)).Revision;
        }
        var vm = new MainViewModel(workspace, new FileDialogsStub { OpenPath = file, NewPath = file, BackupPath = file + ".backup" },
            fisStore: cache, recentSeriesStore: new RecentSeriesStore(root), timeProvider: timeProvider);
        await vm.OpenSeriesCommand.ExecuteAsync(null);
        await vm.OpenDrawRunCommand.ExecuteAsync(new DrawDestination(competition, 1));
        await vm.PrepareDrawCommand.ExecuteAsync(null);
        await vm.OpenTimingRunCommand.ExecuteAsync(new DrawDestination(competition, 1));
        Assert.False(vm.IsError, vm.StatusMessage);
        vm.FollowTimingOrder = false;
        return (workspace, vm);
    }
}
