using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using OpenSkiTime.Application;
using OpenSkiTime.Domain;

namespace OpenSkiTime.Persistence;

internal sealed partial class SqliteSeriesFileSession
{
    public async Task<ImportCommitResult> ApplyImportAsync(ImportCommit commit, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(commit);
        CheckOpen();
        await _write.WaitAsync(ct);
        try
        {
            await using var db = await SqliteSeriesFileStore.NewContextAsync(FilePath, SqliteOpenMode.ReadWrite, ct);
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var series = await db.Series.SingleAsync(ct);
            if (series.Id != commit.SeriesId) { throw new SeriesConflictException(); }
            if (commit.SourceHash.Length != 64 || commit.Rows.Count == 0)
            {
                throw new DomainValidationException("The import review is empty or has no source identity.");
            }
            if (await db.ImportReceipts.AnyAsync(x => x.SeriesId == series.Id && x.SourceHash == commit.SourceHash, ct))
            {
                return new ImportCommitResult(series.Revision, 0, 0, AlreadyApplied: true);
            }
            if (series.Revision != commit.ExpectedRevision) { throw new SeriesConflictException(); }
            var competitions = await db.Competitions.ToDictionaryAsync(x => x.Id, ct);
            var competitors = await db.Competitors.ToDictionaryAsync(x => x.Id, ct);
            var entries = await db.Participations.ToDictionaryAsync(x => (x.CompetitorId, x.CompetitionId), ct);
            var seen = new HashSet<Guid>();
            var created = 0;
            var updated = 0;
            foreach (var item in commit.Rows)
            {
                ct.ThrowIfCancellationRequested();
                var values = item.Values.Validated(series.EndDate.Year);
                CompetitorRow competitor;
                if (item.CompetitorId is { } id)
                {
                    if (!seen.Add(id)) { throw new DomainValidationException("The import review repeats a competitor."); }
                    if (!competitors.TryGetValue(id, out competitor!) || competitor.SeriesId != series.Id)
                    {
                        throw new SeriesConflictException();
                    }
                    updated++;
                }
                else
                {
                    competitor = new CompetitorRow { Id = Guid.NewGuid(), SeriesId = series.Id };
                    competitors.Add(competitor.Id, competitor);
                    db.Competitors.Add(competitor);
                    created++;
                }
                Assign(competitor, values);
                var patchedCompetitions = new HashSet<Guid>();
                foreach (var patch in item.Entries)
                {
                    ct.ThrowIfCancellationRequested();
                    if (!patchedCompetitions.Add(patch.CompetitionId))
                    {
                        throw new DomainValidationException("The import review repeats a competition entry.");
                    }
                    if (!competitions.TryGetValue(patch.CompetitionId, out var competition)
                        || competition.SeriesId != series.Id)
                    {
                        throw new SeriesConflictException();
                    }
                    if (patch.ImportedBib is <= 0 or > 99999)
                    {
                        throw new DomainValidationException("Imported bib must be 1–99999.");
                    }
                    var key = (competitor.Id, patch.CompetitionId);
                    if (!entries.TryGetValue(key, out var entry))
                    {
                        entry = new ParticipationRow { SeriesId = series.Id, CompetitorId = competitor.Id,
                            CompetitionId = patch.CompetitionId };
                        entries.Add(key, entry);
                        db.Participations.Add(entry);
                    }
                    if (patch.Participates is { } participates) { entry.Participates = participates; }
                    if (patch.BibSpecified) { entry.ImportedBib = patch.ImportedBib; }
                }
            }
            var codeDuplicate = competitors.Values.Where(x => x.FederationCodeKey is not null)
                .GroupBy(x => x.FederationCodeKey!, StringComparer.OrdinalIgnoreCase).FirstOrDefault(x => x.Count() > 1);
            if (codeDuplicate is not null)
            {
                throw new DomainValidationException($"Federation code {codeDuplicate.Key} occurs more than once after import.");
            }
            var identityDuplicate = competitors.Values.Where(x => x.BirthYear is not null && x.FirstName.Length > 0)
                .GroupBy(x => (x.Surname.ToUpperInvariant(), x.FirstName.ToUpperInvariant(), x.BirthYear))
                .FirstOrDefault(x => x.Count() > 1);
            if (identityDuplicate is not null)
            {
                throw new DomainValidationException("A name, first name and birth year combination occurs more than once after import.");
            }
            var bibDuplicate = entries.Values.Where(x => x.ImportedBib is not null)
                .GroupBy(x => (x.CompetitionId, x.ImportedBib)).FirstOrDefault(x => x.Count() > 1);
            if (bibDuplicate is not null)
            {
                throw new DomainValidationException($"Imported bib {bibDuplicate.Key.ImportedBib} occurs more than once in a competition.");
            }
            series.Revision++;
            db.ImportReceipts.Add(new ImportReceiptRow
            {
                SeriesId = series.Id, SourceHash = commit.SourceHash, Revision = series.Revision,
                Created = created, Updated = updated,
            });
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateConcurrencyException) { throw new SeriesConflictException(); }
            catch (DbUpdateException ex) when (ex.InnerException is SqliteException { SqliteErrorCode: 19 })
            {
                throw new DomainValidationException("The import violates a database data rule; no rows were saved.");
            }
            await transaction.CommitAsync(ct);
            return new ImportCommitResult(series.Revision, created, updated);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException)
        {
            throw SqliteSeriesFileStore.FileError(ex);
        }
        finally { _write.Release(); }
    }
}
