using OpenSkiTime.Domain;

namespace OpenSkiTime.Application;

public static class CalendarCompetitionMerge
{
    /// <summary>
    /// The existing competition (index) each incoming calendar race updates, or null for a new one. A race is found by
    /// season and codex and by its FIS race ID, which also identifies a race whose codex is not assigned or entered yet.
    /// <paramref name="ambiguous"/> is set when the two identities point to different competitions, the competition found
    /// carries another race's FIS race ID, or two races claim one competition.
    /// </summary>
    public static int?[] Match(IReadOnlyList<CompetitionValues> existing, IReadOnlyList<CompetitionValues> incoming, out bool ambiguous)
    {
        ArgumentNullException.ThrowIfNull(existing); ArgumentNullException.ThrowIfNull(incoming);
        ambiguous = false;
        var result = new int?[incoming.Count];
        var fis = Enumerable.Range(0, existing.Count).Where(i => existing[i].RaceType == RaceType.Fis).ToArray();
        for (var i = 0; i < incoming.Count; i++)
        {
            var race = incoming[i];
            var season = race.Calendar!.Season;
            var raceId = race.Calendar.RaceId;
            var matches = fis.Where(x => Season(existing[x]) == season
                && (race.FisCode is { } codex && existing[x].FisCode?.PadLeft(4, '0') == codex
                    || raceId is not null && existing[x].Calendar?.RaceId == raceId)).ToArray();
            if (matches.Length > 1 || matches.Length == 1 && (result.Contains(matches[0])
                || raceId is not null && existing[matches[0]].Calendar?.RaceId is { } stored && stored != raceId))
            { ambiguous = true; }
            else if (matches.Length == 1) { result[i] = matches[0]; }
        }
        return result;
        static int Season(CompetitionValues x) => x.Calendar?.Season ?? FisSeason.FromDate(x.Date);
    }

    public static CompetitionValues Merge(CompetitionValues existing, CompetitionValues incoming)
    {
        ArgumentNullException.ThrowIfNull(existing); ArgumentNullException.ThrowIfNull(incoming);
        var calendar = incoming.Calendar!;
        var previous = existing.Calendar;
        var td = calendar.TechnicalDelegate;
        if (td is null) { td = previous?.TechnicalDelegate; }
        else if (td.Number.Length == 0 && previous?.TechnicalDelegate is { } old
            && string.Equals(td.LastName, old.LastName, StringComparison.OrdinalIgnoreCase)
            && string.Equals(td.FirstName, old.FirstName, StringComparison.OrdinalIgnoreCase)
            && string.Equals(td.Nation, old.Nation, StringComparison.OrdinalIgnoreCase))
        { td = td with { Number = old.Number }; }
        calendar = calendar with { TechnicalDelegate = td,
            Location = calendar.Location.Length == 0 ? previous?.Location ?? "" : calendar.Location,
            Nation = calendar.Nation.Length == 0 ? previous?.Nation ?? "" : calendar.Nation };
        // A codex entered by the operator stays until FIS publishes one.
        return incoming with { FisCode = incoming.FisCode ?? existing.FisCode, ShortLabel = existing.ShortLabel, RunCount = existing.RunCount,
            IntermediateCount = existing.IntermediateCount, CourseName = existing.CourseName,
            StartAltitudeMeters = existing.StartAltitudeMeters, FinishAltitudeMeters = existing.FinishAltitudeMeters,
            VerticalDropMeters = existing.VerticalDropMeters, CourseLengthMeters = existing.CourseLengthMeters,
            HomologationNumber = string.IsNullOrWhiteSpace(incoming.HomologationNumber) ? existing.HomologationNumber : incoming.HomologationNumber,
            Calendar = calendar };
    }
}
