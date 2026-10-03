using System.Text;
using Microsoft.Data.Sqlite;
using OpenSkiTime.Application;
using OpenSkiTime.Devices;
using OpenSkiTime.Domain;
using OpenSkiTime.Persistence;
using OpenSkiTime.Timing;
using Xunit;

namespace OpenSkiTime.Tests;

public sealed class TimingStorageTests
{
    [Fact]
    public async Task PostRunClassificationsRetainOptionalDsqDetailsAndRestoreTimesThroughUndoAndReopen()
    {
        using var folder = new Folder();
        await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore(), new AlgeDecoderFactory());
        var file = folder.PathFor("review.ost");
        var list = await SeedAsync(workspace, file, 2);
        var timing = workspace.Timing!;
        await timing.SelectRunAsync(list.Id);
        foreach (var row in timing.Snapshot!.Results)
        { await timing.CorrectAsync(new(DecisionKind.Time, CompetitorId: row.CompetitorId, Hundredths: 6000 + row.Bib), "Timer", "Synthetic verified time"); }
        Assert.True(timing.Snapshot!.Complete);
        var originalAudit = timing.Snapshot.AuditVersion;
        await Assert.ThrowsAsync<DomainValidationException>(() => timing.CorrectStatusesAsync([1, 2], TimingStatus.DSQ,
            "Operator", "Judges reviewed finish", new DisqualificationDetails(0, "629.3", "Test judge")));
        Assert.Equal(originalAudit, timing.Snapshot.AuditVersion);
        Assert.All(timing.Snapshot.Results, row => Assert.Equal(TimingStatus.Finished, row.Status));

        var details = new DisqualificationDetails(17, "629.3", "Test judge");
        await timing.CorrectStatusesAsync([1], TimingStatus.DSQ, "Operator", "Judges reviewed finish", details);
        var dsqAudit = timing.Snapshot.Audit[^1];
        Assert.Equal(details, timing.Snapshot.Results.Single(x => x.Bib == 1).Disqualification);
        Assert.Null(timing.Snapshot.Results.Single(x => x.Bib == 1).Hundredths);
        await timing.CorrectStatusesAsync([2], TimingStatus.DNF, "Operator", "Did not complete course");
        await workspace.OpenAsync(file);
        timing = workspace.Timing!;
        await timing.SelectRunAsync(list.Id);
        Assert.Equal(details, timing.Snapshot!.Results.Single(x => x.Bib == 1).Disqualification);
        Assert.Equal(TimingStatus.DNF, timing.Snapshot.Results.Single(x => x.Bib == 2).Status);
        Assert.Null(timing.Snapshot.Results.Single(x => x.Bib == 2).Disqualification);
        Assert.Equal("Operator", timing.Snapshot.Audit.Single(x => x.Id == dsqAudit.Id).Operator);
        await timing.UndoAsync(dsqAudit.Id, "Operator", "Jury withdrew disqualification");
        var restored = timing.Snapshot.Results.Single(x => x.Bib == 1);
        Assert.Equal(TimingStatus.Finished, restored.Status);
        Assert.Equal(6001, restored.Hundredths);
        Assert.Null(restored.Disqualification);
        Assert.Equal(dsqAudit.Id, timing.Snapshot.Audit[^1].ReversesId);
        await timing.CorrectStatusesAsync([1], TimingStatus.DSQ, "Operator", "Classification without optional details");
        Assert.Equal(TimingStatus.DSQ, timing.Snapshot.Results.Single(x => x.Bib == 1).Status);
        Assert.Null(timing.Snapshot.Results.Single(x => x.Bib == 1).Disqualification);
    }

    [Fact]
    public async Task CapturedBytesAssignmentsCorrectionsUndoAndTransferReplayExactly()
    {
        using var folder = new Folder();
        await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore(), new AlgeDecoderFactory());
        var list = await SeedAsync(workspace, folder.PathFor("race.ost"), 2);
        var timing = workspace.Timing!;
        await timing.SelectRunAsync(list.Id);
        var source = new SimulatorTimingSource();
        await timing.StartAsync(source, Options(), "Synthetic operator");
        await timing.ArmAsync(1, null);
        await source.PulseAsync(0, TimeSpan.FromHours(12).Ticks + 9999999);
        await UntilAsync(() => timing.Snapshot!.Results[0].Status == TimingStatus.OnCourse);
        await timing.ArmAsync(2, 1);
        await source.PulseAsync(0, TimeSpan.FromHours(12).Ticks + TimeSpan.FromSeconds(30).Ticks);
        await source.PulseAsync(1, TimeSpan.FromHours(12).Ticks + TimeSpan.FromSeconds(63).Ticks + 9999998);
        await UntilAsync(() => timing.Snapshot!.Results[0].Status == TimingStatus.Finished);
        await timing.ArmAsync(null, 2);
        await source.PulseAsync(1, TimeSpan.FromHours(12).Ticks + TimeSpan.FromSeconds(95).Ticks);
        await UntilAsync(() => timing.Snapshot!.Complete);
        await timing.StopAsync();
        Assert.Equal(6299, timing.Snapshot!.Results[0].Hundredths);
        Assert.Equal(6500, timing.Snapshot.Results[1].Hundredths);
        var before = await workspace.ReadTimingAsync(list.Id);
        Assert.Equal(4, before.Packets.Count);
        Assert.Contains("12:00:00.9999999", Encoding.ASCII.GetString(before.Packets.Single(x => x.Sequence == 1).Bytes), StringComparison.Ordinal);
        Assert.All(before.Sessions, x => Assert.True(x.CleanStop));
        Assert.All(before.Packets, x => Assert.EndsWith("\r", Encoding.ASCII.GetString(x.Bytes), StringComparison.Ordinal));
        var id = timing.Snapshot.Results[0].CompetitorId;
        await Assert.ThrowsAsync<DomainValidationException>(() => timing.CorrectAsync(new(DecisionKind.Time, CompetitorId: id, Hundredths: 6300), "Operator", ""));
        Assert.Equal(4, (await workspace.ReadTimingAsync(list.Id)).Audit.Count);
        await timing.CorrectAsync(new(DecisionKind.Time, CompetitorId: id, Hundredths: 6300), "Operator", "Verified backup time");
        var correction = timing.Snapshot.Audit[^1];
        await timing.UndoAsync(correction.Id, "Operator", "Restore main timing");
        Assert.Equal(6299, timing.Snapshot.Results[0].Hundredths);
        Assert.Equal(correction.Id, timing.Snapshot.Audit[^1].ReversesId);
        var backup = folder.PathFor("transfer.ost");
        await workspace.BackupAsync(backup);
        await workspace.OpenAsync(backup);
        await workspace.Timing!.SelectRunAsync(list.Id);
        var replay = workspace.Timing.Snapshot!;
        Assert.Equal(6299, replay.Results[0].Hundredths);
        Assert.Equal(6, replay.Audit.Count);
        Assert.True(replay.Complete);
        Assert.Contains(replay.Observations, x => x.Observation.DeviceTicks == TimingRulesTests.Date.ToDateTime(new TimeOnly(12, 0)).Ticks + 9999999);
        var after = await workspace.ReadTimingAsync(list.Id);
        Assert.Equal(before.Packets.Select(x => Convert.ToHexString(x.Bytes)), after.Packets.Select(x => Convert.ToHexString(x.Bytes)));
        await Assert.ThrowsAsync<DomainValidationException>(() => workspace.Timing.StartAsync(new SimulatorTimingSource(),
            Options() with { Simulation = false }, "Operator"));
        await using var sql = new SqliteConnection($"Data Source={backup};Pooling=False");
        await sql.OpenAsync();
        foreach (var statement in new[] { "UPDATE RawTimingPackets SET Bytes=X'00'", "DELETE FROM RawTimingPackets",
            "UPDATE TimingAudit SET Reason='overwrite'", "DELETE FROM TimingAudit" })
        {
            using var command = sql.CreateCommand(); command.CommandText = statement;
            await Assert.ThrowsAsync<SqliteException>(() => command.ExecuteNonQueryAsync());
        }
    }

    [Fact]
    public async Task DiskFailureKeepsPendingInputBlocksCloseAndRetryIsIdempotentAfterUncertainCommit()
    {
        using var folder = new Folder();
        var file = folder.PathFor("disk.ost");
        await using (var setup = new SeriesWorkspace(new SqliteSeriesFileStore())) { await SeedAsync(setup, file, 1); }
        await using var session = await new SqliteSeriesFileStore().OpenAsync(file);
        var list = Assert.Single((await session.ReadStartListsAsync((await session.ReadAsync()).Competitions[0].Id)).Revisions);
        var failing = new FailOnceStore((ITimingStore)session);
        await using var timing = new TimingWorkspace(failing, new AlgeDecoderFactory());
        await timing.SelectRunAsync(list.Id);
        var source = new SimulatorTimingSource();
        await timing.StartAsync(source, Options(), "Operator");
        await source.PulseAsync(0, TimeSpan.FromHours(12).Ticks, 1);
        await UntilAsync(() => timing.Fault is not null);
        Assert.Equal(1, timing.Pending);
        Assert.Equal(0, timing.SavedPackets);
        await Assert.ThrowsAsync<SeriesFileException>(() => timing.StopAsync());
        timing.RetryStorage();
        await timing.StopAsync();
        Assert.Null(timing.Fault);
        Assert.Equal(0, timing.Pending);
        Assert.Equal(1, timing.SavedPackets);
        var saved = await ((ITimingStore)session).ReadTimingAsync(list.Id);
        Assert.Single(saved.Packets);
        Assert.Single(saved.Audit);
        Assert.True(saved.Sessions[0].CleanStop);
    }

    [Fact]
    public async Task CaptureLeasePreventsSecondWriterAndClosingDrainsIntoOriginalFile()
    {
        using var folder = new Folder();
        var file = folder.PathFor("one.ost");
        await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore(), new AlgeDecoderFactory());
        var list = await SeedAsync(workspace, file, 1);
        await workspace.Timing!.SelectRunAsync(list.Id);
        var source = new BurstSource(100);
        await workspace.Timing.StartAsync(source, Options(), "Operator");
        await source.Delivered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        await using var other = await new SqliteSeriesFileStore().OpenAsync(file);
        var details = await other.ReadAsync();
        await Assert.ThrowsAsync<SeriesFileException>(() => other.SaveSeriesAsync(details.Values, details.Revision));
        await Assert.ThrowsAsync<SeriesFileException>(() => ((ITimingStore)other).BeginCaptureAsync(list.Id, Options(), "Other", TimingRulesTests.At));
        await workspace.CreateAsync(folder.PathFor("two.ost"), details.Values);
        Assert.Null(workspace.Timing!.ListId);
        Assert.Empty((await workspace.ReadAsync()).Competitions);
        var original = await ((ITimingStore)other).ReadTimingAsync(list.Id);
        Assert.Equal(100, original.Packets.Count);
        Assert.True(original.Sessions[0].CleanStop);
        Assert.All(original.Packets, x => Assert.Equal("garbage\r", Encoding.ASCII.GetString(x.Bytes)));
    }

    [Fact]
    public async Task ActualTimingBuildsRun2AndLaterCorrectionsInvalidateUnstartedOrder()
    {
        using var folder = new Folder();
        var path = folder.PathFor("next-run.ost");
        await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore(), new AlgeDecoderFactory());
        var first = await SeedAsync(workspace, path, 2);
        await workspace.Timing!.SelectRunAsync(first.Id);
        var source = new SimulatorTimingSource();
        await workspace.Timing.StartAsync(source, Options(), "Operator");
        Assert.Null(Assert.Single((await workspace.ReadStartListsAsync(first.Plan.CompetitionId)).Revisions).StartedAt);
        Assert.True(Assert.Single((await workspace.ReadStartListsAsync(first.Plan.CompetitionId)).Revisions).HasCapture);
        var beforeStartRevision = (await workspace.ReadAsync()).Revision;
        var redraw = await Assert.ThrowsAsync<DomainValidationException>(() => workspace.SaveStartListAsync(
            new(first.Plan, beforeStartRevision, "Operator", "Redraw", TimingRulesTests.At)));
        Assert.Contains("Timing capture has begun", redraw.Message, StringComparison.Ordinal);
        await source.PulseAsync(0, TimeSpan.FromHours(12).Ticks, 1);
        await UntilAsync(() => workspace.Timing.Snapshot!.Results.Any(x => x.Status == TimingStatus.OnCourse));
        Assert.NotNull(Assert.Single((await workspace.ReadStartListsAsync(first.Plan.CompetitionId)).Revisions).StartedAt);
        await source.PulseAsync(1, TimeSpan.FromHours(12).Ticks + TimeSpan.FromSeconds(60).Ticks, 1);
        await source.PulseAsync(0, TimeSpan.FromHours(12).Ticks + TimeSpan.FromSeconds(80).Ticks, 2);
        await source.PulseAsync(1, TimeSpan.FromHours(12).Ticks + TimeSpan.FromSeconds(145).Ticks, 2);
        await UntilAsync(() => workspace.Timing.Snapshot!.Complete);
        await workspace.Timing.StopAsync();
        var capture = await workspace.ReadTimingAsync(first.Id);
        var completed = TimingReplay.Restore(capture, new AlgeDecoderFactory());
        var plan = FisStartOrder.SecondRun(capture.List, completed.ToRunFinishes());
        Assert.Equal([2, 1], plan.Entries.Select(x => x.Bib));
        var revision = (await workspace.ReadAsync()).Revision;
        await Assert.ThrowsAsync<DomainValidationException>(() => workspace.SaveStartListAsync(new(plan, revision, "Operator", "Stale input", TimingRulesTests.At)));
        var desk = await workspace.SaveStartListAsync(new(plan, revision, "Operator", "Timing input", TimingRulesTests.At, TimingReplay.InputVersion(capture)));
        var second = desk.Revisions.Single(x => x.Plan.RunNumber == 2);
        Assert.NotNull(second.SourceTimingVersion);
        await workspace.Timing.CorrectAsync(new(DecisionKind.Time, CompetitorId: completed.Results[0].CompetitorId, Hundredths: 7000), "Operator", "Backup correction");
        await Assert.ThrowsAsync<DomainValidationException>(() => workspace.MarkRunStartedAsync(second.Id, desk.SeriesRevision, "Operator", TimingRulesTests.At));
        await workspace.Timing.SelectRunAsync(second.Id);
        await Assert.ThrowsAsync<DomainValidationException>(() => workspace.Timing.StartAsync(new SimulatorTimingSource(), Options(), "Operator"));
    }

    [Fact]
    public async Task UncleanCaptureIsVisibleAfterReopenAndReplay()
    {
        using var folder = new Folder();
        var path = folder.PathFor("replay.ost");
        StartListRevision list;
        await using (var seed = new SeriesWorkspace(new SqliteSeriesFileStore())) { list = await SeedAsync(seed, path, 1); }
        await using (var session = await new SqliteSeriesFileStore().OpenAsync(path))
        {
            var store = (ITimingStore)session;
            var capture = await store.BeginCaptureAsync(list.Id, Options(), "Operator", TimingRulesTests.At);
            await store.AppendRawAsync(TimingRulesTests.Packet(capture, 1, "partial"));
            await store.EndCaptureAsync(capture.Id, TimingRulesTests.At);
        }
        await using (var sql = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            await sql.OpenAsync();
            using var command = sql.CreateCommand();
            // Model abrupt process termination at the persisted boundary, with the original bytes unchanged.
            command.CommandText = "UPDATE TimingCaptures SET CleanStop=0, StoppedAt=NULL";
            await command.ExecuteNonQueryAsync();
        }
        await using var reopened = new SeriesWorkspace(new SqliteSeriesFileStore(), new AlgeDecoderFactory());
        await reopened.OpenAsync(path);
        await reopened.Timing!.SelectRunAsync(list.Id);
        Assert.Equal(2, reopened.Timing.Snapshot!.Unresolved); // incomplete line + interrupted session
        Assert.Equal("partial", Encoding.ASCII.GetString(Assert.Single((await reopened.ReadTimingAsync(list.Id)).Packets).Bytes));
    }

    internal static async Task<StartListRevision> SeedAsync(SeriesWorkspace workspace, string path, int count, int intermediates = 0)
    {
        var model = TimingRulesTests.List(count);
        model = model with { Plan = model.Plan with { Competition = model.Plan.Competition with { IntermediateCount = intermediates } } };
        var series = await workspace.CreateAsync(path, new("Synthetic timing", "Test", "Test", TimingRulesTests.Date, TimingRulesTests.Date, "FIN", "2026/27"));
        series = await workspace.SaveCompetitionAsync(null, model.Plan.Competition, series.Revision);
        var competition = series.Competitions[0].Id;
        var entrants = new List<DrawEntrant>();
        var revision = series.Revision;
        foreach (var entry in model.Plan.Entries)
        {
            var saved = await workspace.SaveDeskRowAsync(null, entry.Entrant.Athlete, competition, true, null, revision);
            revision = saved.Revision; entrants.Add(entry.Entrant with { CompetitorId = saved.Value.Id });
        }
        var plan = FisStartOrder.FirstRun(competition, model.Plan.Competition, Gender.Female, entrants, model.Plan.PointsList, new(), "test");
        return Assert.Single((await workspace.SaveStartListAsync(new(plan, revision, "Test operator", "Fixture", TimingRulesTests.At))).Revisions);
    }

    internal static async Task UntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (!condition()) { await Task.Delay(10, timeout.Token); }
    }
    private static CaptureOptions Options() => new("Test", "Synthetic", TimingRulesTests.Date, Simulation: true);

    private sealed class BurstSource(int count) : ITimingSource
    {
        public TaskCompletionSource Delivered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task ReceiveAsync(Func<TransportPacket, ValueTask> receive, Action<string> status, CancellationToken ct)
        {
            for (var i = 0; i < count; i++) { await receive(new("alge-ascii/v1", "Synthetic", "1", Encoding.ASCII.GetBytes("garbage\r"))); }
            Delivered.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FailOnceStore(ITimingStore inner) : ITimingStore
    {
        private bool _failed;
        public Task<TimingReplayData> ReadTimingAsync(Guid id, CancellationToken ct = default) => inner.ReadTimingAsync(id, ct);
        public Task<CaptureSession> BeginCaptureAsync(Guid id, CaptureOptions options, string who, DateTimeOffset at, CancellationToken ct = default) => inner.BeginCaptureAsync(id, options, who, at, ct);
        public async Task AppendRawAsync(RawTimingPacket packet, CancellationToken ct = default)
        {
            await inner.AppendRawAsync(packet, ct);
            if (!_failed) { _failed = true; throw new IOException("Injected uncertain disk commit"); }
        }
        public Task EndCaptureAsync(Guid id, DateTimeOffset at, CancellationToken ct = default) => inner.EndCaptureAsync(id, at, ct);
        public Task<TimingAudit> AppendTimingAuditAsync(Guid id, long version, TimingDecision before, TimingDecision after,
            string who, string why, DateTimeOffset at, long? undo = null, bool startsRun = false,
            CancellationToken ct = default) => inner.AppendTimingAuditAsync(id, version, before, after, who, why, at, undo, startsRun, ct);
    }

    private sealed class Folder : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "openskitime-m5-tests", Guid.NewGuid().ToString("N"));
        public Folder() => Directory.CreateDirectory(Root);
        public string PathFor(string file) => Path.Combine(Root, file);
        public void Dispose()
        {
            if (!Path.GetFullPath(Root).StartsWith(Path.Combine(Path.GetTempPath(), "openskitime-m5-tests") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            { throw new InvalidOperationException("Unexpected test folder."); }
            Directory.Delete(Root, true);
        }
    }
}
