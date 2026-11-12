using Microsoft.Data.Sqlite;
using OpenSkiTime.Application;
using OpenSkiTime.Domain;
using OpenSkiTime.Persistence;
using Xunit;

namespace OpenSkiTime.Tests;

// Schema versions and upgrades of event series files (SeriesMigrations, SqliteSeriesFileStore.MigrateAsync).
public sealed class SeriesMigrationTests : IDisposable
{
    private const string Probe = "CREATE TABLE MigrationProbe (Id INTEGER PRIMARY KEY NOT NULL); ALTER TABLE Competitions ADD COLUMN Probe TEXT NULL;";
    private static readonly SeriesValues s_series = new("Levi Weekend", "Levi", "Test Club", new DateOnly(2026, 1, 10), new DateOnly(2026, 1, 11), "FIN", "2025/26");
    private static readonly CompetitionValues s_competition = new("Slalom", "3.1 SL", new DateOnly(2026, 1, 10), Discipline.Slalom, RaceType.Club, 2, 1);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "openskitime-m1-tests", Guid.NewGuid().ToString("N"));

    public SeriesMigrationTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, recursive: true);

    private async Task<string> CreateAsync(string name)
    {
        var path = Path.Combine(_root, name);
        await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore());
        var created = await workspace.CreateAsync(path, s_series);
        await workspace.SaveCompetitionAsync(null, s_competition, created.Revision);
        return path;
    }

    private static async Task<object?> ScalarAsync(string path, string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }

    // The schema a file has after the probe migration, taken from a migrated copy of a fresh file.
    private async Task<string[]> ProbeSchemaAsync()
    {
        var copy = await CreateAsync("probe-schema.ost");
        await ScalarAsync(copy, Probe);
        await using var connection = new SqliteConnection($"Data Source={copy};Pooling=False");
        await connection.OpenAsync();
        return await SqliteSeriesFileStore.SchemaAsync(connection, CancellationToken.None);
    }

    private string[] Backups() => Directory.GetFiles(_root, "*-backup-*.ost");

    [Fact]
    public async Task NewFilesRecordTheCurrentVersionAndBaselineFilesOpenUnchanged()
    {
        var path = await CreateAsync("current.ost");
        Assert.Equal((long)SeriesMigrations.Current, await ScalarAsync(path, "PRAGMA user_version"));
        // Files saved before versions were recorded (user_version 0) are the release baseline and open without an upgrade.
        await ScalarAsync(path, "PRAGMA user_version = 0");
        await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore());
        Assert.Equal("Levi Weekend", (await workspace.OpenAsync(path)).Values.Name);
        await workspace.CloseAsync();
        Assert.Equal(0L, await ScalarAsync(path, "PRAGMA user_version"));
        Assert.Empty(Backups());
    }

    [Fact]
    public async Task FileFromANewerVersionIsRejectedWithoutChangingIt()
    {
        var path = await CreateAsync("newer.ost");
        await ScalarAsync(path, $"PRAGMA user_version = {SeriesMigrations.Current + 1}");
        var original = await File.ReadAllBytesAsync(path);
        await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore());
        var error = await Assert.ThrowsAsync<SeriesFileException>(() => workspace.OpenAsync(path));
        Assert.Contains("newer version", error.Message, StringComparison.Ordinal);
        Assert.Equal(original, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task UpgradeKeepsTheDataAndLeavesABackupOfTheOriginal()
    {
        var expected = await ProbeSchemaAsync();
        var path = await CreateAsync("upgrade.ost");
        var original = await File.ReadAllBytesAsync(path);
        var backup = await SqliteSeriesFileStore.MigrateAsync(path, 1, [new(1, "Probe table and column", Probe)], 2, [expected], CancellationToken.None);
        Assert.Equal(2L, await ScalarAsync(path, "PRAGMA user_version"));
        Assert.Equal("Slalom", await ScalarAsync(path, "SELECT Name FROM Competitions"));
        Assert.Equal(0L, await ScalarAsync(path, "SELECT count(*) FROM MigrationProbe"));
        // The backup is a complete copy of the file as it was, and opens with this version.
        Assert.Equal(Path.Combine(_root, Assert.Single(Backups())), backup);
        Assert.StartsWith(Path.Combine(_root, "upgrade.v1-backup-"), backup, StringComparison.Ordinal);
        await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore());
        Assert.Equal("Slalom", Assert.Single((await workspace.OpenAsync(backup)).Competitions).Values.Name);
        Assert.NotEqual(original, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task FailedUpgradeChangesNothingAndLeavesNoBackup()
    {
        var expected = await ProbeSchemaAsync();
        var path = await CreateAsync("tampered.ost");
        await ScalarAsync(path, "CREATE TRIGGER Tamper AFTER INSERT ON RawTimingPackets BEGIN SELECT 1; END");
        var original = await File.ReadAllBytesAsync(path);
        var error = await Assert.ThrowsAsync<SeriesFileException>(() =>
            SqliteSeriesFileStore.MigrateAsync(path, 1, [new(1, "Probe table and column", Probe)], 2, [expected], CancellationToken.None));
        Assert.Contains("unexpected database structure", error.Message, StringComparison.Ordinal);
        Assert.Equal(original, await File.ReadAllBytesAsync(path));
        Assert.Equal(1L, await ScalarAsync(path, "PRAGMA user_version"));
        Assert.Empty(Backups());
    }

    [Fact]
    public async Task MissingUpgradeStepIsRejectedWithoutChangingTheFile()
    {
        var path = await CreateAsync("gap.ost");
        var original = await File.ReadAllBytesAsync(path);
        await Assert.ThrowsAsync<SeriesFileException>(() =>
            SqliteSeriesFileStore.MigrateAsync(path, 1, [new(2, "Second step only", Probe)], 3, [], CancellationToken.None));
        Assert.Equal(original, await File.ReadAllBytesAsync(path));
        Assert.Empty(Backups());
    }
}
