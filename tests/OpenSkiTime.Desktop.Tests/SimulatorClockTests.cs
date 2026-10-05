using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using OpenSkiTime.Desktop;
using OpenSkiTime.Timing;
using Xunit;

namespace OpenSkiTime.Tests;

public partial class DesktopWorkflowTests
{
    // A controllable PC clock in UTC, so local time of day is deterministic.
    private sealed class SimulatorTestClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    private static long TimeOfDayTicks(string text) => TimingTime.TryTimeOfDay(text, out var ticks, out _) ? ticks
        : throw new ArgumentException("Invalid time of day.", nameof(text));

    [AvaloniaFact]
    public async Task SimulatorClockFollowsThePcClockLiveAndPausesForTypedTimes()
    {
        var root = Path.Combine(Path.GetTempPath(), "openskitime-simulator-clock", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var clock = new SimulatorTestClock(new DateTimeOffset(2026, 9, 27, 10, 15, 30, TimeSpan.Zero).AddTicks(1234567));
            var (workspace, vm) = await CreateTimingRunAsync(root, clock);
            await using var ownedWorkspace = workspace;
            using var ownedVm = vm;
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 850 };
            window.Show();
            try
            {
                var view = window.FindControl<TimingView>("TimingWorkspace")!;
                var box = view.FindControl<TextBox>("SimulationTimeBox")!;
                var toggle = view.FindControl<Button>("SimulationClockToggle")!;
                UseSimulatorTiming(vm);
                window.UpdateLayout();
                Assert.True(box.IsEffectivelyVisible);
                Assert.True(vm.IsSimulationClockRunning);
                Assert.Equal("10:15:30.1", vm.SimulationTime);
                Assert.Equal("10:15:30.1", box.Text);
                Assert.Equal("⏸ Pause", toggle.Content);

                // The display timer keeps following the PC clock while the Timing view is shown.
                clock.Now = clock.Now.AddSeconds(2);
                await WaitTimingAsync(vm, () => box.Text == "10:15:32.1");

                await vm.ConnectTimingCommand.ExecuteAsync(null);
                Assert.True(vm.IsTimingConnected, vm.StatusMessage);
                await vm.ToggleTimingChannelCommand.ExecuteAsync("start");
                if (workspace.Timing!.IsHeld(0)) { await vm.ToggleTimingChannelCommand.ExecuteAsync("start"); }

                // Live: the impulse takes the PC clock at the press with full tick precision, not the displayed tenths.
                clock.Now = clock.Now.AddTicks(7654321);
                Click(window, "Test start"); await vm.SimulatePulseCommand.ExecutionTask!;
                await WaitTimingAsync(vm, () => workspace.Timing.Snapshot!.Observations.Count == 1);
                Assert.Equal(TimeOfDayTicks("10:15:32.8888888"), workspace.Timing.Snapshot!.Observations[0].Observation.DeviceTicks % TimeSpan.TicksPerDay);

                // Pause freezes the exact clock time; the display no longer moves and impulses use the frozen time.
                Click(window, "⏸ Pause");
                Assert.False(vm.IsSimulationClockRunning);
                Assert.Equal("10:15:32.8888888", box.Text);
                Assert.Equal("▶ Live", toggle.Content);
                clock.Now = clock.Now.AddSeconds(5);
                await Task.Delay(250);
                Dispatcher.UIThread.RunJobs();
                Assert.Equal("10:15:32.8888888", box.Text);
                Click(window, "Test start"); await vm.SimulatePulseCommand.ExecutionTask!;
                await WaitTimingAsync(vm, () => workspace.Timing.Snapshot!.Observations.Count == 2);
                Assert.Equal(TimeOfDayTicks("10:15:32.8888888"), workspace.Timing.Snapshot!.Observations[1].Observation.DeviceTicks % TimeSpan.TicksPerDay);

                // Live again, then typing a time pauses the clock so the typed value is kept and used.
                Click(window, "▶ Live");
                Assert.True(vm.IsSimulationClockRunning);
                Assert.Equal("10:15:37.8", box.Text);
                box.Text = "12:00:00.1234567";
                Dispatcher.UIThread.RunJobs();
                Assert.False(vm.IsSimulationClockRunning);
                Assert.Equal("12:00:00.1234567", vm.SimulationTime);
                clock.Now = clock.Now.AddSeconds(1);
                await Task.Delay(250);
                Dispatcher.UIThread.RunJobs();
                Assert.Equal("12:00:00.1234567", box.Text);
                Click(window, "Test start"); await vm.SimulatePulseCommand.ExecutionTask!;
                await WaitTimingAsync(vm, () => workspace.Timing.Snapshot!.Observations.Count == 3);
                Assert.Equal(TimeOfDayTicks("12:00:00.1234567"), workspace.Timing.Snapshot!.Observations[2].Observation.DeviceTicks % TimeSpan.TicksPerDay);

                // A malformed typed time is rejected without an impulse.
                box.Text = "12:00";
                Dispatcher.UIThread.RunJobs();
                Click(window, "Test start"); await vm.SimulatePulseCommand.ExecutionTask!;
                Assert.True(vm.IsError);
                Assert.Contains("HH:mm:ss", vm.StatusMessage, StringComparison.Ordinal);
                Assert.Equal(3, workspace.Timing.Snapshot!.Observations.Count);
                await vm.DisconnectTimingCommand.ExecuteAsync(null);
            }
            finally { window.Close(); }
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
