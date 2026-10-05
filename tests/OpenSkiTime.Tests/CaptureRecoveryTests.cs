using System.Text;
using System.Threading.Channels;
using OpenSkiTime.Application;
using OpenSkiTime.Devices;
using OpenSkiTime.Domain;
using OpenSkiTime.Persistence;
using OpenSkiTime.Timing;
using Xunit;

namespace OpenSkiTime.Tests;

// Automatic assignment is a convenience on top of the durable journal. It must never give a competitor an impulse
// that cannot be theirs: recovered, late or previous-run input stays Unassigned for operator review, and a storage
// failure around an automatic decision never stops capture.
public sealed class CaptureRecoveryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "openskitime-capture-recovery", Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    private string PathFor(string name) { Directory.CreateDirectory(_root); return Path.Combine(_root, name); }

    private static CaptureOptions AlgeOptions() => new(TimingSourceTypes.AlgeResultsLabel, "101/0;101/1", TimingRulesTests.Date, 0, 1,
        StartDeviceId: "101", FinishDeviceId: "101");
    private static string Trigger(int channel, int hour, int minute, int second) => $$"""{"deviceId":"101","timestamp":{{TimingRulesTests.Date.ToDateTime(new TimeOnly(hour, minute, second)).Ticks - DateTime.UnixEpoch.Ticks}},"timingChannel":"C{{channel}}","fallingEdge":true,"valid":true,"blocked":false,"type":"StartNumberTrigger","timeOffset":0}""";
    private static string Page(params string[] triggers) => "{\"status\":0,\"data\":[" + string.Join(",", triggers) + "]}";
    private static TimeSpan TimeOfDay(TimingSnapshot snapshot, string? key) => new DateTime(snapshot.Observations
        .Single(x => x.Observation.Key == key).Observation.DeviceTicks!.Value).TimeOfDay;

    // Waits until the writer has decoded this many observations and finished their automatic decisions.
    private static async Task ProcessedAsync(TimingWorkspace timing, int observations)
    {
        await TimingStorageTests.UntilAsync(() => timing.Snapshot!.Observations.Count == observations);
        await timing.FollowStartOrderAsync(true); // takes the capture gate after the writer released it
    }

    [Fact]
    public async Task RecoveredHistoryPageIsAssignedInDeviceTimeOrder()
    {
        await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore(), new AlgeDecoderFactory());
        var list = await TimingStorageTests.SeedAsync(workspace, PathFor("history.ost"), 2);
        var timing = workspace.Timing!;
        await timing.SelectRunAsync(list.Id);
        await timing.FollowStartOrderAsync(true);
        var order = timing.Snapshot!.StartOrder.ToArray();
        var source = new ControlledSource();
        await timing.StartAsync(source, AlgeOptions(), "Operator");
        // History pages list the newest trigger first.
        await source.SendAsync("cloud", Page(Trigger(0, 10, 0, 25), Trigger(0, 10, 0, 5)));
        await ProcessedAsync(timing, 2);
        var snapshot = timing.Snapshot!;
        Assert.Equal(TimeSpan.Parse("10:00:05", System.Globalization.CultureInfo.InvariantCulture), TimeOfDay(snapshot, snapshot.Results.Single(x => x.Bib == order[0]).StartKey));
        Assert.Equal(TimeSpan.Parse("10:00:25", System.Globalization.CultureInfo.InvariantCulture), TimeOfDay(snapshot, snapshot.Results.Single(x => x.Bib == order[1]).StartKey));
        await timing.StopAsync();
    }

    [Fact]
    public async Task ImpulseOlderThanTheLastAssignedAtItsPositionStaysUnassigned()
    {
        await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore(), new AlgeDecoderFactory());
        var list = await TimingStorageTests.SeedAsync(workspace, PathFor("late.ost"), 3);
        var timing = workspace.Timing!;
        await timing.SelectRunAsync(list.Id);
        await timing.FollowStartOrderAsync(true);
        var order = timing.Snapshot!.StartOrder.ToArray();
        var source = new ControlledSource();
        await timing.StartAsync(source, AlgeOptions(), "Operator");
        await source.SendAsync("push", Page(Trigger(0, 10, 0, 5)));
        await ProcessedAsync(timing, 1);
        await source.SendAsync("push", Page(Trigger(0, 10, 0, 25)));
        await ProcessedAsync(timing, 2);
        // Recovered later: a start between two starts that are already assigned cannot be the next starter's start.
        await source.SendAsync("cloud", Page(Trigger(0, 10, 0, 15)));
        await ProcessedAsync(timing, 3);
        var snapshot = timing.Snapshot!;
        Assert.Equal(TimingStatus.Ready, snapshot.Results.Single(x => x.Bib == order[2]).Status);
        Assert.Equal("Unassigned", snapshot.Observations.Single(x => TimeOfDay(snapshot, x.Observation.Key) == new TimeSpan(10, 0, 15)).State);
        Assert.Equal(order[2], timing.ArmedStart);
        await timing.StopAsync();
    }

    [Fact]
    public async Task FinishBeforeTheExpectedCompetitorsStartStaysUnassigned()
    {
        await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore(), new AlgeDecoderFactory());
        var list = await TimingStorageTests.SeedAsync(workspace, PathFor("before-start.ost"), 2);
        var timing = workspace.Timing!;
        await timing.SelectRunAsync(list.Id);
        await timing.FollowStartOrderAsync(true);
        var order = timing.Snapshot!.StartOrder.ToArray();
        var source = new ControlledSource();
        await timing.StartAsync(source, AlgeOptions(), "Operator");
        await source.SendAsync("push", Page(Trigger(0, 10, 1, 0)));
        await ProcessedAsync(timing, 1);
        await source.SendAsync("cloud", Page(Trigger(1, 10, 0, 30)));
        await ProcessedAsync(timing, 2);
        var snapshot = timing.Snapshot!;
        Assert.Equal(TimingStatus.OnCourse, snapshot.Results.Single(x => x.Bib == order[0]).Status);
        Assert.Equal("Unassigned", snapshot.Observations.Single(x => x.Observation.Channel == 1).State);
        await timing.StopAsync();
    }

    [Fact]
    public async Task PreviousRunHistoryReadAfterALiveRunChangeIsNotTimedInTheNextRun()
    {
        await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore(), new AlgeDecoderFactory());
        var first = await TimingStorageTests.SeedAsync(workspace, PathFor("run-change.ost"), 1);
        var timing = workspace.Timing!;
        await timing.SelectRunAsync(first.Id);
        await timing.FollowStartOrderAsync(true);
        var source = new ControlledSource();
        await timing.StartAsync(source, AlgeOptions(), "Operator");
        await source.SendAsync("push", Page(Trigger(0, 10, 0, 0)));
        await ProcessedAsync(timing, 1);
        await source.SendAsync("push", Page(Trigger(1, 10, 1, 0)));
        await ProcessedAsync(timing, 2);
        Assert.Equal(6000, Assert.Single(timing.Snapshot!.Results).Hundredths);
        var started = (await workspace.ReadStartListsAsync(first.Plan.CompetitionId)).Revisions.Single(x => x.Id == first.Id);
        var plan = FisStartOrder.SecondRun(started, timing.Snapshot.ToRunFinishes());
        var second = (await workspace.SaveStartListAsync(new(plan, (await workspace.ReadAsync()).Revision, "Operator", "Run 2",
            TimingRulesTests.At, TimingReplay.InputVersion(await workspace.ReadTimingAsync(first.Id))))).Revisions.Single(x => x.Plan.RunNumber == 2);
        await timing.SelectRunAsync(second.Id);
        Assert.True(timing.IsHeld(0) && timing.IsHeld(1));
        await timing.ExpectAsync(0, null);
        await timing.ExpectAsync(1, null);
        Assert.NotNull(timing.ArmedStart);
        // ALGE Results reads the session's history again after the change: it still contains Run 1.
        await source.SendAsync("cloud", Page(Trigger(1, 10, 1, 0), Trigger(0, 10, 0, 0)));
        await ProcessedAsync(timing, 2);
        var run2 = timing.Snapshot!;
        Assert.Equal(second.Id, run2.ListId);
        Assert.Equal(TimingStatus.Ready, Assert.Single(run2.Results).Status);
        Assert.All(run2.Observations, x => Assert.Equal("Unassigned", x.State));
        // Genuine Run 2 input after the change is still assigned automatically.
        await source.SendAsync("push", Page(Trigger(0, 11, 0, 0)));
        await ProcessedAsync(timing, 3);
        Assert.Equal(TimingStatus.OnCourse, Assert.Single(timing.Snapshot!.Results).Status);
        await timing.StopAsync();
    }

    [Fact]
    public async Task LiveRunChangePutsEveryPositionOnHoldBeforeFurtherInput()
    {
        await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore(), new AlgeDecoderFactory());
        var first = await TimingStorageTests.SeedAsync(workspace, PathFor("hold.ost"), 1);
        await workspace.MarkRunStartedAsync(first.Id, (await workspace.ReadAsync()).Revision, "Operator", TimingRulesTests.At);
        var timing = workspace.Timing!;
        await timing.SelectRunAsync(first.Id);
        await timing.FollowStartOrderAsync(true);
        var source = new SimulatorTimingSource();
        await timing.StartAsync(source, new("Test", "Synthetic", TimingRulesTests.Date, Simulation: true), "Operator");
        var plan = FisStartOrder.SecondRun(first,
            first.Plan.Entries.Select(x => new RunFinish(x.Entrant.CompetitorId, FinishStatus.Finished, 6000)).ToArray());
        var second = (await workspace.SaveStartListAsync(new(plan, (await workspace.ReadAsync()).Revision, "Operator", "Run 2",
            TimingRulesTests.At, TimingReplay.InputVersion(await workspace.ReadTimingAsync(first.Id))))).Revisions.Single(x => x.Plan.RunNumber == 2);
        await timing.SelectRunAsync(second.Id);
        Assert.All(Enumerable.Range(0, 2), channel => Assert.True(timing.IsHeld(channel)));
        Assert.Null(timing.ArmedStart);
        await source.PulseAsync(0, TimeSpan.FromHours(12).Ticks); // a course worker crossing the start beam
        await ProcessedAsync(timing, 1);
        Assert.Null(Assert.Single(timing.Snapshot!.Observations).Bib);
        Assert.Null((await workspace.ReadStartListsAsync(first.Plan.CompetitionId)).Revisions.Single(x => x.Id == second.Id).StartedAt);
        await timing.StopAsync();
    }

    [Fact]
    public async Task UncertainCommitOfAnAutomaticAssignmentDoesNotStopCapture()
    {
        var file = PathFor("uncertain.ost");
        Guid listId;
        await using (var setup = new SeriesWorkspace(new SqliteSeriesFileStore())) { listId = (await TimingStorageTests.SeedAsync(setup, file, 2)).Id; }
        await using var session = await new SqliteSeriesFileStore().OpenAsync(file);
        var store = new UncertainStore((ITimingStore)session, audit: true);
        var timing = new TimingWorkspace(store, new AlgeDecoderFactory());
        await timing.SelectRunAsync(listId);
        var source = new SimulatorTimingSource();
        await timing.StartAsync(source, new("Test", "Synthetic", TimingRulesTests.Date, Simulation: true), "Operator");
        await source.PulseAsync(0, TimeSpan.FromHours(12).Ticks, 1); // explicit device bib: automatic assignment
        await TimingStorageTests.UntilAsync(() => timing.Fault is not null);
        timing.RetryStorage();
        await source.PulseAsync(1, TimeSpan.FromHours(12).Ticks + TimeSpan.FromSeconds(61).Ticks, 1);
        await TimingStorageTests.UntilAsync(() => timing.SavedPackets == 2 && timing.Snapshot!.Observations.Count == 2);
        await timing.StopAsync();
        Assert.Null(timing.Fault);
        var durable = await ((ITimingStore)session).ReadTimingAsync(listId);
        Assert.Equal(2, durable.Packets.Count);
        Assert.Equal(2, durable.Audit.Count); // the uncertain decision once, never twice
        var result = TimingReplay.Restore(durable, new AlgeDecoderFactory()).Results.Single(x => x.Bib == 1);
        Assert.Equal((TimingStatus.Finished, 6100L), (result.Status, result.Hundredths!.Value));
        await timing.DisposeAsync();
    }

    [Fact]
    public async Task UncertainCommitOfALiveRunChangeIsAdoptedAfterRetry()
    {
        var file = PathFor("uncertain-run-change.ost");
        StartListRevision first;
        await using (var setup = new SeriesWorkspace(new SqliteSeriesFileStore()))
        {
            first = await TimingStorageTests.SeedAsync(setup, file, 1);
            await setup.MarkRunStartedAsync(first.Id, (await setup.ReadAsync()).Revision, "Operator", TimingRulesTests.At);
        }
        await using var session = await new SqliteSeriesFileStore().OpenAsync(file);
        var timing = new TimingWorkspace(new UncertainStore((ITimingStore)session, runChange: true), new AlgeDecoderFactory());
        await timing.SelectRunAsync(first.Id);
        var source = new SimulatorTimingSource();
        await timing.StartAsync(source, SimulatorOptions(), "Operator");
        var plan = FisStartOrder.SecondRun(first,
            first.Plan.Entries.Select(x => new RunFinish(x.Entrant.CompetitorId, FinishStatus.Finished, 6000)).ToArray());
        var second = (await session.SaveStartListAsync(new(plan, (await session.ReadAsync()).Revision, "Operator", "Run 2", TimingRulesTests.At,
            TimingReplay.InputVersion(await ((ITimingStore)session).ReadTimingAsync(first.Id))))).Revisions.Single(x => x.Plan.RunNumber == 2);
        var change = timing.SelectRunAsync(second.Id);
        await TimingStorageTests.UntilAsync(() => timing.Fault is not null);
        Assert.False(change.IsCompleted);
        timing.RetryStorage();
        await change.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(second.Id, timing.ListId);
        await timing.ExpectAsync(0, null);
        await source.PulseAsync(0, TimeSpan.FromHours(12).Ticks, 1);
        await TimingStorageTests.UntilAsync(() => timing.SavedPackets == 1 && timing.Pending == 0);
        await timing.StopAsync();
        Assert.Null(timing.Fault);
        var run1 = await ((ITimingStore)session).ReadTimingAsync(first.Id);
        var run2 = await ((ITimingStore)session).ReadTimingAsync(second.Id);
        Assert.True(Assert.Single(run1.Sessions).CleanStop);
        Assert.True(Assert.Single(run2.Sessions).CleanStop);
        Assert.Single(run2.Packets);
        await timing.DisposeAsync();
    }

    [Fact]
    public async Task Run2CaptureThatBeganReconnectsAfterAnAuditedRun1Change()
    {
        await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore(), new AlgeDecoderFactory());
        var first = await TimingStorageTests.SeedAsync(workspace, PathFor("reconnect.ost"), 2);
        await workspace.MarkRunStartedAsync(first.Id, (await workspace.ReadAsync()).Revision, "Operator", TimingRulesTests.At);
        var timing = workspace.Timing!;
        await timing.SelectRunAsync(first.Id);
        await timing.StartAsync(new SimulatorTimingSource(), SimulatorOptions(), "Operator");
        await timing.StopAsync();
        foreach (var entry in first.Plan.Entries)
        { await timing.CorrectAsync(new(DecisionKind.Time, CompetitorId: entry.Entrant.CompetitorId, Hundredths: 6000 + entry.Bib), "Operator", "Synthetic time"); }
        var started = (await workspace.ReadStartListsAsync(first.Plan.CompetitionId)).Revisions.Single(x => x.Id == first.Id);
        var second = (await workspace.SaveStartListAsync(new(FisStartOrder.SecondRun(started, timing.Snapshot!.ToRunFinishes()),
            (await workspace.ReadAsync()).Revision, "Operator", "Run 2", TimingRulesTests.At,
            TimingReplay.InputVersion(await workspace.ReadTimingAsync(first.Id))))).Revisions.Single(x => x.Plan.RunNumber == 2);
        // Run 2 is connected to check the devices; nobody starts.
        await timing.SelectRunAsync(second.Id);
        await timing.StartAsync(new SimulatorTimingSource(), SimulatorOptions(), "Operator");
        await timing.StopAsync();
        // A jury decision changes Run 1 afterwards; any audited Run 1 change used to strand Run 2.
        await timing.SelectRunAsync(first.Id);
        await timing.CorrectStatusesAsync([first.Plan.Entries[0].Bib], TimingStatus.DSQ, "Operator", "Jury decision");
        await timing.SelectRunAsync(second.Id);
        await timing.StartAsync(new SimulatorTimingSource(), SimulatorOptions(), "Operator");
        Assert.True(timing.IsActive);
        await timing.StopAsync();
        // The list on which capture began is still never redrawn.
        var run1 = TimingReplay.Restore(await workspace.ReadTimingAsync(first.Id), new AlgeDecoderFactory());
        await Assert.ThrowsAsync<DomainValidationException>(async () => await workspace.SaveStartListAsync(new(
            FisStartOrder.SecondRun(started, run1.ToRunFinishes()), (await workspace.ReadAsync()).Revision, "Operator", "Redraw",
            TimingRulesTests.At, TimingReplay.InputVersion(await workspace.ReadTimingAsync(first.Id)))));
    }

    private static CaptureOptions SimulatorOptions() => new("Test", "Synthetic", TimingRulesTests.Date, Simulation: true);
    [Fact]
    public async Task OverlappingStopCallsShareOneStop()
    {
        await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore(), new AlgeDecoderFactory());
        var list = await TimingStorageTests.SeedAsync(workspace, PathFor("stop.ost"), 1);
        var timing = workspace.Timing!;
        await timing.SelectRunAsync(list.Id);
        await timing.StartAsync(new SlowStopSource(), new("Test", "Synthetic", TimingRulesTests.Date, Simulation: true), "Operator");
        var first = timing.StopAsync(); var second = timing.StopAsync();
        await Task.WhenAll(first, second);
        Assert.False(timing.IsActive);
        await timing.StopAsync();
    }

    private sealed class ControlledSource : ITimingSource
    {
        private readonly Channel<TransportPacket> _input = Channel.CreateUnbounded<TransportPacket>();
        public ValueTask SendAsync(string stream, string json)
            => _input.Writer.WriteAsync(new("alge-results/v1", "101", stream, Encoding.UTF8.GetBytes(json)));
        public async Task ReceiveAsync(Func<TransportPacket, ValueTask> receive, Action<string> status, CancellationToken ct)
        {
            status("Synthetic ALGE Results");
            await foreach (var packet in _input.Reader.ReadAllAsync(ct)) { await receive(packet); }
        }
        public ValueTask DisposeAsync() { _input.Writer.TryComplete(); return ValueTask.CompletedTask; }
    }

    private sealed class SlowStopSource : ITimingSource
    {
        public async Task ReceiveAsync(Func<TransportPacket, ValueTask> receive, Action<string> status, CancellationToken ct)
        {
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { await Task.Delay(300, CancellationToken.None); } // a device helper still closing
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    // Commits the write, then reports a storage error once: the caller cannot know the write succeeded.
    private sealed class UncertainStore(ITimingStore inner, bool audit = false, bool runChange = false) : ITimingStore
    {
        private bool _failed;
        private void FailOnce() { if (!_failed) { _failed = true; throw new IOException("Injected uncertain commit"); } }
        public Task<TimingReplayData> ReadTimingAsync(Guid id, CancellationToken ct = default) => inner.ReadTimingAsync(id, ct);
        public Task<CaptureSession> BeginCaptureAsync(Guid id, CaptureOptions options, string who, DateTimeOffset at, CancellationToken ct = default) => inner.BeginCaptureAsync(id, options, who, at, ct);
        public Task<IReadOnlyList<CaptureSession>> BeginCaptureGroupAsync(Guid id, IReadOnlyList<CaptureOptions> options, string who, DateTimeOffset at, CancellationToken ct = default) => inner.BeginCaptureGroupAsync(id, options, who, at, ct);
        public async Task<IReadOnlyList<CaptureSession>> SwitchCaptureGroupAsync(IReadOnlyList<Guid> previous, Guid id, IReadOnlyList<CaptureOptions> options, string who, DateTimeOffset at, CancellationToken ct = default)
        {
            var sessions = await inner.SwitchCaptureGroupAsync(previous, id, options, who, at, ct);
            if (runChange) { FailOnce(); }
            return sessions;
        }
        public Task AppendRawAsync(RawTimingPacket packet, CancellationToken ct = default) => inner.AppendRawAsync(packet, ct);
        public Task EndCaptureAsync(Guid id, DateTimeOffset at, CancellationToken ct = default) => inner.EndCaptureAsync(id, at, ct);
        public Task<IReadOnlyList<TimingAudit>> AppendTimingAuditBatchAsync(Guid id, long version, IReadOnlyList<TimingAuditChange> changes,
            string who, string why, DateTimeOffset at, bool startsRun = false, CancellationToken ct = default)
            => inner.AppendTimingAuditBatchAsync(id, version, changes, who, why, at, startsRun, ct);
        public async Task<TimingAudit> AppendTimingAuditAsync(Guid id, long version, TimingDecision before, TimingDecision after,
            string who, string why, DateTimeOffset at, long? undo = null, bool startsRun = false, CancellationToken ct = default)
        {
            var saved = await inner.AppendTimingAuditAsync(id, version, before, after, who, why, at, undo, startsRun, ct);
            if (audit) { FailOnce(); }
            return saved;
        }
    }
}
