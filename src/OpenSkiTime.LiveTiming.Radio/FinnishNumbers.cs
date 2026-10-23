namespace OpenSkiTime.LiveTiming.Radio;

public enum FinnishCase { Nominative, Genitive, Partitive, Inessive, Elative, Illative, Adessive, Ablative, Allative, Essive, Translative }

/// <summary>
/// Inflected Finnish cardinal and ordinal numerals. Every component of a compound numeral takes the same case
/// (kahdellakymmenelläkolmella); in the nominative a multiplied ten, hundred or thousand is partitive (kaksikymmentä).
/// Speech needs the inflected form: a nominative number read in place of an inflected one sounds wrong to listeners.
/// </summary>
public static class FinnishNumbers
{
    public static readonly IReadOnlyDictionary<string, FinnishCase> Codes = new Dictionary<string, FinnishCase>(StringComparer.Ordinal)
    {
        ["nom"] = FinnishCase.Nominative, ["gen"] = FinnishCase.Genitive, ["par"] = FinnishCase.Partitive,
        ["ine"] = FinnishCase.Inessive, ["ela"] = FinnishCase.Elative, ["ill"] = FinnishCase.Illative,
        ["ade"] = FinnishCase.Adessive, ["abl"] = FinnishCase.Ablative, ["all"] = FinnishCase.Allative,
        ["ess"] = FinnishCase.Essive, ["tra"] = FinnishCase.Translative
    };

    // Case order: nominative, genitive, partitive, inessive, elative, illative, adessive, ablative, allative, essive, translative.
    private static readonly Dictionary<int, string[]> Cardinals = new()
    {
        [0] = ["nolla", "nollan", "nollaa", "nollassa", "nollasta", "nollaan", "nollalla", "nollalta", "nollalle", "nollana", "nollaksi"],
        [1] = ["yksi", "yhden", "yhtä", "yhdessä", "yhdestä", "yhteen", "yhdellä", "yhdeltä", "yhdelle", "yhtenä", "yhdeksi"],
        [2] = ["kaksi", "kahden", "kahta", "kahdessa", "kahdesta", "kahteen", "kahdella", "kahdelta", "kahdelle", "kahtena", "kahdeksi"],
        [3] = ["kolme", "kolmen", "kolmea", "kolmessa", "kolmesta", "kolmeen", "kolmella", "kolmelta", "kolmelle", "kolmena", "kolmeksi"],
        [4] = ["neljä", "neljän", "neljää", "neljässä", "neljästä", "neljään", "neljällä", "neljältä", "neljälle", "neljänä", "neljäksi"],
        [5] = ["viisi", "viiden", "viittä", "viidessä", "viidestä", "viiteen", "viidellä", "viideltä", "viidelle", "viitenä", "viideksi"],
        [6] = ["kuusi", "kuuden", "kuutta", "kuudessa", "kuudesta", "kuuteen", "kuudella", "kuudelta", "kuudelle", "kuutena", "kuudeksi"],
        [7] = ["seitsemän", "seitsemän", "seitsemää", "seitsemässä", "seitsemästä", "seitsemään", "seitsemällä", "seitsemältä", "seitsemälle", "seitsemänä", "seitsemäksi"],
        [8] = ["kahdeksan", "kahdeksan", "kahdeksaa", "kahdeksassa", "kahdeksasta", "kahdeksaan", "kahdeksalla", "kahdeksalta", "kahdeksalle", "kahdeksana", "kahdeksaksi"],
        [9] = ["yhdeksän", "yhdeksän", "yhdeksää", "yhdeksässä", "yhdeksästä", "yhdeksään", "yhdeksällä", "yhdeksältä", "yhdeksälle", "yhdeksänä", "yhdeksäksi"],
        [10] = ["kymmenen", "kymmenen", "kymmentä", "kymmenessä", "kymmenestä", "kymmeneen", "kymmenellä", "kymmeneltä", "kymmenelle", "kymmenenä", "kymmeneksi"],
        [100] = ["sata", "sadan", "sataa", "sadassa", "sadasta", "sataan", "sadalla", "sadalta", "sadalle", "satana", "sadaksi"],
        [1000] = ["tuhat", "tuhannen", "tuhatta", "tuhannessa", "tuhannesta", "tuhanteen", "tuhannella", "tuhannelta", "tuhannelle", "tuhantena", "tuhanneksi"]
    };
    private static readonly string[] First = ["ensimmäinen", "ensimmäisen", "ensimmäistä", "ensimmäisessä", "ensimmäisestä", "ensimmäiseen", "ensimmäisellä", "ensimmäiseltä", "ensimmäiselle", "ensimmäisenä", "ensimmäiseksi"];
    private static readonly string[] Second = ["toinen", "toisen", "toista", "toisessa", "toisesta", "toiseen", "toisella", "toiselta", "toiselle", "toisena", "toiseksi"];
    // Stems of the -s ordinals (kolma-s, kolma-nnen, kolma-tta). 1 and 2 use yhde-/kahde- inside compounds (yhdestoista).
    private static readonly Dictionary<int, string> OrdinalStems = new()
    {
        [1] = "yhde", [2] = "kahde", [3] = "kolma", [4] = "neljä", [5] = "viide", [6] = "kuude", [7] = "seitsemä",
        [8] = "kahdeksa", [9] = "yhdeksä", [10] = "kymmene", [100] = "sada", [1000] = "tuhanne"
    };

    public static string Cardinal(long value, FinnishCase @case = FinnishCase.Nominative)
    {
        if (value is < 0 or > 999_999) { throw new ArgumentOutOfRangeException(nameof(value), "Supported range is 0-999999."); }
        if (value == 0) { return Cardinals[0][(int)@case]; }
        var parts = new List<string>();
        var thousands = value / 1000;
        if (thousands == 1) { parts.Add(Cardinals[1000][(int)@case]); }
        else if (thousands > 1) { CardinalBelow1000(thousands, @case, parts); parts.Add(Multiplied(1000, @case)); }
        CardinalBelow1000(value % 1000, @case, parts);
        return string.Concat(parts);
    }
    public static string Ordinal(long value, FinnishCase @case = FinnishCase.Nominative)
    {
        if (value is < 1 or > 9999) { throw new ArgumentOutOfRangeException(nameof(value), "Supported range is 1-9999."); }
        var parts = new List<string>();
        var thousands = value / 1000;
        if (thousands == 1) { parts.Add(OrdinalStem(1000, @case)); }
        else if (thousands > 1) { parts.Add(OrdinalStem((int)thousands, @case)); parts.Add(OrdinalStem(1000, @case)); }
        var rest = value % 1000;
        var hundreds = rest / 100;
        if (hundreds == 1) { parts.Add(OrdinalStem(100, @case)); }
        else if (hundreds > 1) { parts.Add(OrdinalStem((int)hundreds, @case)); parts.Add(OrdinalStem(100, @case)); }
        var tens = (int)(rest % 100);
        if (tens is >= 1 and <= 9) { parts.Add(Last(tens, @case)); }
        else if (tens == 10) { parts.Add(OrdinalStem(10, @case)); }
        else if (tens is >= 11 and <= 19) { parts.Add(OrdinalStem(tens - 10, @case)); parts.Add("toista"); }
        else if (tens >= 20)
        {
            parts.Add(OrdinalStem(tens / 10, @case)); parts.Add(OrdinalStem(10, @case));
            if (tens % 10 > 0) { parts.Add(Last(tens % 10, @case)); }
        }
        return string.Concat(parts);
    }
    private static void CardinalBelow1000(long value, FinnishCase @case, List<string> parts)
    {
        var hundreds = value / 100;
        if (hundreds == 1) { parts.Add(Cardinals[100][(int)@case]); }
        else if (hundreds > 1) { parts.Add(Cardinals[(int)hundreds][(int)@case]); parts.Add(Multiplied(100, @case)); }
        var rest = (int)(value % 100);
        if (rest is >= 1 and <= 10) { parts.Add(Cardinals[rest][(int)@case]); }
        else if (rest is >= 11 and <= 19) { parts.Add(Cardinals[rest - 10][(int)@case]); parts.Add("toista"); }
        else if (rest >= 20)
        {
            parts.Add(Cardinals[rest / 10][(int)@case]); parts.Add(Multiplied(10, @case));
            if (rest % 10 > 0) { parts.Add(Cardinals[rest % 10][(int)@case]); }
        }
    }
    private static string Multiplied(int unit, FinnishCase @case) => Cardinals[unit][(int)(@case == FinnishCase.Nominative ? FinnishCase.Partitive : @case)];
    private static string Last(int digit, FinnishCase @case) => digit switch
    {
        1 => First[(int)@case], 2 => Second[(int)@case], _ => OrdinalStem(digit, @case)
    };
    private static string OrdinalStem(int unit, FinnishCase @case)
    {
        var stem = OrdinalStems[unit];
        var a = stem.Any(c => c is 'a' or 'o' or 'u') ? "a" : "ä";
        return stem + @case switch
        {
            FinnishCase.Nominative => "s", FinnishCase.Genitive => "nnen", FinnishCase.Partitive => "tt" + a,
            FinnishCase.Inessive => "nness" + a, FinnishCase.Elative => "nnest" + a, FinnishCase.Illative => "nteen",
            FinnishCase.Adessive => "nnell" + a, FinnishCase.Ablative => "nnelt" + a, FinnishCase.Allative => "nnelle",
            FinnishCase.Essive => "nten" + a, _ => "nneksi"
        };
    }
}
