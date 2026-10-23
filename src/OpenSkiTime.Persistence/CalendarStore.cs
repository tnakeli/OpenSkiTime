using Microsoft.EntityFrameworkCore;
using OpenSkiTime.Application;
using OpenSkiTime.Domain;

namespace OpenSkiTime.Persistence;

internal sealed partial class SqliteSeriesFileSession
{
    public Task<SeriesDetails> ApplyCalendarAsync(SeriesValues values, IReadOnlyList<CompetitionValues> competitions,
        long expectedRevision, CancellationToken ct = default)
    {
        var seriesValues = values.Validated();
        var imported = competitions.Select(x => x.Validated()).ToArray();
        if (imported.Length == 0 || imported.Any(x => x.RaceType != RaceType.Fis || x.Calendar is null)
            || imported.Where(x => x.FisCode is not null).GroupBy(x => (x.Calendar!.Season, x.FisCode)).Any(x => x.Count() != 1)
            || imported.Where(x => x.Calendar!.RaceId is not null).GroupBy(x => (x.Calendar!.Season, x.Calendar.RaceId)).Any(x => x.Count() != 1))
        { throw new DomainValidationException("Select a calendar event with unique FIS competitions."); }
        return WriteAsync(async db =>
        {
            var series = await LoadForWriteAsync(db, expectedRevision, ct);
            var existing = await db.Competitions.Where(x => x.SeriesId == series.Id).ToListAsync(ct);
            var staged = new List<(CompetitionRow Row, CompetitionValues Values, bool IsNew)>();
            var matched = CalendarCompetitionMerge.Match(existing.Select(Values).ToArray(), imported, out var ambiguous);
            if (ambiguous)
            {
                throw new DomainValidationException("A calendar race matches different or several competitions by codex and FIS race ID. "
                    + "Check the codices in Competitions. No fields were changed.");
            }
            for (var i = 0; i < imported.Length; i++)
            {
                var incoming = imported[i];
                var row = matched[i] is { } index ? existing[index] : null;
                var merged = row is null ? incoming : CalendarCompetitionMerge.Merge(Values(row), incoming).Validated();
                if (row is not null && !Values(row).HasSameStartOrderRules(merged)
                    && await db.Runs.AnyAsync(x => x.CompetitionId == row.Id, ct))
                { throw new DomainValidationException("Calendar changes discipline or date for a competition with start-list history. No fields were changed."); }
                staged.Add((row ?? new CompetitionRow { Id = Guid.NewGuid(), SeriesId = series.Id }, merged, row is null));
            }
            var changedIds = staged.Select(x => x.Row.Id).ToHashSet();
            var labels = existing.Where(x => !changedIds.Contains(x.Id)).Select(x => x.ShortLabel)
                .Concat(staged.Select(x => x.Values.ShortLabel)).ToArray();
            if (labels.Distinct(StringComparer.OrdinalIgnoreCase).Count() != labels.Length)
            { throw new DomainValidationException("A calendar short label conflicts with an existing competition. No fields were changed."); }
            SqliteSeriesFileStore.Assign(series, seriesValues);
            foreach (var item in staged)
            {
                Assign(item.Row, item.Values);
                if (item.IsNew) { db.Competitions.Add(item.Row); }
            }
            series.Revision++;
        }, ct);
    }
}
