namespace OpenSkiTime.Tests.FullRace;

// Independent expectations for the synthetic race. They are calculated from the generator's planned impulses and the
// published rules, without calling the application's timing, start-order or result code.
internal sealed record ExpectedRunRow(string Code, int Bib, RunOutcome Outcome, long? Hundredths, long? IntermediateHundredths, int? Rank);
internal sealed record ExpectedFinalRow(string Code, int Bib, string Status, long? Run1, long? Run2, long? Total, int? Rank);

internal static class ExpectedResults
{
    // Run rows for a start order (code by 1-based position). Rank: one more than the number of strictly faster times.
    public static IReadOnlyList<ExpectedRunRow> Run(int run, IReadOnlyList<(int Position, int Bib, string Code)> order,
        IReadOnlyDictionary<string, SyntheticRun> plans)
    {
        var rows = order.Select(x =>
        {
            var plan = plans[x.Code];
            var start = SyntheticRace.StartTimeOfDay(run, x.Position);
            var finished = plan.Outcome == RunOutcome.Finished;
            return new ExpectedRunRow(x.Code, x.Bib, plan.Outcome,
                finished ? SyntheticRace.ExpectedHundredths(start, plan) : null,
                plan.Outcome is RunOutcome.Finished or RunOutcome.DSQ or RunOutcome.DNF && !plan.MissingIntermediate
                    ? plan.IntermediateHundredths : null, null);
        }).ToArray();
        var times = rows.Where(x => x.Hundredths is not null).Select(x => x.Hundredths!.Value).ToArray();
        return rows.Select(x => x with { Rank = x.Hundredths is { } t ? times.Count(o => o < t) + 1 : null }).ToArray();
    }

    // ICR 621.11: Run 1 classified competitors by time (ties: higher bib listed first). The best `reversal` plus anyone
    // tied with the last of them start in reverse result-list order (tied: lower bib first), then the rest in result order.
    public static IReadOnlyList<int> SecondRunOrder(IReadOnlyList<ExpectedRunRow> run1, int reversal)
    {
        var classified = run1.Where(x => x.Outcome == RunOutcome.Finished)
            .OrderBy(x => x.Hundredths).ThenByDescending(x => x.Bib).ToList();
        var count = Math.Min(reversal, classified.Count);
        while (count < classified.Count && classified[count].Hundredths == classified[count - 1].Hundredths) { count++; }
        var reversed = classified.Take(count).OrderByDescending(x => x.Hundredths).ThenBy(x => x.Bib);
        return reversed.Concat(classified.Skip(count)).Select(x => x.Bib).ToArray();
    }

    public static IReadOnlyList<ExpectedFinalRow> Final(IReadOnlyList<ExpectedRunRow> run1, IReadOnlyList<ExpectedRunRow> run2)
    {
        var second = run2.ToDictionary(x => x.Code);
        var rows = run1.Select(first =>
        {
            if (first.Outcome != RunOutcome.Finished) { return new ExpectedFinalRow(first.Code, first.Bib, first.Outcome.ToString(), null, null, null, null); }
            var later = second[first.Code];
            return later.Outcome == RunOutcome.Finished
                ? new ExpectedFinalRow(first.Code, first.Bib, "Finished", first.Hundredths, later.Hundredths, first.Hundredths + later.Hundredths, null)
                : new ExpectedFinalRow(first.Code, first.Bib, later.Outcome.ToString(), first.Hundredths, null, null, null);
        }).ToArray();
        var totals = rows.Where(x => x.Total is not null).Select(x => x.Total!.Value).ToArray();
        return rows.Select(x => x with { Rank = x.Total is { } t ? totals.Count(o => o < t) + 1 : null })
            .OrderBy(x => x.Total is null).ThenBy(x => x.Total ?? 0).ThenByDescending(x => x.Total is null ? 0 : x.Bib).ToArray();
    }

    // FIS Alpine Points Rules 2026/27 4.4–4.5, written out separately from the application implementation:
    // race points P = (Tx/To − 1)·F rounded to 0.01; A = sum of the best five list points among the first ten,
    // B = best five list points among starters, C = race points of the A group; penalty (A + B − C) / 10.
    public static (decimal Calculated, decimal Applied) Penalty(IReadOnlyList<ExpectedFinalRow> final,
        IReadOnlyDictionary<string, SyntheticAthlete> athletes, IReadOnlySet<string> starters,
        int f, decimal max, decimal minimum, decimal maximum, decimal adder)
    {
        var classified = final.Where(x => x.Total is not null).OrderBy(x => x.Total).ThenBy(x => x.Bib).ToArray();
        var winner = classified[0].Total!.Value;
        decimal Used(string code) => Math.Min(athletes[code].SlalomPoints ?? max, max);
        decimal RacePoints(ExpectedFinalRow row) => Math.Min(decimal.Round((row.Total!.Value - winner) * (decimal)f / winner, 2, MidpointRounding.AwayFromZero), max);
        var topTen = classified.Where(x => x.Rank <= 10).ToArray();
        var a = topTen.OrderBy(x => Used(x.Code)).ThenByDescending(RacePoints).ThenBy(x => x.Bib).Take(5).ToArray();
        var b = final.Where(x => starters.Contains(x.Code)).OrderBy(x => Used(x.Code)).ThenBy(x => x.Bib).Take(5).ToArray();
        var calculated = decimal.Round((a.Sum(x => Used(x.Code)) + b.Sum(x => Used(x.Code)) - a.Sum(RacePoints)) / 10m, 2, MidpointRounding.AwayFromZero);
        var unlisted = a.Count(x => athletes[x.Code].SlalomPoints is not null) < 3 || classified.Count(x => athletes[x.Code].SlalomPoints is null) >= 3;
        var floor = unlisted ? Math.Max(minimum, 2 * max) : minimum;
        return (calculated, Math.Min(Math.Max(calculated + adder, floor), Math.Max(maximum, floor)));
    }

    public static string FormatHundredths(long? value) => value is { } v
        ? (v >= 6000 ? $"{v / 6000}:{v / 100 % 60:00}.{v % 100:00}" : $"{v / 100}.{v % 100:00}") : "-";
}
