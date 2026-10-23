namespace OpenSkiTime.LiveTiming.Radio;

public sealed record RadioRacer(LiveCompetitor Competitor, LiveResult? Result, LiveResult? Previous)
{
    public int Bib => Competitor.Bib;
}

/// <summary>
/// The facts one generation may state. The language model sees bibs, nations, clubs, statuses and timing values but never
/// racer names; it refers to every fact through a typed slot that code renders in Finnish before speech.
/// </summary>
public sealed class FactSheet
{
    private FactSheet(LiveSnapshot state, IReadOnlyDictionary<int, RadioRacer> racers, IReadOnlyDictionary<string, int> counts)
    { State = state; Racers = racers; Counts = counts; }
    public LiveSnapshot State { get; }
    public int Run => State.CurrentRun;
    public IReadOnlyDictionary<int, RadioRacer> Racers { get; }
    public IReadOnlyDictionary<string, int> Counts { get; }
    public const int MaxRacers = 16;

    public static FactSheet Build(LiveSnapshot state, IEnumerable<int> bibs)
    {
        ArgumentNullException.ThrowIfNull(state); ArgumentNullException.ThrowIfNull(bibs);
        var run = state.Runs.First(x => x.Number == state.CurrentRun);
        var previous = state.Runs.FirstOrDefault(x => x.Number == state.CurrentRun - 1);
        // The racers concerned first, then the current top three for context, never more than the prompt budget allows.
        var leaders = run.Results.Where(x => (x.TotalRank ?? x.Rank) is <= 3).OrderBy(x => x.TotalRank ?? x.Rank).Select(x => x.Bib);
        var racers = new Dictionary<int, RadioRacer>();
        foreach (var bib in bibs.Concat(leaders).Distinct())
        {
            if (racers.Count >= MaxRacers) { break; }
            if (state.Competitors.FirstOrDefault(x => x.Bib == bib) is not { } competitor || !run.StartOrder.Contains(bib)) { continue; }
            racers[bib] = new(competitor, run.Results.FirstOrDefault(x => x.Bib == bib), previous?.Results.FirstOrDefault(x => x.Bib == bib));
        }
        int Count(LiveStatus status) => run.StartOrder.Count(bib => (run.Results.FirstOrDefault(x => x.Bib == bib)?.Status ?? LiveStatus.Ready) == status);
        var counts = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["run"] = run.Number, ["runs"] = RacePhases.PlannedRuns(state.Competition), ["starters"] = run.StartOrder.Length,
            ["finished"] = Count(LiveStatus.Finished), ["oncourse"] = Count(LiveStatus.OnCourse), ["remaining"] = Count(LiveStatus.Ready),
            ["dnf"] = Count(LiveStatus.DNF), ["dsq"] = Count(LiveStatus.DSQ), ["dns"] = Count(LiveStatus.DNS)
        };
        return new(state, racers, counts);
    }

    /// <summary>Prompt view: the minimum the language model needs, with the slots it may use for each racer.</summary>
    public object ToPrompt()
    {
        static object? Values(LiveResult? r) => r is null ? null : new
        {
            status = r.Status.ToString(), run_time_hundredths = r.Hundredths, rank = r.Rank, gap_to_leader_hundredths = r.Difference,
            total_hundredths = r.TotalHundredths, total_rank = r.TotalRank, total_gap_hundredths = r.TotalDifference
        };
        return new
        {
            current_run = Run,
            counts = Counts,
            racers = Racers.Values.Select(x => new
            {
                bib = x.Bib, nation = x.Competitor.Nation, club = x.Competitor.Club,
                current_run = Values(x.Result), previous_run = Values(x.Previous),
                slots = RadioSlots.Available(this, x.Bib)
            })
        };
    }
}
