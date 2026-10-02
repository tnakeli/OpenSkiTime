using OpenSkiTime.Rewrite.Domain;

namespace OpenSkiTime.Rewrite.Timing;

public sealed record PenaltyCompetitor(StartListEntry Entry, bool Started, TimingStatus Status,
    long? TotalHundredths, int? Rank)
{
    public decimal? ListedPoints => Entry.Entrant.Points;
    public int Bib => Entry.Bib;
}

public sealed record PenaltyRuleValues(decimal Minimum, decimal Maximum, decimal Adder, decimal Correction = 0)
{
    public void Validate()
    {
        if (Minimum < 0 || Maximum < Minimum || Maximum > 999.99m || Adder < 0 || Adder > 999.99m || Math.Abs(Correction) > 999.99m)
        { throw new DomainValidationException("Check the category minimum, maximum and adder against the valid FIS list."); }
    }
}

public sealed record PenaltySelection(PenaltyCompetitor Competitor, decimal UsedPoints, decimal? RacePoints,
    bool SubstitutedMaximum);

public sealed record FisPenaltyResult(int FValue, decimal MaximumPoints, PenaltyRuleValues Rules,
    IReadOnlyList<PenaltySelection> BestClassified, IReadOnlyList<PenaltySelection> BestStarted,
    decimal SumA, decimal SumB, decimal SumC, decimal Calculated, decimal Applied,
    bool DoubleMinimum, IReadOnlyDictionary<int, decimal> RacePoints);

// FIS Alpine Points Rules 2026/27, sections 4.4.1-4.5 and Alpine formula (figure 1).
public static class FisPenalty
{
    public static int FValue(Discipline discipline) => discipline switch
    {
        Discipline.Downhill => 1250,
        Discipline.Slalom => 730,
        Discipline.GiantSlalom => 1010,
        Discipline.SuperG => 1190,
        _ => throw new DomainValidationException("This FIS penalty profile supports DH, SL, GS and SG.")
    };

    public static decimal MaximumPoints(Discipline discipline) => discipline switch
    {
        Discipline.Downhill => 330m,
        Discipline.Slalom => 165m,
        Discipline.GiantSlalom => 220m,
        Discipline.SuperG => 270m,
        _ => throw new DomainValidationException("This FIS penalty profile supports DH, SL, GS and SG.")
    };

    public static FisPenaltyResult Calculate(Discipline discipline, IReadOnlyList<PenaltyCompetitor> entrants,
        PenaltyRuleValues rules)
        => Calculate(FValue(discipline), MaximumPoints(discipline), entrants, rules);

    public static FisPenaltyResult Calculate(FisPenaltyProfile profile, IReadOnlyList<PenaltyCompetitor> entrants)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return Calculate(profile.FValue, profile.MaximumPoints, entrants,
            new(profile.Minimum, profile.Maximum, profile.Adder, profile.Correction));
    }

    private static FisPenaltyResult Calculate(int f, decimal max, IReadOnlyList<PenaltyCompetitor> entrants,
        PenaltyRuleValues rules)
    {
        ArgumentNullException.ThrowIfNull(entrants);
        ArgumentNullException.ThrowIfNull(rules);
        rules.Validate();
        if (f <= 0 || max <= 0) { throw new DomainValidationException("FIS factor and points cap must be positive."); }
        if (entrants.Count == 0 || entrants.Select(x => x.Bib).Distinct().Count() != entrants.Count)
        { throw new DomainValidationException("A unique final result is required for the penalty calculation."); }
        var classified = entrants.Where(x => x.Status == TimingStatus.Finished).OrderBy(x => x.TotalHundredths)
            .ThenBy(x => x.Bib).ToArray();
        if (classified.Length < 5 || entrants.Count(x => x.Started) < 5
            || classified.Any(x => x.TotalHundredths is null or <= 0 || x.Rank is null or <= 0))
        { throw new DomainValidationException("At least five started and five classified racers with complete times are needed for this penalty calculation."); }
        var winner = classified[0].TotalHundredths!.Value;
        if (classified.Any(x => x.TotalHundredths < winner)) { throw new DomainValidationException("Check the final result times."); }
        var racePoints = classified.ToDictionary(x => x.Bib,
            x => decimal.Round(((decimal)x.TotalHundredths!.Value / winner - 1m) * f, 2, MidpointRounding.AwayFromZero));
        var topTen = classified.Where(x => x.Rank <= 10).ToArray();
        if (topTen.Length < 5) { throw new DomainValidationException("The first ten classified results are incomplete."); }
        decimal Used(PenaltyCompetitor row) => Math.Min(row.ListedPoints ?? max, max);
        PenaltySelection Select(PenaltyCompetitor row, bool includeRacePoints) => new(row, Used(row),
            includeRacePoints ? Math.Min(racePoints[row.Bib], max) : null, row.ListedPoints is null || row.ListedPoints > max);
        var bestClassified = topTen.OrderBy(Used).ThenByDescending(x => racePoints[x.Bib])
            .ThenBy(x => x.Bib).Take(5).Select(x => Select(x, true)).ToArray();
        var bestStarted = entrants.Where(x => x.Started).OrderBy(Used).ThenBy(x => x.Bib)
            .Take(5).Select(x => Select(x, false)).ToArray();
        var a = bestClassified.Sum(x => x.UsedPoints);
        var b = bestStarted.Sum(x => x.UsedPoints);
        var c = bestClassified.Sum(x => x.RacePoints ?? 0);
        var calculated = decimal.Round((a + b - c) / 10m, 2, MidpointRounding.AwayFromZero);
        var doubleMinimum = bestClassified.Count(x => x.Competitor.ListedPoints is not null) < 3
            || classified.Count(x => x.ListedPoints is null) >= 3;
        var minimum = doubleMinimum ? Math.Max(rules.Minimum, 2m * max) : rules.Minimum;
        // The double-maximum rule establishes a new minimum even if it exceeds the category ceiling.
        var applied = Math.Min(Math.Max(calculated - rules.Correction + rules.Adder, minimum), Math.Max(rules.Maximum, minimum));
        return new(f, max, rules, bestClassified, bestStarted, a, b, c, calculated, applied,
            doubleMinimum, racePoints);
    }
}
