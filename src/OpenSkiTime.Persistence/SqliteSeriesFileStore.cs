using Microsoft.Data.Sqlite;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OpenSkiTime.Application;
using OpenSkiTime.Domain;

namespace OpenSkiTime.Persistence;

public sealed class SqliteSeriesFileStore : ISeriesFileStore
{
    public async Task<ISeriesFileSession> CreateAsync(string filePath, SeriesValues values,
        IReadOnlyList<CompetitionValues>? competitions = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(values);
        var fullPath = FullPath(filePath);
        var validated = values.Validated();
        var initialCompetitions = (competitions ?? []).Select(x => x.Validated()).ToArray();
        if (initialCompetitions.Select(x => x.ShortLabel).Distinct(StringComparer.OrdinalIgnoreCase).Count() != initialCompetitions.Length)
        { throw new DomainValidationException("Calendar competition short labels must be unique."); }
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
                await CreateSchemaAsync(db, ct);
                var row = new SeriesRow { Id = Guid.NewGuid(), Revision = 1 };
                Assign(row, validated);
                db.Series.Add(row);
                foreach (var competition in initialCompetitions)
                {
                    var race = new CompetitionRow { Id = Guid.NewGuid(), SeriesId = row.Id };
                    SqliteSeriesFileSession.Assign(race, competition);
                    db.Competitions.Add(race);
                }
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

    // The complete schema of a series file: the EF model, the history triggers and the FIS points tables.
    private static async Task CreateSchemaAsync(SeriesDbContext db, CancellationToken ct)
    {
        await db.Database.EnsureCreatedAsync(ct);
        await db.Database.ExecuteSqlRawAsync(HistoryTriggers, ct);
        await db.Database.ExecuteSqlRawAsync(FisPointsStore.Schema, ct);
        var version = "PRAGMA user_version = " + SeriesMigrations.Current.ToString(System.Globalization.CultureInfo.InvariantCulture);
        await db.Database.ExecuteSqlRawAsync(version, ct);
    }

    private const string HistoryTriggers = """
                    CREATE TRIGGER RawTimingPackets_NoUpdate BEFORE UPDATE ON RawTimingPackets BEGIN SELECT RAISE(ABORT, 'History is immutable'); END;
                    CREATE TRIGGER RawTimingPackets_NoDelete BEFORE DELETE ON RawTimingPackets BEGIN SELECT RAISE(ABORT, 'History is immutable'); END;
                    CREATE TRIGGER AuxiliaryRawPackets_NoUpdate BEFORE UPDATE ON AuxiliaryRawPackets BEGIN SELECT RAISE(ABORT, 'History is immutable'); END;
                    CREATE TRIGGER AuxiliaryRawPackets_NoDelete BEFORE DELETE ON AuxiliaryRawPackets BEGIN SELECT RAISE(ABORT, 'History is immutable'); END;
                    CREATE TRIGGER TimingAudit_NoUpdate BEFORE UPDATE ON TimingAudit BEGIN SELECT RAISE(ABORT, 'History is immutable'); END;
                    CREATE TRIGGER TimingAudit_NoDelete BEFORE DELETE ON TimingAudit BEGIN SELECT RAISE(ABORT, 'History is immutable'); END;
                    CREATE TRIGGER ApprovedResults_NoUpdate BEFORE UPDATE ON ApprovedResults BEGIN SELECT RAISE(ABORT, 'History is immutable'); END;
                    CREATE TRIGGER ApprovedResults_NoDelete BEFORE DELETE ON ApprovedResults BEGIN SELECT RAISE(ABORT, 'History is immutable'); END;
                    CREATE TRIGGER RaceInformation_NoUpdate BEFORE UPDATE ON RaceInformation BEGIN SELECT RAISE(ABORT, 'History is immutable'); END;
                    CREATE TRIGGER RaceInformation_NoDelete BEFORE DELETE ON RaceInformation BEGIN SELECT RAISE(ABORT, 'History is immutable'); END;
                    CREATE TRIGGER TimingReports_NoUpdate BEFORE UPDATE ON TimingReports BEGIN SELECT RAISE(ABORT, 'History is immutable'); END;
                    CREATE TRIGGER TimingReports_NoDelete BEFORE DELETE ON TimingReports BEGIN SELECT RAISE(ABORT, 'History is immutable'); END;
                    CREATE TRIGGER TimingReportImages_NoUpdate BEFORE UPDATE ON TimingReportImages BEGIN SELECT RAISE(ABORT, 'History is immutable'); END;
                    CREATE TRIGGER TimingReportImages_NoDelete BEFORE DELETE ON TimingReportImages BEGIN SELECT RAISE(ABORT, 'History is immutable'); END;
                    CREATE TRIGGER ApprovedTimingReports_NoUpdate BEFORE UPDATE ON ApprovedTimingReports BEGIN SELECT RAISE(ABORT, 'History is immutable'); END;
                    CREATE TRIGGER ApprovedTimingReports_NoDelete BEFORE DELETE ON ApprovedTimingReports BEGIN SELECT RAISE(ABORT, 'History is immutable'); END;
                    CREATE TRIGGER TimingReportSubmissions_NoUpdate BEFORE UPDATE ON TimingReportSubmissions BEGIN SELECT RAISE(ABORT, 'History is immutable'); END;
                    CREATE TRIGGER TimingReportSubmissions_NoDelete BEFORE DELETE ON TimingReportSubmissions BEGIN SELECT RAISE(ABORT, 'History is immutable'); END;
                    """;

    // The schemas a file of the current format may have exactly, built once from the same code that creates files: the
    // complete schema, and the same with the optional PDF factory table that the first PDF settings save adds.
    private static readonly Lazy<Task<string[][]>> s_expectedSchemas = new(async () =>
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using (var db = new SeriesDbContext(new DbContextOptionsBuilder<SeriesDbContext>().UseSqlite(connection).Options))
        { await CreateSchemaAsync(db, CancellationToken.None); }
        var complete = await SchemaAsync(connection, CancellationToken.None);
        await using (var extension = connection.CreateCommand())
        {
            extension.CommandText = SqliteSeriesFileSession.PdfFactorySchema;
            await extension.ExecuteNonQueryAsync();
        }
        return [complete, await SchemaAsync(connection, CancellationToken.None)];
    });

    // Every schema object except SQLite's internal ones, with its SQL text (whitespace normalized), in a stable order.
    internal static Task<string[]> SchemaAsync(SqliteConnection connection, CancellationToken ct) => SchemaAsync(connection, null, ct);

    private static async Task<string[]> SchemaAsync(SqliteConnection connection, SqliteTransaction? transaction, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        // Only SQLite's planner statistics are skipped. Internal tables and automatic indexes are compared too (a fresh file has
        // the same ones), so an object hidden under the reserved "sqlite_" prefix, such as a trigger written with
        // writable_schema, cannot escape the check.
        command.CommandText = @"SELECT type, name, tbl_name, sql FROM sqlite_master
            WHERE NOT (type = 'table' AND name IN ('sqlite_stat1', 'sqlite_stat2', 'sqlite_stat3', 'sqlite_stat4')) ORDER BY type, name";
        await using var reader = await command.ExecuteReaderAsync(ct);
        var objects = new List<string>();
        while (await reader.ReadAsync(ct))
        {
            var sql = reader.IsDBNull(3) ? "" : string.Join(" ", (IEnumerable<string>)reader.GetString(3).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            objects.Add($"{reader.GetString(0)}\u001f{reader.GetString(1)}\u001f{reader.GetString(2)}\u001f{sql}");
        }
        return objects.ToArray();
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
            var version = await VerifyFormatAsync(fullPath, ct);
            if (version < SeriesMigrations.Current)
            {
                await MigrateAsync(fullPath, version, SeriesMigrations.All, SeriesMigrations.Current, await s_expectedSchemas.Value, ct);
                await VerifyFormatAsync(fullPath, ct);
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

    /// <summary>
    /// Checks a file read-only and returns its schema version. A file of the current version must have exactly the current
    /// schema; an older supported version is returned for upgrade. Unsupported files are rejected without being changed.
    /// </summary>
    private static async Task<int> VerifyFormatAsync(string path, CancellationToken ct)
    {
        await using var connection = new SqliteConnection(ConnectionString(path, SqliteOpenMode.ReadOnly));
        await connection.OpenAsync(ct);
        await using (var untrusted = connection.CreateCommand())
        {
            untrusted.CommandText = "PRAGMA trusted_schema=OFF";
            await untrusted.ExecuteNonQueryAsync(ct);
        }
        await using var tableCheck = connection.CreateCommand();
        tableCheck.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='Series'";
        if ((long)(await tableCheck.ExecuteScalarAsync(ct))! != 1)
        {
            throw new SeriesFileException("This is not a supported OpenSkiTime event series file. Choose a file created by the current version.");
        }

        await using var marker = connection.CreateCommand();
        marker.CommandText = "SELECT count(*), min(FormatId), max(FormatId) FROM Series";
        await using var reader = await marker.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        if (reader.GetInt64(0) != 1 || reader.IsDBNull(1) || reader.IsDBNull(2)
            || reader.GetString(1) != SeriesDbContext.Format || reader.GetString(2) != SeriesDbContext.Format)
        {
            throw new SeriesFileException("This development file uses an incompatible format. Create a new event series file; files from before the first supported release are not upgraded.");
        }

        var version = await VersionAsync(connection, null, ct);
        if (version > SeriesMigrations.Current)
        {
            throw new SeriesFileException("This event series file was saved by a newer version of OpenSkiTime. Update OpenSkiTime to open it; the file was not changed.");
        }

        // A file can come from another machine. Its tables, indexes and history triggers must be exactly those this version
        // creates: a removed immutability trigger or an added trigger would silently rewrite raw timing input or audit history.
        // An older version is checked the same way after its upgrade, inside the upgrade transaction (MigrateAsync).
        if (version == SeriesMigrations.Current && !Matches(await SchemaAsync(connection, ct), await s_expectedSchemas.Value))
        {
            throw UnexpectedSchema();
        }

        await using var integrity = connection.CreateCommand();
        integrity.CommandText = "PRAGMA integrity_check";
        if (!string.Equals((string?)await integrity.ExecuteScalarAsync(ct), "ok", StringComparison.OrdinalIgnoreCase))
        {
            throw new SeriesFileException("The event series database failed its integrity check. Restore a verified backup.");
        }
        return version;
    }

    private static bool Matches(string[] actual, IReadOnlyList<string[]> expected)
        => expected.Any(schema => schema.AsEnumerable().SequenceEqual(actual));

    private static SeriesFileException UnexpectedSchema() => new(
        "This event series file has an unexpected database structure; it may have been changed outside OpenSkiTime. Open the original file or a verified backup.");

    // user_version 0 marks a file written before schema versions were recorded: the release baseline.
    private static async Task<int> VersionAsync(SqliteConnection connection, SqliteTransaction? transaction, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "PRAGMA user_version";
        var stored = (long)(await command.ExecuteScalarAsync(ct))!;
        return stored == 0 ? SeriesMigrations.Baseline : checked((int)stored);
    }

    /// <summary>
    /// Upgrades a file from <paramref name="from"/> to <paramref name="target"/>. A copy of the file is saved first as
    /// "name.v{from}-backup-{time}.ost" next to it. All steps run in one transaction that commits only when foreign keys hold
    /// and the schema is exactly one of <paramref name="expectedSchemas"/>; otherwise nothing changes and the copy is removed.
    /// </summary>
    internal static async Task<string> MigrateAsync(string path, int from, IReadOnlyList<SeriesMigration> migrations, int target,
        IReadOnlyList<string[]> expectedSchemas, CancellationToken ct)
    {
        var steps = migrations.Where(x => x.From >= from && x.From < target).OrderBy(x => x.From).ToArray();
        if (steps.Length != target - from || steps.Where((x, i) => x.From != from + i).Any())
        {
            throw new SeriesFileException($"This event series file (version {from}) cannot be upgraded by this version of OpenSkiTime. The file was not changed.");
        }

        var backup = MigrationBackupPath(path, from);
        await CopyDatabaseAsync(path, backup, ct);
        var committed = false;
        try
        {
            await using var connection = new SqliteConnection(ConnectionString(path, SqliteOpenMode.ReadWrite));
            await connection.OpenAsync(ct);
            await ExecuteAsync(connection, null, "PRAGMA foreign_keys=OFF", ct); // Table rebuilds need it; set outside the transaction.
            await ExecuteAsync(connection, null, "PRAGMA trusted_schema=OFF", ct);
            await using var transaction = connection.BeginTransaction();
            if (await VersionAsync(connection, transaction, ct) != from)
            {
                throw new SeriesFileException("The event series file changed while it was being opened. Try again.");
            }
            foreach (var step in steps) { await ExecuteAsync(connection, transaction, step.Sql, ct); }
            await ExecuteAsync(connection, transaction, $"PRAGMA user_version = {target}", ct);
            await using (var keys = connection.CreateCommand())
            {
                keys.Transaction = transaction;
                keys.CommandText = "PRAGMA foreign_key_check";
                await using var violations = await keys.ExecuteReaderAsync(ct);
                if (await violations.ReadAsync(ct))
                {
                    throw new SeriesFileException("The event series file could not be upgraded because its data violates a data rule. The file was not changed.");
                }
            }
            if (!Matches(await SchemaAsync(connection, transaction, ct), expectedSchemas)) { throw UnexpectedSchema(); }
            await transaction.CommitAsync(ct);
            committed = true;
            return backup;
        }
        finally
        {
            if (!committed && File.Exists(backup)) { File.Delete(backup); }
        }
    }

    private static string MigrationBackupPath(string path, int from)
    {
        var folder = Path.GetDirectoryName(path)!;
        var name = $"{Path.GetFileNameWithoutExtension(path)}.v{from}-backup-{DateTime.Now:yyyyMMdd-HHmmss}";
        var candidate = Path.Combine(folder, name + ".ost");
        for (var i = 2; File.Exists(candidate); i++) { candidate = Path.Combine(folder, $"{name}-{i}.ost"); }
        return candidate;
    }

    private static async Task ExecuteAsync(SqliteConnection connection, SqliteTransaction? transaction, string sql, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(ct);
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
            // Schema objects may not call functions with side effects (defense in depth behind the schema check on open).
            await db.Database.ExecuteSqlRawAsync("PRAGMA trusted_schema=OFF", ct);
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

        await CopyDatabaseAsync(source, destination, ct, verify: true);
    }

    // A consistent single-file copy through SQLite's backup API, written to a temporary file and moved into place.
    private static async Task CopyDatabaseAsync(string source, string destination, CancellationToken ct, bool verify = false)
    {
        var parent = Path.GetDirectoryName(Path.GetFullPath(destination))!;
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

            if (verify) { await VerifyFormatAsync(temporary, ct); }
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
        => SaveCompetitionAsync(id, values, expectedRevision, false, ct);

    public Task<SeriesDetails> SaveCompetitionAsync(Guid? id, CompetitionValues values, long expectedRevision,
        bool saveCourseToSameDisciplineRaces, CancellationToken ct = default)
        => SaveCompetitionAsync(id, values, expectedRevision, saveCourseToSameDisciplineRaces, false, ct);

    public Task<SeriesDetails> SaveCompetitionAsync(Guid? id, CompetitionValues values, long expectedRevision,
        bool saveCourseToSameDisciplineRaces, bool saveTdToAllRaces, CancellationToken ct = default)
        => WriteAsync(async db =>
        {
            var validated = values.Validated();
            var series = await LoadForWriteAsync(db, expectedRevision, ct);
            CompetitionRow row;
            if (id is { } existingId)
            {
                row = await db.Competitions.SingleOrDefaultAsync(x => x.Id == existingId && x.SeriesId == series.Id, ct)
                    ?? throw new SeriesFileException("The competition no longer exists in this event series.");
                if (validated.RaceType == RaceType.Fis && row.RaceType != RaceType.Fis)
                {
                    var plans = await (from list in db.StartLists join run in db.Runs on list.RunId equals run.Id
                        where run.CompetitionId == existingId select list.PlanJson).ToListAsync(ct);
                    if (plans.Any(json => StoredJson.Read<StartListPlan>(json, "start-list plan").Setup is not null))
                    { throw new DomainValidationException("This competition has a flexible start order, which a FIS competition cannot use. Keep it a Club race."); }
                }
                if (!Values(row).HasSameStartOrderRules(validated) && await db.Runs.AnyAsync(x => x.CompetitionId == existingId, ct))
                { throw new DomainValidationException("Discipline, date and run count affect the saved starting order and cannot be changed after drawing. Race type, names, codes and course details can be edited."); }
            }
            else
            {
                row = new CompetitionRow { Id = Guid.NewGuid(), SeriesId = series.Id };
            }

            var shared = new List<(CompetitionRow Row, CompetitionValues Values)>();
            if (saveCourseToSameDisciplineRaces || saveTdToAllRaces)
            {
                foreach (var other in await db.Competitions.Where(x => x.SeriesId == series.Id && x.Id != row.Id).ToListAsync(ct))
                {
                    var updated = Values(other);
                    // Course and homologation describe one discipline's slope; share them only within that discipline.
                    var shareCourse = saveCourseToSameDisciplineRaces && updated.Discipline == validated.Discipline;
                    if (!shareCourse && !saveTdToAllRaces) { continue; }
                    if (shareCourse) { updated = updated with
                    {
                        CourseName = validated.CourseName,
                        HomologationNumber = validated.HomologationNumber,
                        StartAltitudeMeters = validated.StartAltitudeMeters,
                        FinishAltitudeMeters = validated.FinishAltitudeMeters,
                        VerticalDropMeters = validated.VerticalDropMeters,
                        CourseLengthMeters = validated.CourseLengthMeters
                    }; }
                    if (saveTdToAllRaces)
                    {
                        var td = validated.Calendar?.TechnicalDelegate;
                        var calendar = updated.Calendar;
                        if (calendar is null && td is not null)
                        { calendar = new CompetitionCalendarData(FisSeason.FromDate(updated.Date), "", "", "", "", null); }
                        updated = updated with { Calendar = calendar is null ? null : calendar with { TechnicalDelegate = td } };
                    }
                    shared.Add((other, updated.Validated()));
                }
            }
            if (id is null) { db.Competitions.Add(row); }
            Assign(row, validated);
            foreach (var (other, updated) in shared) { Assign(other, updated); }
            series.Revision++;
        }, ct, allowCaptureOwner: true);

    public Task<SeriesDetails> RemoveCompetitionAsync(Guid id, long expectedRevision, CancellationToken ct = default)
        => WriteAsync(async db =>
        {
            var series = await LoadForWriteAsync(db, expectedRevision, ct);
            var competition = await db.Competitions.SingleOrDefaultAsync(x => x.Id == id && x.SeriesId == series.Id, ct)
                ?? throw new SeriesFileException("The competition no longer exists in this event series.");
            if (await db.Runs.AnyAsync(x => x.CompetitionId == id, ct))
            { throw new DomainValidationException("This competition has start-list history and cannot be removed."); }
            if (await db.Set<RaceInformationRow>().AnyAsync(x => x.CompetitionId == id, ct))
            { throw new DomainValidationException("This competition has saved race information and cannot be removed."); }
            db.Competitions.Remove(competition);
            series.Revision++;
        }, ct);

    private async Task<SeriesDetails> WriteAsync(Func<SeriesDbContext, Task> change, CancellationToken ct, bool allowCaptureOwner = false)
    {
        CheckOpen();
        using var idleLease = allowCaptureOwner && _captureLease is not null ? null : AcquireIdleWriteLease();
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
        if (_captureLease is not null) { throw new SeriesFileException("Timing capture must drain before closing this series."); }
        _disposed = true;
        _write.Dispose();
        return ValueTask.CompletedTask;
    }

    internal static void Assign(CompetitionRow row, CompetitionValues values)
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
        row.CourseName = values.CourseName;
        row.StartAltitudeMeters = values.StartAltitudeMeters;
        row.FinishAltitudeMeters = values.FinishAltitudeMeters;
        row.VerticalDropMeters = values.VerticalDropMeters;
        row.HomologationNumber = values.HomologationNumber;
        row.CourseLengthMeters = values.CourseLengthMeters;
        row.CalendarJson = values.Calendar is null ? null : JsonSerializer.Serialize(values.Calendar);
    }

    private static CompetitionValues Values(CompetitionRow row) => new(
        row.Name, row.ShortLabel, row.Date, row.Discipline, row.RaceType,
        row.RunCount, row.IntermediateCount, row.FisCode,
        row.CourseName, row.StartAltitudeMeters, row.FinishAltitudeMeters,
        row.VerticalDropMeters, row.HomologationNumber,
        StoredJson.ReadOptional<CompetitionCalendarData>(row.CalendarJson, "competition calendar data"), row.CourseLengthMeters);
}
