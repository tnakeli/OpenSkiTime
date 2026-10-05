using OpenSkiTime.LiveTiming;
using OpenSkiTime.Domain;
using OpenSkiTime.Timing;

namespace OpenSkiTime.Desktop;

public static class LiveSnapshotMapper
{
    public static LiveRun MapRun(StartListRevision list, TimingSnapshot snapshot, TimeZoneInfo zone, DateTimeOffset observedAt)
    {
        ArgumentNullException.ThrowIfNull(list); ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(zone);
        var observations = snapshot.Observations.ToDictionary(x => x.Observation.Key, x => x.Observation);
        (DateTimeOffset? At, long? Ticks) Source(string? key)
        {
            if (key is null || !observations.TryGetValue(key, out var o) || o.DeviceTicks is not { } ticks) { return (null, null); }
            var local = new DateTime(ticks,DateTimeKind.Unspecified); // The decoder has already supplied the date and midnight rollover.
            if (zone.IsInvalidTime(local) || zone.IsAmbiguousTime(local))
            { throw new LiveValidationException("Device timestamp needs an unambiguous race time zone. Review live timing clock settings."); }
            return (new DateTimeOffset(local, zone.GetUtcOffset(local)), ticks);
        }
        var best = snapshot.Results.Where(x => x.Status == TimingStatus.Finished).Select(x => x.Hundredths).Min();
        var results = snapshot.Results.Select(r =>
        {
            var start = Source(r.StartKey); var finish = Source(r.FinishKey);
            var correction = snapshot.Audit.LastOrDefault(a => a.After.Kind is DecisionKind.Status or DecisionKind.Time
                && (a.After.Bib == r.Bib || a.After.CompetitorId == r.CompetitorId));
            var at = correction?.At ?? finish.At ?? start.At ?? list.CreatedAt;
            var rank = r.Rank ?? (r.Status == TimingStatus.Finished ? snapshot.Results.Count(x => x.Status == TimingStatus.Finished && x.Hundredths < r.Hundredths) + 1 : (int?)null);
            return new LiveResult(r.Bib, Enum.Parse<LiveStatus>(r.Status.ToString()), r.Hundredths, rank,
                r.Status == TimingStatus.Finished && best is not null ? r.Hundredths - best : null, at,
                start.At, start.Ticks, finish.Ticks, r.Splits.Where(x => x.Hundredths is not null).Select(x =>
                { var source = Source(x.ObservationKey); return new LiveSplit(x.Number, x.Hundredths!.Value, source.At ?? at, source.Ticks); }).ToArray());
        }).ToArray();
        return new(list.Plan.RunNumber, list.CreatedAt,
            snapshot.StartOrder.Count > 0 ? snapshot.StartOrder.ToArray() : list.Plan.Entries.OrderBy(x => x.Position).Select(x => x.Bib).ToArray(), results);
    }
    // From Run 2 on, each finisher's combined time over all runs so far, ranked like the timing view's Run 2 ranking
    // (equal totals share a rank) with the gap to the best total. A racer without a finished time in every earlier run
    // has no total. Recalculated from the published run results, so a correction in any run updates later totals.
    public static LiveRun[] WithTotals(IEnumerable<LiveRun> runs)
    {
        ArgumentNullException.ThrowIfNull(runs);
        var done = new List<LiveRun>();
        foreach (var run in runs.OrderBy(x => x.Number))
        {
            var totals = run.Results.ToDictionary(r => r.Bib, r => done.Count > 0 && r.Status == LiveStatus.Finished
                && done.All(e => e.Results.Any(p => p.Bib == r.Bib && p.Status == LiveStatus.Finished))
                    ? r.Hundredths + done.Sum(e => e.Results.First(p => p.Bib == r.Bib).Hundredths!.Value) : null);
            var best = totals.Values.Min();
            done.Add(run with { Results = run.Results.Select(r => r with { TotalHundredths = totals[r.Bib],
                TotalRank = ResultOrder.Rank(totals[r.Bib], totals.Values), TotalDifference = totals[r.Bib] - best }).ToArray() });
        }
        return done.ToArray();
    }
    public static LiveCompetitor[] Competitors(IEnumerable<StartListRevision> lists)
    {
        ArgumentNullException.ThrowIfNull(lists);
        return lists.SelectMany(x => x.Plan.Entries).GroupBy(x => x.Bib).Select(g =>
        {
            if (g.Select(x => x.Entrant.CompetitorId).Distinct().Count() != 1) { throw new LiveValidationException("A bib is assigned to different competitors across runs."); }
            var a = g.Last().Entrant.Athlete;
            return new LiveCompetitor(g.Key, a.Surname.ToUpperInvariant(), a.FirstName, a.Nation ?? "", a.Club ?? "", a.FederationCode ?? "");
        }).OrderBy(x => x.Bib).ToArray();
    }
}
