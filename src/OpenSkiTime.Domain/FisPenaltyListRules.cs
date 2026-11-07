namespace OpenSkiTime.Domain;

public sealed record FisCategoryPenalty(string Category, int RaceLevel, decimal Minimum, decimal Maximum);
public sealed record FisDisciplinePenalty(string Discipline, Gender Gender, int FValue,
    decimal MaximumPoints, decimal Correction, IReadOnlyList<decimal> Adders);
public sealed record FisPenaltyProfile(string Category, int RaceLevel, int FValue, decimal MaximumPoints,
    decimal Correction, decimal Adder, decimal Minimum, decimal Maximum);

// Portable snapshot of the rule tables supplied with the points list, not operator preferences.
public sealed record FisPenaltyListRules(int Season, IReadOnlyList<FisCategoryPenalty> Categories,
    IReadOnlyList<FisDisciplinePenalty> Disciplines)
{
    // Seasons are numbered by the year the season ends (2027 = 2026/27). The penalty calculation was reviewed against the
    // 2026/27 edition; 2025/26 (2026) uses the same calculation by decision of the project owner, see docs/fis-penalty-review.md.
    public static readonly IReadOnlyList<int> SupportedSeasons = [2026, 2027];

    public static string SeasonLabel(int season) => $"{season - 1}/{season % 100:00}";

    public bool IsSupportedSeason => SupportedSeasons.Contains(Season);

    public string UnsupportedSeasonMessage
        => $"The penalty calculation rules for season {SeasonLabel(Season)} have not been reviewed (supported: {string.Join(", ", SupportedSeasons.Select(SeasonLabel))}).";

    public FisPenaltyProfile Resolve(string category, Discipline discipline, Gender gender)
    {
        var code = discipline switch
        {
            Discipline.Slalom => "SL", Discipline.GiantSlalom => "GS", Discipline.Downhill => "DH",
            Discipline.SuperG => "SG", _ => throw new DomainValidationException("Penalty review supports SL, GS, DH and SG.")
        };
        var cats = Categories.Where(x => x.Category.Equals(category.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();
        var dis = Disciplines.Where(x => x.Discipline == code && x.Gender == gender).ToArray();
        if (cats.Length != 1 || dis.Length != 1)
        { throw new DomainValidationException("The drawn FIS list has no unique penalty rules for this category, discipline and gender. Check competition details and the points list."); }
        var c = cats[0]; var d = dis[0];
        if (c.RaceLevel is < 0 or > 4 || d.Adders.Count != 5 || d.FValue <= 0 || d.MaximumPoints <= 0
            || c.Minimum < 0 || c.Maximum < c.Minimum || c.Maximum > 999.99m || d.Adders.Any(x => x < 0 || x > 999.99m))
        { throw new DomainValidationException("The FIS penalty rule snapshot is invalid."); }
        return new(c.Category, c.RaceLevel, d.FValue, d.MaximumPoints, d.Correction,
            d.Adders[c.RaceLevel], c.Minimum, c.Maximum);
    }
}
