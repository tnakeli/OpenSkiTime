using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace OpenSkiTime.Rewrite.Domain;

public sealed record DrawEntrant(Guid CompetitorId, CompetitorValues Athlete, decimal? Points);
public sealed record StartListEntry(int Position, int Bib, DrawEntrant Entrant, string Group);
public sealed record PointsListSource(string Code, DateOnly ValidFrom, DateOnly ValidTo);
public sealed record DrawOptions(int FirstGroup = 15, int ReverseCount = 30, int FirstBib = 1);
public enum FinishStatus { Finished, DNS, DNF, DSQ, NPS }
public sealed record RunFinish(Guid CompetitorId, FinishStatus Status, long? Hundredths);
public sealed record StartListPlan(Guid CompetitionId, CompetitionValues Competition, Gender Gender, int RunNumber,
    string RuleVersion, string Seed, DrawOptions Options, PointsListSource PointsList,
    Guid? SourceListId, IReadOnlyList<RunFinish> SourceResults, IReadOnlyList<StartListEntry> Entries);
public sealed record StartListRevision(Guid Id, int Revision, DateTimeOffset CreatedAt, DateTimeOffset? ApprovedAt,
    string Operator, string Reason, StartListPlan Plan)
{
    public DateTimeOffset? StartedAt { get; init; }
    public string? StartedBy { get; init; }
    public string? SourceTimingVersion { get; init; }
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
        if (pointsList.ValidFrom > competition.Date || pointsList.ValidTo < competition.Date)
        { throw new DomainValidationException("Download a FIS points list effective on the competition date before drawing."); }
        if (options.FirstGroup is < 1 or > 15 || options.ReverseCount is not (15 or 30)
            || options.FirstBib < 1 || (long)options.FirstBib + entrants.Count - 1 > 99999)
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
        return new(competitionId, competition, gender, 1, RuleVersion, seed, options, pointsList, null, [],
            ordered.Select((x, i) => new StartListEntry(i + 1, options.FirstBib + i, x.Entrant, x.Group)).ToArray());
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
        return plan with { RunNumber = 2, SourceListId = first.Id, SourceResults = results.ToArray(),
            Options = plan.Options with { ReverseCount = reversal },
            Entries = reversed.Concat(rest).Select((x, i) => x with { Position = i + 1,
                Group = i < count ? "Reversed group" : "Result order" }).ToArray() };
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
