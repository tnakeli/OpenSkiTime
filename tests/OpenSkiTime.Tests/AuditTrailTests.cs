using System.Globalization;
using Microsoft.Data.Sqlite;
using OpenSkiTime.Application;
using OpenSkiTime.Devices;
using OpenSkiTime.Persistence;
using Xunit;

namespace OpenSkiTime.Tests;

public sealed class AuditTrailTests
{
    [Fact]
    public async Task EntireHistoryPagesWithoutLossAtEqualTimestampsAndReadsWithoutChangingFile()
    {
        using var folder = new Folder();
        var path = folder.PathFor("history.ost");
        await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore());
        var list = await TimingStorageTests.SeedAsync(workspace, path, 1);
        await workspace.CloseAsync();
        var at = new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.FromHours(3)).AddTicks(1234567);
        await using (var sql = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            await sql.OpenAsync();
            using var transaction = sql.BeginTransaction();
            using var command = sql.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "INSERT INTO TimingAudit(ListId,At,Operator,Reason,BeforeJson,AfterJson) VALUES($list,$at,'Synthetic operator',$reason,'{}',$after)";
            command.Parameters.AddWithValue("$list", list.Id.ToString().ToUpperInvariant());
            command.Parameters.AddWithValue("$at", at.ToString("O", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$reason", "Synthetic correction");
            command.Parameters.AddWithValue("$after", "{\"Kind\":2,\"Bib\":1,\"Hundredths\":1234}");
            for (var i = 0; i < 651; i++)
            {
                if (i == 650)
                {
                    command.Parameters["$after"].Value = System.Text.Json.JsonSerializer.Serialize(new OpenSkiTime.Timing.TimingDecision(
                        OpenSkiTime.Timing.DecisionKind.Status, CompetitorId: list.Plan.Entries[0].Entrant.CompetitorId, Status: OpenSkiTime.Timing.TimingStatus.DNS));
                }
                await command.ExecuteNonQueryAsync();
            }
            command.CommandText = "UPDATE Competitors SET Surname='CURRENT'";
            await command.ExecuteNonQueryAsync();
            transaction.Commit();
        }
        var original = await File.ReadAllBytesAsync(path);
        File.SetAttributes(path, FileAttributes.ReadOnly);
        try
        {
            var reader = new SqliteAuditTrailReader();
            var all = new List<AuditRecord>(); AuditCursor? cursor = null;
            do
            {
                var page = await reader.ReadAsync(path, new(Source: "TimingAudit", CompetitionId: list.Plan.CompetitionId,
                    Run: 1, Search: "correction", Operator: "operator", Bib: 1, From: at.AddTicks(-1), Until: at.AddTicks(1), After: cursor, PageSize: 77));
                all.AddRange(page.Records); cursor = page.Next;
            } while (cursor is not null);
            Assert.Equal(651, all.Count);
            Assert.Equal(651, all.Select(r => r.Reference).Distinct().Count());
            Assert.All(all, row => Assert.Equal(at, row.At));
            Assert.All(all, row => Assert.Equal(list.Plan.Entries[0].Entrant.Athlete.Surname, row.Surname));
            var detail = await reader.ReadDetailAsync(path, all[0].Reference);
            Assert.Contains("1234", detail.Text, StringComparison.Ordinal);
            Assert.Contains(detail.Links, link => link.Reference.Source == "StartLists");
            var summary = await reader.ReadSummaryAsync(path);
            Assert.Equal(651, summary.Tables.Single(t => t.Source == "TimingAudit").Count);
            var state = await reader.ReadAsync(path, new(AuditArea.CurrentState, Source: "Competitors"));
            Assert.Equal("Current state", Assert.Single(state.Records).Provenance);
            Assert.Null(state.Records[0].At);
        }
        finally { File.SetAttributes(path, FileAttributes.Normal); }
        Assert.Equal(original, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task RawBytesRemainExactAndSnapshotLeaseRejectsActiveCaptureThenTransfersReplay()
    {
        using var folder = new Folder();
        var path = folder.PathFor("capture.ost");
        await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore());
        var list = await TimingStorageTests.SeedAsync(workspace, path, 1);
        await workspace.CloseAsync();
        await using var session = await new SqliteSeriesFileStore().OpenAsync(path);
        var store = (ITimingStore)session;
        var capture = await store.BeginCaptureAsync(list.Id, new("Simulator", "test", TimingRulesTests.Date, Simulation: true), "Synthetic operator", TimingRulesTests.At);
        byte[] bytes = [0, 255, 13, 10, 1];
        var packet = new RawTimingPacket(capture.Id, 1, TimingRulesTests.At, "timy", "test", "stream", bytes);
        await store.AppendRawAsync(packet);
        var reader = new SqliteAuditTrailReader(new AlgeDecoderFactory());
        var raw = Assert.Single((await reader.ReadAsync(path, new(AuditArea.RawTiming))).Records);
        var detail = await reader.ReadDetailAsync(path, raw.Reference);
        Assert.Contains(Convert.ToBase64String(bytes), detail.Text, StringComparison.Ordinal);
        Assert.Contains(Convert.ToHexString(bytes), detail.Text, StringComparison.Ordinal);
        Assert.Contains("Derived by replay", detail.Text, StringComparison.Ordinal);
        Assert.Contains(detail.Links, link => link.Reference.Source == "TimingCaptures");
        var backup = folder.PathFor("copy.ost");
        var error = await Assert.ThrowsAsync<SeriesFileException>(() => reader.CreateSupportSnapshotAsync(path, backup));
        Assert.Contains("Disconnect", error.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(backup));
        // Inspection and a failed export leave capture writable, including a duplicate original packet.
        await store.AppendRawAsync(packet with { Sequence = 2 });
        var precise = packet with { Sequence = 3, Protocol = "alge-ascii/v1", Bytes = System.Text.Encoding.ASCII.GetBytes(" 0001 C0 12:00:00.1234567\r") };
        await store.AppendRawAsync(precise);
        var preciseRow = (await reader.ReadAsync(path, new(AuditArea.RawTiming))).Records.Single(r => r.Summary.Contains("sequence 3", StringComparison.Ordinal));
        var preciseDetail = await reader.ReadDetailAsync(path, preciseRow.Reference);
        Assert.Contains("\"Precision\": 7", preciseDetail.Text, StringComparison.Ordinal);
        var expectedTicks = TimingRulesTests.Date.ToDateTime(new TimeOnly(12, 0)).Ticks + 1234567;
        Assert.Contains(expectedTicks.ToString(CultureInfo.InvariantCulture), preciseDetail.Text, StringComparison.Ordinal);
        await store.EndCaptureAsync(capture.Id, TimingRulesTests.At);
        await reader.CreateSupportSnapshotAsync(path, backup);
        await using var reopened = await new SqliteSeriesFileStore().OpenAsync(backup);
        var replay = await ((ITimingStore)reopened).ReadTimingAsync(list.Id);
        Assert.Equal(3, replay.Packets.Count);
        Assert.All(replay.Packets.Take(2), p => Assert.Equal(bytes, p.Bytes));
        Assert.Equal(precise.Bytes, replay.Packets[2].Bytes);
        Assert.All(replay.Sessions, s => Assert.True(s.CleanStop));
    }

    [Fact]
    public async Task IncompatibleFileAndCancelledInspectionLeaveSourceUnchanged()
    {
        using var folder = new Folder();
        var path = folder.PathFor("incompatible.ost");
        await using (var sql = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            await sql.OpenAsync(); using var command = sql.CreateCommand();
            command.CommandText = "CREATE TABLE Series(FormatId TEXT); INSERT INTO Series VALUES('unsupported')";
            await command.ExecuteNonQueryAsync();
        }
        var before = await File.ReadAllBytesAsync(path);
        var reader = new SqliteAuditTrailReader();
        await Assert.ThrowsAsync<SeriesFileException>(() => reader.ReadAsync(path, new()));
        Assert.Equal(before, await File.ReadAllBytesAsync(path));
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.ReadAsync(path, new(), cancel.Token));
        await Assert.ThrowsAsync<ArgumentException>(() => reader.ReadDetailAsync(path, new("Series; DELETE FROM Series", 1)));
    }

    private sealed class Folder : IDisposable
    {
        private readonly string _path = Path.Combine(Path.GetTempPath(), "ost-audit-tests", Guid.NewGuid().ToString("N"));
        public Folder() => Directory.CreateDirectory(_path);
        public string PathFor(string name) => Path.Combine(_path, name);
        public void Dispose() => Directory.Delete(_path, recursive: true);
    }

    [Fact]
    public async Task ReusedCurrentStateRowCannotSubstituteAnotherCompetitorInOldSelection()
    {
        using var folder = new Folder();
        var path = folder.PathFor("registration.ost");
        await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore());
        var series = await workspace.CreateAsync(path, new("Synthetic", "Test", "Test", TimingRulesTests.Date, TimingRulesTests.Date, "FIN", "2026/27"));
        var first = await workspace.SaveDeskRowAsync(null, new("FIRST", "Synthetic", 2000, null, "FIN", "Test", null), null, false, null, series.Revision);
        var reader = new SqliteAuditTrailReader();
        var oldSelection = Assert.Single((await reader.ReadAsync(path, new(AuditArea.CurrentState, "Competitors"))).Records).Reference;
        var revision = await workspace.RemoveCompetitorAsync(first.Value.Id, first.Revision);
        await workspace.SaveDeskRowAsync(null, new("SECOND", "Synthetic", 2000, null, "FIN", "Test", null), null, false, null, revision);
        var newSelection = Assert.Single((await reader.ReadAsync(path, new(AuditArea.CurrentState, "Competitors"))).Records).Reference;
        Assert.Equal(oldSelection.RowId, newSelection.RowId);
        Assert.NotEqual(oldSelection.StoredKey, newSelection.StoredKey);
        var detail = await reader.ReadDetailAsync(path, oldSelection);
        Assert.Contains("no longer available", detail.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("SECOND", detail.Text, StringComparison.Ordinal);
    }
}
