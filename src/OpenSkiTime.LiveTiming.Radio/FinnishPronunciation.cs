using System.Globalization;
using System.Text.RegularExpressions;

namespace OpenSkiTime.LiveTiming.Radio;

/// <summary>
/// Rewrites a foreign racer name so that a Finnish voice says it closer to its own language: ordered spelling rules per
/// nation (German "Schneider" → "Šnaider", Swedish "Sjöberg" → "Šöberj", Italian "Giacomo" → "Džakomo"). Finnish
/// voices read Finnish spelling, including š and ž, letter by letter as written. Deterministic and offline; the names
/// never leave this computer for it. Languages without regular spelling (English) are left as written, and an operator
/// lexicon entry always wins.
/// </summary>
public static class FinnishPronunciation
{
    private static readonly (string Pattern, string Replacement)[] German =
    [
        ("tsch", "tš"), ("sch", "š"), ("^sp", "šp"), ("^st", "št"), ("chs", "ks"), ("^ch(?=r)", "k"), ("ch", "h"), ("ck", "k"), ("ph", "f"),
        ("th", "t"), ("tz", "ts"), ("z", "ts"), ("ß", "s"), ("ei", "ai"), ("ey", "ai"), ("eu", "oi"), ("äu", "oi"), ("ie", "ii"),
        ("v", "f"), ("w", "v"), ("qu", "kv"), ("ü", "y"), ("([aeiou])h(?=[^aeiouäöüy]|$)", "$1$1")
    ];
    private static readonly (string, string)[] Scandinavian =
    [
        ("stj", "š"), ("skj", "š"), ("sch", "š"), ("sj", "š"), ("^sk(?=[eiyäöø])", "š"), ("tj", "š"), ("kj", "š"), ("^k(?=[eiyäöø])", "š"),
        ("^g(?=[eiyäöø])", "j"), ("^(dj|gj|hj|lj)", "j"), ("(?<=[lr])g$", "j"), ("ø", "ö"), ("æ", "ä"), ("aa", "oo"), ("å", "oo"),
        ("ck", "k"), ("ph", "f"), ("th", "t"), ("qu", "kv"), ("qv", "kv"), ("q", "k"), ("x", "ks"), ("z", "s"), ("c(?=[eiy])", "s"), ("c", "k"),
        ("w", "v")
    ];
    private static readonly (string, string)[] Norwegian = [("ei", "äi"), .. Scandinavian];
    private static readonly (string, string)[] Danish =
    [
        ("ø", "ö"), ("æ", "ä"), ("aa", "oo"), ("å", "oo"), ("ck", "k"), ("ph", "f"), ("th", "t"), ("qu", "kv"), ("x", "ks"), ("z", "s"),
        ("c(?=[eiy])", "s"), ("c", "k"), ("w", "v")
    ];
    private static readonly (string, string)[] Italian =
    [
        ("cch", "kk"), ("ch", "k"), ("gh", "g"), ("gli", "lji"), ("gn", "nj"), ("sci(?=[aou])", "š"), ("sc(?=[ei])", "š"),
        ("cci(?=[aou])", "tš"), ("cc(?=[ei])", "tš"), ("ci(?=[aou])", "tš"), ("c(?=[ei])", "tš"), ("ggi(?=[aou])", "dž"), ("gg(?=[ei])", "dž"),
        ("gi(?=[aou])", "dž"), ("g(?=[ei])", "dž"), ("c", "k"), ("zz", "ts"), ("z", "ts"), ("qu", "kv"), ("h", "")
    ];
    private static readonly (string, string)[] Spanish =
    [
        // Silent h and the j sound go first, so letters produced later are not converted again.
        ("ch", "tš"), ("h", ""), ("j", "h"), ("g(?=[ei])", "h"), ("ll", "j"), ("ñ", "nj"), ("qu", "k"), ("gu(?=[ei])", "g"), ("z", "s"),
        ("c(?=[ei])", "s"), ("c", "k"), ("á", "a"), ("é", "e"), ("í", "i"), ("ó", "o"), ("ú", "u"), ("y$", "i")
    ];
    private static readonly (string, string)[] French =
    [
        ("eau", "oo"), ("au", "oo"), ("ou", "u"), ("oi", "ua"), ("ch", "š"), ("qu", "k"), ("gn", "nj"), ("ç", "s"), ("[éèêë]", "e"),
        ("â", "a"), ("î", "i"), ("ô", "o"), ("û", "u"), ("j", "ž"), ("g(?=[eiy])", "ž"), ("c(?=[eiy])", "s"), ("c", "k"), ("h", ""),
        ("w", "v"), ("z", "s"), ("(?<=\\p{L}{3})e$", ""), ("(?<=[aeiouy])[stdx]$", "")
    ];
    private static readonly (string, string)[] Czech =
    [
        ("ch", "h"), ("č", "tš"), ("c", "ts"), ("ř", "rž"), ("ý", "ii"), ("á", "aa"), ("é", "ee"), ("í", "ii"), ("ó", "oo"), ("[úů]", "uu"),
        ("ě", "je"), ("ť", "t"), ("ď", "d"), ("ň", "nj"), ("[ĺľ]", "l"), ("ô", "uo"), ("ä", "e"), ("w", "v")
    ];
    private static readonly (string, string)[] Polish =
    [
        ("szcz", "štš"), ("sz", "š"), ("cz", "tš"), ("rz", "ž"), ("ch", "h"), ("d[żź]", "dž"), ("ś", "š"), ("ć", "tš"), ("[źż]", "ž"),
        ("ń", "nj"), ("ł", "u"), ("ó", "u"), ("ą", "on"), ("ę", "en"), ("w", "v"), ("c", "ts")
    ];
    private static readonly (string, string)[] SouthSlavic = [("č", "tš"), ("ć", "tš"), ("đ", "dž"), ("c", "ts"), ("w", "v")];
    private static readonly (string, string)[] Baltic =
    [
        ("č", "tš"), ("c", "ts"), ("ā", "aa"), ("ē", "ee"), ("ī", "ii"), ("ū", "uu"), ("ō", "oo"), ("ė", "e"), ("y", "ii"), ("ą", "a"),
        ("ę", "e"), ("į", "i"), ("ų", "u"), ("ģ", "dj"), ("ķ", "tj"), ("ļ", "lj"), ("ņ", "nj")
    ];
    private static readonly Dictionary<string, (string, string)[]> Rules = new(StringComparer.OrdinalIgnoreCase)
    {
        ["GER"] = German, ["AUT"] = German, ["SUI"] = German, ["LIE"] = German, ["SWE"] = Scandinavian, ["NOR"] = Norwegian, ["DEN"] = Danish,
        ["ITA"] = Italian, ["SMR"] = Italian, ["ESP"] = Spanish, ["AND"] = Spanish, ["ARG"] = Spanish, ["CHI"] = Spanish, ["MEX"] = Spanish,
        ["FRA"] = French, ["MON"] = French, ["CZE"] = Czech, ["SVK"] = Czech, ["POL"] = Polish, ["SLO"] = SouthSlavic, ["CRO"] = SouthSlavic,
        ["BIH"] = SouthSlavic, ["SRB"] = SouthSlavic, ["MNE"] = SouthSlavic, ["LAT"] = Baltic, ["LTU"] = Baltic
    };
    private static readonly Dictionary<string, Regex> Compiled = [];
    private static readonly Lock Gate = new();

    /// <summary>The spoken form of one name word (with hyphenated parts) for a racer of the given nation.</summary>
    public static string Spoken(string word, string nation)
    {
        ArgumentNullException.ThrowIfNull(word);
        if (string.IsNullOrEmpty(nation) || !Rules.TryGetValue(nation.Trim(), out var rules)) { return word; }
        return string.Join('-', word.Split('-').Select(part => Part(part, rules)));
    }
    private static string Part(string part, (string Pattern, string Replacement)[] rules)
    {
        if (part.Length == 0) { return part; }
        var culture = CultureInfo.GetCultureInfo("fi-FI");
        var spoken = part.ToLower(culture);
        foreach (var (pattern, replacement) in rules) { spoken = Regex(pattern).Replace(spoken, replacement); }
        if (spoken.Length == 0) { return part; }
        // A capital stays a capital: "Göran" → "Jöran", "von" → "fon".
        return char.IsUpper(part[0]) ? char.ToUpper(spoken[0], culture) + spoken[1..] : spoken;
    }
    private static Regex Regex(string pattern)
    {
        lock (Gate)
        {
            if (!Compiled.TryGetValue(pattern, out var regex))
            { Compiled[pattern] = regex = new Regex(pattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)); }
            return regex;
        }
    }
}
