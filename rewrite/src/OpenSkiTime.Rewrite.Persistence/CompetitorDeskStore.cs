using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using OpenSkiTime.Rewrite.Application;
using OpenSkiTime.Rewrite.Domain;

namespace OpenSkiTime.Rewrite.Persistence;

internal sealed partial class SqliteSeriesFileSession
{
    public async Task<CompetitorDeskDetails> ReadCompetitorDeskAsync(CancellationToken ct = default)
    {
        CheckOpen();
        try
        {
            await using var db = await SqliteSeriesFileStore.NewContextAsync(FilePath, SqliteOpenMode.ReadWrite, ct);
            var series = await db.Series.AsNoTracking().SingleAsync(ct);
            var competitors = await db.Competitors.AsNoTracking()
                .OrderBy(x => x.Surname).ThenBy(x => x.FirstName).ToListAsync(ct);
            var participations = await db.Participations.AsNoTracking().ToListAsync(ct);
            var categories = await db.CategoryRules.AsNoTracking()
                .OrderBy(x => x.DisplayOrder).ThenBy(x => x.Label).ToListAsync(ct);
            return new CompetitorDeskDetails(series.Id, series.Revision,
                competitors.Select(x => new CompetitorDetails(x.Id, Values(x))).ToArray(),
                participations.Select(x => new ParticipationDetails(x.CompetitorId, x.CompetitionId,
                    x.Participates, x.ImportedBib, x.StartOrder)).ToArray(),
                categories.Select(x => new CategoryRuleDetails(x.Id, Values(x))).ToArray());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException)
        {
            throw SqliteSeriesFileStore.FileError(ex);
        }
    }

    public Task<DeskMutationResult<CompetitorDetails>> SaveDeskRowAsync(Guid? id, CompetitorValues values,
        Guid? competitionId, bool participates, int? importedBib, long expectedRevision, CancellationToken ct = default)
        => WriteDeskAsync(async (db, series) =>
        {
            var validated = values.Validated(series.EndDate.Year);
            if (importedBib is <= 0 or > 99999)
            {
                throw new DomainValidationException("Imported bib must be a positive number up to 99999.");
            }
            if (competitionId is null && (participates || importedBib is not null))
            {
                throw new DomainValidationException("Select a competition before editing its entry or imported bib.");
            }

            CompetitorRow row;
            if (id is { } existingId)
            {
                row = await db.Competitors.SingleOrDefaultAsync(x => x.Id == existingId && x.SeriesId == series.Id, ct)
                    ?? throw new SeriesFileException("The competitor no longer exists in this event series.");
            }
            else
            {
                row = new CompetitorRow { Id = Guid.NewGuid(), SeriesId = series.Id };
                db.Competitors.Add(row);
            }

            var codeKey = validated.FederationCode?.ToUpperInvariant();
            if (codeKey is not null && await db.Competitors.AnyAsync(x => x.SeriesId == series.Id
                    && x.Id != row.Id && x.FederationCodeKey == codeKey, ct))
            {
                throw new DomainValidationException("Federation code is already assigned in this event series.");
            }
            Assign(row, validated);

            if (competitionId is { } raceId)
            {
                if (!await db.Competitions.AnyAsync(x => x.Id == raceId && x.SeriesId == series.Id, ct))
                {
                    throw new SeriesFileException("The selected competition no longer exists in this event series.");
                }
                if (importedBib is { } bib && await db.Participations.AnyAsync(x => x.SeriesId == series.Id
                        && x.CompetitionId == raceId && x.CompetitorId != row.Id && x.ImportedBib == bib, ct))
                {
                    throw new DomainValidationException("Imported bib is already used in this competition.");
                }

                var entry = await db.Participations.SingleOrDefaultAsync(x => x.CompetitorId == row.Id
                    && x.CompetitionId == raceId, ct);
                if (entry is null)
                {
                    entry = new ParticipationRow { SeriesId = series.Id, CompetitorId = row.Id, CompetitionId = raceId };
                    db.Participations.Add(entry);
                }
                entry.Participates = participates;
                entry.ImportedBib = importedBib;
            }

            return new CompetitorDetails(row.Id, validated);
        }, expectedRevision, ct);

    public async Task<long> RemoveCompetitorAsync(Guid id, long expectedRevision, CancellationToken ct = default)
    {
        var result = await WriteDeskAsync(async (db, series) =>
        {
            var row = await db.Competitors.SingleOrDefaultAsync(x => x.Id == id && x.SeriesId == series.Id, ct)
                ?? throw new SeriesFileException("The competitor no longer exists in this event series.");
            db.Competitors.Remove(row);
            return true;
        }, expectedRevision, ct);
        return result.Revision;
    }

    public Task<DeskMutationResult<CategoryRuleDetails>> SaveCategoryRuleAsync(Guid? id, CategoryRuleValues values,
        long expectedRevision, CancellationToken ct = default)
        => WriteDeskAsync(async (db, series) =>
        {
            var validated = values.Validated(series.EndDate.Year);
            CategoryRuleRow row;
            if (id is { } existingId)
            {
                row = await db.CategoryRules.SingleOrDefaultAsync(x => x.Id == existingId && x.SeriesId == series.Id, ct)
                    ?? throw new SeriesFileException("The category rule no longer exists in this event series.");
            }
            else
            {
                row = new CategoryRuleRow { Id = Guid.NewGuid(), SeriesId = series.Id };
                db.CategoryRules.Add(row);
            }
            var key = validated.Label.ToUpperInvariant();
            if (await db.CategoryRules.AnyAsync(x => x.SeriesId == series.Id && x.Id != row.Id && x.LabelKey == key, ct))
            {
                throw new DomainValidationException("A category with that label already exists in this event series.");
            }
            row.Label = validated.Label;
            row.LabelKey = key;
            row.BirthYearMin = validated.BirthYearMin;
            row.BirthYearMax = validated.BirthYearMax;
            row.Gender = validated.Gender;
            row.DisplayOrder = validated.DisplayOrder;
            return new CategoryRuleDetails(row.Id, validated);
        }, expectedRevision, ct);

    public async Task<long> RemoveCategoryRuleAsync(Guid id, long expectedRevision, CancellationToken ct = default)
    {
        var result = await WriteDeskAsync(async (db, series) =>
        {
            var row = await db.CategoryRules.SingleOrDefaultAsync(x => x.Id == id && x.SeriesId == series.Id, ct)
                ?? throw new SeriesFileException("The category rule no longer exists in this event series.");
            db.CategoryRules.Remove(row);
            return true;
        }, expectedRevision, ct);
        return result.Revision;
    }

    public async Task<long> ReplaceCategoryRulesAsync(IReadOnlyList<CategoryRuleValues> rules,
        long expectedRevision, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(rules);
        var result = await WriteDeskAsync(async (db, series) =>
        {
            var validated = rules.Select(x => x.Validated(series.EndDate.Year)).ToArray();
            if (validated.Select(x => x.Label).Distinct(StringComparer.OrdinalIgnoreCase).Count() != validated.Length)
            {
                throw new DomainValidationException("Category labels must be unique.");
            }
            await db.CategoryRules.Where(x => x.SeriesId == series.Id).ExecuteDeleteAsync(ct);
            foreach (var rule in validated)
            {
                db.CategoryRules.Add(new CategoryRuleRow
                {
                    Id = Guid.NewGuid(), SeriesId = series.Id, Label = rule.Label,
                    LabelKey = rule.Label.ToUpperInvariant(), BirthYearMin = rule.BirthYearMin,
                    BirthYearMax = rule.BirthYearMax, Gender = rule.Gender, DisplayOrder = rule.DisplayOrder,
                });
            }
            return true;
        }, expectedRevision, ct);
        return result.Revision;
    }

    private async Task<DeskMutationResult<T>> WriteDeskAsync<T>(
        Func<SeriesDbContext, SeriesRow, Task<T>> change, long expectedRevision, CancellationToken ct)
    {
        CheckOpen();
        using var idleLease = AcquireIdleWriteLease();
        await _write.WaitAsync(ct);
        try
        {
            await using var db = await SqliteSeriesFileStore.NewContextAsync(FilePath, SqliteOpenMode.ReadWrite, ct);
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var series = await LoadForWriteAsync(db, expectedRevision, ct);
            var value = await change(db, series);
            series.Revision++;
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateConcurrencyException) { throw new SeriesConflictException(); }
            catch (DbUpdateException ex) when (ex.InnerException is SqliteException { SqliteErrorCode: 19 })
            {
                throw new SeriesFileException("The competitor desk rejected a conflicting or invalid value.", ex);
            }
            await transaction.CommitAsync(ct);
            return new DeskMutationResult<T>(series.Revision, value);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException)
        {
            throw SqliteSeriesFileStore.FileError(ex);
        }
        finally { _write.Release(); }
    }

    private static void Assign(CompetitorRow row, CompetitorValues values)
    {
        row.Surname = values.Surname;
        row.FirstName = values.FirstName;
        row.BirthYear = values.BirthYear;
        row.FederationCode = values.FederationCode;
        row.FederationCodeKey = values.FederationCode?.ToUpperInvariant();
        row.Nation = values.Nation;
        row.Club = values.Club;
        row.Gender = values.Gender;
    }

    private static CompetitorValues Values(CompetitorRow row) => new(row.Surname, row.FirstName,
        row.BirthYear, row.FederationCode, row.Nation, row.Club, row.Gender);

    private static CategoryRuleValues Values(CategoryRuleRow row) => new(row.Label, row.BirthYearMin,
        row.BirthYearMax, row.Gender, row.DisplayOrder);
}
