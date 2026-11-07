using System.Globalization;

namespace OpenSkiTime.Domain;

public enum PointsDirection { LowerIsBetter, HigherIsBetter }
public enum FirstRunKind { DrawAll, ByPoints }
public enum LaterRunKind { ReverseStartOrder, ReverseResults, SameOrder }
public enum NonFinishers { StartLast, DoNotStart }

/// <summary>
/// How the first run of a start block is ordered. DrawAll draws everyone as one group. ByPoints draws the best
/// <see cref="FirstGroupSize"/> competitors with points among themselves (equal points at the boundary join them), follows with
/// the others in points order (equal points drawn among themselves) and draws competitors without points last.
/// </summary>
public sealed record FirstRunSetting(FirstRunKind Kind = FirstRunKind.DrawAll, int FirstGroupSize = 15,
    PointsDirection Direction = PointsDirection.LowerIsBetter, bool ZeroIsNoPoints = false);

/// <summary>
/// How a later run of a start block is ordered from the run before it. ReverseStartOrder needs no results.
/// ReverseResults starts the best <see cref="ReverseCount"/> classified competitors in reverse result order (null: all of them),
/// then the others in result order; DNF/DSQ of the previous run start last or not at all. SameOrder repeats the order.
/// A competitor who did not start the previous run (DNS, NPS) is left out whenever results are known.
/// </summary>
public sealed record LaterRunSetting(LaterRunKind Kind = LaterRunKind.ReverseStartOrder, int? ReverseCount = 30,
    NonFinishers NonFinishers = NonFinishers.StartLast);

/// <summary>
/// One or more classes drawn and listed together. Seed: the block's own draw seed, so another block can be drawn again without
/// changing this one. FirstBib: optional start of a bib range; otherwise numbering continues from the previous block.
/// </summary>
public sealed record StartBlock(string Name, IReadOnlyList<string> Classes, FirstRunSetting FirstRun, LaterRunSetting LaterRun,
    string Seed, int? FirstBib = null);

/// <summary>The blocks of a non-FIS start list in start order, with the options each block was ordered by.</summary>
public sealed record StartOrderSetup(IReadOnlyList<StartBlock> Blocks)
{
    public bool UsesPoints => Blocks.Any(x => x.FirstRun.Kind == FirstRunKind.ByPoints);
    public bool UsesResults(int runNumber) => runNumber > 1 && Blocks.Any(x => x.LaterRun.Kind == LaterRunKind.ReverseResults);

    public StartBlock? BlockOf(string className)
        => Blocks.FirstOrDefault(x => x.Classes.Any(c => string.Equals(c, className, StringComparison.OrdinalIgnoreCase)));
}

public static class FlexStartOrder
{
    public const string RuleVersion = "OpenSkiTime flexible start order v1 / SHA256-double-draw-v1";
    public const int MaxRuns = 99;

    public static StartListPlan FirstRun(Guid competitionId, CompetitionValues competition, Gender? gender,
        IReadOnlyList<DrawEntrant> entrants, PointsListSource pointsList, DrawOptions options, StartOrderSetup setup)
    {
        ArgumentNullException.ThrowIfNull(competition);
        ArgumentNullException.ThrowIfNull(entrants);
        ArgumentNullException.ThrowIfNull(pointsList);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(setup);
        ValidateCompetition(competition);
        if (gender is not (null or Gender.Male or Gender.Female)) { throw new DomainValidationException("Choose Men, Women or all genders."); }
        if (options.FirstBib < 1) { throw new DomainValidationException("The first bib must be at least 1."); }
        ValidateSetup(setup, firstRun: true);
        if (entrants.Count == 0 || entrants.Select(x => x.CompetitorId).Distinct().Count() != entrants.Count)
        { throw new DomainValidationException("The draw needs a non-empty, unique entry list."); }
        foreach (var entry in entrants)
        {
            var a = entry.Athlete;
            if (string.IsNullOrWhiteSpace(a.Surname) || entry.Points < 0 || (gender is not null && a.Gender != gender))
            { throw new DomainValidationException($"Complete the name and gender of {a.FederationCode} {a.Surname} before drawing."); }
            if (setup.BlockOf(entry.Class) is null)
            { throw new DomainValidationException($"{a.Surname} {a.FirstName} has no class in the start order setup."); }
        }
        var codes = entrants.Select(x => x.Athlete.FederationCode?.Trim()).Where(x => !string.IsNullOrEmpty(x)).ToArray();
        if (codes.Distinct(StringComparer.OrdinalIgnoreCase).Count() != codes.Length)
        { throw new DomainValidationException("Codes must be unique in the draw."); }
        if (pointsList.IsNone && setup.UsesPoints) { throw new DomainValidationException("A points list is required when a block is ordered by points."); }

        var ordered = new List<(DrawEntrant Entrant, string Group)>();
        foreach (var block in setup.Blocks)
        {
            var members = entrants.Where(x => setup.BlockOf(x.Class) == block)
                .OrderBy(x => x.Athlete.FederationCode ?? string.Empty, StringComparer.Ordinal).ThenBy(x => x.CompetitorId).ToArray();
            var random = new FisStartOrder.DrawRandom(block.Seed);
            if (block.FirstRun.Kind == FirstRunKind.DrawAll)
            { ordered.AddRange(random.DoubleDraw(members).Select(x => (x, "Drawn"))); }
            else { ordered.AddRange(ByPoints(members, block.FirstRun, random)); }
        }
        var bibs = AssignBibs(setup, ordered.Select(x => x.Entrant), options);
        return new(competitionId, competition, gender, 1, RuleVersion, "per-block seeds", options, pointsList, null, [],
            ordered.Select((x, i) => new StartListEntry(i + 1, bibs[i], x.Entrant, x.Group)).ToArray(), null, setup);
    }

    private static List<(DrawEntrant, string)> ByPoints(DrawEntrant[] members, FirstRunSetting rule, FisStartOrder.DrawRandom random)
    {
        if (rule.FirstGroupSize is < 1 or > 1000) { throw new DomainValidationException("The first group must have 1–1000 competitors."); }
        bool HasPoints(DrawEntrant x) => x.Points is { } p && !(rule.ZeroIsNoPoints && p == 0);
        var pointed = members.Where(HasPoints);
        var ranked = (rule.Direction == PointsDirection.LowerIsBetter ? pointed.OrderBy(x => x.Points) : pointed.OrderByDescending(x => x.Points))
            .ThenBy(x => x.Athlete.FederationCode ?? string.Empty, StringComparer.Ordinal).ThenBy(x => x.CompetitorId).ToArray();
        var firstCount = Math.Min(rule.FirstGroupSize, ranked.Length);
        while (firstCount > 0 && firstCount < ranked.Length && ranked[firstCount].Points == ranked[firstCount - 1].Points) { firstCount++; }
        var result = new List<(DrawEntrant, string)>();
        result.AddRange(random.DoubleDraw(ranked.Take(firstCount)).Select(x => (x, "First group")));
        foreach (var tied in ranked.Skip(firstCount).GroupBy(x => x.Points))
        { result.AddRange(random.DoubleDraw(tied).Select(x => (x, "Points order"))); }
        result.AddRange(random.DoubleDraw(members.Where(x => !HasPoints(x))).Select(x => (x, "No points")));
        return result;
    }

    /// <summary>Bibs for the entrants in start order: global first bib and skipped bibs, restarted at a block's own first bib.</summary>
    private static int[] AssignBibs(StartOrderSetup setup, IEnumerable<DrawEntrant> inOrder, DrawOptions options)
    {
        var list = inOrder.ToArray();
        var bibs = new List<int>(list.Length);
        var skip = options.SkippedBibs?.ToHashSet() ?? [];
        var next = options.FirstBib;
        foreach (var block in setup.Blocks)
        {
            var count = list.Skip(bibs.Count).TakeWhile(x => setup.BlockOf(x.Class) == block).Count();
            if (count == 0) { continue; }
            if (block.FirstBib is { } own)
            {
                if (own < 1 || own > 99999) { throw new DomainValidationException($"The first bib of {block.Name} must be 1–99999."); }
                if (own < next) { throw new DomainValidationException($"The first bib of {block.Name} overlaps the bibs of the block before it."); }
                next = own;
            }
            var assigned = FisStartOrder.Bibs(next, skip, count);
            bibs.AddRange(assigned);
            next = assigned[^1] + 1;
        }
        return bibs.ToArray();
    }

    /// <summary>The start order of run N+1 from the list of run N, block by block.</summary>
    public static StartListPlan LaterRun(StartListRevision source, IReadOnlyList<RunFinish> results, StartOrderSetup setup)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(results);
        ArgumentNullException.ThrowIfNull(setup);
        var plan = source.Plan;
        ValidateCompetition(plan.Competition);
        var runNumber = plan.RunNumber + 1;
        if (runNumber > plan.Competition.RunCount) { throw new DomainValidationException($"The competition has {plan.Competition.RunCount} run(s)."); }
        ValidateSetup(setup, firstRun: false);
        var known = results.Count > 0;
        if (known)
        {
            if (results.Count != plan.Entries.Count || results.Select(x => x.CompetitorId).Distinct().Count() != results.Count
                || results.Any(x => !plan.Entries.Any(e => e.Entrant.CompetitorId == x.CompetitorId)))
            { throw new DomainValidationException($"Provide one result or status for every Run {plan.RunNumber} starter."); }
            foreach (var result in results)
            {
                if (!Enum.IsDefined(result.Status) || (result.Status == FinishStatus.Finished
                    ? result.Hundredths is null or <= 0 : result.Hundredths is not null))
                { throw new DomainValidationException("Finished competitors need a positive time; DNS/DNF/DSQ/NPS must not have a time."); }
            }
        }
        var status = results.ToDictionary(x => x.CompetitorId);
        var ordered = new List<(StartListEntry Entry, string Group)>();
        foreach (var block in setup.Blocks)
        {
            var members = plan.Entries.OrderBy(x => x.Position).Where(x => setup.BlockOf(x.Entrant.Class) == block).ToList();
            if (known) { members.RemoveAll(x => status[x.Entrant.CompetitorId].Status is FinishStatus.DNS or FinishStatus.NPS); }
            switch (block.LaterRun.Kind)
            {
                case LaterRunKind.SameOrder:
                    ordered.AddRange(members.Select(x => (x, "Same order")));
                    break;
                case LaterRunKind.ReverseStartOrder:
                    ordered.AddRange(Enumerable.Reverse(members).Select(x => (x, "Reversed start order")));
                    break;
                default:
                    if (!known) { throw new DomainValidationException($"Run {plan.RunNumber} results are needed to order {block.Name}."); }
                    ordered.AddRange(ReverseResults(members, status, block.LaterRun));
                    break;
            }
        }
        if (ordered.Count == 0) { throw new DomainValidationException($"There are no starters for Run {runNumber}."); }
        if (setup.Blocks.Sum(x => plan.Entries.Count(e => setup.BlockOf(e.Entrant.Class) == x)) != plan.Entries.Count)
        { throw new DomainValidationException("Every class of the previous run must be in a block."); }
        return plan with { RunNumber = runNumber, SourceListId = source.Id, SourceResults = results.ToArray(), Changes = null, Setup = setup,
            RuleVersion = RuleVersion, Entries = ordered.Select((x, i) => x.Entry with { Position = i + 1, Group = x.Group }).ToArray() };
    }

    private static List<(StartListEntry, string)> ReverseResults(List<StartListEntry> members,
        Dictionary<Guid, RunFinish> status, LaterRunSetting rule)
    {
        long Time(StartListEntry e) => status[e.Entrant.CompetitorId].Hundredths!.Value;
        var ranked = members.Where(x => status[x.Entrant.CompetitorId].Status == FinishStatus.Finished)
            .OrderBy(Time).ThenByDescending(x => x.Bib).ToArray();
        var count = rule.ReverseCount is { } limit ? Math.Min(limit, ranked.Length) : ranked.Length;
        while (count > 0 && count < ranked.Length && Time(ranked[count]) == Time(ranked[count - 1])) { count++; }
        var reversed = ranked.Take(count).OrderByDescending(Time).ThenBy(x => x.Bib);
        var rest = ranked.Skip(count).OrderBy(Time).ThenByDescending(x => x.Bib);
        var result = reversed.Select(x => (Entry: x, Group: "Reversed results")).Concat(rest.Select(x => (x, "Result order"))).ToList();
        if (rule.NonFinishers == NonFinishers.StartLast)
        { result.AddRange(members.Where(x => status[x.Entrant.CompetitorId].Status is FinishStatus.DNF or FinishStatus.DSQ).Select(x => (x, "Did not finish"))); }
        return result;
    }

    public static void ValidateCompetition(CompetitionValues competition)
    {
        ArgumentNullException.ThrowIfNull(competition);
        if (competition.RunCount is < 1 or > MaxRuns) { throw new DomainValidationException($"Runs must be 1–{MaxRuns.ToString(CultureInfo.InvariantCulture)}."); }
    }

    public static void ValidateSetup(StartOrderSetup setup, bool firstRun)
    {
        ArgumentNullException.ThrowIfNull(setup);
        if (setup.Blocks.Count == 0) { throw new DomainValidationException("The start order needs at least one block."); }
        if (setup.Blocks.Select(x => x.Name.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() != setup.Blocks.Count
            || setup.Blocks.Any(x => string.IsNullOrWhiteSpace(x.Name) || x.Name.Length > 100))
        { throw new DomainValidationException("Block names must be unique and at most 100 characters."); }
        var classes = setup.Blocks.SelectMany(x => x.Classes).ToArray();
        if (classes.Distinct(StringComparer.OrdinalIgnoreCase).Count() != classes.Length || setup.Blocks.Any(x => x.Classes.Count == 0))
        { throw new DomainValidationException("Every class belongs to exactly one block, and a block needs a class."); }
        foreach (var block in setup.Blocks)
        {
            if (firstRun && string.IsNullOrWhiteSpace(block.Seed)) { throw new DomainValidationException("A draw seed is required."); }
            if (!Enum.IsDefined(block.FirstRun.Kind) || !Enum.IsDefined(block.FirstRun.Direction)
                || !Enum.IsDefined(block.LaterRun.Kind) || !Enum.IsDefined(block.LaterRun.NonFinishers))
            { throw new DomainValidationException($"Check the options of {block.Name}."); }
            if (block.LaterRun.ReverseCount is < 1) { throw new DomainValidationException("The number of reversed competitors must be at least 1."); }
        }
    }
}
