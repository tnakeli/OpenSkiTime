using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace OpenSkiTime.Domain;

public sealed record DrawEntrant(Guid CompetitorId, CompetitorValues Athlete, decimal? Points);
public sealed record StartListEntry(int Position, int Bib, DrawEntrant Entrant, string Group);
public sealed record PointsListSource(string Code, DateOnly ValidFrom, DateOnly ValidTo,
    FisPenaltyListRules? PenaltyRules = null)
{
    // FIS requires the list valid on the race date. Another list may still be used; the start list records which one.
    public bool IsEffectiveOn(DateOnly date) => ValidFrom <= date && date <= ValidTo;
}
// SkippedBibs: bib numbers that are not handed out (missing or reserved bibs); assignment continues with the next number.
public sealed record DrawOptions(int FirstGroup = 15, int ReverseCount = 30, int FirstBib = 1, IReadOnlyList<int>? SkippedBibs = null);
public enum StartListChangeKind { Move, AssignBibs }
/// <summary>
/// A recorded change on top of the drawn order (Run 1) or the result order (Run 2): a competitor moved to another
/// position, for example as a penalty (the competitor keeps the bib), or bibs assigned again in the current order. The
/// saved list is the draw (or Run 2 order) with these changes applied in sequence, so it stays verifiable and every
/// change keeps its reason.
/// </summary>
public sealed record StartListChange(StartListChangeKind Kind, string Reason, Guid? CompetitorId = null, int? ToPosition = null,
    int? FirstBib = null, IReadOnlyList<int>? SkippedBibs = null);
public enum FinishStatus { Finished, DNS, DNF, DSQ, NPS }
public sealed record RunFinish(Guid CompetitorId, FinishStatus Status, long? Hundredths);
public sealed record StartListPlan(Guid CompetitionId, CompetitionValues Competition, Gender Gender, int RunNumber,
    string RuleVersion, string Seed, DrawOptions Options, PointsListSource PointsList,
    Guid? SourceListId, IReadOnlyList<RunFinish> SourceResults, IReadOnlyList<StartListEntry> Entries,
    IReadOnlyList<StartListChange>? Changes = null);
public sealed record StartListRevision(Guid Id, int Revision, DateTimeOffset CreatedAt, DateTimeOffset? ApprovedAt,
    string Operator, string Reason, StartListPlan Plan)
{
    public DateTimeOffset? StartedAt { get; init; }
    public string? StartedBy { get; init; }
    public string? SourceTimingVersion { get; init; }
    public bool HasCapture { get; init; }
}

public static class FisStartOrder
{
    public const string RuleVersion = "FIS ICR July 2026 / 621.3, 621.11 / SHA256-double-draw-v1";

    public static StartListPlan FirstRun(Guid competitionId, CompetitionValues competition, Gender gender,
        IReadOnlyList<DrawEntrant> entrants, PointsListSource pointsList, DrawOptions options, string seed)
    {
        ArgumentNullException.ThrowIfNull(competition);
        ArgumentNullException.ThrowIfNull(entrants);
        ArgumentNullException.ThrowIfNull(pointsList);
        ArgumentNullException.ThrowIfNull(options);
        ValidateProfile(competition);
        if (gender is not (Gender.Male or Gender.Female)) { throw new DomainValidationException("Choose Men or Women."); }
        if (options.FirstGroup is < 1 or > 15 || options.ReverseCount is not (15 or 30) || options.FirstBib < 1)
        { throw new DomainValidationException("Check first group (1–15), reversal (15 or 30) and first bib."); }
        if (string.IsNullOrWhiteSpace(seed)) { throw new DomainValidationException("A draw seed is required."); }
        if (entrants.Count == 0 || entrants.Select(x => x.CompetitorId).Distinct().Count() != entrants.Count)
        { throw new DomainValidationException("The draw needs a non-empty, unique entry list."); }
        foreach (var entry in entrants)
        {
            var a = entry.Athlete;
            if (a.Gender != gender || string.IsNullOrWhiteSpace(a.Surname) || string.IsNullOrWhiteSpace(a.FirstName)
                || a.BirthYear is null || string.IsNullOrWhiteSpace(a.Nation)
                || string.IsNullOrWhiteSpace(a.FederationCode) || entry.Points < 0)
            { throw new DomainValidationException($"Complete Code, name, year, gender and nation for {a.FederationCode} {a.Surname} before drawing."); }
        }
        if (entrants.Select(x => x.Athlete.FederationCode!.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() != entrants.Count)
        { throw new DomainValidationException("Codes must be unique in the draw."); }

        var random = new DrawRandom(seed);
        var ranked = entrants.Where(x => x.Points is not null).OrderBy(x => x.Points)
            .ThenBy(x => x.Athlete.FederationCode, StringComparer.Ordinal).ThenBy(x => x.CompetitorId).ToArray();
        var firstCount = Math.Min(options.FirstGroup, ranked.Length);
        while (firstCount > 0 && firstCount < ranked.Length && ranked[firstCount].Points == ranked[firstCount - 1].Points) { firstCount++; }
        var ordered = new List<(DrawEntrant Entrant, string Group)>();
        ordered.AddRange(random.DoubleDraw(ranked.Take(firstCount)).Select(x => (x, "First group")));
        foreach (var tied in ranked.Skip(firstCount).GroupBy(x => x.Points))
        { ordered.AddRange(random.DoubleDraw(tied).Select(x => (x, "Points order"))); }
        ordered.AddRange(random.DoubleDraw(entrants.Where(x => x.Points is null)
            .OrderBy(x => x.Athlete.FederationCode, StringComparer.Ordinal).ThenBy(x => x.CompetitorId))
            .Select(x => (x, "No points")));
        var bibs = Bibs(options.FirstBib, options.SkippedBibs, ordered.Count);
        return new(competitionId, competition, gender, 1, RuleVersion, seed, options, pointsList, null, [],
            ordered.Select((x, i) => new StartListEntry(i + 1, bibs[i], x.Entrant, x.Group)).ToArray());
    }

    public static StartListPlan SecondRun(StartListRevision first, IReadOnlyList<RunFinish> results, int? reverseCount = null)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(results);
        var plan = first.Plan;
        ValidateProfile(plan.Competition);
        if (plan.RunNumber != 1 || plan.Competition.RunCount != 2)
        { throw new DomainValidationException("Prepare Run 1 of a two-run competition first."); }
        var reversal = reverseCount ?? plan.Options.ReverseCount;
        if (reversal is not (15 or 30)) { throw new DomainValidationException("Choose a reversal of 15 or 30."); }
        if (results.Count != plan.Entries.Count || results.Select(x => x.CompetitorId).Distinct().Count() != results.Count
            || results.Any(x => !plan.Entries.Any(e => e.Entrant.CompetitorId == x.CompetitorId)))
        { throw new DomainValidationException("Provide one result or status for every Run 1 starter."); }
        foreach (var result in results)
        {
            if (!Enum.IsDefined(result.Status) || (result.Status == FinishStatus.Finished
                ? result.Hundredths is null or <= 0 : result.Hundredths is not null))
            { throw new DomainValidationException("Finished competitors need a positive time; DNS/DNF/DSQ/NPS must not have a time."); }
        }
        var times = results.Where(x => x.Status == FinishStatus.Finished).ToDictionary(x => x.CompetitorId, x => x.Hundredths!.Value);
        var ranked = plan.Entries.Where(x => times.ContainsKey(x.Entrant.CompetitorId))
            .OrderBy(x => times[x.Entrant.CompetitorId]).ThenByDescending(x => x.Bib).ToArray();
        if (ranked.Length == 0) { throw new DomainValidationException("There are no classified competitors for Run 2."); }
        var count = Math.Min(reversal, ranked.Length);
        while (count < ranked.Length && times[ranked[count].Entrant.CompetitorId] == times[ranked[count - 1].Entrant.CompetitorId]) { count++; }
        var reversed = ranked.Take(count).OrderByDescending(x => times[x.Entrant.CompetitorId]).ThenBy(x => x.Bib);
        var rest = ranked.Skip(count).OrderBy(x => times[x.Entrant.CompetitorId]).ThenByDescending(x => x.Bib);
        return plan with { RunNumber = 2, SourceListId = first.Id, SourceResults = results.ToArray(), Changes = null,
            Options = plan.Options with { ReverseCount = reversal },
            Entries = reversed.Concat(rest).Select((x, i) => x with { Position = i + 1,
                Group = i < count ? "Reversed group" : "Result order" }).ToArray() };
    }

    /// <summary>Bibs for <paramref name="count"/> starters from <paramref name="firstBib"/> upwards, leaving out skipped numbers.</summary>
    public static IReadOnlyList<int> Bibs(int firstBib, IReadOnlyCollection<int>? skipped, int count)
    {
        if (firstBib is < 1 or > 99999) { throw new DomainValidationException("The first bib must be 1–99999."); }
        if (skipped?.Any(x => x is < 1 or > 99999) == true) { throw new DomainValidationException("Skipped bibs must be 1–99999."); }
        var skip = skipped?.ToHashSet() ?? [];
        var bibs = new List<int>(count);
        for (var bib = firstBib; bibs.Count < count; bib++)
        {
            if (bib > 99999) { throw new DomainValidationException("Bibs would exceed 99999. Choose a lower first bib or skip fewer numbers."); }
            if (!skip.Contains(bib)) { bibs.Add(bib); }
        }
        return bibs;
    }

    /// <summary>Reads "13, 27, 40-42" as 13, 27, 40, 41, 42. Empty text means no skipped bibs.</summary>
    public static IReadOnlyList<int> ParseBibList(string? text)
    {
        var bibs = new SortedSet<int>();
        foreach (var part in (text ?? string.Empty).Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var range = part.Split('-', StringSplitOptions.TrimEntries);
            if (range.Length is 1 or 2 && range.All(x => int.TryParse(x, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n is >= 1 and <= 99999))
            {
                var from = int.Parse(range[0], CultureInfo.InvariantCulture);
                var to = int.Parse(range[^1], CultureInfo.InvariantCulture);
                if (to >= from && to - from < 1000) { for (var bib = from; bib <= to; bib++) { bibs.Add(bib); } continue; }
            }
            throw new DomainValidationException($"Skipped bibs: \"{part}\" is not a bib number or a range such as 40-42.");
        }
        return bibs.ToArray();
    }

    /// <summary>Applies one recorded change and appends it to the plan's changes.</summary>
    public static StartListPlan Apply(StartListPlan plan, StartListChange change)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(change);
        if (string.IsNullOrWhiteSpace(change.Reason) || change.Reason.Length > 200)
        { throw new DomainValidationException("Enter a reason (at most 200 characters) for the start list change."); }
        var entries = plan.Entries.OrderBy(x => x.Position).ToList();
        switch (change.Kind)
        {
            case StartListChangeKind.Move:
                var moved = entries.FirstOrDefault(x => x.Entrant.CompetitorId == change.CompetitorId)
                    ?? throw new DomainValidationException("The competitor to move is not on this start list.");
                if (change.ToPosition is not { } position || position < 1 || position > entries.Count)
                { throw new DomainValidationException($"Choose a start position from 1 to {entries.Count}."); }
                if (change.FirstBib is not null || change.SkippedBibs is not null) { throw new DomainValidationException("A move does not assign bibs."); }
                entries.Remove(moved);
                entries.Insert(position - 1, moved);
                break;
            case StartListChangeKind.AssignBibs:
                if (plan.RunNumber != 1) { throw new DomainValidationException("Bibs are assigned on the Run 1 start list; later runs keep them."); }
                if (change.CompetitorId is not null || change.ToPosition is not null || change.FirstBib is not { } first)
                { throw new DomainValidationException("Assigning bibs needs only the first bib and any skipped bibs."); }
                var bibs = Bibs(first, change.SkippedBibs, entries.Count);
                entries = entries.Select((x, i) => x with { Bib = bibs[i] }).ToList();
                break;
            default:
                throw new DomainValidationException("Unknown start list change.");
        }
        return plan with
        {
            Entries = entries.Select((x, i) => x with { Position = i + 1 }).ToArray(),
            Changes = [.. plan.Changes ?? [], change with { Reason = change.Reason.Trim() }],
        };
    }

    /// <summary>The drawn (or Run 2) plan with the recorded changes applied in order.</summary>
    public static StartListPlan WithChanges(StartListPlan plan, IReadOnlyList<StartListChange>? changes)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var result = plan with { Changes = null };
        foreach (var change in changes ?? []) { result = Apply(result, change); }
        return result;
    }

    // Entrants sharing identical FIS points with at least one other entrant; the draw decides their order (ICR 621.3).
    // Outside the first group each such group is drawn among itself; at or inside it they are drawn with the whole
    // first group. Entrants without points form the separate no-points group; missing points are not an equal value.
    public static IReadOnlySet<Guid> EqualPointsCompetitors(IEnumerable<StartListEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        return entries.Where(x => x.Entrant.Points is not null).GroupBy(x => x.Entrant.Points!.Value)
            .Where(x => x.Count() > 1).SelectMany(x => x).Select(x => x.Entrant.CompetitorId).ToHashSet();
    }

    public static void ValidateProfile(CompetitionValues competition)
    {
        ArgumentNullException.ThrowIfNull(competition);
        if ((competition.Discipline is Discipline.Slalom or Discipline.GiantSlalom) && competition.RunCount == 2) { return; }
        if ((competition.Discipline is Discipline.Downhill or Discipline.SuperG) && competition.RunCount == 1) { return; }
        throw new DomainValidationException("This draw profile supports two-run SL/GS and single-run DH/SG. Other formats need their own approved rules.");
    }

    private sealed class DrawRandom(string seed)
    {
        private uint _counter;
        private int Next(int count)
        {
            var bound = (uint)count;
            var threshold = unchecked(0U - bound) % bound;
            uint value;
            do
            {
                var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(seed + ":" + (_counter++).ToString(CultureInfo.InvariantCulture)));
                value = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
            } while (value < threshold);
            return (int)(value % bound);
        }

        public DrawEntrant[] DoubleDraw(IEnumerable<DrawEntrant> entrants)
        {
            var athletes = entrants.ToList();
            var slots = Enumerable.Range(0, athletes.Count).ToList();
            var output = new DrawEntrant[athletes.Count];
            while (athletes.Count > 0)
            {
                var athlete = Next(athletes.Count);
                var slot = Next(slots.Count);
                output[slots[slot]] = athletes[athlete];
                athletes.RemoveAt(athlete);
                slots.RemoveAt(slot);
            }
            return output;
        }
    }
}
