using Microsoft.EntityFrameworkCore;
using OpenSkiTime.Rewrite.Application;
using OpenSkiTime.Rewrite.Domain;

namespace OpenSkiTime.Rewrite.Persistence;

internal sealed partial class SqliteSeriesFileSession
{
    public Task<SeriesDetails> ApplyCalendarAsync(SeriesValues values, IReadOnlyList<CompetitionValues> competitions,
        long expectedRevision, CancellationToken ct = default)
    {
        var seriesValues = values.Validated();
        var imported = competitions.Select(x => x.Validated()).ToArray();
        if (imported.Length == 0 || imported.Any(x => x.RaceType != RaceType.Fis || x.Calendar is null)
            || imported.GroupBy(x => (x.Calendar!.Season, x.FisCode)).Any(x => x.Count() != 1))
        { throw new DomainValidationException("Select a calendar event with unique FIS competitions."); }
        return WriteAsync(async db =>
        {
            var series = await LoadForWriteAsync(db, expectedRevision, ct);
            var existing = await db.Competitions.Where(x => x.SeriesId == series.Id).ToListAsync(ct);
            var staged = new List<(CompetitionRow Row, CompetitionValues Values, bool IsNew)>();
            foreach (var incoming in imported)
            {
                var matches = existing.Where(x => x.RaceType == RaceType.Fis && x.FisCode?.PadLeft(4, '0') == incoming.FisCode
                    && (Values(x).Calendar?.Season ?? FisSeason.FromDate(x.Date)) == incoming.Calendar!.Season).ToArray();
                if (matches.Length > 1) { throw new DomainValidationException("Existing season/codex matches are ambiguous. No fields were changed."); }
                var row = matches.SingleOrDefault();
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
