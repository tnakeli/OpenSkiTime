using System.Globalization;

namespace OpenSkiTime.LiveTiming.Radio;

/// <summary>
/// Speaks the display values timing already calculated. Times and differences are the published hundredths, never
/// recomputed or re-rounded; statuses use fixed alpine terminology; names come verbatim from the snapshot.
/// </summary>
public static class FinnishVerbalizer
{
    /// <summary>Run or total time: "yksi minuutti viisi pilkku nolla kolme" or "viisikymmentäyhdeksän pilkku kaksitoista sekuntia".</summary>
    public static string Time(long hundredths)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(hundredths);
        var minutes = hundredths / 6000;
        var seconds = hundredths / 100 % 60;
        var fraction = Fraction(hundredths % 100);
        return minutes == 0
            ? $"{FinnishNumbers.Cardinal(seconds)} pilkku {fraction} sekuntia"
            : $"{FinnishNumbers.Cardinal(minutes)} {(minutes == 1 ? "minuutti" : "minuuttia")} {FinnishNumbers.Cardinal(seconds)} pilkku {fraction}";
    }
    /// <summary>Gap to the leader: always ends with "sekuntia" or "minuuttia ... sekuntia" so it cannot be heard as a time.</summary>
    public static string Difference(long hundredths)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(hundredths);
        var minutes = hundredths / 6000;
        var seconds = $"{FinnishNumbers.Cardinal(hundredths / 100 % 60)} pilkku {Fraction(hundredths % 100)} sekuntia";
        return minutes == 0 ? seconds : $"{FinnishNumbers.Cardinal(minutes)} {(minutes == 1 ? "minuutti" : "minuuttia")} {seconds}";
    }
    // Hundredths are read digit by digit when below ten, so 0,05 and 0,50 never sound alike.
    private static string Fraction(long hundredths) => hundredths < 10
        ? "nolla " + FinnishNumbers.Cardinal(hundredths) : FinnishNumbers.Cardinal(hundredths);

    /// <summary>Status as a predicate after the racer name ("keskeytti") or as a noun ("keskeytys").</summary>
    public static string Status(LiveStatus status, bool noun = false) => (status, noun) switch
    {
        (LiveStatus.DNF, false) => "keskeytti",
        (LiveStatus.DNF, true) => "keskeytys",
        (LiveStatus.DSQ, false) => "hylättiin",
        (LiveStatus.DSQ, true) => "hylkäys",
        (LiveStatus.DNS, false) => "ei lähtenyt matkaan",
        (LiveStatus.DNS, true) => "poisjäänti lähdöstä",
        (LiveStatus.NPS, false) => "ei lähtenyt matkaan",
        (LiveStatus.NPS, true) => "poisjäänti lähdöstä",
        (LiveStatus.Finished, false) => "tuli maaliin",
        (LiveStatus.Finished, true) => "maaliintulo",
        (LiveStatus.OnCourse, false) => "on radalla",
        (LiveStatus.OnCourse, true) => "lasku käynnissä",
        (_, false) => "odottaa lähtöä",
        _ => "lähtövuoro tulossa"
    };

    /// <summary>
    /// "First Last" as published. Only an all-capital surname (the timing list convention) is title-cased, because speech
    /// engines may spell capitals letter by letter. A foreign racer's name is respelled for a Finnish voice by the racer's
    /// nation (<see cref="FinnishPronunciation"/>). An operator lexicon entry (whole name or one word) always wins.
    /// </summary>
    public static string Name(LiveCompetitor competitor, IReadOnlyDictionary<string, string> lexicon)
    {
        ArgumentNullException.ThrowIfNull(competitor); ArgumentNullException.ThrowIfNull(lexicon);
        var written = Written(competitor);
        if (lexicon.TryGetValue(written, out var spoken)) { return spoken; }
        return string.Join(' ', written.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(x => lexicon.TryGetValue(x, out var word) ? word : FinnishPronunciation.Spoken(x, competitor.Nation)));
    }

    // Finnish adjectives of nationality, spoken before the name in an international field ("ruotsalainen Saga Porttila").
    private static readonly Dictionary<string, string> Demonyms = new(StringComparer.OrdinalIgnoreCase)
    {
        ["FIN"] = "suomalainen", ["SWE"] = "ruotsalainen", ["NOR"] = "norjalainen", ["DEN"] = "tanskalainen", ["ISL"] = "islantilainen",
        ["EST"] = "virolainen", ["LAT"] = "latvialainen", ["LTU"] = "liettualainen", ["GER"] = "saksalainen", ["AUT"] = "itävaltalainen",
        ["SUI"] = "sveitsiläinen", ["LIE"] = "liechtensteinilainen", ["FRA"] = "ranskalainen", ["ITA"] = "italialainen", ["ESP"] = "espanjalainen",
        ["AND"] = "andorralainen", ["MON"] = "monacolainen", ["BEL"] = "belgialainen", ["NED"] = "hollantilainen", ["GBR"] = "brittiläinen",
        ["IRL"] = "irlantilainen", ["POL"] = "puolalainen", ["CZE"] = "tšekkiläinen", ["SVK"] = "slovakialainen", ["SLO"] = "slovenialainen",
        ["CRO"] = "kroatialainen", ["BIH"] = "bosnialainen", ["SRB"] = "serbialainen", ["MNE"] = "montenegrolainen", ["HUN"] = "unkarilainen",
        ["ROU"] = "romanialainen", ["BUL"] = "bulgarialainen", ["GRE"] = "kreikkalainen", ["TUR"] = "turkkilainen", ["UKR"] = "ukrainalainen",
        ["USA"] = "yhdysvaltalainen", ["CAN"] = "kanadalainen", ["ARG"] = "argentiinalainen", ["CHI"] = "chileläinen", ["AUS"] = "australialainen",
        ["NZL"] = "uusiseelantilainen", ["JPN"] = "japanilainen", ["KOR"] = "korealainen", ["CHN"] = "kiinalainen"
    };
    public static string? Demonym(string nation) => Demonyms.GetValueOrDefault((nation ?? "").Trim());
    public static string Written(LiveCompetitor competitor)
    {
        ArgumentNullException.ThrowIfNull(competitor);
        var last = competitor.LastName.Any(char.IsLower) ? competitor.LastName : TitleCase(competitor.LastName);
        return string.Join(' ', new[] { competitor.FirstName.Trim(), last.Trim() }.Where(x => x.Length > 0));
    }
    private static string TitleCase(string upper)
    {
        var chars = upper.ToLower(CultureInfo.GetCultureInfo("fi-FI")).ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        { if (i == 0 || chars[i - 1] is ' ' or '-' or '\'') { chars[i] = char.ToUpper(chars[i], CultureInfo.GetCultureInfo("fi-FI")); } }
        return new string(chars);
    }
    /// <summary>Parses "written = spoken" lines. Invalid lines are ignored; the lexicon is optional operator help.</summary>
    public static IReadOnlyDictionary<string, string> Lexicon(string text)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in (text ?? "").Split('\n'))
        {
            var separator = line.IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0) { continue; }
            var written = line[..separator].Trim(); var spoken = line[(separator + 1)..].Trim();
            if (written.Length is > 0 and <= 80 && spoken.Length is > 0 and <= 80 && !spoken.Any(char.IsDigit)) { map[written] = spoken; }
        }
        return map;
    }
}
