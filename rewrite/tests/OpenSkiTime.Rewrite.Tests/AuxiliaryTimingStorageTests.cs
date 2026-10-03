using System.Text;
using System.Threading.Channels;
using Microsoft.Data.Sqlite;
using OpenSkiTime.Rewrite.Application;
using OpenSkiTime.Rewrite.Devices;
using OpenSkiTime.Rewrite.Domain;
using OpenSkiTime.Rewrite.Persistence;
using OpenSkiTime.Rewrite.Timing;
using Xunit;

namespace OpenSkiTime.Rewrite.Tests;

public sealed class AuxiliaryTimingStorageTests
{
    [Fact]
    public async Task ConcurrentAAndBAreIsolatedAndStoppingADoesNotReleaseBLease()
    {
        using var folder = new Folder();
        var path = folder.PathFor("race.ost");
        await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore(), new AlgeDecoderFactory());
        var list = await TimingStorageTests.SeedAsync(workspace, path, 1);
        var a = workspace.Timing!; var b = workspace.Auxiliary!;
        await a.SelectRunAsync(list.Id);
        var sourceA = new SimulatorTimingSource(); var sourceB = new PacketSource();
        await Task.WhenAll(a.StartAsync(sourceA, Options("A"), "Operator"),
            b.StartAsync(list.Id, AuxiliaryTimingRole.B, sourceB, Options("B"), "Operator"));
        await sourceA.PulseAsync(0, TimeSpan.FromHours(12).Ticks, 1);
        await sourceA.PulseAsync(1, TimeSpan.FromHours(12).Ticks + TimeSpan.FromSeconds(60).Ticks, 1);
        await TimingStorageTests.UntilAsync(() => a.Snapshot!.Complete);
        var authoritative = await workspace.ReadTimingAsync(list.Id);
        var version = TimingReplay.InputVersion(authoritative);
        var result = Assert.Single(a.Snapshot!.Results);
        await sourceB.SendAsync("0001* C0 12:00:00.0001\r");
        await sourceB.SendAsync("0001* C1 12:01:00.0002\r");
        await sourceB.SendAsync("broken\0input\r");
        await sourceB.SendAsync("0001* C1 12:01:00.0002\r");
        await TimingStorageTests.UntilAsync(() => b.State(AuxiliaryTimingRole.B).SavedPackets == 4 && b.State(AuxiliaryTimingRole.B).Observations.Count == 4);
        Assert.Equal(result, Assert.Single(a.Snapshot.Results));
        Assert.Equal(version, TimingReplay.InputVersion(await workspace.ReadTimingAsync(list.Id)));
        Assert.Equal(2, (await workspace.ReadTimingAsync(list.Id)).Packets.Count);
        Assert.Equal(4, b.State(AuxiliaryTimingRole.B).Observations.Count);
        await a.StopAsync();
        Assert.True(b.IsActive);
        await using var other = await new SqliteSeriesFileStore().OpenAsync(path);
        await Assert.ThrowsAsync<SeriesFileException>(() => ((ITimingStore)other).BeginCaptureAsync(list.Id, Options("other"), "Other", TimingRulesTests.At));
        await Assert.ThrowsAsync<SeriesFileException>(() => ((IAuxiliaryTimingStore)other).BeginAuxiliaryCaptureAsync(list.Id,
            AuxiliaryTimingRole.HandStart, Options("other"), "Other", TimingRulesTests.At, false));
        await sourceB.SendAsync("0002 C0 12:02:00.0000\r");
        await TimingStorageTests.UntilAsync(() => b.State(AuxiliaryTimingRole.B).SavedPackets == 5);
        await b.StopAsync(AuxiliaryTimingRole.B);
        var next = await ((ITimingStore)other).BeginCaptureAsync(list.Id, Options("other"), "Other", TimingRulesTests.At);
        await ((ITimingStore)other).EndCaptureAsync(next.Id, TimingRulesTests.At);
    }

    [Fact]
    public async Task StoppingBLeavesACapturingAndSeriesCloseDrainsAllRolesIntoPortableBackup()
    {
        using var folder = new Folder();
        var path = folder.PathFor("portable.ost");
        await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore(), new AlgeDecoderFactory());
        var list = await TimingStorageTests.SeedAsync(workspace, path, 1);
        await workspace.Timing!.SelectRunAsync(list.Id);
        var a = new SimulatorTimingSource(); var b = new PacketSource();
        await workspace.Timing.StartAsync(a, Options("A"), "Operator");
        await workspace.Auxiliary!.StartAsync(list.Id, AuxiliaryTimingRole.B, b, Options("B"), "Operator");
        await b.SendAsync("bad\r");
        await workspace.Auxiliary.StopAsync(AuxiliaryTimingRole.B);
        Assert.True(workspace.Timing.IsActive);
        await using var other = await new SqliteSeriesFileStore().OpenAsync(path);
        await Assert.ThrowsAsync<SeriesFileException>(() => ((IAuxiliaryTimingStore)other).BeginAuxiliaryCaptureAsync(list.Id,
            AuxiliaryTimingRole.B, Options("other"), "Other", TimingRulesTests.At, false));
        var start = new PacketSource(); var finish = new PacketSource();
        var handOptions = Options("Hand") with { StartChannel = 0, FinishChannel = 0 };
        await workspace.Auxiliary.StartAsync(list.Id, AuxiliaryTimingRole.HandStart, start, handOptions, "Operator", false);
        await workspace.Auxiliary.StartAsync(list.Id, AuxiliaryTimingRole.HandFinish, finish, handOptions, "Operator", false);
        await start.SendAsync("0001 C0M 12:00:00.12\r");
        await finish.SendAsync("0001 C0M 12:01:00.34\r");
        await a.PulseAsync(0, TimeSpan.FromHours(12).Ticks, 1);
        await workspace.CloseAsync();
        await workspace.OpenAsync(path);
        var before = await workspace.ReadAuxiliaryTimingAsync(list.Id);
        Assert.Equal(3, before.Packets.Count);
        Assert.All(before.Sessions, x => Assert.True(x.Capture.CleanStop));
        var decoded = before.Decode(new AlgeDecoderFactory());
        Assert.Contains(decoded, x => x.Role == AuxiliaryTimingRole.HandStart && x.Observation.Channel == 0 && x.Observation.Precision == 2);
        Assert.Contains(decoded, x => x.Role == AuxiliaryTimingRole.HandFinish && x.Observation.Channel == 1 && x.Observation.Precision == 2);
        Assert.All(decoded.Where(x => x.Role != AuxiliaryTimingRole.B), x => Assert.False(x.Live));
        Assert.Single((await workspace.ReadTimingAsync(list.Id)).Packets);
        var backup = folder.PathFor("backup.ost");
        await workspace.BackupAsync(backup);
        await workspace.OpenAsync(backup);
        var restored = await workspace.ReadAuxiliaryTimingAsync(list.Id);
        Assert.Equal(before.Packets.OrderBy(x => x.SessionId).Select(x => Convert.ToHexString(x.Bytes)),
            restored.Packets.OrderBy(x => x.SessionId).Select(x => Convert.ToHexString(x.Bytes)));
        Assert.Equal(decoded, restored.Decode(new AlgeDecoderFactory()));
        await using var sql = new SqliteConnection($"Data Source={backup};Pooling=False");
        await sql.OpenAsync();
        foreach (var statement in new[] { "UPDATE AuxiliaryRawPackets SET Bytes=X'00'", "DELETE FROM AuxiliaryRawPackets" })
        {
            using var command = sql.CreateCommand(); command.CommandText = statement;
            await Assert.ThrowsAsync<SqliteException>(() => command.ExecuteNonQueryAsync());
        }
    }

    [Fact]
    public async Task FailedAuxiliaryCommitIsNotShownSavedAndRetryPreservesOriginalPacketExactlyOnce()
    {
        using var folder = new Folder();
        var path = folder.PathFor("failure.ost");
        Guid listId;
        await using (var setup = new SeriesWorkspace(new SqliteSeriesFileStore()))
        { listId = (await TimingStorageTests.SeedAsync(setup, path, 1)).Id; }
        await using var session = await new SqliteSeriesFileStore().OpenAsync(path);
        var store = new UncertainStore((IAuxiliaryTimingStore)session);
        await using var b = new AuxiliaryTimingWorkspace(store, new AlgeDecoderFactory());
        await using var a = new TimingWorkspace((ITimingStore)session, new AlgeDecoderFactory());
        await a.SelectRunAsync(listId);
        var sourceA = new SimulatorTimingSource(); var sourceB = new PacketSource();
        await a.StartAsync(sourceA, Options("A"), "Operator");
        await b.StartAsync(listId, AuxiliaryTimingRole.B, sourceB, Options("B"), "Operator");
        await sourceB.SendAsync("0001* C0 12:00:00.0000001\r");
        await TimingStorageTests.UntilAsync(() => b.State(AuxiliaryTimingRole.B).Fault is not null);
        Assert.Equal(1, b.State(AuxiliaryTimingRole.B).Pending);
        Assert.Equal(0, b.State(AuxiliaryTimingRole.B).SavedPackets);
        Assert.Empty(b.State(AuxiliaryTimingRole.B).Observations);
        await sourceA.PulseAsync(0, TimeSpan.FromHours(12).Ticks, 1);
        await TimingStorageTests.UntilAsync(() => a.Snapshot!.Results[0].Status == TimingStatus.OnCourse);
        await Assert.ThrowsAsync<SeriesFileException>(() => b.StopAsync(AuxiliaryTimingRole.B));
        b.RetryStorage(AuxiliaryTimingRole.B);
        await b.StopAsync(AuxiliaryTimingRole.B);
        Assert.Equal(1, b.State(AuxiliaryTimingRole.B).SavedPackets);
        Assert.Null(b.State(AuxiliaryTimingRole.B).Fault);
        Assert.Single((await store.ReadAuxiliaryTimingAsync(listId)).Packets);
        Assert.True(a.IsActive);
        await a.StopAsync();
    }

    [Fact]
    public async Task AuxiliaryObservationCannotBeAssignedThroughAuthoritativeStore()
    {
        using var folder = new Folder();
        await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore(), new AlgeDecoderFactory());
        var list = await TimingStorageTests.SeedAsync(workspace, folder.PathFor("isolation.ost"), 1);
        var source = new PacketSource();
        await workspace.Auxiliary!.StartAsync(list.Id, AuxiliaryTimingRole.B, source, Options("B"), "Operator");
        await source.SendAsync("0001* C0 12:00:00.0001\r");
        await workspace.Auxiliary.StopAsync(AuxiliaryTimingRole.B);
        var observation = Assert.Single((await workspace.ReadAuxiliaryTimingAsync(list.Id)).Decode(new AlgeDecoderFactory()));
        await workspace.Timing!.SelectRunAsync(list.Id);
        await Assert.ThrowsAsync<DomainValidationException>(() => workspace.Timing.CorrectAsync(
            new(DecisionKind.Assignment, observation.Observation.Key, Bib: 1), "Operator", "Attempt invalid assignment"));
        Assert.Empty((await workspace.ReadTimingAsync(list.Id)).Audit);
        Assert.Empty(workspace.Timing.Snapshot!.Observations);
    }

    [Fact]
    public async Task LiveRunSwitchKeepsFragmentInOriginalRunAndContinuesSameTransport()
    {
        using var folder = new Folder();
        await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore(), new AlgeDecoderFactory());
        var first = await TimingStorageTests.SeedAsync(workspace, folder.PathFor("switch.ost"), 1);
        await workspace.MarkRunStartedAsync(first.Id, (await workspace.ReadAsync()).Revision, "Operator", TimingRulesTests.At);
        var plan = FisStartOrder.SecondRun(first,
            first.Plan.Entries.Select(x => new RunFinish(x.Entrant.CompetitorId, FinishStatus.Finished, 6000)).ToArray());
        var desk = await workspace.SaveStartListAsync(new(plan, (await workspace.ReadAsync()).Revision,
            "Operator", "Synthetic second run", TimingRulesTests.At));
        var second = desk.Revisions.Single(x => x.Plan.RunNumber == 2);
        var source = new PacketSource();
        await workspace.Auxiliary!.StartAsync(first.Id, AuxiliaryTimingRole.B, source, Options("B"), "Operator");
        await source.SendAsync("0001 C0 12:00:");
        await TimingStorageTests.UntilAsync(() => workspace.Auxiliary.State(AuxiliaryTimingRole.B).SavedPackets == 1);
        var switching = workspace.Auxiliary.SwitchRunAsync(AuxiliaryTimingRole.B, second.Id);
        await source.SendAsync("00.0001\r");
        await switching;
        Assert.Equal(second.Id, workspace.Auxiliary.State(AuxiliaryTimingRole.B).ListId);
        await source.SendAsync("0001 C0 13:00:00.0002\r");
        await workspace.Auxiliary.StopAsync(AuxiliaryTimingRole.B);
        var old = await workspace.ReadAuxiliaryTimingAsync(first.Id);
        var next = await workspace.ReadAuxiliaryTimingAsync(second.Id);
        Assert.Equal(2, old.Packets.Count);
        Assert.Single(next.Packets);
        Assert.Equal(TimingRulesTests.Date.ToDateTime(new TimeOnly(12, 0)).Ticks + 1000,
            Assert.Single(old.Decode(new AlgeDecoderFactory())).Observation.DeviceTicks);
        Assert.Equal(TimingRulesTests.Date.ToDateTime(new TimeOnly(13, 0)).Ticks + 2000,
            Assert.Single(next.Decode(new AlgeDecoderFactory())).Observation.DeviceTicks);
        Assert.Empty((await workspace.ReadTimingAsync(first.Id)).Packets);
        Assert.Empty((await workspace.ReadTimingAsync(second.Id)).Packets);
    }

    [Fact]
    public async Task InterruptedAuxiliaryCaptureReopensForReviewWithoutAWarningOrInput()
    {
        using var folder = new Folder();
        var path = folder.PathFor("interrupted.ost");
        Guid listId;
        await using (var workspace = new SeriesWorkspace(new SqliteSeriesFileStore(), new AlgeDecoderFactory()))
        {
            listId = (await TimingStorageTests.SeedAsync(workspace, path, 1)).Id;
            var source = new PacketSource();
            await workspace.Auxiliary!.StartAsync(listId, AuxiliaryTimingRole.B, source, Options("B"), "Operator");
            await source.SendAsync("partial");
            await workspace.Auxiliary.StopAsync(AuxiliaryTimingRole.B);
        }
        await using (var sql = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            await sql.OpenAsync();
            using var command = sql.CreateCommand();
            command.CommandText = "UPDATE AuxiliaryCaptures SET CleanStop=0, StoppedAt=NULL";
            await command.ExecuteNonQueryAsync();
        }
        await using var reopened = new SeriesWorkspace(new SqliteSeriesFileStore(), new AlgeDecoderFactory());
        await reopened.OpenAsync(path);
        var auxiliary = await reopened.ReadAuxiliaryTimingAsync(listId);
        Assert.Equal("partial", Encoding.ASCII.GetString(Assert.Single(auxiliary.Packets).Bytes));
        Assert.Equal(2, auxiliary.Decode(new AlgeDecoderFactory()).Count);
        await reopened.Timing!.SelectRunAsync(listId);
        Assert.Empty(reopened.Timing.Snapshot!.Observations);
        Assert.Equal(0, reopened.Timing.Snapshot.Unresolved);
    }

    [Fact]
    public async Task StopCanDrainWhileRunSwitchWaitsForIncompleteDeviceMessage()
    {
        using var folder = new Folder();
        await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore(), new AlgeDecoderFactory());
        var first = await TimingStorageTests.SeedAsync(workspace, folder.PathFor("pending-switch.ost"), 1);
        var source = new PacketSource();
        await workspace.Auxiliary!.StartAsync(first.Id, AuxiliaryTimingRole.B, source, Options("B"), "Operator");
        await source.SendAsync("0001 C0 12:00:");
        await TimingStorageTests.UntilAsync(() => workspace.Auxiliary.State(AuxiliaryTimingRole.B).SavedPackets == 1);
        var switching = workspace.Auxiliary.SwitchRunAsync(AuxiliaryTimingRole.B, Guid.NewGuid());
        await workspace.Auxiliary.StopAsync(AuxiliaryTimingRole.B).WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<SeriesFileException>(() => switching);
        Assert.False(workspace.Auxiliary.IsActive);
        var data = await workspace.ReadAuxiliaryTimingAsync(first.Id);
        Assert.Single(data.Packets);
        Assert.True(Assert.Single(data.Sessions).Capture.CleanStop);
        Assert.Equal(ObservationKind.Invalid, Assert.Single(data.Decode(new AlgeDecoderFactory())).Observation.Kind);
    }

    [Fact]
    public async Task UncertainRunSwitchCommitRecoversExactlyOneBoundaryBeforeDrainingPendingInput()
    {
        using var folder = new Folder();
        var path = folder.PathFor("uncertain-switch.ost");
        Guid firstId; Guid secondId;
        await using (var setup = new SeriesWorkspace(new SqliteSeriesFileStore()))
        {
            var first = await TimingStorageTests.SeedAsync(setup, path, 1); firstId = first.Id;
            await setup.MarkRunStartedAsync(first.Id, (await setup.ReadAsync()).Revision, "Operator", TimingRulesTests.At);
            var plan = FisStartOrder.SecondRun(first,
                first.Plan.Entries.Select(x => new RunFinish(x.Entrant.CompetitorId, FinishStatus.Finished, 6000)).ToArray());
            secondId = (await setup.SaveStartListAsync(new(plan, (await setup.ReadAsync()).Revision,
                "Operator", "Synthetic second run", TimingRulesTests.At))).Revisions.Single(x => x.Plan.RunNumber == 2).Id;
        }
        await using var session = await new SqliteSeriesFileStore().OpenAsync(path);
        var store = new UncertainStore((IAuxiliaryTimingStore)session, failSwitch: true);
        await using var auxiliary = new AuxiliaryTimingWorkspace(store, new AlgeDecoderFactory());
        var source = new PacketSource();
        await auxiliary.StartAsync(firstId, AuxiliaryTimingRole.B, source, Options("B"), "Operator");
        await source.SendAsync("0001 C0 12:00:00.0000\r");
        await TimingStorageTests.UntilAsync(() => auxiliary.State(AuxiliaryTimingRole.B).SavedPackets == 1);
        var switching = auxiliary.SwitchRunAsync(AuxiliaryTimingRole.B, secondId);
        await TimingStorageTests.UntilAsync(() => auxiliary.State(AuxiliaryTimingRole.B).Fault is not null);
        await source.SendAsync("0001 C0 13:00:00.0000\r");
        await TimingStorageTests.UntilAsync(() => auxiliary.State(AuxiliaryTimingRole.B).Pending == 1);
        Assert.Equal(firstId, auxiliary.State(AuxiliaryTimingRole.B).ListId);
        await Assert.ThrowsAsync<SeriesFileException>(() => auxiliary.StopAsync(AuxiliaryTimingRole.B));
        auxiliary.RetryStorage(AuxiliaryTimingRole.B);
        await switching.WaitAsync(TimeSpan.FromSeconds(5));
        await auxiliary.StopAsync(AuxiliaryTimingRole.B);
        var previous = await store.ReadAuxiliaryTimingAsync(firstId);
        var next = await store.ReadAuxiliaryTimingAsync(secondId);
        Assert.True(Assert.Single(previous.Sessions).Capture.CleanStop);
        Assert.True(Assert.Single(next.Sessions).Capture.CleanStop);
        Assert.Single(previous.Packets); Assert.Single(next.Packets);
        Assert.Equal(0, auxiliary.State(AuxiliaryTimingRole.B).Pending);
        Assert.Null(auxiliary.State(AuxiliaryTimingRole.B).Fault);
    }

    private static CaptureOptions Options(string endpoint) => new("Synthetic", endpoint, TimingRulesTests.Date, Simulation: true);
    private sealed class PacketSource : ITimingSource
    {
        private readonly Channel<byte[]> _input = Channel.CreateUnbounded<byte[]>();
        public ValueTask SendAsync(string text) => _input.Writer.WriteAsync(Encoding.ASCII.GetBytes(text));
        public async Task ReceiveAsync(Func<TransportPacket, ValueTask> receive, Action<string> status, CancellationToken ct)
        {
            status("Synthetic connected");
            using var stop = ct.Register(() => _input.Writer.TryComplete());
            await foreach (var bytes in _input.Reader.ReadAllAsync(CancellationToken.None))
            { await receive(new("alge-ascii/v1", "Synthetic", "1", bytes)); }
        }
        public ValueTask DisposeAsync() { _input.Writer.TryComplete(); return ValueTask.CompletedTask; }
    }
    private sealed class UncertainStore(IAuxiliaryTimingStore inner, bool failSwitch = false) : IAuxiliaryTimingStore
    {
        private bool _failed;
        public Task<AuxiliaryTimingData> ReadAuxiliaryTimingAsync(Guid listId, CancellationToken ct = default) => inner.ReadAuxiliaryTimingAsync(listId, ct);
        public Task<AuxiliaryCaptureSession> BeginAuxiliaryCaptureAsync(Guid listId, AuxiliaryTimingRole role, CaptureOptions options,
            string operatorName, DateTimeOffset at, bool live, CancellationToken ct = default)
            => inner.BeginAuxiliaryCaptureAsync(listId, role, options, operatorName, at, live, ct);
        public async Task AppendAuxiliaryRawAsync(RawTimingPacket packet, CancellationToken ct = default)
        {
            await inner.AppendAuxiliaryRawAsync(packet, ct);
            if (!failSwitch && !_failed) { _failed = true; throw new IOException("Injected uncertain commit"); }
        }
        public async Task<AuxiliaryCaptureSession> SwitchAuxiliaryCaptureAsync(Guid sessionId, Guid listId, DateTimeOffset at, CancellationToken ct = default)
        {
            var result = await inner.SwitchAuxiliaryCaptureAsync(sessionId, listId, at, ct);
            if (failSwitch && !_failed) { _failed = true; throw new IOException("Injected uncertain switch commit"); }
            return result;
        }
        public Task EndAuxiliaryCaptureAsync(Guid sessionId, DateTimeOffset at, CancellationToken ct = default)
            => inner.EndAuxiliaryCaptureAsync(sessionId, at, ct);
    }
    private sealed class Folder : IDisposable
    {
        private readonly string _path = Path.Combine(Path.GetTempPath(), "openskitime-aux-tests", Guid.NewGuid().ToString("N"));
        public Folder() => Directory.CreateDirectory(_path);
        public string PathFor(string name) => Path.Combine(_path, name);
        public void Dispose() => Directory.Delete(_path, true);
    }
}
