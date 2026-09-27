using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using OpenSkiTime.Rewrite.Application;
using OpenSkiTime.Rewrite.Domain;

namespace OpenSkiTime.Rewrite.Persistence;

internal sealed partial class SqliteSeriesFileSession
{
    public async Task<DeskBatchResult> ApplyDeskBatchAsync(DeskBatch batch, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        CheckOpen();
        if (batch.Rows.Count == 0 && batch.DeletedIds.Count == 0)
        {
            throw new DomainValidationException("There are no pending competitor changes.");
        }
        await _write.WaitAsync(ct);
        try
        {
            await using var db = await SqliteSeriesFileStore.NewContextAsync(FilePath, SqliteOpenMode.ReadWrite, ct);
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var series = await LoadForWriteAsync(db, batch.ExpectedRevision, ct);
            if (series.Id != batch.SeriesId) { throw new SeriesConflictException(); }
            var competitions = await db.Competitions.ToDictionaryAsync(x => x.Id, ct);
            var competitors = await db.Competitors.ToDictionaryAsync(x => x.Id, ct);
            var entries = await db.Participations.ToDictionaryAsync(x => (x.CompetitorId, x.CompetitionId), ct);
            var deletions = batch.DeletedIds.ToHashSet();
            if (deletions.Count != batch.DeletedIds.Count
                || deletions.Any(id => !competitors.TryGetValue(id, out var row) || row.SeriesId != series.Id))
            {
                throw new SeriesConflictException();
            }
            foreach (var id in deletions)
            {
                db.Competitors.Remove(competitors[id]);
                competitors.Remove(id);
                foreach (var key in entries.Keys.Where(key => key.CompetitorId == id).ToArray())
                {
                    db.Participations.Remove(entries[key]);
                    entries.Remove(key);
                }
            }
            var seen = new HashSet<Guid>();
            var created = 0;
            var updated = 0;
            foreach (var item in batch.Rows)
            {
                ct.ThrowIfCancellationRequested();
                var values = item.Values.Validated(series.EndDate.Year);
                CompetitorRow row;
                if (item.CompetitorId is { } id)
                {
                    if (!seen.Add(id) || !competitors.TryGetValue(id, out row!) || row.SeriesId != series.Id)
                    {
                        throw new SeriesConflictException();
                    }
                    updated++;
                }
                else
                {
                    row = new CompetitorRow { Id = Guid.NewGuid(), SeriesId = series.Id };
                    competitors.Add(row.Id, row);
                    db.Competitors.Add(row);
                    created++;
                }
                Assign(row, values);
                var entryIds = new HashSet<Guid>();
                foreach (var patch in item.Entries)
                {
                    if (!entryIds.Add(patch.CompetitionId)
                        || !competitions.TryGetValue(patch.CompetitionId, out var competition)
                        || competition.SeriesId != series.Id)
                    {
                        throw new SeriesConflictException();
                    }
                    if (patch.ImportedBib is <= 0 or > 99999)
                    {
                        throw new DomainValidationException("Bib reference must be 1–99999.");
                    }
                    var key = (row.Id, patch.CompetitionId);
                    if (!entries.TryGetValue(key, out var entry))
                    {
                        entry = new ParticipationRow
                        {
                            SeriesId = series.Id, CompetitorId = row.Id, CompetitionId = patch.CompetitionId,
                        };
                        db.Participations.Add(entry);
                        entries.Add(key, entry);
                    }
                    if (patch.Participates is { } participates) { entry.Participates = participates; }
                    if (patch.BibSpecified) { entry.ImportedBib = patch.ImportedBib; }
                }
            }
            if (competitors.Values.Where(x => x.FederationCodeKey is not null)
                .GroupBy(x => x.FederationCodeKey!, StringComparer.OrdinalIgnoreCase).Any(x => x.Count() > 1))
            {
                throw new DomainValidationException("A federation code occurs more than once after these changes.");
            }
            if (competitors.Values.Where(x => x.BirthYear is not null && x.FirstName.Length > 0)
                .GroupBy(x => (x.Surname.ToUpperInvariant(), x.FirstName.ToUpperInvariant(), x.BirthYear))
                .Any(x => x.Count() > 1))
            {
                throw new DomainValidationException("A name and birth year combination occurs more than once after these changes.");
            }
            if (entries.Values.Where(x => x.ImportedBib is not null)
                .GroupBy(x => (x.CompetitionId, x.ImportedBib)).Any(x => x.Count() > 1))
            {
                throw new DomainValidationException("A bib reference occurs more than once in a competition.");
            }
            series.Revision++;
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateConcurrencyException) { throw new SeriesConflictException(); }
            catch (DbUpdateException ex) when (ex.InnerException is SqliteException { SqliteErrorCode: 19 })
            {
                throw new DomainValidationException("The competitor changes conflict with existing data; nothing was saved.");
            }
            await transaction.CommitAsync(ct);
            return new DeskBatchResult(series.Revision, created, updated, deletions.Count);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException)
        {
            throw SqliteSeriesFileStore.FileError(ex);
        }
        finally { _write.Release(); }
    }
}
