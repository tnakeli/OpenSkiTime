using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using OpenSkiTime.Domain;

namespace OpenSkiTime.Application;

/// <summary>A race row of the public race list. Gender is M, W or empty when the list does not tell it.</summary>
public sealed record FissiRace(int RaceId, string Codex, string Discipline, string Gender);
/// <summary>A registered athlete as shown on the site; the name is one text ("First name SURNAME").</summary>
public sealed record FissiAthlete(string? Code, string FullName, int? BirthYear, string? Nation, string? Club);
public sealed record FissiRacePage(string Codex, DateOnly Date, IReadOnlyList<FissiAthlete> Athletes);

/// <summary>
/// Reads the public pages of regs.fissi.fi, which has no API. A registration page that is not completely understood is
/// refused: a missing athlete would be proposed for removal from the race.
/// </summary>
public static partial class FissiRegistrationHtml
{
    private const int MaximumRows = 1000;
    private const string Unreadable = "regs.fissi.fi returned a page that cannot be read completely. Nothing was changed; check the registrations on the site.";
    private static readonly string[] s_columns = ["col-fis", "col-year", "col-nat", "col-club", "col-pts"];

    public static IReadOnlyList<FissiRace> ReadRaces(string html)
    {
        ArgumentNullException.ThrowIfNull(html);
        return Read(html, page =>
        {
            var races = new List<FissiRace>();
            foreach (Match row in RaceRow().Matches(page))
            {
                var id = RaceId().Match(row.Groups["tag"].Value);
                var codex = RaceCodex().Match(row.Groups["body"].Value);
                // A race without a codex cannot be matched to a competition.
                if (!id.Success || !codex.Success
                    || !int.TryParse(id.Groups[1].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var raceId)) { continue; }
                var line = RaceLine().Match(row.Groups["body"].Value);
                races.Add(new FissiRace(raceId, codex.Groups[1].Value.PadLeft(4, '0'),
                    Text(RaceDiscipline().Match(row.Groups["body"].Value).Groups[1].Value).ToUpperInvariant(),
                    Gender(line.Success ? line.Groups[1].Value : string.Empty)));
                if (races.Count > MaximumRows) { throw new DomainValidationException(Unreadable); }
            }
            // Links to races but no readable row (or no race list at all): the page is not the race list this reader knows.
            return races.Count > 0 || (page.Contains("class=\"v3-segments\"", StringComparison.Ordinal) && !RaceId().IsMatch(page))
                ? races : throw new DomainValidationException(Unreadable);
        });
    }

    public static FissiRacePage ReadRace(string html)
    {
        ArgumentNullException.ThrowIfNull(html);
        return Read(html, page =>
        {
            var meta = Single(Meta(), page).Groups[1].Value;
            var codex = Single(MetaCodex(), meta).Groups[1].Value.PadLeft(4, '0');
            if (!DateOnly.TryParseExact(Single(MetaDate(), meta).Groups[1].Value, "d.M.yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            { throw new DomainValidationException(Unreadable); }
            if (!int.TryParse(Single(MetaCount(), meta).Groups[1].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var count) || count > MaximumRows)
            { throw new DomainValidationException(Unreadable); }
            // A race without registrations states the count 0 and has no table.
            if (count == 0 && !page.Contains("<table", StringComparison.OrdinalIgnoreCase)) { return new FissiRacePage(codex, date, []); }
            var athletes = ReadAthletes(Single(Table(), page).Groups[1].Value);
            return athletes.Count == count ? new FissiRacePage(codex, date, athletes) : throw new DomainValidationException(Unreadable);
        });
    }

    private static List<FissiAthlete> ReadAthletes(string table)
    {
        var headers = HeaderCell().Matches(Single(Head(), table).Groups[1].Value).Select(x => x.Groups[1].Value).ToArray();
        if (headers.Length != 7 || s_columns.Where((name, index) => !HasClass(headers[index + 2], name)).Any())
        { throw new DomainValidationException(Unreadable); }
        var body = Single(Body(), table).Groups[1].Value;
        var rows = Row().Matches(body);
        if (rows.Count != RowStart().Count(body) || rows.Count > MaximumRows) { throw new DomainValidationException(Unreadable); }
        return rows.Select(x => ReadAthlete(x.Groups[1].Value)).ToList();
    }

    private static FissiAthlete ReadAthlete(string row)
    {
        var cells = Cell().Matches(row);
        if (cells.Count != 7 || !HasClass(cells[0].Groups[1].Value, "row-num") || !HasClass(cells[1].Groups[1].Value, "name")
            || s_columns.Where((name, index) => !HasClass(cells[index + 2].Groups[1].Value, name)).Any())
        { throw new DomainValidationException(Unreadable); }
        string Content(int index) => Text(cells[index].Groups[2].Value);
        var name = Content(1);
        var code = Optional(Content(2));
        var yearText = Optional(Content(3));
        var nation = Optional(Text(Title().Match(cells[4].Groups[1].Value).Groups[1].Value))?.ToUpperInvariant();
        int? year = null;
        if (yearText is not null)
        {
            if (yearText.Length != 4 || !int.TryParse(yearText, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
            { throw new DomainValidationException(Unreadable); }
            year = value;
        }
        if (name.Length > 200 || !name.Any(char.IsLetter) || (code is not null && (code.Length > 20 || !code.All(char.IsAsciiLetterOrDigit)))
            || (nation is not null && (nation.Length != 3 || !nation.All(char.IsAsciiLetterUpper))))
        { throw new DomainValidationException(Unreadable); }
        return new FissiAthlete(code, name, year, nation, Optional(Content(5)));
    }

    private static T Read<T>(string html, Func<string, T> read)
    {
        // The timeouts bound the work on a hostile or broken page.
        try { return read(html); }
        catch (RegexMatchTimeoutException) { throw new DomainValidationException(Unreadable); }
    }

    private static Match Single(Regex pattern, string text)
    {
        var matches = pattern.Matches(text);
        return matches.Count == 1 ? matches[0] : throw new DomainValidationException(Unreadable);
    }

    private static string Text(string html) => Space().Replace(WebUtility.HtmlDecode(Tag().Replace(html, " ")), " ").Trim();
    private static string? Optional(string text) => text is "" or "-" or "–" or "—" ? null : text;
    private static bool HasClass(string attributes, string name)
        => Class().Match(attributes) is { Success: true } match && match.Groups[1].Value.Split(' ').Contains(name, StringComparer.Ordinal);

    // Line 2 is "<span>codex</span> Men · FIS".
    private static string Gender(string line) => Text(Span().Replace(line, " ")).Split('·')[0].Trim().ToUpperInvariant() switch
    {
        "MEN" or "MIEHET" => "M",
        "WOMEN" or "LADIES" or "NAISET" => "W",
        _ => string.Empty,
    };

    private const RegexOptions Options = RegexOptions.Singleline | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase;
    private const int Timeout = 2000;

    [GeneratedRegex("""(?<tag><a\s[^>]*class="[^"]*\bv3-race-row\b[^"]*"[^>]*>)(?<body>.*?)</a>""", Options, Timeout)]
    private static partial Regex RaceRow();
    [GeneratedRegex("""registered_athletes\.php\?race_id=(\d{1,9})\b""", Options, Timeout)]
    private static partial Regex RaceId();
    [GeneratedRegex("""<span class="v3-race-codex">\s*#([0-9]{1,4})\s*</span>""", Options, Timeout)]
    private static partial Regex RaceCodex();
    [GeneratedRegex("""<span class="v3-disc\b[^"]*">([^<]*)</span>""", Options, Timeout)]
    private static partial Regex RaceDiscipline();
    [GeneratedRegex("""<div class="v3-race-line2">(.*?)</div>""", Options, Timeout)]
    private static partial Regex RaceLine();
    [GeneratedRegex("""<div class="meta">(.*?)</div>""", Options, Timeout)]
    private static partial Regex Meta();
    [GeneratedRegex("""<span class="v3-mono">\s*#([0-9]{1,4})\s*</span>""", Options, Timeout)]
    private static partial Regex MetaCodex();
    [GeneratedRegex("""<span class="v3-mono v3-tiny v3-upper">\s*([0-9]{1,2}\.[0-9]{1,2}\.[0-9]{4})\s*</span>""", Options, Timeout)]
    private static partial Regex MetaDate();
    [GeneratedRegex("""fa-users"></i>\s*([0-9]{1,6})\s*</span>""", Options, Timeout)]
    private static partial Regex MetaCount();
    [GeneratedRegex("""<table\s[^>]*id="reg-ath-table"[^>]*>(.*?)</table>""", Options, Timeout)]
    private static partial Regex Table();
    [GeneratedRegex("""<thead[^>]*>(.*?)</thead>""", Options, Timeout)]
    private static partial Regex Head();
    [GeneratedRegex("""<tbody[^>]*>(.*?)</tbody>""", Options, Timeout)]
    private static partial Regex Body();
    [GeneratedRegex("""<th\b([^>]*)>""", Options, Timeout)]
    private static partial Regex HeaderCell();
    [GeneratedRegex("""<tr\b""", Options, Timeout)]
    private static partial Regex RowStart();
    [GeneratedRegex("""<tr\b[^>]*>(.*?)</tr>""", Options, Timeout)]
    private static partial Regex Row();
    [GeneratedRegex("""<td\b([^>]*)>(.*?)</td>""", Options, Timeout)]
    private static partial Regex Cell();
    [GeneratedRegex("(?<![-\\w])title=\"([^\"]*)\"", Options, Timeout)]
    private static partial Regex Title();
    [GeneratedRegex("(?<![-\\w])class=\"([^\"]*)\"", Options, Timeout)]
    private static partial Regex Class();
    [GeneratedRegex("""<span\b[^>]*>.*?</span>""", Options, Timeout)]
    private static partial Regex Span();
    [GeneratedRegex("<[^>]*>", Options, Timeout)]
    private static partial Regex Tag();
    [GeneratedRegex(@"\s+", Options, Timeout)]
    private static partial Regex Space();
}
