namespace OpenSkiTime.LiveTiming.Harness;

public static class SyntheticRace
{
    public static LiveSnapshot Create(int count = 50, string codex = "9754")
    {
        var at = new DateTimeOffset(2026, 10, 2, 9, 0, 0, TimeSpan.Zero);
        var competitors = Enumerable.Range(1, count).Select(i => new LiveCompetitor(i, "TEST" + i, "Synthetic", "FIN", "Test Club", (990000 + i).ToString(System.Globalization.CultureInfo.InvariantCulture))).ToArray();
        return new(1, new("OpenSkiTime synthetic live test", "Test slope", "SL", new(2026,10,2), true, codex, "M", "FIS", 2), competitors, 1,
            [new(1, at, competitors.Select(x => x.Bib).ToArray(), competitors.Select(c => new LiveResult(c.Bib, LiveStatus.Ready, null, null, null, at)).ToArray())], at);
    }
    public static LiveSnapshot Update(LiveSnapshot state, int bib, LiveStatus status, long? time = null, int split = 0)
    {
        ArgumentNullException.ThrowIfNull(state);
        var at = state.UpdatedAt.AddSeconds(1);
        var run = state.Runs.First(r => r.Number == state.CurrentRun);
        var old = run.Results.First(r => r.Bib == bib);
        var result = old with { Status = status, Hundredths = time, At = at,
            StartedAt = status == LiveStatus.OnCourse && old.StartedAt is null ? at : old.StartedAt,
            StartSourceTicks = old.StartSourceTicks ?? (status == LiveStatus.OnCourse ? TimeSpan.FromHours(12).Ticks + bib * TimeSpan.TicksPerSecond : null),
            FinishSourceTicks = status == LiveStatus.Finished ? TimeSpan.FromHours(12).Ticks + bib * TimeSpan.TicksPerSecond + time * 100000 : old.FinishSourceTicks };
        if (split > 0)
        {
            result = result with { Hundredths = null,
                Intermediates = old.Splits.Where(x => x.Number != split).Append(new LiveSplit(split, 1800 + bib * 7L + (split-1) * 1500, at)).ToArray() };
        }
        var results = run.Results.Select(r => r.Bib == bib ? result : r).ToArray();
        var finishes = results.Where(r => r.Status == LiveStatus.Finished).ToArray();
        var best = finishes.Select(r => r.Hundredths).Min();
        results = results.Select(r => r with { Rank = r.Status == LiveStatus.Finished ? finishes.Count(x => x.Hundredths < r.Hundredths) + 1 : null,
            Difference = r.Status == LiveStatus.Finished ? r.Hundredths - best : null }).ToArray();
        return state with { Version = state.Version + 1, UpdatedAt = at,
            Runs = state.Runs.Select(r => r.Number == run.Number ? r with { Results = results } : r).ToArray() };
    }
    public static IEnumerable<LiveSnapshot> Simulate(LiveSnapshot initial)
    {
        ArgumentNullException.ThrowIfNull(initial);
        var state = initial;
        foreach (var competitor in initial.Competitors)
        {
            var bib = competitor.Bib;
            if (bib % 17 == 0) { yield return state = Update(state,bib,LiveStatus.DNS); continue; }
            yield return state = Update(state,bib,LiveStatus.OnCourse);
            yield return state = Update(state,bib,LiveStatus.OnCourse,split:1);
            if (bib % 13 == 0) { yield return state = Update(state,bib,LiveStatus.DNF); continue; }
            yield return state = Update(state,bib,LiveStatus.OnCourse,split:2);
            yield return state = Update(state,bib,LiveStatus.Finished,5500 + (bib * 137 % 1800));
            if (bib % 11 == 0) { yield return state = Update(state,bib,LiveStatus.DSQ); }
        }
    }
}
