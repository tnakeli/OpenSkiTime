using System.Text.Json;
using OpenSkiTime.Domain;

namespace OpenSkiTime.Persistence;

/// <summary>
/// Rejects a registration write that would enter a competitor in a competition of the other gender
/// (<see cref="CompetitionGenderRule"/>). Only what the write changes is checked: new entries and the entries of
/// competitors whose gender is new or changed. Existing entries of untouched competitors never block unrelated work.
/// </summary>
internal static class EntryGenderCheck
{
    public static void Ensure(IReadOnlyDictionary<Guid, CompetitionRow> competitions, IReadOnlyDictionary<Guid, CompetitorRow> competitors,
        IEnumerable<ParticipationRow> entries, IReadOnlySet<Guid> regendered, IReadOnlySet<(Guid CompetitorId, Guid CompetitionId)> entered)
    {
        foreach (var entry in entries.Where(x => x.Participates
            && (regendered.Contains(x.CompetitorId) || entered.Contains((x.CompetitorId, x.CompetitionId)))))
        {
            if (!competitions.TryGetValue(entry.CompetitionId, out var competition)
                || !competitors.TryGetValue(entry.CompetitorId, out var competitor)) { continue; }
            var code = CalendarGender(competition);
            if (CompetitionGenderRule.Allows(code, competitor.Gender)) { continue; }
            var name = $"{competitor.Surname} {competitor.FirstName}".Trim();
            throw new DomainValidationException(CompetitionGenderRule.Violation(name, competitor.Gender!.Value, competition.ShortLabel,
                CompetitionGenderRule.Required(code)!.Value) + " Nothing was saved.");
        }
    }

    private static string? CalendarGender(CompetitionRow competition)
        => competition.CalendarJson is null ? null : JsonSerializer.Deserialize<CompetitionCalendarData>(competition.CalendarJson)?.Gender;
}
