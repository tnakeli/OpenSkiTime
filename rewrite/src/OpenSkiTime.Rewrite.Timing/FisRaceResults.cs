using OpenSkiTime.Rewrite.Domain;

namespace OpenSkiTime.Rewrite.Timing;

public sealed record FisResultRow(StartListEntry Entry, TimingStatus Status, int StatusRun,
    long? Run1Hundredths, long? Run2Hundredths, long? TotalHundredths, int? Rank)
{
    public bool Started => StatusRun != 1 || Status is not (TimingStatus.DNS or TimingStatus.NPS);
    public DisqualificationDetails? Disqualification { get; init; }
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
        if (first.Plan.RunNumber != 1 || firstTiming.ListId != first.Id)
        { throw new DomainValidationException("Choose the saved Run 1 timing for this start list."); }
        EnsureClassified(firstTiming, 1);
        if (first.Plan.Competition.RunCount == 2)
        {
            if (second is null || secondTiming is null || second.Plan.RunNumber != 2
                || second.Plan.SourceListId != first.Id || secondTiming.ListId != second.Id)
            { throw new DomainValidationException("Choose the saved Run 2 timing for this Run 1 start list."); }
            EnsureClassified(secondTiming, 2);
            if (!SourceResultsMatch(second.Plan.SourceResults, firstTiming))
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
            { return new FisResultRow(entry, firstRow.Status, 1, null, null, null, null)
                { Disqualification = firstRow.Disqualification }; }
            if (secondRows is null)
            { return new FisResultRow(entry, TimingStatus.Finished, 1, firstRow.Hundredths, null, firstRow.Hundredths, null); }
            if (!secondRows.TryGetValue(entry.Entrant.CompetitorId, out var later))
            { throw new DomainValidationException("A Run 1 finisher is missing from the Run 2 start list."); }
            return later.Status == TimingStatus.Finished
                ? new FisResultRow(entry, TimingStatus.Finished, 2, firstRow.Hundredths, later.Hundredths,
                    firstRow.Hundredths + later.Hundredths, null)
                : new FisResultRow(entry, later.Status, 2, firstRow.Hundredths, null, null, null)
                    { Disqualification = later.Disqualification };
        }).ToArray();
        var totals = interim.Where(x => x.TotalHundredths is not null).Select(x => x.TotalHundredths!.Value).ToArray();
        return new(first, second, interim.Select(x => x with { Rank = x.TotalHundredths is { } total
            ? totals.Count(y => y < total) + 1 : null }).ToArray());
    }

    private static bool SourceResultsMatch(IReadOnlyList<RunFinish> source, TimingSnapshot current)
    {
        var finishes = current.ToRunFinishes();
        if (source.SequenceEqual(finishes)) { return true; }
        if (source.Count != finishes.Count || source.Select(x => x.CompetitorId).Distinct().Count() != source.Count)
        { return false; }
        // An official post-run classification does not rewrite the saved starting order.
        // Changed finisher times or newly eligible starters still require review/redrawing.
        return source.All(saved => finishes.SingleOrDefault(x => x.CompetitorId == saved.CompetitorId) is { } now
            && (saved == now || (saved.Status == FinishStatus.Finished
                && now.Status is FinishStatus.DNF or FinishStatus.DSQ or FinishStatus.DNS or FinishStatus.NPS
                && current.Audit.LastOrDefault(x => x.After.Kind == DecisionKind.Status
                    && x.After.CompetitorId == now.CompetitorId)?.After.Status?.ToString() == now.Status.ToString())));
    }

    private static void EnsureClassified(TimingSnapshot timing, int run)
    {
        var pending = timing.Results.Where(x => x.Status is not (TimingStatus.Finished or TimingStatus.DNS
            or TimingStatus.DNF or TimingStatus.DSQ or TimingStatus.NPS)).Select(x => x.Bib).ToArray();
        if (timing.Results.Count == 0 || pending.Length > 0)
        {
            var bibs = pending.Length == 0 ? "no starters" : "Bib " + string.Join(", ", pending.Take(10))
                + (pending.Length > 10 ? "…" : "");
            throw new DomainValidationException($"Run {run}: {pending.Length} starter(s) still need a time or classification ({bibs}). Check Timing.");
        }
    }
}
