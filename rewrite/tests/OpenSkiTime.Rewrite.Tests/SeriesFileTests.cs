using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using OpenSkiTime.Rewrite.Application;
using OpenSkiTime.Rewrite.Domain;
using OpenSkiTime.Rewrite.Persistence;
using Xunit;

namespace OpenSkiTime.Rewrite.Tests;

public class SeriesFileTests
{
    private static readonly SeriesValues s_series = new(
        "Levi Weekend", "Levi", "Test Club", new DateOnly(2026, 1, 10),
        new DateOnly(2026, 1, 11), "FIN", "2025/26");

    private static readonly CompetitionValues s_competition = new(
        "Slalom", "3.1 SL", new DateOnly(2026, 1, 10), Discipline.Slalom,
        RaceType.Club, 2, 1, CourseName: "Front slope", StartAltitudeMeters: 800,
        FinishAltitudeMeters: 600, VerticalDropMeters: 200);

    [Fact]
    public async Task OneSeriesPerFileReopensAndTransfersWithItsCompetitions()
    {
        using var folder = new TestFolder();
        var source = folder.PathFor("Levi.ost");
        var transfer = folder.PathFor("other-machine", "Levi-copy.ost");
        Directory.CreateDirectory(Path.GetDirectoryName(transfer)!);
        await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore());

        var created = await workspace.CreateAsync(source, s_series);
        var edited = await workspace.SaveSeriesAsync(s_series with { Name = "Levi Edited" }, created.Revision);
        var withCompetition = await workspace.SaveCompetitionAsync(null, s_competition, edited.Revision);
        var comp = Assert.Single(withCompetition.Competitions);
        var changed = await workspace.SaveCompetitionAsync(comp.Id,
            s_competition with { Name = "Edited Slalom" }, withCompetition.Revision);
        Assert.Equal("Edited Slalom", Assert.Single(changed.Competitions).Values.Name);

        await workspace.BackupAsync(transfer);
        Assert.True(File.Exists(transfer));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(transfer)!));
        await workspace.CloseAsync();
        Assert.False(workspace.IsOpen);
        var fromTransfer = await workspace.OpenAsync(transfer);
        Assert.Equal(created.Id, fromTransfer.Id);
        Assert.Equal("Levi Edited", fromTransfer.Values.Name);
        Assert.Equal(comp.Id, Assert.Single(fromTransfer.Competitions).Id);
        Assert.Equal("Front slope", fromTransfer.Competitions[0].Values.CourseName);

        var other = folder.PathFor("Other.ost");
        var otherSeries = await workspace.CreateAsync(other, s_series with { Name = "Other Weekend" });
        Assert.NotEqual(created.Id, otherSeries.Id);
        Assert.Empty(otherSeries.Competitions);
        var reopenedSource = await workspace.OpenAsync(source);
        Assert.Equal(created.Id, reopenedSource.Id);
        Assert.Equal("Edited Slalom", Assert.Single(reopenedSource.Competitions).Values.Name);
    }

    [Fact]
    public async Task OlderSchemaIsBackedUpBeforeRealMigrationAndUnknownSchemaIsRejected()
    {
        using var folder = new TestFolder();
        var path = folder.PathFor("older.ost");
        var options = new DbContextOptionsBuilder<SeriesDbContext>()
            .UseSqlite($"Data Source={path};Pooling=False").Options;
        var seriesId = Guid.NewGuid();
        await using (var db = new SeriesDbContext(options))
        {
            await db.GetService<IMigrator>().MigrateAsync("20260926121328_InitialSeries");
        }

        await using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var insert = connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO Series (SingleRow, FormatId, Id, Name, Location, Organizer,
                  StartDate, EndDate, Nation, Season, Revision)
                VALUES (1, 'OpenSkiTime.New/1', $id, 'Old name', 'Levi', 'Test Club',
                  '2026-01-10', '2026-01-11', 'FIN', '2025/26', 1)
                """;
            insert.Parameters.AddWithValue("$id", seriesId.ToString().ToUpperInvariant());
            await insert.ExecuteNonQueryAsync();
        }

        await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore());
        var upgraded = await workspace.OpenAsync(path);
        Assert.Equal(seriesId, upgraded.Id);
        Assert.Equal("Old name", upgraded.Values.Name);
        Assert.Empty(upgraded.Competitions);
        var backup = Assert.Single(Directory.GetFiles(folder.Root, "*.before-upgrade-*.ost"));
        await using (var backupDb = new SeriesDbContext(new DbContextOptionsBuilder<SeriesDbContext>()
            .UseSqlite($"Data Source={backup};Pooling=False").Options))
        {
            Assert.Single(await backupDb.Database.GetAppliedMigrationsAsync());
        }

        var afterEdit = await workspace.SaveCompetitionAsync(null, s_competition, upgraded.Revision);
        Assert.Single(afterEdit.Competitions);
        await workspace.CloseAsync();
        await using (var db = new SeriesDbContext(options))
        {
            Assert.Equal(2, (await db.Database.GetAppliedMigrationsAsync()).Count());
        }

        await using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var insert = connection.CreateCommand();
            insert.CommandText = "INSERT INTO __EFMigrationsHistory (MigrationId, ProductVersion) VALUES ('99999999999999_Unknown', '99.0')";
            await insert.ExecuteNonQueryAsync();
        }

        await Assert.ThrowsAsync<SeriesFileException>(() => workspace.OpenAsync(path));
        Assert.False(workspace.IsOpen);
    }

    [Fact]
    public async Task RejectedEditsAndFailedOpenLeaveExistingFileUnchanged()
    {
        using var folder = new TestFolder();
        await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore());
        var file = folder.PathFor("protected.ost");
        var created = await workspace.CreateAsync(file, s_series);
        var first = await workspace.SaveCompetitionAsync(null, s_competition, created.Revision);
        await Assert.ThrowsAsync<SeriesConflictException>(() =>
            workspace.SaveSeriesAsync(s_series with { Name = "Stale" }, created.Revision));
        await Assert.ThrowsAsync<SeriesFileException>(() =>
            workspace.SaveCompetitionAsync(null, s_competition with { ShortLabel = "3.1 sl" }, first.Revision));
        Assert.Equal("Levi Weekend", (await workspace.ReadAsync()).Values.Name);
        Assert.Single((await workspace.ReadAsync()).Competitions);

        var notSeries = folder.PathFor("not-series.ost");
        await File.WriteAllTextAsync(notSeries, "not a series");
        await Assert.ThrowsAsync<SeriesFileException>(() => workspace.OpenAsync(notSeries));
        Assert.Equal(file, workspace.FilePath);
        await Assert.ThrowsAsync<SeriesFileException>(() => workspace.BackupAsync(file));
        Assert.True(File.Exists(file));
    }

    private sealed class TestFolder : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "openskitime-m1-tests", Guid.NewGuid().ToString("N"));
        public TestFolder() => Directory.CreateDirectory(Root);
        public string PathFor(params string[] parts) => Path.Combine([Root, .. parts]);

        public void Dispose()
        {
            var full = Path.GetFullPath(Root);
            var parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "openskitime-m1-tests"));
            if (!full.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Unexpected test directory.");
            }

            Directory.Delete(full, recursive: true);
        }
    }
}
