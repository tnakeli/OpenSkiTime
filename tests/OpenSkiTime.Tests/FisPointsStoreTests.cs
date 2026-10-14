using Microsoft.Data.Sqlite;
using OpenSkiTime.Application;
using OpenSkiTime.Domain;
using OpenSkiTime.Persistence;
using Xunit;

namespace OpenSkiTime.Tests;

// FIS points saved with the series are part of the series file, written in the same transaction as the competitor save.
public class FisPointsStoreTests
{
    private static readonly SeriesValues s_series = new("Synthetic Cup", "Synthetic Fell", "Synthetic Club",
        new DateOnly(2026, 11, 19), new DateOnly(2026, 11, 20), "FIN", "2026/27");
    private static readonly CompetitorValues s_athlete = new("Synthskier", "Aino", 2008, "990001", "FIN", "Club", Gender.Female);
    private static readonly FisPointsListInfo s_list = new("1327", "13th synthetic list", new(2026, 9, 24), new(2026, 9, 30));

    private static long SavedRowCount(string file)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = file, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT (SELECT count(*) FROM CompetitorFisPoints) + (SELECT count(*) FROM FisPointsLists)";
        return (long)command.ExecuteScalar()!;
    }

    [Fact]
    public async Task PointsAreSavedAtomicallyAndRemovedWithTheCompetitor()
    {
        var folder = Path.Combine(Path.GetTempPath(), "openskitime-fis-points", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var file = Path.Combine(folder, "series.ost");
            await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore());
            var series = await workspace.CreateAsync(file, s_series);
            var added = await workspace.SaveDeskRowAsync(null, s_athlete, null, false, null, series.Revision);

            // A new file has the points tables but no saved points.
            var desk = await workspace.ReadCompetitorDeskAsync();
            Assert.Null(desk.FisPointsOf(added.Value.Id));
            Assert.Equal(0, SavedRowCount(file));

            // Invalid points or an unknown list save nothing: neither the competitor change nor any points.
            var points = new CompetitorFisPoints("1327", null, null, 41.86m, 55.10m, null);
            await Assert.ThrowsAsync<DomainValidationException>(() => workspace.ApplyDeskBatchAsync(new DeskBatch(series.Id, added.Revision,
                [new DeskBatchRow(added.Value.Id, s_athlete with { Club = "Changed" }, [], points, true)], [])));
            await Assert.ThrowsAsync<DomainValidationException>(() => workspace.ApplyDeskBatchAsync(new DeskBatch(series.Id, added.Revision,
                [new DeskBatchRow(added.Value.Id, s_athlete, [], points with { Sl = 41.861m }, true)], [], [s_list])));
            desk = await workspace.ReadCompetitorDeskAsync();
            Assert.Equal(added.Revision, desk.Revision);
            Assert.Equal("Club", Assert.Single(desk.Competitors).Values.Club);
            Assert.Equal(0, SavedRowCount(file));

            var saved = await workspace.ApplyDeskBatchAsync(new DeskBatch(series.Id, added.Revision,
                [new DeskBatchRow(added.Value.Id, s_athlete, [], points, true)], [], [s_list]));
            desk = await workspace.ReadCompetitorDeskAsync();
            Assert.Equal(points, desk.FisPointsOf(added.Value.Id));
            Assert.Equal(s_list, desk.PointsList("1327"));
            Assert.Equal(2, SavedRowCount(file));

            // A points-only save of another list's points needs that list; the earlier list stays known.
            await Assert.ThrowsAsync<DomainValidationException>(() => workspace.ApplyDeskBatchAsync(new DeskBatch(series.Id, saved.Revision,
                [new DeskBatchRow(added.Value.Id, s_athlete, [], points with { ListCode = "1401" }, true)], [])));
            Assert.Equal(points, (await workspace.ReadCompetitorDeskAsync()).FisPointsOf(added.Value.Id));

            // Reopening keeps the points; deleting the competitor removes them.
            await workspace.CloseAsync();
            await workspace.OpenAsync(file);
            desk = await workspace.ReadCompetitorDeskAsync();
            Assert.Equal(points, desk.FisPointsOf(added.Value.Id));
            await workspace.ApplyDeskBatchAsync(new DeskBatch(series.Id, desk.Revision, [], [added.Value.Id]));
            desk = await workspace.ReadCompetitorDeskAsync();
            Assert.Empty(desk.Competitors);
            Assert.Empty(desk.FisPoints!);
            Assert.Equal(s_list, desk.PointsList("1327"));
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(folder, recursive: true); }
    }

    // Unreadable saved points are reported as a file error the operator can act on, never an unhandled exception.
    [Fact]
    public async Task MalformedSavedPointsAreReportedAsAFileError()
    {
        var folder = Path.Combine(Path.GetTempPath(), "openskitime-fis-points", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var file = Path.Combine(folder, "series.ost");
            await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore());
            var series = await workspace.CreateAsync(file, s_series);
            var added = await workspace.SaveDeskRowAsync(null, s_athlete, null, false, null, series.Revision);
            await workspace.CloseAsync();
            await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = file, Pooling = false }.ToString()))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "INSERT INTO CompetitorFisPoints (CompetitorId, Json) VALUES ($id, '{not json')";
                command.Parameters.AddWithValue("$id", added.Value.Id.ToString().ToUpperInvariant());
                await command.ExecuteNonQueryAsync();
            }
            await workspace.OpenAsync(file);
            var error = await Assert.ThrowsAsync<SeriesFileException>(() => workspace.ReadCompetitorDeskAsync());
            Assert.Contains("FIS points are unreadable", error.Message, StringComparison.Ordinal);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(folder, recursive: true); }
    }

    // Saved points belong to the athlete of the FIS code: a code change without new points clears them on every save path.
    [Fact]
    public async Task ChangingTheFisCodeWithoutNewPointsClearsTheSavedPoints()
    {
        var folder = Path.Combine(Path.GetTempPath(), "openskitime-fis-points", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var file = Path.Combine(folder, "series.ost");
            await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore());
            var series = await workspace.CreateAsync(file, s_series);
            var added = await workspace.SaveDeskRowAsync(null, s_athlete, null, false, null, series.Revision);
            var id = added.Value.Id;
            var points = new CompetitorFisPoints("1327", null, null, 41.86m, 55.10m, null);
            async Task<long> SavePointsAsync(CompetitorValues values)
            {
                var desk = await workspace.ReadCompetitorDeskAsync();
                return (await workspace.ApplyDeskBatchAsync(new DeskBatch(series.Id, desk.Revision, [new DeskBatchRow(id, values, [], points, true)], [], [s_list]))).Revision;
            }

            // Other changes keep the points.
            var revision = await SavePointsAsync(s_athlete);
            revision = (await workspace.ApplyDeskBatchAsync(new DeskBatch(series.Id, revision, [new DeskBatchRow(id, s_athlete with { Club = "Other" }, [])], []))).Revision;
            Assert.Equal(points, (await workspace.ReadCompetitorDeskAsync()).FisPointsOf(id));
            // Batch save with a new code and no points.
            await workspace.ApplyDeskBatchAsync(new DeskBatch(series.Id, revision, [new DeskBatchRow(id, s_athlete with { FederationCode = "990002" }, [])], []));
            Assert.Null((await workspace.ReadCompetitorDeskAsync()).FisPointsOf(id));
            // Single-row save with a new code.
            revision = await SavePointsAsync(s_athlete with { FederationCode = "990002" });
            await workspace.SaveDeskRowAsync(id, s_athlete with { FederationCode = "990003" }, null, false, null, revision);
            Assert.Null((await workspace.ReadCompetitorDeskAsync()).FisPointsOf(id));
            // A code change that brings its own points keeps those points.
            await SavePointsAsync(s_athlete);
            Assert.Equal(points, (await workspace.ReadCompetitorDeskAsync()).FisPointsOf(id));
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(folder, recursive: true); }
    }
}
