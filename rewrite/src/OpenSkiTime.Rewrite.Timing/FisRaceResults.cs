using OpenSkiTime.Rewrite.Domain;

namespace OpenSkiTime.Rewrite.Timing;

public sealed record FisResultRow(StartListEntry Entry, TimingStatus Status, int StatusRun,
    long? Run1Hundredths, long? Run2Hundredths, long? TotalHundredths, int? Rank)
{
    public bool Started => StatusRun != 1 || Status is not (TimingStatus.DNS or TimingStatus.NPS);
}

public sealed record FisRaceResult(StartListRevision FirstList, StartListRevision? SecondList,
    IReadOnlyList<FisResultRow> Rows)
{
    public IReadOnlyList<PenaltyCompetitor> PenaltyCompetitors => Rows.Select(x =>
        new PenaltyCompetitor(x.Entry, x.Started, x.Status, x.TotalHundredths, x.Rank)).ToArray();
}

public static class FisRaceResults
{
    public static FisRaceResult Assemble(StartListRevision first, TimingSnapshot firstTiming,
        StartListRevision? second = null, TimingSnapshot? secondTiming = null)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(firstTiming);
        if (first.Plan.RunNumber != 1 || firstTiming.ListId != first.Id || !firstTiming.Complete || firstTiming.Unresolved != 0)
        { throw new DomainValidationException("Run 1 timing must be complete, with all observations resolved."); }
        if (first.Plan.Competition.RunCount == 2)
        {
            if (second is null || secondTiming is null || second.Plan.RunNumber != 2
                || second.Plan.SourceListId != first.Id || secondTiming.ListId != second.Id
                || !secondTiming.Complete || secondTiming.Unresolved != 0)
            { throw new DomainValidationException("Run 2 timing must be complete, with all observations resolved."); }
            if (!second.Plan.SourceResults.SequenceEqual(firstTiming.ToRunFinishes()))
            { throw new DomainValidationException("Run 1 results changed after the Run 2 start list was created."); }
        }
        else if (first.Plan.Competition.RunCount != 1 || second is not null || secondTiming is not null)
        { throw new DomainValidationException("Only one-run and two-run Alpine results are supported."); }

        var firstRows = firstTiming.Results.ToDictionary(x => x.CompetitorId);
        var secondRows = secondTiming?.Results.ToDictionary(x => x.CompetitorId);
        if (firstRows.Count != first.Plan.Entries.Count || first.Plan.Entries.Any(x => !firstRows.ContainsKey(x.Entrant.CompetitorId)))
        { throw new DomainValidationException("Run 1 results do not match its start list."); }
        if (second is not null && (secondRows!.Count != second.Plan.Entries.Count || second.Plan.Entries.Any(x => !secondRows.ContainsKey(x.Entrant.CompetitorId))))
        { throw new DomainValidationException("Run 2 results do not match its start list."); }

        var interim = first.Plan.Entries.Select(entry =>
        {
            var firstRow = firstRows[entry.Entrant.CompetitorId];
            if (firstRow.Status != TimingStatus.Finished)
            { return new FisResultRow(entry, firstRow.Status, 1, null, null, null, null); }
            if (secondRows is null)
            { return new FisResultRow(entry, TimingStatus.Finished, 1, firstRow.Hundredths, null, firstRow.Hundredths, null); }
            if (!secondRows.TryGetValue(entry.Entrant.CompetitorId, out var later))
            { throw new DomainValidationException("A Run 1 finisher is missing from the Run 2 start list."); }
            return later.Status == TimingStatus.Finished
                ? new FisResultRow(entry, TimingStatus.Finished, 2, firstRow.Hundredths, later.Hundredths,
                    firstRow.Hundredths + later.Hundredths, null)
                : new FisResultRow(entry, later.Status, 2, firstRow.Hundredths, null, null, null);
        }).ToArray();
        var totals = interim.Where(x => x.TotalHundredths is not null).Select(x => x.TotalHundredths!.Value).ToArray();
        return new(first, second, interim.Select(x => x with { Rank = x.TotalHundredths is { } total
            ? totals.Count(y => y < total) + 1 : null }).ToArray());
    }
}
