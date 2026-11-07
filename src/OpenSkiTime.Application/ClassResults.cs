using OpenSkiTime.Domain;
using OpenSkiTime.Timing;

namespace OpenSkiTime.Application;

/// <summary>
/// One competitor in the results of a class: the time of every run, the total and the rank within the class.
/// Outcome, OutcomeRun and Detail say why a competitor has no total: the status of the first run that was not finished
/// (DNS, DNF, DSQ, NPS) and, for a disqualification, its gate or reason.
/// </summary>
public sealed record ClassResultRow(int? Rank, int Bib, CompetitorValues Athlete, IReadOnlyList<string> RunTimes,
    long? TotalHundredths, string Difference, string Status, TimingStatus? Outcome = null, int OutcomeRun = 0, string Detail = "");
public sealed record ClassResultGroup(string Class, IReadOnlyList<ClassResultRow> Rows)
{
    public IEnumerable<ClassResultRow> Classified => Rows.Where(x => x.TotalHundredths is not null);
    /// <summary>The competitors without a total, by outcome (DNS, DNF, DSQ, NPS) and run; no outcome means no result yet.</summary>
    public IEnumerable<IGrouping<(TimingStatus? Outcome, int Run), ClassResultRow>> Others
        => Rows.Where(x => x.TotalHundredths is null)
            .GroupBy(x => (x.Outcome, x.OutcomeRun))
            .OrderBy(g => g.Key.Outcome switch { TimingStatus.DNS => 0, TimingStatus.DNF => 1, TimingStatus.DSQ => 2, TimingStatus.NPS => 3, _ => 4 })
            .ThenBy(g => g.Key.OutcomeRun);
}

/// <summary>
/// Results of a Club race, class by class. The total of a competitor is the sum of the times of every run of the race and
/// exists only when every run is finished; equal totals share a rank and are listed with the higher bib first. Everyone
/// else is listed after the classified competitors with the first run in which they did not finish.
/// </summary>
public static class ClassResultsBuilder
{
    public static IReadOnlyList<ClassResultGroup> Build(PdfReportSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var runCount = source.Competition?.Values.RunCount ?? 1;
        var runs = source.Runs.GroupBy(x => x.List.Plan.RunNumber).ToDictionary(x => x.Key, x => x.ToArray());
        var starters = source.Runs.Where(x => x.List.Plan.RunNumber == 1).SelectMany(x => x.List.Plan.Entries.OrderBy(e => e.Position)).ToArray();
        var rows = new List<(string Class, ClassResultRow Row)>();
        (TimingStatus Status, long? Hundredths, DisqualificationDetails? Disqualification)? Result(int run, Guid competitorId)
        {
            var timed = runs.GetValueOrDefault(run)?.SelectMany(x => x.Timing.Results).FirstOrDefault(x => x.CompetitorId == competitorId && !x.IsForerunner);
            if (timed is not null && timed.Status is not (TimingStatus.Ready or TimingStatus.OnCourse or TimingStatus.Review)) { return (timed.Status, timed.Hundredths, timed.Disqualification); }
            // Results typed in without capture are saved with the start list of the next run.
            if (runs.GetValueOrDefault(run)?.All(x => x.Source.Sessions.Count == 0) == true
                && runs.GetValueOrDefault(run + 1)?.SelectMany(x => x.List.Plan.SourceResults).FirstOrDefault(x => x.CompetitorId == competitorId) is { } saved)
            { return (Enum.Parse<TimingStatus>(saved.Status.ToString()), saved.Hundredths, null); }
            return timed is null ? null : (timed.Status, timed.Hundredths, timed.Disqualification);
        }
        foreach (var entry in starters)
        {
            var times = new List<string>();
            long? total = 0;
            var status = string.Empty;
            TimingStatus? outcome = null;
            var outcomeRun = 0;
            var detail = string.Empty;
            for (var run = 1; run <= runCount; run++)
            {
                var result = Result(run, entry.Entrant.CompetitorId);
                if (result is { Status: TimingStatus.Finished, Hundredths: { } hundredths }) { times.Add(TimingTime.Format(hundredths)); total += hundredths; continue; }
                var open = result is null || result.Value.Status is TimingStatus.Ready or TimingStatus.OnCourse or TimingStatus.Review;
                times.Add(open ? "—" : result!.Value.Status.ToString());
                total = null;
                if (outcome is null && !open)
                {
                    (outcome, outcomeRun) = (result!.Value.Status, run);
                    status = $"{result.Value.Status} R{run}";
                    if (result.Value.Disqualification is { } dsq)
                    { detail = dsq.Gate is { } gate ? $"Gate {gate}" : dsq.Reason; }
                }
            }
            var className = entry.Entrant.Class.Length > 0 ? entry.Entrant.Class : source.Desk.Categories.Count == 0 ? string.Empty
                : CategoryResolver.Resolve(entry.Entrant.Athlete, source.Desk.Categories);
            rows.Add((className, new(null, entry.Bib, entry.Entrant.Athlete, times, total, string.Empty, status, outcome, outcomeRun, detail)));
        }
        // Classes in the order they start.
        var order = starters.Select(x => x.Entrant.Class).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        int StartIndex(string name) { var i = order.FindIndex(x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase)); return i >= 0 ? i : int.MaxValue; }
        return rows.GroupBy(x => x.Class, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => StartIndex(g.Key)).ThenBy(g => g.Key, StringComparer.CurrentCulture)
            .Select(g =>
            {
                var totals = g.Select(x => x.Row.TotalHundredths).ToArray();
                var best = totals.Min();
                var ordered = g.Select(x => x.Row).OrderByOfficialResult(x => x.TotalHundredths, x => x.Bib).ThenBy(x => x.Bib)
                    .Select(x => x with
                    {
                        Rank = ResultOrder.Rank(x.TotalHundredths, totals),
                        Difference = x.TotalHundredths is { } t && best is { } b ? "+" + TimingTime.Format(t - b) : string.Empty,
                    }).ToArray();
                return new ClassResultGroup(g.Key, ordered);
            }).ToArray();
    }

    /// <summary>True when every run of the race has a start list with timing and every starter is finished or classified.</summary>
    public static bool IsComplete(PdfReportSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var runCount = source.Competition?.Values.RunCount ?? 1;
        // Every list of the first run (a field: all genders, Men or Women) needs all runs, each finished or classified.
        bool RunComplete(RunReportData data)
            => data.Timing.Complete || (data.Source.Sessions.Count == 0 && source.Runs.Where(x => x.List.Plan.RunNumber == data.List.Plan.RunNumber + 1 && x.List.Plan.Gender == data.List.Plan.Gender)
                .Any(x => x.List.Plan.SourceResults.Count == data.List.Plan.Entries.Count));
        var fields = source.Runs.Where(x => x.List.Plan.RunNumber == 1).Select(x => x.List.Plan.Gender).Distinct().ToArray();
        return fields.Length > 0 && fields.All(field => Enumerable.Range(1, runCount).All(run =>
            source.Runs.FirstOrDefault(x => x.List.Plan.RunNumber == run && x.List.Plan.Gender == field) is { } data && RunComplete(data)));
    }
}
