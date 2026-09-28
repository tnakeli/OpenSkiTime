using Microsoft.Data.Sqlite;
using OpenSkiTime.Rewrite.Application;
using OpenSkiTime.Rewrite.Devices;
using OpenSkiTime.Rewrite.Domain;
using OpenSkiTime.Rewrite.Persistence;
using OpenSkiTime.Rewrite.Timing;
using Xunit;

namespace OpenSkiTime.Rewrite.Tests;

public sealed class TimestampTransferTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FinishTransferReturnsPreviousRacerToCourseAndPreservesAllOriginalInput(bool replaceExisting)
    {
        using var folder = new TestFolder();
        await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore(), new AlgeDecoderFactory());
        var list = await TimingStorageTests.SeedAsync(workspace, folder.File, 2);
        var timing = workspace.Timing!;
        await timing.SelectRunAsync(list.Id);
        await timing.FollowStartOrderAsync(true);
        var source = new SimulatorTimingSource();
        await timing.StartAsync(source, new("Simulator", "Test", TimingRulesTests.Date, Simulation: true), "Operator");
        var a = list.Plan.Entries[0].Bib; var b = list.Plan.Entries[1].Bib;
        await Pulse(timing, source, 0, 0);
        await Pulse(timing, source, 0, 5);
        await Pulse(timing, source, 1, 40);
        var finish = timing.Snapshot!.Results.Single(x => x.Bib == a).FinishKey!;
        if (replaceExisting) { await Pulse(timing, source, 1, 45); }
        var before = await workspace.ReadTimingAsync(list.Id);
        var target = TimingEngine.CurrentDecision(new(DecisionKind.Assignment, finish), timing.Snapshot!.Audit);
        var keys = timing.Snapshot.Observations.Where(x => x.Bib == b).Select(x => x.Observation.Key).ToArray();
        await Assert.ThrowsAsync<DomainValidationException>(() => timing.MoveTimestampAsync(Guid.NewGuid(), b, finish, target, keys, "Operator"));
        await Assert.ThrowsAsync<DomainValidationException>(() => timing.MoveTimestampAsync(list.Id, b, finish, target with { Bib = null }, keys, "Operator"));
        if (replaceExisting)
        { await Assert.ThrowsAsync<DomainValidationException>(() => timing.MoveTimestampAsync(list.Id, b, finish, target, [], "Operator")); }
        Assert.Equal(before.Audit.Count, (await workspace.ReadTimingAsync(list.Id)).Audit.Count);
        await timing.MoveTimestampAsync(list.Id, b, finish, target, keys, "Operator");
        Assert.Equal(TimingStatus.OnCourse, timing.Snapshot!.Results.Single(x => x.Bib == a).Status);
        Assert.Equal(a, timing.ArmedFinish);
        Assert.Equal(3500, timing.Snapshot.Results.Single(x => x.Bib == b).Hundredths);
        var saved = await workspace.ReadTimingAsync(list.Id);
        Assert.Equal(before.Audit.Count + (replaceExisting ? 2 : 1), saved.Audit.Count);
        Assert.Equal(before.Packets.Select(x => Convert.ToHexString(x.Bytes)), saved.Packets.Select(x => Convert.ToHexString(x.Bytes)));
        Assert.All(saved.Audit.Skip(before.Audit.Count), x => Assert.Equal("Operator", x.Operator));
        Assert.Equal(replaceExisting ? 1 : 0, timing.Snapshot.Observations.Count(x => x.State == "Unassigned"));
        await Pulse(timing, source, 1, 50);
        Assert.Equal(5000, timing.Snapshot!.Results.Single(x => x.Bib == a).Hundredths);
        await timing.StopAsync(); await workspace.CloseAsync(); await workspace.OpenAsync(folder.File);
        await workspace.Timing!.SelectRunAsync(list.Id);
        Assert.Equal(3500, workspace.Timing.Snapshot!.Results.Single(x => x.Bib == b).Hundredths);
        Assert.Equal(5000, workspace.Timing.Snapshot.Results.Single(x => x.Bib == a).Hundredths);
    }

    [Fact]
    public async Task TimestampReplacementRollsBackBothAssignmentsWhenSecondAuditInsertFails()
    {
        using var folder = new TestFolder();
        await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore(), new AlgeDecoderFactory());
        var list = await TimingStorageTests.SeedAsync(workspace, folder.File, 2);
        var timing = workspace.Timing!;
        await timing.SelectRunAsync(list.Id); await timing.FollowStartOrderAsync(true);
        var source = new SimulatorTimingSource();
        await timing.StartAsync(source, new("Simulator", "Test", TimingRulesTests.Date, Simulation: true), "Operator");
        await Pulse(timing, source, 0, 0); await Pulse(timing, source, 0, 5);
        await Pulse(timing, source, 1, 40); await Pulse(timing, source, 1, 45);
        await timing.StopAsync();
        var snapshot = timing.Snapshot!;
        var a = snapshot.Results[0]; var b = snapshot.Results[1];
        var target = TimingEngine.CurrentDecision(new(DecisionKind.Assignment, a.FinishKey), snapshot.Audit);
        var sourceKeys = snapshot.Observations.Where(x => x.Bib == b.Bib).Select(x => x.Observation.Key).ToArray();
        await using (var sql = new SqliteConnection($"Data Source={folder.File};Pooling=False"))
        {
            await sql.OpenAsync();
            using var command = sql.CreateCommand();
            // The first insert releases B's old finish; fail the subsequent attribution insert.
            command.CommandText = "CREATE TRIGGER FailTransfer BEFORE INSERT ON TimingAudit WHEN NEW.Reason LIKE 'Drag timestamp%' AND json_extract(NEW.AfterJson, '$.Bib') IS NOT NULL BEGIN SELECT RAISE(ABORT, 'Injected second audit failure'); END";
            await command.ExecuteNonQueryAsync();
        }
        await Assert.ThrowsAsync<SeriesFileException>(() => timing.MoveTimestampAsync(list.Id, b.Bib, a.FinishKey!, target, sourceKeys, "Operator"));
        Assert.Equal(snapshot.AuditVersion, timing.Snapshot!.AuditVersion);
        Assert.Equal(snapshot.Audit, (await workspace.ReadTimingAsync(list.Id)).Audit);
        await workspace.CloseAsync(); await workspace.OpenAsync(folder.File); await workspace.Timing!.SelectRunAsync(list.Id);
        Assert.All(workspace.Timing.Snapshot!.Results, x => Assert.Equal(4000, x.Hundredths));
    }

    private static async Task Pulse(TimingWorkspace timing, SimulatorTimingSource source, int channel, int seconds)
    {
        var count = timing.Snapshot!.Observations.Count;
        await source.PulseAsync(channel, TimeSpan.FromHours(12).Ticks + seconds * TimeSpan.TicksPerSecond);
        await TimingStorageTests.UntilAsync(() => timing.Snapshot!.Observations.Count > count);
        await timing.FollowStartOrderAsync(true);
    }

    private sealed class TestFolder : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "openskitime-transfer-tests", Guid.NewGuid().ToString("N"));
        public string File => Path.Combine(_root, "synthetic.ost");
        public TestFolder() => Directory.CreateDirectory(_root);
        public void Dispose()
        {
            if (!Path.GetFullPath(_root).StartsWith(Path.Combine(Path.GetTempPath(), "openskitime-transfer-tests") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            { throw new InvalidOperationException("Unexpected test folder."); }
            Directory.Delete(_root, true);
        }
    }
}
