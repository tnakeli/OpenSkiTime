namespace OpenSkiTime.Rewrite.Domain;

public enum Discipline { Slalom, GiantSlalom, SuperG, Downhill, AlpineCombined, Other }
public enum RaceType { Club, National, Fis, Training }

public sealed record SeriesValues(
    string Name, string Location, string Organizer, DateOnly StartDate,
    DateOnly EndDate, string Nation, string Season)
{
    public SeriesValues Validated()
    {
        var name = Required(Name, nameof(Name), 160);
        var location = Required(Location, nameof(Location), 160);
        var organizer = Required(Organizer, nameof(Organizer), 160);
        var nation = Required(Nation, nameof(Nation), 3).ToUpperInvariant();
        var season = Required(Season, nameof(Season), 40);
        if (nation.Length != 3 || !nation.All(c => c is >= 'A' and <= 'Z'))
        {
            throw new DomainValidationException("Nation must be a three-letter code.");
        }

        if (EndDate < StartDate)
        {
            throw new DomainValidationException("End date must not precede start date.");
        }

        return this with { Name = name, Location = location, Organizer = organizer, Nation = nation, Season = season };
    }

    internal static string Required(string? value, string field, int maxLength)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length > maxLength)
        {
            throw new DomainValidationException($"{field} is required and must be at most {maxLength} characters.");
        }

        return trimmed;
    }
}

public sealed record CompetitionValues(
    string Name, string ShortLabel, DateOnly Date, Discipline Discipline,
    RaceType RaceType, int RunCount, int IntermediateCount,
    string? FisCode = null, string? LocalRaceCode = null,
    string? CourseName = null, int? StartAltitudeMeters = null,
    int? FinishAltitudeMeters = null, int? VerticalDropMeters = null,
    string? HomologationNumber = null)
{
    public CompetitionValues Validated()
    {
        var name = SeriesValues.Required(Name, nameof(Name), 160);
        var shortLabel = SeriesValues.Required(ShortLabel, nameof(ShortLabel), 20);
        if (!Enum.IsDefined(Discipline) || !Enum.IsDefined(RaceType))
        {
            throw new DomainValidationException("Select a valid discipline and race type.");
        }

        if (RunCount is < 1 or > 9 || IntermediateCount is < 0 or > 20)
        {
            throw new DomainValidationException("Runs must be 1–9 and intermediates 0–20.");
        }

        var fisCode = Optional(FisCode, 50);
        if (RaceType == RaceType.Fis && fisCode is null)
        {
            throw new DomainValidationException("A FIS competition needs a FIS race code.");
        }

        foreach (var altitude in new[] { StartAltitudeMeters, FinishAltitudeMeters, VerticalDropMeters })
        {
            if (altitude is < 0 or > 9000)
            {
                throw new DomainValidationException("Altitude and drop must be between 0 and 9000 metres.");
            }
        }

        return this with
        {
            Name = name, ShortLabel = shortLabel, FisCode = fisCode,
            LocalRaceCode = Optional(LocalRaceCode, 50), CourseName = Optional(CourseName, 160),
            HomologationNumber = Optional(HomologationNumber, 50),
        };
    }

    private static string? Optional(string? value, int maxLength)
    {
        var trimmed = value?.Trim();
        if (trimmed?.Length > maxLength)
        {
            throw new DomainValidationException($"Value must be at most {maxLength} characters.");
        }

        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }
}

public sealed record CompetitionDetails(Guid Id, CompetitionValues Values);
public sealed record SeriesDetails(Guid Id, SeriesValues Values, long Revision, IReadOnlyList<CompetitionDetails> Competitions);

public sealed class DomainValidationException(string message) : Exception(message);
