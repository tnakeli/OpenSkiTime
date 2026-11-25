using System.Text;
using OpenSkiTime.Application;
using OpenSkiTime.Devices;
using OpenSkiTime.Domain;
using OpenSkiTime.Persistence;
using OpenSkiTime.Timing;
using Xunit;

namespace OpenSkiTime.Tests;

// Opening Timing with the wrong device must leave a run usable: only received input decides whether a run is real or
// training and whether its start list can still change.
public sealed class TimingInputLockTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "openskitime-input-lock", Guid.NewGuid().ToString("N"));
    public TimingInputLockTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);

    private static SeriesWorkspace Workspace() => new(new SqliteSeriesFileStore(new AlgeDecoderFactory()), new AlgeDecoderFactory());
    private string PathFor(string name) => Path.Combine(_root, name + ".ost");
    private static CaptureOptions Options(bool simulation) => new(simulation ? "Simulator" : "Timy 2/3 · USB", "Synthetic", TimingRulesTests.Date, Simulation: simulation);

    private static async Task ReceiveAsync(TimingWorkspace timing, bool simulation, params string[] lines)
    {
        var source = new LineSource(lines);
        await timing.StartAsync(source, Options(simulation), "Operator");
        await source.Delivered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        await TimingStorageTests.UntilAsync(() => timing.Pending == 0);
        await timing.StopAsync();
    }

    private static async Task<StartListRevision> LatestAsync(SeriesWorkspace workspace, StartListRevision list)
        => (await workspace.ReadStartListsAsync(list.Plan.CompetitionId)).Revisions.MaxBy(x => x.Revision)!;

    private static async Task<StartListRevision> RedrawAsync(SeriesWorkspace workspace, StartListRevision list)
        => (await workspace.SaveStartListAsync(new(list.Plan, (await workspace.ReadAsync()).Revision, "Operator", "Redraw", TimingRulesTests.At)))
            .Revisions.MaxBy(x => x.Revision)!;

    [Fact]
    public async Task ASimulatorSessionWithoutImpulsesDoesNotKeepRealTimingOutOfTheRun()
    {
        await using var workspace = Workspace();
        var list = await TimingStorageTests.SeedAsync(workspace, PathFor("empty-simulator"), 2);
        var timing = workspace.Timing!;
        await timing.SelectRunAsync(list.Id);
        await ReceiveAsync(timing, simulation: true);
        Assert.False(timing.IsSimulation);
        Assert.False((await LatestAsync(workspace, list)).HasCapture);

        await ReceiveAsync(timing, simulation: false, "12:00:00.0  \r", " 0001 C0 12:00:01.0000\r");

        Assert.Single(timing.Snapshot!.Observations);
        Assert.False(TimingReplay.UsesTraining(await workspace.ReadTimingAsync(list.Id)));
        Assert.True((await LatestAsync(workspace, list)).HasCapture);
    }

    [Fact]
    public async Task DeviceHeartbeatsLeaveTheStartListOpenAndTheFirstImpulseLocksIt()
    {
        await using var workspace = Workspace();
        var list = await TimingStorageTests.SeedAsync(workspace, PathFor("heartbeats"), 2);
        var timing = workspace.Timing!;
        await timing.SelectRunAsync(list.Id);
        await ReceiveAsync(timing, simulation: false, "20:46:07.0  \r", "20:46:08.0  \r");
        Assert.Equal(2, (await workspace.ReadTimingAsync(list.Id)).Packets.Count); // raw input is kept all the same
        var redrawn = await RedrawAsync(workspace, list);
        Assert.Equal(2, redrawn.Revision);

        // A held impulse is not assigned to anyone, and still is timing input.
        await timing.SelectRunAsync(redrawn.Id);
        await timing.HoldAllAsync();
        await ReceiveAsync(timing, simulation: false, " 0001 C0 20:46:10.05\r");
        Assert.Equal("Unassigned", Assert.Single(timing.Snapshot!.Observations).State);
        var locked = await Assert.ThrowsAsync<DomainValidationException>(() => RedrawAsync(workspace, redrawn));
        Assert.Contains("can no longer be redrawn", locked.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AStoreWithoutDecodersTreatsEveryDeviceMessageAsInput()
    {
        await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore(), new AlgeDecoderFactory());
        var list = await TimingStorageTests.SeedAsync(workspace, PathFor("no-decoders"), 1);
        await workspace.Timing!.SelectRunAsync(list.Id);
        await ReceiveAsync(workspace.Timing, simulation: false, "20:46:07.0  \r");
        Assert.True((await LatestAsync(workspace, list)).HasCapture);
    }

    [Fact]
    public async Task TrainingTimesAreSetAsideUnchangedAndRealTimingContinuesOnTheSameOrder()
    {
        await using var workspace = Workspace();
        var list = await TimingStorageTests.SeedAsync(workspace, PathFor("set-aside"), 2);
        var timing = workspace.Timing!;
        await timing.SelectRunAsync(list.Id);
        await ReceiveAsync(timing, simulation: true, " 0001* C0 12:00:00.0000\r", " 0001* C1 12:01:00.0000\r");
        await timing.CorrectStatusesAsync([2], TimingStatus.DNS, "Operator", "Training");
        Assert.True(timing.HasTrainingInput);
        var before = await workspace.ReadTimingAsync(list.Id);

        var refused = await Assert.ThrowsAsync<DomainValidationException>(() => timing.StartAsync(new LineSource([]), Options(false), "Operator"));
        Assert.Contains("Set them aside", refused.Message, StringComparison.Ordinal);
        Assert.False(timing.IsActive);
        await workspace.SetTrainingAsideAsync(list.Id, (await workspace.ReadAsync()).Revision, "Operator", TimingRulesTests.At);

        var next = await LatestAsync(workspace, list);
        Assert.Equal(list.Revision + 1, next.Revision);
        Assert.Equal(list.Plan.Entries.Select(x => (x.Position, x.Bib, x.Entrant.CompetitorId)), next.Plan.Entries.Select(x => (x.Position, x.Bib, x.Entrant.CompetitorId)));
        Assert.Null(next.StartedAt);
        Assert.Contains("training times set aside", next.Reason, StringComparison.Ordinal);
        // Nothing of the training is lost or carried over, including the training DNS.
        var after = await workspace.ReadTimingAsync(list.Id);
        Assert.Equal(before.Packets.Select(x => Convert.ToHexString(x.Bytes)), after.Packets.Select(x => Convert.ToHexString(x.Bytes)));
        Assert.Equal(before.Audit.Select(x => x.Id), after.Audit.Select(x => x.Id));
        Assert.True(TimingReplay.UsesTraining(after)); // results of the training revision stay barred from FIS
        await timing.SelectRunAsync(next.Id);
        Assert.False(timing.HasTrainingInput);
        Assert.All(timing.Snapshot!.Results, x => Assert.Equal(TimingStatus.Ready, x.Status));
        await ReceiveAsync(timing, simulation: false, " 0001* C0 13:00:00.0000\r");
        Assert.Equal(TimingStatus.OnCourse, timing.Snapshot!.Results.Single(x => x.Bib == 1).Status);
        Assert.False(TimingReplay.UsesTraining(await workspace.ReadTimingAsync(next.Id)));
    }

    [Fact]
    public async Task RealTimesCannotBeSetAsideAndKeepTheSimulatorOut()
    {
        await using var workspace = Workspace();
        var list = await TimingStorageTests.SeedAsync(workspace, PathFor("real"), 2);
        var timing = workspace.Timing!;
        await timing.SelectRunAsync(list.Id);
        await ReceiveAsync(timing, simulation: true); // opened only
        await ReceiveAsync(timing, simulation: false, " 0001* C0 12:00:00.0000\r");
        var refused = await Assert.ThrowsAsync<DomainValidationException>(() => timing.StartAsync(new LineSource([]), Options(true), "Operator"));
        Assert.Contains("real times", refused.Message, StringComparison.Ordinal);
        var revision = (await workspace.ReadAsync()).Revision;
        await Assert.ThrowsAsync<DomainValidationException>(() => workspace.SetTrainingAsideAsync(list.Id, revision, "Operator", TimingRulesTests.At));
        Assert.Equal(list.Id, (await LatestAsync(workspace, list)).Id);
    }

    [Fact]
    public async Task ADecisionAfterTrainingDoesNotKeepTheTrainingFromBeingSetAside()
    {
        await using var workspace = Workspace();
        var list = await TimingStorageTests.SeedAsync(workspace, PathFor("decision-after-training"), 2);
        var timing = workspace.Timing!;
        await timing.SelectRunAsync(list.Id);
        await ReceiveAsync(timing, simulation: false); // a real device, opened only
        await ReceiveAsync(timing, simulation: true, " 0001* C0 12:00:00.0000\r");
        await timing.CorrectStatusesAsync([2], TimingStatus.DNS, "Operator", "Rehearsal, after the simulator was closed");
        Assert.True(timing.HasTrainingInput);
        await workspace.SetTrainingAsideAsync(list.Id, (await workspace.ReadAsync()).Revision, "Operator", TimingRulesTests.At);
        await timing.SelectRunAsync((await LatestAsync(workspace, list)).Id);
        await ReceiveAsync(timing, simulation: false, " 0001* C0 13:00:00.0000\r");
    }

    [Fact]
    public async Task ADecisionAfterAnUnusedSimulatorIsReal()
    {
        await using var workspace = Workspace();
        var list = await TimingStorageTests.SeedAsync(workspace, PathFor("decision-after-unused"), 2);
        var timing = workspace.Timing!;
        await timing.SelectRunAsync(list.Id);
        await ReceiveAsync(timing, simulation: true); // opened by mistake and closed
        await timing.CorrectStatusesAsync([2], TimingStatus.DNS, "Operator", "Does not start");
        Assert.False(timing.HasTrainingInput);
        // Until a real device is used, nothing distinguishes this from a rehearsal: no certification.
        Assert.True(TimingReplay.UsesTraining(await workspace.ReadTimingAsync(list.Id)));
        var revision = (await workspace.ReadAsync()).Revision;
        await Assert.ThrowsAsync<DomainValidationException>(() => workspace.SetTrainingAsideAsync(list.Id, revision, "Operator", TimingRulesTests.At));
        await ReceiveAsync(timing, simulation: false, " 0001* C0 12:00:00.0000\r");
        Assert.Equal(TimingStatus.DNS, timing.Snapshot!.Results.Single(x => x.Bib == 2).Status);
        Assert.False(TimingReplay.UsesTraining(await workspace.ReadTimingAsync(list.Id)));
    }

    [Fact]
    public async Task DecisionsDuringAndAfterAnUnusedSimulatorNeverStrandTheRun()
    {
        await using var workspace = Workspace();
        var list = await TimingStorageTests.SeedAsync(workspace, PathFor("decisions-around-unused"), 2);
        var timing = workspace.Timing!;
        await timing.SelectRunAsync(list.Id);
        await timing.StartAsync(new LineSource([]), Options(true), "Operator");
        await timing.CorrectStatusesAsync([2], TimingStatus.DNS, "Operator", "While the simulator is open");
        await timing.StopAsync();
        await timing.CorrectStatusesAsync([2], null, "Operator", "After it was closed");
        Assert.False(timing.HasTrainingInput);
        await ReceiveAsync(timing, simulation: false, " 0001* C0 12:00:00.0000\r");
        Assert.Equal(TimingStatus.OnCourse, timing.Snapshot!.Results.Single(x => x.Bib == 1).Status);
    }

    [Fact]
    public async Task DecisionsWhileTheSimulatorIsUsedBecomeTrainingWithItsFirstImpulse()
    {
        await using var workspace = Workspace();
        var list = await TimingStorageTests.SeedAsync(workspace, PathFor("decision-then-impulse"), 2);
        var timing = workspace.Timing!;
        await timing.SelectRunAsync(list.Id);
        var source = new SimulatorTimingSource();
        await timing.StartAsync(source, Options(true), "Operator");
        await timing.CorrectStatusesAsync([2], TimingStatus.DNS, "Operator", "Rehearsal");
        await source.PulseAsync(0, TimeSpan.FromHours(12).Ticks, 1);
        await TimingStorageTests.UntilAsync(() => timing.HasTrainingInput && timing.Pending == 0);
        await timing.StopAsync();
        await ReceiveAsync(timing, simulation: true); // training goes on
        await workspace.SetTrainingAsideAsync(list.Id, (await workspace.ReadAsync()).Revision, "Operator", TimingRulesTests.At);
        await timing.SelectRunAsync((await LatestAsync(workspace, list)).Id);
        Assert.All(timing.Snapshot!.Results, x => Assert.Equal(TimingStatus.Ready, x.Status));
    }

    [Fact]
    public async Task DecisionsOfRealTimingCountAsRealTimes()
    {
        await using var workspace = Workspace();
        var list = await TimingStorageTests.SeedAsync(workspace, PathFor("real-decisions"), 2);
        var timing = workspace.Timing!;
        await timing.SelectRunAsync(list.Id);
        await ReceiveAsync(timing, simulation: false, "12:00:00.0  \r");
        await timing.CorrectStatusesAsync([2], TimingStatus.DNS, "Operator", "Did not start");
        var refused = await Assert.ThrowsAsync<DomainValidationException>(() => timing.StartAsync(new LineSource([]), Options(true), "Operator"));
        Assert.Contains("real times", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEmptySimulatorSessionEndsBeforeLaterRealDecisions()
    {
        await using var workspace = Workspace();
        var list = await TimingStorageTests.SeedAsync(workspace, PathFor("closed-simulator"), 2);
        var timing = workspace.Timing!;
        await timing.SelectRunAsync(list.Id);
        await ReceiveAsync(timing, simulation: true);
        await ReceiveAsync(timing, simulation: false, " 0001* C0 12:00:00.0000\r");
        Assert.NotEmpty(timing.Snapshot!.Audit);
        Assert.False(timing.HasTrainingInput);
        await ReceiveAsync(timing, simulation: false, " 0001* C1 12:01:00.0000\r"); // real timing goes on
        Assert.False(TimingReplay.UsesTraining(await workspace.ReadTimingAsync(list.Id)));
    }

    [Fact]
    public async Task AnInterruptedSimulatorSessionDoesNotClaimLaterRealDecisions()
    {
        var path = PathFor("interrupted-simulator");
        StartListRevision list;
        await using (var workspace = Workspace())
        {
            list = await TimingStorageTests.SeedAsync(workspace, path, 2);
            await workspace.Timing!.SelectRunAsync(list.Id);
            await ReceiveAsync(workspace.Timing, simulation: true);
        }
        await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE TimingCaptures SET CleanStop=0, StoppedAt=NULL"; // the application ended abruptly
            await command.ExecuteNonQueryAsync();
        }
        await using var reopened = Workspace();
        await reopened.OpenAsync(path);
        var timing = reopened.Timing!;
        await timing.SelectRunAsync(list.Id);
        await ReceiveAsync(timing, simulation: false, " 0001* C0 12:00:00.0000\r");
        Assert.NotEmpty(timing.Snapshot!.Audit);
        Assert.False(timing.HasTrainingInput);
        await ReceiveAsync(timing, simulation: false, " 0001* C1 12:01:00.0000\r");
        Assert.False(TimingReplay.UsesTraining(await reopened.ReadTimingAsync(list.Id)));
    }

    [Fact]
    public async Task TrainingInputWithoutAnyImpulseIsReportedLikeItIsRefused()
    {
        await using var workspace = Workspace();
        var list = await TimingStorageTests.SeedAsync(workspace, PathFor("training-heartbeats"), 1);
        var timing = workspace.Timing!;
        await timing.SelectRunAsync(list.Id);
        await ReceiveAsync(timing, simulation: true, "12:00:00.0  \r");
        Assert.True(timing.HasTrainingInput);
        await Assert.ThrowsAsync<DomainValidationException>(() => timing.StartAsync(new LineSource([]), Options(false), "Operator"));
        await timing.SelectRunAsync(list.Id);
        Assert.True(timing.HasTrainingInput);
        await workspace.SetTrainingAsideAsync(list.Id, (await workspace.ReadAsync()).Revision, "Operator", TimingRulesTests.At);
        await timing.SelectRunAsync((await LatestAsync(workspace, list)).Id);
        await ReceiveAsync(timing, simulation: false, " 0001* C0 12:00:00.0000\r");
    }

    [Fact]
    public async Task TrainingTimesBehindALaterRunCannotBeSetAside()
    {
        await using var workspace = Workspace();
        var first = await TimingStorageTests.SeedAsync(workspace, PathFor("later-run"), 2);
        var timing = workspace.Timing!;
        await timing.SelectRunAsync(first.Id);
        await ReceiveAsync(timing, simulation: true, " 0001* C0 12:00:00.0000\r", " 0001* C1 12:01:00.0000\r", " 0002* C0 12:02:00.0000\r", " 0002* C1 12:03:01.0000\r");
        var captured = await workspace.ReadTimingAsync(first.Id);
        await workspace.SaveStartListAsync(new(FisStartOrder.SecondRun(captured.List, timing.Snapshot!.ToRunFinishes()), (await workspace.ReadAsync()).Revision,
            "Operator", "Run 2", TimingRulesTests.At, TimingReplay.InputVersion(captured)));
        var revision = (await workspace.ReadAsync()).Revision;
        var error = await Assert.ThrowsAsync<DomainValidationException>(() => workspace.SetTrainingAsideAsync(first.Id, revision, "Operator", TimingRulesTests.At));
        Assert.Contains("Run 2", error.Message, StringComparison.Ordinal);
    }

    private sealed class LineSource(string[] lines) : ITimingSource
    {
        public TaskCompletionSource Delivered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task ReceiveAsync(Func<TransportPacket, ValueTask> receive, Action<string> status, CancellationToken ct)
        {
            foreach (var line in lines) { await receive(new("alge-ascii/v1", "Synthetic", "1", Encoding.ASCII.GetBytes(line))); }
            Delivered.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
