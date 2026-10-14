namespace OpenSkiTime.Domain;

/// <summary>
/// Who may be entered in a competition. The FIS calendar gender of the competition decides: M is a men's competition,
/// W (or L) a women's competition. A (mixed), an empty value or a competitor without a gender does not restrict entry.
/// </summary>
public static class CompetitionGenderRule
{
    public static Gender? Required(string? calendarGender) => calendarGender?.Trim().ToUpperInvariant() switch
    {
        "M" => Gender.Male,
        "W" or "L" => Gender.Female,
        _ => null,
    };

    public static bool Allows(string? calendarGender, Gender? competitor)
        => Required(calendarGender) is not { } required || competitor is null || competitor == required;

    public static string Violation(string competitorName, Gender competitor, string competitionLabel, Gender required)
        => $"{competitorName} ({Label(competitor)}) cannot be entered in {competitionLabel}, a {(required == Gender.Male ? "men's" : "women's")} competition.";

    private static string Label(Gender gender) => gender switch
    {
        Gender.Female => "Women",
        Gender.Male => "Men",
        _ => "Other",
    };
}
