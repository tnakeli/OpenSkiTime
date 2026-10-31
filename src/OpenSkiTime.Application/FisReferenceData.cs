namespace OpenSkiTime.Application;

/// <summary>A FIS nation code (three letters, mostly the IOC code) with its English name.</summary>
public sealed record FisNation(string Code, string Name)
{
    public override string ToString() => $"{Code} – {Name}";
}

/// <summary>
/// Suggestions for FIS calendar categories and nation codes. They are suggestions only: an operator may still type a
/// code that is not listed, so a new FIS code never blocks race setup.
/// </summary>
public static class FisReferenceData
{
    /// <summary>Common alpine calendar category codes, highest level first.</summary>
    public static IReadOnlyList<string> CalendarCategories { get; } =
    [
        "OWG", "WSC", "WC", "WJC", "YOG", "EYOF", "EC", "NAC", "FEC", "ANC", "SAC", "UVS",
        "NC", "NJC", "NJR", "FIS", "ENL", "UNI", "CIT", "CHI", "CISM", "MAS",
    ];

    public static IReadOnlyList<FisNation> Nations { get; } =
    [
        new("AFG", "Afghanistan"), new("ALB", "Albania"), new("ALG", "Algeria"), new("AND", "Andorra"),
        new("ARG", "Argentina"), new("ARM", "Armenia"), new("AUS", "Australia"), new("AUT", "Austria"),
        new("AZE", "Azerbaijan"), new("BEL", "Belgium"), new("BIH", "Bosnia and Herzegovina"), new("BLR", "Belarus"),
        new("BOL", "Bolivia"), new("BRA", "Brazil"), new("BUL", "Bulgaria"), new("CAN", "Canada"),
        new("CHI", "Chile"), new("CHN", "China"), new("COL", "Colombia"), new("CRO", "Croatia"),
        new("CYP", "Cyprus"), new("CZE", "Czechia"), new("DEN", "Denmark"), new("ECU", "Ecuador"),
        new("EGY", "Egypt"), new("ESP", "Spain"), new("EST", "Estonia"), new("FIN", "Finland"),
        new("FRA", "France"), new("GBR", "Great Britain"), new("GEO", "Georgia"), new("GER", "Germany"),
        new("GRE", "Greece"), new("HKG", "Hong Kong"), new("HUN", "Hungary"), new("IND", "India"),
        new("IRI", "Iran"), new("IRL", "Ireland"), new("ISL", "Iceland"), new("ISR", "Israel"),
        new("ITA", "Italy"), new("JAM", "Jamaica"), new("JPN", "Japan"), new("KAZ", "Kazakhstan"),
        new("KGZ", "Kyrgyzstan"), new("KOR", "Korea"), new("KOS", "Kosovo"), new("LAT", "Latvia"),
        new("LBN", "Lebanon"), new("LIE", "Liechtenstein"), new("LTU", "Lithuania"), new("LUX", "Luxembourg"),
        new("MAD", "Madagascar"), new("MAR", "Morocco"), new("MDA", "Moldova"), new("MEX", "Mexico"),
        new("MGL", "Mongolia"), new("MKD", "North Macedonia"), new("MLT", "Malta"), new("MNE", "Montenegro"),
        new("MON", "Monaco"), new("NED", "Netherlands"), new("NOR", "Norway"), new("NZL", "New Zealand"),
        new("PAK", "Pakistan"), new("PER", "Peru"), new("PHI", "Philippines"), new("POL", "Poland"),
        new("POR", "Portugal"), new("PUR", "Puerto Rico"), new("ROU", "Romania"), new("RSA", "South Africa"),
        new("RUS", "Russia"), new("SMR", "San Marino"), new("SRB", "Serbia"), new("SLO", "Slovenia"),
        new("SUI", "Switzerland"), new("SVK", "Slovakia"), new("SWE", "Sweden"), new("THA", "Thailand"),
        new("TJK", "Tajikistan"), new("TPE", "Chinese Taipei"), new("TUR", "Türkiye"), new("UKR", "Ukraine"),
        new("URU", "Uruguay"), new("USA", "United States"), new("UZB", "Uzbekistan"), new("VEN", "Venezuela"),
    ];

    /// <summary>
    /// The FIS seasons offered for a competition date: the season of the date, one before and one after, plus the
    /// current value when it is a valid year outside that range. Ascending.
    /// </summary>
    public static IReadOnlyList<string> SeasonChoices(DateOnly? date, string? current)
    {
        var seasons = new SortedSet<int>();
        if (date is { } d)
        {
            var season = OpenSkiTime.Domain.FisSeason.FromDate(d);
            seasons.UnionWith([season - 1, season, season + 1]);
        }
        if (int.TryParse(current, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var value)
            && value is >= 1900 and <= 2999)
        { seasons.Add(value); }
        return seasons.Select(x => x.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray();
    }

    /// <summary>
    /// Series season labels offered for a series start date: the season of the date, one before and one after, as
    /// "2026/27". Empty without a date. The series season stays free text, so another label can still be typed.
    /// </summary>
    public static IReadOnlyList<string> SeriesSeasonChoices(DateOnly? startDate)
        => startDate is { } d ? [OpenSkiTime.Domain.FisSeason.SeriesLabel(d.AddYears(-1)), OpenSkiTime.Domain.FisSeason.SeriesLabel(d),
            OpenSkiTime.Domain.FisSeason.SeriesLabel(d.AddYears(1))] : [];

    /// <summary>True when the nation matches the search text by code or by name (case-insensitive).</summary>
    public static bool Matches(FisNation nation, string? search)
    {
        ArgumentNullException.ThrowIfNull(nation);
        if (string.IsNullOrWhiteSpace(search)) { return true; }
        var text = search.Trim();
        return nation.Code.StartsWith(text, StringComparison.OrdinalIgnoreCase)
            || nation.Name.Contains(text, StringComparison.OrdinalIgnoreCase);
    }
}
