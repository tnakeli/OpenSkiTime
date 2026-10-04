using System.Text;
using OpenSkiTime.Application;
using OpenSkiTime.Devices;
using OpenSkiTime.Domain;
using OpenSkiTime.Persistence;
using OpenSkiTime.Timing;
using Xunit;

namespace OpenSkiTime.Tests;

public sealed class MultiDeviceCaptureTests : IDisposable
{
    private static readonly long Noon = TimeSpan.FromHours(12).Ticks;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "openskitime-multi-device", Guid.NewGuid().ToString("N"));
    public MultiDeviceCaptureTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);

    private static CaptureOptions Device(string endpoint, string group, params CaptureChannelRoute[] routes) =>
        new(TimingSourceTypes.SimulatorLabel, endpoint, TimingRulesTests.Date, Simulation: true) { Routes = routes, ClockGroup = group };
    private static CaptureChannelRoute R(int channel, int position) => new(channel, position);

    [Fact]
    public async Task StartFinishAndIntermediateOnDifferentDevicesTimeOneRunAndReplayIdentically()
    {
        await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore(), new AlgeDecoderFactory());
        var path = Path.Combine(_root, "multi.ost");
        var list = await TimingStorageTests.SeedAsync(workspace, path, 2, intermediates: 1);
        var timing = workspace.Timing!;
        await timing.SelectRunAsync(list.Id);
        var startClock = new SimulatorTimingSource();
        var finishClock = new SimulatorTimingSource();
        var splitClock = new SimulatorTimingSource();
        // Each device uses its own physical channel numbering; routes map them to Start, Finish and Intermediate 1.
        await timing.StartAsync([
            new(startClock, Device("Start Timy", "g1", R(0, 0))),
            new(finishClock, Device("Finish Timy", "g1", R(0, 1))),
            new(splitClock, Device("Split MT1", "g1", R(3, 2)))], "Synthetic operator");
        Assert.Equal(3, timing.ActiveCaptureOptions.Count);
        await timing.FollowStartOrderAsync(true);
        var bib = timing.Snapshot!.StartOrder[0];
        await startClock.PulseAsync(0, Noon + 1234);
        await UntilAsync(() => timing.Snapshot!.Results.Single(x => x.Bib == bib).Status == TimingStatus.OnCourse);
        await splitClock.PulseAsync(3, Noon + TimeSpan.FromSeconds(30).Ticks + 1234);
        // Devices deliver independently; send the finish after the split was processed, as on a real course.
        await UntilAsync(() => timing.Snapshot!.Results.Single(x => x.Bib == bib).Splits[0].ObservationKey is not null);
        await finishClock.PulseAsync(0, Noon + TimeSpan.FromSeconds(62).Ticks + 3456 + 1234);
        await UntilAsync(() => timing.Snapshot!.Results.Single(x => x.Bib == bib).Status == TimingStatus.Finished);
        await UntilAsync(() => timing.Snapshot!.Results.Single(x => x.Bib == bib).Splits[0].Hundredths is not null);
        var result = timing.Snapshot!.Results.Single(x => x.Bib == bib);
        Assert.Equal(6200, result.Hundredths);
        Assert.Equal(3000, Assert.Single(result.Splits).Hundredths);
        Assert.Contains("Start Timy", timing.Connection, StringComparison.Ordinal);
        await timing.StopAsync();

        var saved = await workspace.ReadTimingAsync(list.Id);
        Assert.Equal(3, saved.Sessions.Count);
        Assert.All(saved.Sessions, x => Assert.True(x.CleanStop));
        Assert.Equal(["Finish Timy", "Split MT1", "Start Timy"], saved.Sessions.Select(x => x.Options.Endpoint).Order());
        Assert.All(saved.Packets.GroupBy(x => x.SessionId), g => Assert.Equal(Enumerable.Range(1, g.Count()).Select(x => (long)x), g.Select(x => x.Sequence).Order()));
        Assert.Equal(6200, TimingReplay.Restore(saved, new AlgeDecoderFactory()).Results.Single(x => x.Bib == bib).Hundredths);

        await using var reopened = new SeriesWorkspace(new SqliteSeriesFileStore(), new AlgeDecoderFactory());
        await reopened.OpenAsync(path);
        await reopened.Timing!.SelectRunAsync(list.Id);
        var replayed = reopened.Timing.Snapshot!.Results.Single(x => x.Bib == bib);
        Assert.Equal((TimingStatus.Finished, 6200L, 3000L), (replayed.Status, replayed.Hundredths!.Value, replayed.Splits[0].Hundredths!.Value));
        Assert.Equal(3, reopened.Timing.LastCaptureGroup.Count);
        var restored = TimingRoleConfiguration.FromCaptureOptions([.. reopened.Timing.LastCaptureGroup]);
        Assert.Equal(3, restored.Assignments.Count);
    }

    [Fact]
    public async Task OneFailingDeviceNeverStopsTheOtherDevicesOrDurableCapture()
    {
        await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore(), new AlgeDecoderFactory());
        var list = await TimingStorageTests.SeedAsync(workspace, Path.Combine(_root, "failure.ost"), 1);
        var timing = workspace.Timing!;
        await timing.SelectRunAsync(list.Id);
        var clock = new SimulatorTimingSource();
        await timing.StartAsync([
            new(new FailingSource(), Device("Broken", "g2", R(0, 0))),
            new(clock, Device("Working", "g2", R(1, 1), R(0, 2)))], "Synthetic operator");
        await UntilAsync(() => timing.Connection.Contains("Port vanished", StringComparison.Ordinal));
        await clock.PulseAsync(1, Noon);
        await UntilAsync(() => timing.Snapshot!.Observations.Any(x => x.Observation.Channel == 1));
        Assert.True(timing.IsActive);
        Assert.Contains(timing.Snapshot!.Observations, x => x.Observation.Message.Contains("Port vanished", StringComparison.Ordinal));
        await timing.StopAsync();
        var saved = await workspace.ReadTimingAsync(list.Id);
        Assert.Equal(2, saved.Sessions.Count);
        Assert.Contains(saved.Packets, x => x.Protocol == "transport-status");
        Assert.Contains(saved.Packets, x => Encoding.ASCII.GetString(x.Bytes).Contains("C1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AllDeviceSessionsSwitchRunsTogether()
    {
        await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore(), new AlgeDecoderFactory());
        var first = await TimingStorageTests.SeedAsync(workspace, Path.Combine(_root, "switch.ost"), 1);
        var timing = workspace.Timing!;
        await timing.SelectRunAsync(first.Id);
        await workspace.MarkRunStartedAsync(first.Id, (await workspace.ReadAsync()).Revision, "Operator", TimingRulesTests.At);
        var start = new SimulatorTimingSource(); var finish = new SimulatorTimingSource();
        await timing.StartAsync([new(start, Device("A", "g3", R(0, 0))), new(finish, Device("B", "g3", R(0, 1)))], "Synthetic operator");
        var plan = FisStartOrder.SecondRun(first,
            first.Plan.Entries.Select(x => new RunFinish(x.Entrant.CompetitorId, FinishStatus.Finished, 6000)).ToArray());
        var second = (await workspace.SaveStartListAsync(new(plan, (await workspace.ReadAsync()).Revision,
            "Operator", "Synthetic second run", TimingRulesTests.At, TimingReplay.InputVersion(await workspace.ReadTimingAsync(first.Id))))).Revisions.Single(x => x.Plan.RunNumber == 2);
        await timing.SelectRunAsync(second.Id);
        Assert.Equal(second.Id, timing.ListId);
        await finish.PulseAsync(0, Noon);
        await UntilAsync(() => timing.Snapshot!.Observations.Any(x => x.Observation.Channel == 1));
        await timing.StopAsync();
        var old = await workspace.ReadTimingAsync(first.Id);
        var next = await workspace.ReadTimingAsync(second.Id);
        Assert.Equal(2, old.Sessions.Count); Assert.Equal(2, next.Sessions.Count);
        Assert.All(old.Sessions.Concat(next.Sessions), x => Assert.True(x.CleanStop));
        Assert.Single(next.Packets);
    }

    [Fact]
    public async Task ReplayMergesOneCaptureGroupInReceiveOrderButKeepsEachDeviceSequence()
    {
        var group = "g4";
        var a = new CaptureSession(Guid.NewGuid(), Guid.Empty, Device("A", group, R(0, 0)), TimingRulesTests.At, TimingRulesTests.At, true);
        var b = new CaptureSession(Guid.NewGuid(), Guid.Empty, Device("B", group, R(0, 1)), TimingRulesTests.At, TimingRulesTests.At, true);
        var legacy = new CaptureSession(Guid.NewGuid(), Guid.Empty, new("Test", "Old", TimingRulesTests.Date, Simulation: true), TimingRulesTests.At, TimingRulesTests.At, true);
        RawTimingPacket P(CaptureSession s, long sequence, int second) => new(s.Id, sequence, TimingRulesTests.At.AddSeconds(second), "alge-ascii/v1", "x", "1", []);
        // Device A's clock-out-of-order receive time must not reorder its own sequence.
        var units = TimingReplay.Units([legacy, a, b], [P(a, 1, 5), P(a, 2, 1), P(b, 1, 3), P(legacy, 1, 9)]);
        Assert.Equal(2, units.Count);
        Assert.Equal([legacy.Id], units[0].Sessions.Select(x => x.Id));
        Assert.Equal([(b.Id, 1L), (a.Id, 1L), (a.Id, 2L)], units[1].Packets.Select(x => (x.Session.Id, x.Packet.Sequence)));
    }

    [Fact]
    public void AlgeResultsTimesUseTheDeviceClockSetInAlgeResultsWithoutTimeZones()
    {
        var options = new CaptureOptions(TimingSourceTypes.AlgeResultsLabel, "101/0", TimingRulesTests.Date, 0, 1, StartDeviceId: "101", FinishDeviceId: "101");
        var session = new CaptureSession(Guid.NewGuid(), Guid.Empty, options, TimingRulesTests.At, null, false);
        var instant = new DateTime(TimingRulesTests.Date.Year, TimingRulesTests.Date.Month, TimingRulesTests.Date.Day, 10, 0, 0, DateTimeKind.Utc);
        var stamp = instant.Ticks - DateTime.UnixEpoch.Ticks;
        var json = $$"""{"status":0,"data":[{"deviceId":"101","timestamp":{{stamp}},"timeOffset":120,"timingChannel":"C0","type":"StartNumberTrigger","valid":true,"blocked":false,"fallingEdge":true}]}""";
        var observation = Assert.Single(new AlgeResultsDecoder(session).Feed(new(session.Id, 1, TimingRulesTests.At, "alge-results/v1", "101", "cloud", Encoding.UTF8.GetBytes(json))));
        Assert.Equal(ObservationKind.Impulse, observation.Kind);
        Assert.Equal(TimingRulesTests.Date.ToDateTime(new TimeOnly(12, 0)).Ticks, observation.DeviceTicks);
        Assert.Equal(0, observation.Channel);
        Assert.DoesNotContain("UTC", observation.Message, StringComparison.Ordinal);
        Assert.Contains("12:00:00", observation.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnroutedDeviceChannelIsShownAsASignalButNeverTimed()
    {
        await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore(), new AlgeDecoderFactory());
        var list = await TimingStorageTests.SeedAsync(workspace, Path.Combine(_root, "signals.ost"), 1);
        var timing = workspace.Timing!;
        await timing.SelectRunAsync(list.Id);
        var clock = new SimulatorTimingSource();
        await timing.StartAsync([new(clock, Device("Timy", "g5", R(0, 0), R(1, 1)))], "Synthetic operator");
        await clock.PulseAsync(4, Noon); // C4 is not assigned to any role
        await clock.PulseAsync(0, Noon + TimeSpan.TicksPerSecond);
        await UntilAsync(() => timing.RecentSignals.Count == 2);
        Assert.Equal([0, 4], timing.RecentSignals.Select(x => x.Channel).Order());
        Assert.All(timing.RecentSignals, x => Assert.Equal(TimingSignals.DeviceKey(Device("Timy", "g5", R(0, 0)), "Simulator"), x.Device));
        Assert.DoesNotContain(timing.Snapshot!.Observations, x => x.State == "Assigned" && x.Observation.PhysicalChannel == 4);
        await timing.StopAsync();
        Assert.Empty(timing.RecentSignals);
    }

    [Fact]
    public async Task SignalMonitorKeepsTheNewestTimeWhenHistoryArrivesNewestFirst()
    {
        await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore(), new AlgeDecoderFactory());
        var list = await TimingStorageTests.SeedAsync(workspace, Path.Combine(_root, "history.ost"), 1);
        var timing = workspace.Timing!;
        await timing.SelectRunAsync(list.Id);
        static string Dto(int second) => $$"""{"deviceId":"231203016","timestamp":{{new DateTime(2026, 10, 4, 21, 0, second, DateTimeKind.Utc).Ticks - DateTime.UnixEpoch.Ticks}},"timingChannel":"C1","fallingEdge":true,"valid":true,"blocked":false,"type":"StartNumberTrigger","timeOffset":180}""";
        var page = "{\"status\":0,\"data\":[" + Dto(30) + "," + Dto(20) + "," + Dto(10) + "]}";
        var options = new CaptureOptions(TimingSourceTypes.AlgeResultsLabel, "231203016/1", TimingRulesTests.Date, Simulation: true)
            { Routes = [new(0, 0, "231203016"), new(2, 1, "231203016"), new(1, 2, "231203016")], ClockGroup = "g6" };
        await timing.StartAsync([new(new OnePacket(page), options)], "Synthetic operator");
        await UntilAsync(() => timing.RecentSignals.Count == 1);
        var signal = Assert.Single(timing.RecentSignals);
        // 21:00:30 UTC with timeOffset 180 is device time 00:00:30, the newest of the three.
        Assert.Equal(("alge:231203016", 1, new TimeSpan(0, 0, 30)), (signal.Device, signal.Channel, new DateTime(signal.DeviceTicks).TimeOfDay));
        await timing.StopAsync();
    }

    private sealed class OnePacket(string body) : ITimingSource
    {
        public async Task ReceiveAsync(Func<TransportPacket, ValueTask> receive, Action<string> status, CancellationToken ct)
        { await receive(new("alge-results/v1", "231203016", "cloud", Encoding.UTF8.GetBytes(body))); await Task.Delay(Timeout.Infinite, ct); }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public void RunningDisplayTimeIgnoresADeviceTimeFromAnotherDay()
    {
        var time = new StepTime();
        var clock = new RunningTimingClock(time);
        TimingObservation At(DateOnly date, int second, ObservationKind kind = ObservationKind.Impulse) => new("k" + second, Guid.Empty, 1, "x", "f" + second, kind, 0,
            date.ToDateTime(new TimeOnly(12, 0, second)).Ticks, 4, null, false, "sync:g:0", "");
        var raceDay = new DateOnly(2026, 9, 13);
        var start = At(raceDay, 0);
        clock.Observe(start, time.GetUtcNow());
        time.Advance(TimeSpan.FromSeconds(5));
        clock.Observe(At(raceDay, 5, ObservationKind.Information), time.GetUtcNow());
        // A device reporting the real calendar date three weeks later must not make the competitor look 21 days on course.
        clock.Observe(At(new DateOnly(2026, 10, 4), 6), time.GetUtcNow());
        time.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(700, clock.ElapsedHundredths(start));
    }

    private sealed class StepTime : TimeProvider
    {
        private DateTimeOffset _utc = new(2026, 9, 13, 9, 0, 0, TimeSpan.Zero);
        private long _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;
        public override DateTimeOffset GetUtcNow() => _utc;
        public void Advance(TimeSpan value) { _timestamp += value.Ticks; _utc += value; }
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (!condition()) { await Task.Delay(10, timeout.Token); }
    }

    private sealed class FailingSource : ITimingSource
    {
        public Task ReceiveAsync(Func<TransportPacket, ValueTask> receive, Action<string> status, CancellationToken ct)
            => throw new IOException("Port vanished");
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
