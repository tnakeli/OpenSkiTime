using Microsoft.Data.Sqlite;
using OpenSkiTime.Application;
using OpenSkiTime.Domain;
using OpenSkiTime.Persistence;
using Xunit;

namespace OpenSkiTime.Tests;

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
    public async Task NewDevelopmentFileContainsCurrentSchemaWithoutMigrationHistory()
    {
        using var folder = new TestFolder();
        var path = folder.PathFor("current.ost");
        await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore());
        await workspace.CreateAsync(path, s_series);
        await workspace.CloseAsync();
        await using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM sqlite_master WHERE name='__EFMigrationsHistory'";
        Assert.Equal(0L, await command.ExecuteScalarAsync());
        command.CommandText = "SELECT count(*) FROM pragma_table_info('Competitions') WHERE name='LocalRaceCode'";
        Assert.Equal(0L, await command.ExecuteScalarAsync());
        command.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='trigger' AND name IN ('RawTimingPackets_NoUpdate','RawTimingPackets_NoDelete','TimingAudit_NoUpdate','TimingAudit_NoDelete','ApprovedResults_NoUpdate','ApprovedResults_NoDelete','RaceInformation_NoUpdate','RaceInformation_NoDelete')";
        Assert.Equal(8L, await command.ExecuteScalarAsync());
    }

    [Theory]
    [InlineData("OpenSkiTime.New/1")]
    [InlineData("OpenSkiTime.Development/1")]
    [InlineData("OpenSkiTime.Development/2")]
    [InlineData("OpenSkiTime.Development/999")]
    public async Task IncompatibleDevelopmentFileIsRejectedWithoutChangingIt(string format)
    {
        using var folder = new TestFolder();
        var path = folder.PathFor("incompatible.ost");
        await using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE Series (FormatId TEXT NOT NULL); INSERT INTO Series (FormatId) VALUES ($format)";
            command.Parameters.AddWithValue("$format", format);
            await command.ExecuteNonQueryAsync();
        }
        var original = await File.ReadAllBytesAsync(path);
        await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore());
        var error = await Assert.ThrowsAsync<SeriesFileException>(() => workspace.OpenAsync(path));
        Assert.Contains("Create a new event series file", error.Message, StringComparison.Ordinal);
        Assert.Equal(original, await File.ReadAllBytesAsync(path));
        Assert.Single(Directory.GetFiles(folder.Root));
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
