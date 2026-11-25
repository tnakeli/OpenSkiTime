using System.Text;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OpenSkiTime.Application;
using OpenSkiTime.Desktop;
using Xunit;

namespace OpenSkiTime.Tests;

public partial class DesktopWorkflowTests
{
    // A device that stays attached: whoever opens it receives what it sends from then on.
    private sealed class BenchTimy : IDisposable
    {
        private readonly Func<TimingConnection, ITimingSource?>? _previous = TimingSourceFactory.TestSource;
        private Func<TransportPacket, ValueTask>? _receive;
        public int Opened { get; private set; }
        public int MostOpen { get; private set; }
        public int Open { get; private set; }

        public BenchTimy() => TimingSourceFactory.TestSource = x => x.Source == TimingSourceType.TimyUsb ? new Source(this) : NoDeviceInTests.Silent(x);
        public void Dispose() => TimingSourceFactory.TestSource = _previous;
        public ValueTask SendAsync(string line) => _receive?.Invoke(new("alge-ascii/v1", "Timy:1", "1", Encoding.ASCII.GetBytes(line))) ?? ValueTask.CompletedTask;

        private sealed class Source(BenchTimy device) : ITimingSource
        {
            public async Task ReceiveAsync(Func<TransportPacket, ValueTask> receive, Action<string> status, CancellationToken ct)
            {
                device.Opened++; device.Open++; device.MostOpen = Math.Max(device.MostOpen, device.Open);
                device._receive = receive;
                status("Connected · Timy 1");
                try { await Task.Delay(Timeout.Infinite, ct); }
                finally { device._receive = null; device.Open--; }
            }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private static async Task BenchUntilAsync(MainViewModel vm, Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition()) { await Task.Delay(20, timeout.Token); vm.RefreshTiming(); Dispatcher.UIThread.RunJobs(); }
    }

    private static async Task InTimingListeningRunAsync(Func<SeriesWorkspace, MainViewModel, BenchTimy, Task> test)
    {
        var root = Path.Combine(Path.GetTempPath(), "openskitime-listening", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var device = new BenchTimy();
            var (workspace, vm) = await CreateTimingRunAsync(root);
            await using var ownedWorkspace = workspace;
            using var ownedVm = vm;
            await test(workspace, vm, device);
            Assert.InRange(device.MostOpen, 0, 1); // never opened twice at the same time
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [AvaloniaFact]
    public async Task TimingIsReceivedWhileItsViewIsOpenAndSettingsOnlyShowSignals() => await InTimingListeningRunAsync(async (workspace, vm, device) =>
    {
        // Nothing is chosen yet: the view says so and can be opened again after choosing.
        Assert.False(vm.IsTimingConnected);
        Assert.Contains("No timing devices are chosen", vm.TimingProblem, StringComparison.Ordinal);
        Assert.Equal(0, device.Opened);

        vm.ShowTimingDevicesCommand.Execute(null);
        await vm.TimingListeningTask;
        await BenchUntilAsync(vm, () => device.Open == 1);
        await device.SendAsync(" 0001 C3 12:00:00.0000\r");
        await Task.Delay(300);
        Dispatcher.UIThread.RunJobs();
        vm.RefreshTimingRoleSignals();
        Assert.Equal("C3 12:00:00.00", vm.TimingStartRole.SignalText);
        Assert.Contains("not recorded", vm.TimingConnection, StringComparison.Ordinal);
        Assert.Empty((await workspace.ReadTimingAsync(workspace.Timing!.ListId!.Value)).Sessions);

        // Timing: received on HOLD, shown in Timestamps at once, also on a channel without a role.
        await vm.ReturnToTimingCommand.ExecuteAsync(null);
        Assert.True(vm.IsTimingConnected, vm.TimingProblem);
        Assert.Equal("", vm.TimingProblem);
        await BenchUntilAsync(vm, () => device.Open == 1);
        await device.SendAsync(" 0002 C0 12:00:10.0000\r");
        await device.SendAsync(" 0003 C3 12:00:11.0000\r");
        await BenchUntilAsync(vm, () => vm.TimestampRows.Count == 2);
        Assert.Equal(["C3 · no role · 12:00:11.0000000", "Unassigned impulse"], vm.TimestampRows.Select(x => x.Name));
        Assert.Equal("12:00:10.0000000", vm.TimestampRows[1].Cells[0]!.Time);
        Assert.All(vm.TimingRows, x => Assert.Equal("Ready", x.Status));

        // Any other view: the device is released and nothing is received.
        await vm.ToggleTimingChannelCommand.ExecuteAsync("start");
        var bib = vm.TimingRows[0].Bib;
        await device.SendAsync($" {bib:0000}* C0 12:00:20.0000\r");
        await BenchUntilAsync(vm, () => vm.TimingRows.Single(x => x.Bib == bib).Status == "On course");
        vm.ShowCompetitionsCommand.Execute(null);
        await vm.TimingListeningTask;
        Assert.False(vm.IsTimingConnected);
        Assert.Equal(0, device.Open);
        Assert.Contains("1 competitor(s) on course", vm.StatusMessage, StringComparison.Ordinal);
        Assert.True((await workspace.ReadTimingAsync(workspace.Timing.ListId!.Value)).Sessions.All(x => x.CleanStop));
        var packets = (await workspace.ReadTimingAsync(workspace.Timing.ListId!.Value)).Packets.Count;
        await device.SendAsync(" 0005 C1 12:00:30.0000\r");
        Assert.Equal(packets, (await workspace.ReadTimingAsync(workspace.Timing.ListId!.Value)).Packets.Count);

        await vm.ReturnToTimingCommand.ExecuteAsync(null);
        Assert.True(vm.IsTimingConnected, vm.TimingProblem);
        Assert.False(vm.StartInputOn);
    });

    [AvaloniaFact]
    public async Task AWrongDeviceInTimingIsCorrectedInSettingsWithoutAnyConnectStep() => await InTimingListeningRunAsync(async (workspace, vm, device) =>
    {
        // Opened with the simulator by mistake: nothing was received, so the run stays a real one.
        UseSimulatorTiming(vm);
        await vm.OpenTimingRunCommand.ExecuteAsync(new DrawDestination(vm.TimingCompetition!, 1));
        Assert.True(vm.IsTimingConnected, vm.TimingProblem);
        vm.ShowTimingDevicesCommand.Execute(null);
        await vm.TimingListeningTask;
        vm.TimingStartRole.Source = TimingSourceTypes.Mt1SerialLabel; // no COM port chosen yet
        await vm.ReturnToTimingCommand.ExecuteAsync(null);
        Assert.False(vm.IsTimingConnected);
        Assert.Contains("COM port", vm.TimingProblem, StringComparison.Ordinal);
        vm.ShowTimingDevicesCommand.Execute(null);
        vm.TimingStartRole.Source = TimingSourceTypes.TimyUsbLabel;
        await vm.ReturnToTimingCommand.ExecuteAsync(null);
        Assert.True(vm.IsTimingConnected, vm.TimingProblem);
        Assert.False(vm.HasTimingProblem);
        Assert.False(workspace.Timing!.IsSimulation);
        await BenchUntilAsync(vm, () => device.Open == 1);
    });

    [AvaloniaFact]
    public async Task TimingStartsAgainAfterStorageTroubleWhileLeavingTheView() => await InTimingListeningRunAsync(async (workspace, vm, device) =>
    {
        vm.TimingStartRole.Source = TimingSourceTypes.Mt1SerialLabel; vm.TimingStartRole.Source = TimingSourceTypes.TimyUsbLabel; // chosen by the operator
        await vm.OpenTimingRunCommand.ExecuteAsync(new DrawDestination(vm.TimingCompetition!, 1));
        Assert.True(vm.IsTimingConnected, vm.TimingProblem);
        await BenchUntilAsync(vm, () => device.Open == 1);
        var saved = workspace.Timing!.SavedPackets;
        await using (var blocker = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={workspace.FilePath};Pooling=False"))
        {
            await blocker.OpenAsync();
            await using (var block = blocker.CreateCommand()) { block.CommandText = "BEGIN EXCLUSIVE"; await block.ExecuteNonQueryAsync(); }
            await device.SendAsync(" 0001 C0 12:00:10.0000\r");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            while (workspace.Timing.Fault is null) { await Task.Delay(50, timeout.Token); }
            vm.ShowCompetitionsCommand.Execute(null);
            await vm.TimingListeningTask;
            Assert.True(workspace.Timing.IsStopping); // the impulse is still waiting for storage
            await using var release = blocker.CreateCommand(); release.CommandText = "ROLLBACK"; await release.ExecuteNonQueryAsync();
        }
        vm.RetryTimingStorageCommand.Execute(null);
        await vm.ReturnToTimingCommand.ExecuteAsync(null);
        using var restart = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (!vm.IsTimingConnected) { await Task.Delay(50, restart.Token); Dispatcher.UIThread.RunJobs(); vm.RefreshTiming(); vm.RetryTimingStart(); await vm.TimingListeningTask; }
        Assert.Equal("", vm.TimingProblem);
        await BenchUntilAsync(vm, () => device.Open == 1);
        await device.SendAsync(" 0002 C0 12:00:20.0000\r");
        await BenchUntilAsync(vm, () => vm.TimestampRows.Count == 2); // nothing was lost
    });

    [AvaloniaFact]
    public async Task ASlowBClockNeverDelaysTheReturnToTiming() => await InTimingListeningRunAsync(async (_, vm, device) =>
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var timy = TimingSourceFactory.TestSource!;
        TimingSourceFactory.TestSource = x => x.Source == TimingSourceType.Mt1Serial ? new SlowSource(release.Task) : timy(x);
        vm.TimingStartRole.UsbId = "1";
        await vm.AddBackupClockCommand.ExecuteAsync(null);
        vm.BackupTimingRoles[0].Source = TimingSourceTypes.Mt1SerialLabel;
        vm.BackupTimingRoles[0].Port = "COM9";
        await vm.OpenTimingRunCommand.ExecuteAsync(new DrawDestination(vm.TimingCompetition!, 1));
        await vm.BackupClockTask;
        vm.ShowTimingDevicesCommand.Execute(null); // B starts closing and does not finish
        await vm.ReturnToTimingCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(vm.IsTimingConnected, vm.TimingProblem);
        await BenchUntilAsync(vm, () => device.Open == 1);
        Assert.False(vm.BackupClockTask.IsCompleted);
        release.SetResult();
        await vm.BackupClockTask.WaitAsync(TimeSpan.FromSeconds(20));
    });

    // A device that takes its time to close.
    private sealed class SlowSource(Task release) : ITimingSource
    {
        public async Task ReceiveAsync(Func<TransportPacket, ValueTask> receive, Action<string> status, CancellationToken ct)
        {
            status("Connected · slow device");
            try { await Task.Delay(Timeout.Infinite, ct); }
            finally { await release; }
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [AvaloniaFact]
    public async Task TimingDevicesShowsSignalsAfterTheBClockHasReleasedItsDevice() => await InTimingListeningRunAsync(async (workspace, vm, device) =>
    {
        vm.TimingStartRole.UsbId = "1";
        await vm.AddBackupClockCommand.ExecuteAsync(null);
        vm.BackupTimingRoles[0].Source = TimingSourceTypes.Mt1SerialLabel;
        vm.BackupTimingRoles[0].Port = "COM9";
        await vm.OpenTimingRunCommand.ExecuteAsync(new DrawDestination(vm.TimingCompetition!, 1));
        Assert.True(vm.IsTimingConnected, vm.TimingProblem);
        await vm.BackupClockTask;
        Assert.True(workspace.Auxiliary!.State(AuxiliaryTimingRole.B).IsActive);
        vm.ShowTimingDevicesCommand.Execute(null);
        await vm.TimingListeningTask;
        await BenchUntilAsync(vm, () => vm.BackupClockTask.IsCompleted && device.Open == 1);
        Assert.False(workspace.Auxiliary.State(AuxiliaryTimingRole.B).IsActive);
        Assert.Contains("not recorded", vm.TimingConnection, StringComparison.Ordinal);
    });

    [AvaloniaFact]
    public async Task TheStartListIsOpenAgainWhenAnUnusedTimingViewWasLeftForIt() => await InTimingListeningRunAsync(async (_, vm, device) =>
    {
        vm.TimingStartRole.UsbId = "1";
        await vm.OpenTimingRunCommand.ExecuteAsync(new DrawDestination(vm.TimingCompetition!, 1));
        await BenchUntilAsync(vm, () => device.Open == 1);
        await device.SendAsync("12:00:00.0  \r");
        await BenchUntilAsync(vm, () => vm.TimingDeviceClock == "12:00:00");
        await vm.OpenDrawRunCommand.ExecuteAsync(new DrawDestination(vm.TimingCompetition!, 1));
        Assert.Equal(0, device.Open);
        Assert.True(vm.CanEditStartList, vm.DrawBlockReason);
    });

    [AvaloniaFact]
    public async Task ARunWithTrainingTimesOffersToSetThemAsideForRealTiming() => await InTimingListeningRunAsync(async (workspace, vm, device) =>
    {
        UseSimulatorTiming(vm);
        await vm.StartTimingAsync();
        var bib = vm.TimingRows[0].Bib;
        await PulseStartAsync(vm, workspace, bib, "12:00:00.0000");
        var training = workspace.Timing!.ListId!.Value;

        vm.ShowTimingDevicesCommand.Execute(null);
        vm.TimingStartRole.Source = TimingSourceTypes.TimyUsbLabel;
        await vm.ReturnToTimingCommand.ExecuteAsync(null);
        Assert.False(vm.IsTimingConnected);
        Assert.True(vm.CanSetTrainingAside);
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 850 };
        window.Show();
        try
        {
            window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            var note = window.FindControl<TimingView>("TimingWorkspace")!.FindControl<Border>("TimingProblemNote")!;
            Assert.True(note.IsEffectivelyVisible);
            Assert.Contains(note.GetVisualDescendants().OfType<Button>(), x => Equals(x.Content, "Set training times aside") && x.IsEffectivelyVisible);
            CaptureDraw(window, Environment.GetEnvironmentVariable("OPENSKITIME_RACE_VISUAL_DIR"), "timing-training-note.png");
            await vm.SetTrainingAsideCommand.ExecuteAsync(null);
            Assert.False(vm.IsError, vm.StatusMessage);
            Assert.True(vm.IsTimingConnected, vm.TimingProblem);
            window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            Assert.False(note.IsEffectivelyVisible);
        }
        finally { window.Close(); }
        Assert.NotEqual(training, workspace.Timing.ListId);
        Assert.Equal("Ready", vm.TimingRows.Single(x => x.Bib == bib).Status);
        Assert.NotEmpty((await workspace.ReadTimingAsync(training)).Packets);
    });
}
