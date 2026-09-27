using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using OpenSkiTime.Rewrite.Application;
using OpenSkiTime.Rewrite.Domain;

namespace OpenSkiTime.Rewrite.Persistence;

public sealed class SqliteSeriesFileStore : ISeriesFileStore
{
    public async Task<ISeriesFileSession> CreateAsync(string filePath, SeriesValues values, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(values);
        var fullPath = FullPath(filePath);
        var validated = values.Validated();
        var parent = Path.GetDirectoryName(fullPath)!;
        if (!Directory.Exists(parent))
        {
            throw new SeriesFileException("Choose an existing folder for the event series.");
        }

        if (File.Exists(fullPath))
        {
            throw new SeriesFileException("That file already exists. Choose another name or open it.");
        }

        var temporary = Path.Combine(parent, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var db = await NewContextAsync(temporary, SqliteOpenMode.ReadWriteCreate, ct))
            {
                await db.Database.MigrateAsync(ct);
                var row = new SeriesRow { Id = Guid.NewGuid(), Revision = 1 };
                Assign(row, validated);
                db.Series.Add(row);
                await db.SaveChangesAsync(ct);
            }

            File.Move(temporary, fullPath);
            return await OpenAsync(fullPath, ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException)
        {
            throw FileError(ex);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    public async Task<ISeriesFileSession> OpenAsync(string filePath, CancellationToken ct = default)
    {
        var fullPath = FullPath(filePath);
        if (!File.Exists(fullPath))
        {
            throw new SeriesFileException("The selected event series file does not exist.");
        }

        try
        {
            await VerifyFormatAsync(fullPath, ct);
            await using (var db = await NewContextAsync(fullPath, SqliteOpenMode.ReadWrite, ct))
            {
                var known = db.Database.GetMigrations().ToHashSet(StringComparer.Ordinal);
                var applied = (await db.Database.GetAppliedMigrationsAsync(ct)).ToHashSet(StringComparer.Ordinal);
                if (applied.Except(known).Any())
                {
                    throw new SeriesFileException("This file uses a newer or unknown schema. Open it with a compatible version.");
                }

                if (known.Except(applied).Any())
                {
                    var backupPath = BeforeUpgradePath(fullPath);
                    await BackupFileAsync(fullPath, backupPath, ct);
                    await db.Database.MigrateAsync(ct);
                }
            }

            var session = new SqliteSeriesFileSession(fullPath);
            await session.ReadAsync(ct);
            return session;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException)
        {
            throw FileError(ex);
        }
    }

    private static string FullPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new SeriesFileException("Choose an event series file.");
        }

        try { return Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new SeriesFileException("The selected file path is invalid.", ex);
        }
    }

    private static string BeforeUpgradePath(string path)
        => path + $".before-upgrade-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.ost";

    private static async Task VerifyFormatAsync(string path, CancellationToken ct)
    {
        await using var connection = new SqliteConnection(ConnectionString(path, SqliteOpenMode.ReadOnly));
        await connection.OpenAsync(ct);
        await using var tableCheck = connection.CreateCommand();
        tableCheck.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='Series'";
        if ((long)(await tableCheck.ExecuteScalarAsync(ct))! != 1)
        {
            throw new SeriesFileException("This is not a New OpenSkiTime event series file. Legacy files must be imported later.");
        }

        await using var marker = connection.CreateCommand();
        marker.CommandText = "SELECT count(*), min(FormatId), max(FormatId) FROM Series";
        await using var reader = await marker.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        if (reader.GetInt64(0) != 1 || reader.IsDBNull(1) || reader.IsDBNull(2)
            || reader.GetString(1) != SeriesDbContext.Format || reader.GetString(2) != SeriesDbContext.Format)
        {
            throw new SeriesFileException("The file has an invalid or unsupported event series format.");
        }

        await using var integrity = connection.CreateCommand();
        integrity.CommandText = "PRAGMA integrity_check";
        if (!string.Equals((string?)await integrity.ExecuteScalarAsync(ct), "ok", StringComparison.OrdinalIgnoreCase))
        {
            throw new SeriesFileException("The event series database failed its integrity check. Restore a verified backup.");
        }
    }

    internal static async Task<SeriesDbContext> NewContextAsync(string path, SqliteOpenMode mode, CancellationToken ct)
    {
        var options = new DbContextOptionsBuilder<SeriesDbContext>()
            .UseSqlite(ConnectionString(path, mode))
            .Options;
        var db = new SeriesDbContext(options);
        try
        {
            await db.Database.OpenConnectionAsync(ct);
            await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL", ct);
            await db.Database.ExecuteSqlRawAsync("PRAGMA synchronous=FULL", ct);
            await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys=ON", ct);
            return db;
        }
        catch
        {
            await db.DisposeAsync();
            throw;
        }
    }

    internal static string ConnectionString(string path, SqliteOpenMode mode)
        => new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = mode, ForeignKeys = true,
            Pooling = false, DefaultTimeout = 5,
        }.ToString();

    internal static async Task BackupFileAsync(string source, string destination, CancellationToken ct)
    {
        if (Path.GetFullPath(source).Equals(Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
        {
            throw new SeriesFileException("Choose a different file for the backup.");
        }

        if (File.Exists(destination))
        {
            throw new SeriesFileException("The backup destination already exists.");
        }

        var parent = Path.GetDirectoryName(Path.GetFullPath(destination))!;
        if (!Directory.Exists(parent))
        {
            throw new SeriesFileException("Choose an existing folder for the backup.");
        }

        var temporary = Path.Combine(parent, $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var from = new SqliteConnection(ConnectionString(source, SqliteOpenMode.ReadOnly)))
            await using (var to = new SqliteConnection(ConnectionString(temporary, SqliteOpenMode.ReadWriteCreate)))
            {
                await from.OpenAsync(ct);
                await to.OpenAsync(ct);
                from.BackupDatabase(to);
            }

            await using (var compact = new SqliteConnection(ConnectionString(temporary, SqliteOpenMode.ReadWrite)))
            {
                await compact.OpenAsync(ct);
                await using var command = compact.CreateCommand();
                command.CommandText = "PRAGMA journal_mode=DELETE";
                await command.ExecuteScalarAsync(ct);
            }

            await VerifyFormatAsync(temporary, ct);
            File.Move(temporary, destination);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    internal static SeriesFileException FileError(Exception ex) => ex switch
    {
        SqliteException { SqliteErrorCode: 5 or 6 } => new SeriesFileException("The database is busy or locked. Close the other writer and retry.", ex),
        SqliteException { SqliteErrorCode: 11 or 26 } => new SeriesFileException("The database is damaged or is not a SQLite file. Restore a verified backup.", ex),
        SqliteException { SqliteErrorCode: 8 or 10 or 13 } => new SeriesFileException("The database could not be written. Check permissions and free disk space.", ex),
        UnauthorizedAccessException => new SeriesFileException("Access to the selected file was denied.", ex),
        _ => new SeriesFileException("The event series file could not be read or saved.", ex),
    };

    internal static void Assign(SeriesRow row, SeriesValues values)
    {
        row.Name = values.Name;
        row.Location = values.Location;
        row.Organizer = values.Organizer;
        row.StartDate = values.StartDate;
        row.EndDate = values.EndDate;
        row.Nation = values.Nation;
        row.Season = values.Season;
    }

    internal static SeriesValues Values(SeriesRow row) => new(
        row.Name, row.Location, row.Organizer, row.StartDate, row.EndDate, row.Nation, row.Season);
}

internal sealed partial class SqliteSeriesFileSession(string filePath) : ISeriesFileSession
{
    private readonly SemaphoreSlim _write = new(1, 1);
    private bool _disposed;
    public string FilePath { get; } = filePath;

    public async Task<SeriesDetails> ReadAsync(CancellationToken ct = default)
    {
        CheckOpen();
        try
        {
            await using var db = await SqliteSeriesFileStore.NewContextAsync(FilePath, SqliteOpenMode.ReadWrite, ct);
            var series = await db.Series.AsNoTracking().SingleAsync(ct);
            var competitions = await db.Competitions.AsNoTracking()
                .OrderBy(x => x.Date).ThenBy(x => x.ShortLabel)
                .ToListAsync(ct);
            return new SeriesDetails(series.Id, SqliteSeriesFileStore.Values(series), series.Revision,
                competitions.Select(x => new CompetitionDetails(x.Id, Values(x))).ToArray());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException)
        {
            throw SqliteSeriesFileStore.FileError(ex);
        }
    }

    public Task<SeriesDetails> SaveSeriesAsync(SeriesValues values, long expectedRevision, CancellationToken ct = default)
        => WriteAsync(async db =>
        {
            var row = await LoadForWriteAsync(db, expectedRevision, ct);
            SqliteSeriesFileStore.Assign(row, values.Validated());
            row.Revision++;
        }, ct);

    public Task<SeriesDetails> SaveCompetitionAsync(Guid? id, CompetitionValues values, long expectedRevision, CancellationToken ct = default)
        => WriteAsync(async db =>
        {
            var validated = values.Validated();
            var series = await LoadForWriteAsync(db, expectedRevision, ct);
            CompetitionRow row;
            if (id is { } existingId)
            {
                row = await db.Competitions.SingleOrDefaultAsync(x => x.Id == existingId && x.SeriesId == series.Id, ct)
                    ?? throw new SeriesFileException("The competition no longer exists in this event series.");
                if (!Values(row).HasSameStartOrderRules(validated) && await db.Runs.AnyAsync(x => x.CompetitionId == existingId, ct))
                { throw new DomainValidationException("Discipline, race type, date and run count affect the saved starting order and cannot be changed after drawing. Names, codes and course details can be edited."); }
            }
            else
            {
                row = new CompetitionRow { Id = Guid.NewGuid(), SeriesId = series.Id };
                db.Competitions.Add(row);
            }

            Assign(row, validated);
            series.Revision++;
        }, ct);

    public Task<SeriesDetails> RemoveCompetitionAsync(Guid id, long expectedRevision, CancellationToken ct = default)
        => WriteAsync(async db =>
        {
            var series = await LoadForWriteAsync(db, expectedRevision, ct);
            var competition = await db.Competitions.SingleOrDefaultAsync(x => x.Id == id && x.SeriesId == series.Id, ct)
                ?? throw new SeriesFileException("The competition no longer exists in this event series.");
            if (await db.Runs.AnyAsync(x => x.CompetitionId == id, ct))
            { throw new DomainValidationException("This competition has start-list history and cannot be removed."); }
            db.Competitions.Remove(competition);
            series.Revision++;
        }, ct);

    private async Task<SeriesDetails> WriteAsync(Func<SeriesDbContext, Task> change, CancellationToken ct)
    {
        CheckOpen();
        await _write.WaitAsync(ct);
        try
        {
            await using var db = await SqliteSeriesFileStore.NewContextAsync(FilePath, SqliteOpenMode.ReadWrite, ct);
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            await change(db);
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateConcurrencyException) { throw new SeriesConflictException(); }
            catch (DbUpdateException ex) when (ex.InnerException is SqliteException { SqliteExtendedErrorCode: 2067 })
            {
                throw new SeriesFileException("A competition with that short label already exists in this series.", ex);
            }
            catch (DbUpdateException ex) when (ex.InnerException is SqliteException { SqliteErrorCode: 19 })
            {
                throw new SeriesFileException("The database rejected this change because it violates a data rule.", ex);
            }

            await transaction.CommitAsync(ct);
            return await ReadAsync(ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException)
        {
            throw SqliteSeriesFileStore.FileError(ex);
        }
        finally { _write.Release(); }
    }

    private static async Task<SeriesRow> LoadForWriteAsync(SeriesDbContext db, long expectedRevision, CancellationToken ct)
    {
        var row = await db.Series.SingleAsync(ct);
        if (row.Revision != expectedRevision)
        {
            throw new SeriesConflictException();
        }
        return row;
    }

    public async Task BackupAsync(string destinationPath, CancellationToken ct = default)
    {
        CheckOpen();
        await _write.WaitAsync(ct);
        try { await SqliteSeriesFileStore.BackupFileAsync(FilePath, destinationPath, ct); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException)
        {
            throw SqliteSeriesFileStore.FileError(ex);
        }
        finally { _write.Release(); }
    }

    private void CheckOpen()
    {
        if (_disposed)
        {
            throw new SeriesFileException("This event series is closed.");
        }
    }

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        _write.Dispose();
        return ValueTask.CompletedTask;
    }

    private static void Assign(CompetitionRow row, CompetitionValues values)
    {
        row.Name = values.Name;
        row.ShortLabel = values.ShortLabel;
        row.ShortLabelKey = values.ShortLabel.ToUpperInvariant();
        row.Date = values.Date;
        row.Discipline = values.Discipline;
        row.RaceType = values.RaceType;
        row.RunCount = values.RunCount;
        row.IntermediateCount = values.IntermediateCount;
        row.FisCode = values.FisCode;
        row.LocalRaceCode = values.LocalRaceCode;
        row.CourseName = values.CourseName;
        row.StartAltitudeMeters = values.StartAltitudeMeters;
        row.FinishAltitudeMeters = values.FinishAltitudeMeters;
        row.VerticalDropMeters = values.VerticalDropMeters;
        row.HomologationNumber = values.HomologationNumber;
    }

    private static CompetitionValues Values(CompetitionRow row) => new(
        row.Name, row.ShortLabel, row.Date, row.Discipline, row.RaceType,
        row.RunCount, row.IntermediateCount, row.FisCode, row.LocalRaceCode,
        row.CourseName, row.StartAltitudeMeters, row.FinishAltitudeMeters,
        row.VerticalDropMeters, row.HomologationNumber);
}
