using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using OpenSkiTime.Application;
using OpenSkiTime.Domain;
using OpenSkiTime.Timing;

namespace OpenSkiTime.Persistence;

// Ski-Alp-Pro test import. It is the only operation that replaces a competition's start lists, timing and approvals: the
// complete series is first saved as a verified copy next to the file, and the replacement is one transaction.
internal sealed partial class SqliteSeriesFileSession : ISkiAlpProImportStore
{
    // Protected history of one competition, in the order the rows can be removed.
    private static readonly string[] s_replacedHistory = ["TimingReportSubmissions", "ApprovedTimingReports", "TimingReports",
        "TimingReportImages", "ApprovedResults", "TimingAudit", "RawTimingPackets", "AuxiliaryRawPackets"];

    public async Task<SkiAlpProReplacement> ReadSkiAlpProReplacementAsync(Guid competitionId, CancellationToken ct = default)
    {
        CheckOpen();
        try
        {
            await using var db = await SqliteSeriesFileStore.NewContextAsync(FilePath, SqliteOpenMode.ReadWrite, ct);
            await using var transaction = ((SqliteConnection)db.Database.GetDbConnection()).BeginTransaction(deferred: true);
            await db.Database.UseTransactionAsync(transaction, ct);
            return await ReadReplacementAsync(db, competitionId, ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException)
        { throw SqliteSeriesFileStore.FileError(ex); }
    }

    private static async Task<SkiAlpProReplacement> ReadReplacementAsync(SeriesDbContext db, Guid competitionId, CancellationToken ct)
    {
        var lists = await ListIdsAsync(db, competitionId, ct);
        var captures = await db.Captures.AsNoTracking().Where(x => lists.Contains(x.ListId)).ToArrayAsync(ct);
        var captureIds = captures.Select(x => x.Id).ToArray();
        var auxiliary = await db.AuxiliaryCaptures.AsNoTracking().Where(x => lists.Contains(x.ListId)).Select(x => x.Id).ToArrayAsync(ct);
        return new(await db.Participations.CountAsync(x => x.CompetitionId == competitionId && x.Participates, ct), lists.Length,
            captures.Length + auxiliary.Length, captures.Count(x => !ToCapture(x).Options.Simulation) + auxiliary.Length,
            await db.RawPackets.CountAsync(x => captureIds.Contains(x.SessionId), ct)
                + await db.AuxiliaryRawPackets.CountAsync(x => auxiliary.Contains(x.SessionId), ct),
            await db.TimingAudit.CountAsync(x => lists.Contains(x.ListId), ct),
            await db.ApprovedResults.CountAsync(x => x.CompetitionId == competitionId, ct),
            await db.Set<TimingReportRow>().CountAsync(x => x.CompetitionId == competitionId, ct)
                + await db.Set<ApprovedTimingReportRow>().CountAsync(x => x.CompetitionId == competitionId, ct)
                + await db.Set<TimingReportImageRow>().CountAsync(x => x.CompetitionId == competitionId, ct));
    }

    private static Task<Guid[]> ListIdsAsync(SeriesDbContext db, Guid competitionId, CancellationToken ct)
        => db.StartLists.Where(x => db.Runs.Any(r => r.Id == x.RunId && r.CompetitionId == competitionId)).Select(x => x.Id).ToArrayAsync(ct);

    public async Task<SkiAlpProImportResult> ApplySkiAlpProImportAsync(SkiAlpProCommit commit, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(commit);
        CheckOpen();
        var competition = commit.Competition.Validated();
        if (string.IsNullOrWhiteSpace(commit.Operator) || string.IsNullOrWhiteSpace(commit.Reason))
        { throw new DomainValidationException("An import needs an operator and a reason."); }
        if (commit.Athletes.Count == 0 || commit.Athletes.Select(x => x.Id).Distinct().Count() != commit.Athletes.Count)
        { throw new DomainValidationException("The import needs a non-empty, unique list of competitors."); }
        var bibs = commit.Athletes.Where(x => x.Bib is not null).Select(x => x.Bib!.Value).ToArray();
        if (bibs.Any(x => x is < 1 or > 99999) || bibs.Distinct().Count() != bibs.Length)
        { throw new DomainValidationException("Imported bibs must be unique and 1–99999."); }
        if (commit.Runs.Select(x => x.Plan.RunNumber).Distinct().Count() != commit.Runs.Count || commit.Runs.Any(x => x.Plan.CompetitionId != commit.CompetitionId
            || x.Session is null && (x.Packets.Count > 0 || x.Decisions.Count > 0)
            || x.Session is { } s && (s.ListId != x.ListId || !s.Options.Simulation || x.Packets.Any(p => p.SessionId != s.Id))))
        { throw new DomainValidationException("The import holds start lists or timing of another run."); }
        // Nobody may be timing while a competition is replaced.
        using var idleLease = AcquireIdleWriteLease();
        await _write.WaitAsync(ct);
        string? backup = null;
        var keepBackup = false;
        try
        {
            if (!commit.IsNewCompetition)
            {
                backup = SqliteSeriesFileStore.SiblingBackupPath(FilePath, "before-import", commit.At.DateTime);
                await SqliteSeriesFileStore.BackupFileAsync(FilePath, backup, ct);
            }
            await using (var db = await SqliteSeriesFileStore.NewContextAsync(FilePath, SqliteOpenMode.ReadWrite, ct))
            {
                await using var transaction = await db.Database.BeginTransactionAsync(ct);
                var series = await LoadForWriteAsync(db, commit.ExpectedRevision, ct);
                if (series.Id != commit.SeriesId) { throw new SeriesConflictException(); }
                try
                {
                    await SaveImportAsync(db, series, commit with { Competition = competition }, ct);
                    series.Revision++;
                    await db.SaveChangesAsync(ct);
                }
                catch (DbUpdateConcurrencyException) { throw new SeriesConflictException(); }
                catch (DbUpdateException ex) when (ex.InnerException is SqliteException { SqliteErrorCode: 19 })
                { throw new DomainValidationException("The import conflicts with other data in this event series; nothing was saved."); }
                // From here on the commit may have happened even if it reports a failure: never remove the copy then.
                keepBackup = true;
                await transaction.CommitAsync(ct);
            }
            return new(await ReadAsync(ct), backup);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException)
        { throw SqliteSeriesFileStore.FileError(ex); }
        finally
        {
            if (!keepBackup && backup is not null && File.Exists(backup)) { File.Delete(backup); }
            _write.Release();
        }
    }

    private static async Task SaveImportAsync(SeriesDbContext db, SeriesRow series, SkiAlpProCommit commit, CancellationToken ct)
    {
        var race = await db.Competitions.SingleOrDefaultAsync(x => x.Id == commit.CompetitionId && x.SeriesId == series.Id, ct);
        if (commit.IsNewCompetition != race is null) { throw new SeriesConflictException(); }
        if (race is null)
        {
            race = new CompetitionRow { Id = commit.CompetitionId, SeriesId = series.Id };
            db.Competitions.Add(race);
        }
        else
        {
            if (!Values(race).HasSameStartOrderRules(commit.Competition)) { throw new SeriesConflictException(); }
            await RemoveCompetitionDataAsync(db, race.Id, ct);
        }
        Assign(race, commit.Competition);

        var competitors = await db.Competitors.Where(x => x.SeriesId == series.Id).ToDictionaryAsync(x => x.Id, ct);
        var entries = new List<ParticipationRow>();
        var recoded = new List<Guid>();
        foreach (var athlete in commit.Athletes)
        {
            if (athlete.IsNew == competitors.ContainsKey(athlete.Id)) { throw new SeriesConflictException(); }
            if (athlete.IsNew)
            {
                var added = new CompetitorRow { Id = athlete.Id, SeriesId = series.Id };
                competitors.Add(added.Id, added);
                db.Competitors.Add(added);
            }
            var row = competitors[athlete.Id];
            var previousCode = row.FederationCodeKey;
            Assign(row, athlete.Values.Validated(series.EndDate.Year));
            if (!athlete.IsNew && !string.Equals(previousCode, row.FederationCodeKey, StringComparison.OrdinalIgnoreCase)) { recoded.Add(row.Id); }
            if (!athlete.Entered && athlete.Bib is null) { continue; }
            entries.Add(new() { SeriesId = series.Id, CompetitorId = row.Id, CompetitionId = race.Id, Participates = athlete.Entered, ImportedBib = athlete.Bib });
        }
        db.Participations.AddRange(entries);
        if (competitors.Values.Where(x => x.FederationCodeKey is not null).GroupBy(x => x.FederationCodeKey!, StringComparer.OrdinalIgnoreCase).Any(x => x.Count() > 1))
        { throw new DomainValidationException("A federation code occurs more than once after this import."); }
        if (competitors.Values.Where(x => x.BirthYear is not null && x.FirstName.Length > 0)
            .GroupBy(x => (x.Surname.ToUpperInvariant(), x.FirstName.ToUpperInvariant(), x.BirthYear)).Any(x => x.Count() > 1))
        { throw new DomainValidationException("A name and birth year combination occurs more than once after this import."); }
        // Every competition of a changed competitor is checked, not only the imported one.
        var ids = commit.Athletes.Select(x => x.Id).ToHashSet();
        var others = await db.Participations.AsNoTracking().Where(x => x.SeriesId == series.Id && x.CompetitionId != race.Id).ToArrayAsync(ct);
        var races = await db.Competitions.Where(x => x.SeriesId == series.Id && x.Id != race.Id).ToDictionaryAsync(x => x.Id, ct);
        races.Add(race.Id, race);
        EntryGenderCheck.Ensure(races, competitors, entries.Concat(others), ids, entries.Where(x => x.Participates).Select(x => (x.CompetitorId, x.CompetitionId)).ToHashSet());
        var points = commit.Athletes.Where(x => x.Points is not null).Select(x => (x.Id, x.Points));
        var (changes, lists) = await FisPointsStore.ValidateAsync(db, points, commit.PointsList is null ? [] : [commit.PointsList], ct);
        await db.SaveChangesAsync(ct);
        await FisPointsStore.ClearAsync(db, recoded, ct);
        await FisPointsStore.WriteAsync(db, changes, lists, ct);

        string? timingVersion = null;
        foreach (var run in commit.Runs.OrderBy(x => x.Plan.RunNumber)) { timingVersion = await SaveImportedRunAsync(db, commit, run, timingVersion, ct); }
    }

    // Saves one run and returns the version of its timing, which the next run's start list is based on.
    private static async Task<string?> SaveImportedRunAsync(SeriesDbContext db, SkiAlpProCommit commit, SkiAlpProRunCommit run, string? sourceVersion, CancellationToken ct)
    {
        // The same rules as for a drawn list: the recorded draw inputs and changes must reproduce the list exactly.
        await ValidateStartPlanAsync(db, run.Plan, ct);
        var row = new RunRow { Id = Guid.NewGuid(), CompetitionId = commit.CompetitionId, Gender = RunField(run.Plan.Gender), Number = run.Plan.RunNumber };
        db.Runs.Add(row);
        db.StartLists.Add(new StartListRow { Id = run.ListId, RunId = row.Id, Revision = 1, CreatedAt = commit.At, Operator = commit.Operator,
            Reason = commit.Reason, PlanJson = JsonSerializer.Serialize(run.Plan with { Entries = [] }), SourceTimingVersion = sourceVersion,
            StartedAt = run.Session is null ? null : commit.At, StartedBy = run.Session is null ? null : commit.Operator });
        foreach (var entry in run.Plan.Entries)
        {
            db.StartListEntries.Add(new StartListEntryRow { ListId = run.ListId, Position = entry.Position, Bib = entry.Bib,
                CompetitorId = entry.Entrant.CompetitorId, EntryJson = JsonSerializer.Serialize(entry) });
        }
        if (run.Session is not { } session) { await db.SaveChangesAsync(ct); return null; }
        session.Options.Validate();
        db.Captures.Add(new CaptureRow { Id = session.Id, ListId = run.ListId, OptionsJson = JsonSerializer.Serialize(session.Options),
            StartedAt = session.StartedAt, StoppedAt = session.StoppedAt, CleanStop = session.CleanStop });
        foreach (var packet in run.Packets)
        {
            db.RawPackets.Add(new RawPacketRow { SessionId = packet.SessionId, Sequence = packet.Sequence, ReceivedAt = packet.ReceivedAt,
                Protocol = packet.Protocol, Source = packet.Source, Stream = packet.Stream, Bytes = packet.Bytes });
        }
        await db.SaveChangesAsync(ct);
        var starters = run.Plan.Entries.Select(x => x.Entrant.CompetitorId).ToHashSet();
        var bibs = run.Plan.Entries.Select(x => x.Bib).ToHashSet();
        if (run.Decisions.Select(TimingEngine.DecisionKey).Distinct().Count() != run.Decisions.Count)
        { throw new DomainValidationException("An import must decide each timing target at most once."); }
        var audit = new List<TimingAuditRow>();
        foreach (var decision in run.Decisions)
        {
            TimingEngine.ValidateDecisionShape(decision);
            var valid = decision.Kind switch
            {
                DecisionKind.Assignment => (decision.Bib is null || bibs.Contains(decision.Bib.Value)) && await ReferencesRunInputAsync(db, run.ListId, decision.ObservationKey!, ct),
                DecisionKind.Status or DecisionKind.Time => starters.Contains(decision.CompetitorId!.Value),
                _ => false
            };
            if (!valid) { throw new DomainValidationException("The import refers to a starter or an impulse that is not in this run."); }
            audit.Add(new TimingAuditRow { ListId = run.ListId, At = commit.At, Operator = commit.Operator, Reason = commit.Reason,
                BeforeJson = JsonSerializer.Serialize(SkiAlpProImport.Before(decision)), AfterJson = JsonSerializer.Serialize(decision) });
        }
        db.TimingAudit.AddRange(audit);
        await db.SaveChangesAsync(ct);
        return TimingReplay.InputVersion(new(new(run.ListId, 1, commit.At, null, commit.Operator, commit.Reason, run.Plan), [], [],
            audit.Select(ToAudit).OrderBy(x => x.Id).ToArray()));
    }

    // Removes everything recorded for a competition except its settings and race information. History rows are protected
    // by triggers: their own SQL is taken from the file and put back unchanged in the same transaction, and the schema must
    // then equal what it was.
    private static async Task RemoveCompetitionDataAsync(SeriesDbContext db, Guid competitionId, CancellationToken ct)
    {
        var connection = (SqliteConnection)db.Database.GetDbConnection();
        var transaction = (SqliteTransaction)db.Database.CurrentTransaction!.GetDbTransaction();
        async Task ExecuteAsync(string sql)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync(ct);
        }
        var schema = await SqliteSeriesFileStore.SchemaAsync(connection, transaction, ct);
        var triggers = new List<string>();
        foreach (var table in s_replacedHistory)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT sql FROM sqlite_master WHERE type = 'trigger' AND name = $name AND tbl_name = $table";
            command.Parameters.AddWithValue("$name", table + "_NoDelete");
            command.Parameters.AddWithValue("$table", table);
            triggers.Add(await command.ExecuteScalarAsync(ct) as string
                ?? throw new SeriesFileException("The history protection of this event series file is missing. Open the original file or a verified backup."));
        }
        foreach (var table in s_replacedHistory) { await ExecuteAsync($"DROP TRIGGER {table}_NoDelete"); }

        var lists = await ListIdsAsync(db, competitionId, ct);
        var captures = await db.Captures.Where(x => lists.Contains(x.ListId)).Select(x => x.Id).ToArrayAsync(ct);
        var auxiliary = await db.AuxiliaryCaptures.Where(x => lists.Contains(x.ListId)).Select(x => x.Id).ToArrayAsync(ct);
        var approvals = await db.Set<ApprovedTimingReportRow>().Where(x => x.CompetitionId == competitionId).Select(x => x.Id).ToArrayAsync(ct);
        await db.Set<TimingReportSubmissionRow>().Where(x => approvals.Contains(x.ApprovalId)).ExecuteDeleteAsync(ct);
        await db.Set<ApprovedTimingReportRow>().Where(x => x.CompetitionId == competitionId).ExecuteDeleteAsync(ct);
        await db.Set<TimingReportRow>().Where(x => x.CompetitionId == competitionId).ExecuteDeleteAsync(ct);
        await db.Set<TimingReportImageRow>().Where(x => x.CompetitionId == competitionId).ExecuteDeleteAsync(ct);
        await db.ApprovedResults.Where(x => x.CompetitionId == competitionId).ExecuteDeleteAsync(ct);
        await db.TimingAudit.Where(x => lists.Contains(x.ListId)).ExecuteDeleteAsync(ct);
        await db.RawPackets.Where(x => captures.Contains(x.SessionId)).ExecuteDeleteAsync(ct);
        await db.AuxiliaryRawPackets.Where(x => auxiliary.Contains(x.SessionId)).ExecuteDeleteAsync(ct);
        await db.Captures.Where(x => lists.Contains(x.ListId)).ExecuteDeleteAsync(ct);
        await db.AuxiliaryCaptures.Where(x => lists.Contains(x.ListId)).ExecuteDeleteAsync(ct);
        await db.StartListEntries.Where(x => lists.Contains(x.ListId)).ExecuteDeleteAsync(ct);
        await db.StartLists.Where(x => lists.Contains(x.Id)).ExecuteDeleteAsync(ct);
        await db.Runs.Where(x => x.CompetitionId == competitionId).ExecuteDeleteAsync(ct);
        await db.Participations.Where(x => x.CompetitionId == competitionId).ExecuteDeleteAsync(ct);

        foreach (var trigger in triggers) { await ExecuteAsync(trigger); }
        var restored = await SqliteSeriesFileStore.SchemaAsync(connection, transaction, ct);
        if (!schema.AsEnumerable().SequenceEqual(restored))
        { throw new SeriesFileException("The history protection could not be restored; nothing was imported."); }
    }
}
