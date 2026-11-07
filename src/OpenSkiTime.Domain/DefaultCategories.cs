namespace OpenSkiTime.Domain;

/// <summary>
/// The age classes of Finnish alpine races for a season, one class for girls (N) and one for boys (M) in each age group.
/// The reference year is the season's end year (2027 for 2026/27), so every season the birth years move on by one:
/// U10 is born reference-10 to reference-8, U12 reference-12 to -11, U14 -14 to -13, U16 -16 to -15, U18 -18 to -17,
/// U21 -21 to -19, and the general class (Y) is everybody older. The classes do not overlap, so every competitor has one class.
/// </summary>
public static class DefaultCategories
{
    private static readonly (string Label, int Youngest, int Oldest)[] s_ageGroups =
    [
        ("10", 8, 10), ("12", 11, 12), ("14", 13, 14), ("16", 15, 16), ("18", 17, 18), ("21", 19, 21),
    ];

    public static IReadOnlyList<CategoryRuleValues> ForSeason(int seasonEndYear)
    {
        if (seasonEndYear is < 1900 or > 2999) { throw new DomainValidationException("The season must be a year between 1900 and 2999."); }
        var rules = new List<CategoryRuleValues>();
        foreach (var (label, youngest, oldest) in s_ageGroups)
        {
            rules.Add(new("N" + label, seasonEndYear - oldest, seasonEndYear - youngest, Gender.Female, rules.Count));
            rules.Add(new("M" + label, seasonEndYear - oldest, seasonEndYear - youngest, Gender.Male, rules.Count));
        }
        rules.Add(new("NY", 1850, seasonEndYear - 22, Gender.Female, rules.Count));
        rules.Add(new("MY", 1850, seasonEndYear - 22, Gender.Male, rules.Count));
        return rules;
    }
}
