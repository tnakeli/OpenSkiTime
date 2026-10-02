using OpenSkiTime.Rewrite.Domain;

namespace OpenSkiTime.Rewrite.Application;

public static class CalendarCompetitionMerge
{
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
        return incoming with { ShortLabel = existing.ShortLabel, RunCount = existing.RunCount,
            IntermediateCount = existing.IntermediateCount, CourseName = existing.CourseName,
            StartAltitudeMeters = existing.StartAltitudeMeters, FinishAltitudeMeters = existing.FinishAltitudeMeters,
            VerticalDropMeters = existing.VerticalDropMeters, CourseLengthMeters = existing.CourseLengthMeters,
            HomologationNumber = string.IsNullOrWhiteSpace(incoming.HomologationNumber) ? existing.HomologationNumber : incoming.HomologationNumber,
            Calendar = calendar };
    }
}
