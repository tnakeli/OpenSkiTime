using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace OpenSkiTime.LiveTiming.Radio;

public sealed record SlotToken(string Kind, string Reference, string? Case, string Text);

/// <summary>Words a line may contain besides slots, and the racer names it must never write itself.</summary>
public sealed partial class LineRules
{
    public LineRules(LiveSnapshot state, IEnumerable<string> hostNames, string operatorContext)
    {
        ArgumentNullException.ThrowIfNull(state); ArgumentNullException.ThrowIfNull(hostNames);
        Hosts = hostNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in state.Competitors)
        {
            foreach (var part in $"{c.FirstName} {c.LastName}".Split([' ', '-'], StringSplitOptions.RemoveEmptyEntries))
            { if (part.Length >= 3 && !Hosts.Contains(part)) { names.Add(part); } }
        }
        RacerNames = names;
        var words = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "FIS", "SM", "MM", "EM", "Suomi", "Suomen", "Suomessa" };
        var sources = new[] { state.Competition.Name, state.Competition.Place, state.Competition.Slope, operatorContext }
            .Concat(state.Competitors.Select(x => x.Club)).Concat(Hosts);
        foreach (var text in sources) { foreach (Match m in Words().Matches(text ?? "")) { words.Add(m.Value); } }
        Allowed = words;
        // Inflected forms of allowed names (Testitunturilla, Rinneseuran) and of the racers' countries (Ruotsin, Ruotsista).
        Stems = state.Competitors.Select(x => x.Nation).Append("FIN").Distinct(StringComparer.OrdinalIgnoreCase)
            .SelectMany(x => NationStems.TryGetValue(x.ToUpperInvariant(), out var stems) ? stems : [])
            .Concat(words.Where(x => x.Length >= 5 && char.IsUpper(x[0])).Select(x => x[..^1])).Distinct().ToArray();
    }
    public IReadOnlySet<string> Hosts { get; }
    public IReadOnlySet<string> RacerNames { get; }
    public IReadOnlySet<string> Allowed { get; }
    public IReadOnlyList<string> Stems { get; }
    internal static Regex WordPattern => Words();
    [GeneratedRegex(@"\p{L}[\p{L}\-']*")]
    private static partial Regex Words();
    // Finnish country stems so hosts may name the racers' countries in any case (Ruotsi, Ruotsin, Ruotsista).
    private static readonly Dictionary<string, string[]> NationStems = new(StringComparer.Ordinal)
    {
        ["FIN"] = ["Suom"], ["SWE"] = ["Ruots"], ["NOR"] = ["Norj"], ["AUT"] = ["Itäval"], ["SUI"] = ["Sveits"], ["GER"] = ["Saks"],
        ["FRA"] = ["Ransk"], ["ITA"] = ["Itali"], ["USA"] = ["Yhdysval"], ["CAN"] = ["Kanad"], ["SLO"] = ["Sloveni"], ["CRO"] = ["Kroati"],
        ["CZE"] = ["Tšeki", "Tseki"], ["SVK"] = ["Slovaki"], ["POL"] = ["Puol"], ["GBR"] = ["Britanni", "Iso-Britanni"], ["EST"] = ["Viro", "Viron", "Virossa", "Virosta", "Viroon"],
        ["LAT"] = ["Latvi"], ["LTU"] = ["Liettu"], ["JPN"] = ["Japan"], ["AND"] = ["Andorr"], ["ESP"] = ["Espanj"], ["BEL"] = ["Belgi"],
        ["NED"] = ["Alankoma", "Hollan"], ["DEN"] = ["Tansk"], ["ISL"] = ["Islan"], ["AUS"] = ["Australi"], ["NZL"] = ["Uusi-Seelan", "Uuden-Seelan", "Uudessa-Seelan"],
        ["CHN"] = ["Kiin"], ["KOR"] = ["Korea"], ["ARG"] = ["Argentiin"], ["CHI"] = ["Chile"], ["BUL"] = ["Bulgari"], ["ROU"] = ["Romani"],
        ["HUN"] = ["Unkar"], ["UKR"] = ["Ukrain"], ["LIE"] = ["Liechtenstein"], ["MON"] = ["Monaco"], ["BIH"] = ["Bosni"], ["SRB"] = ["Serbi"]
    };
}

/// <summary>
/// Slot tokens such as {racer:12}, {time:12} or {rank:12:ess}. The language model writes only slots for facts; code
/// validates every line, renders the slots in Finnish and derives the conditions that make a clip stale.
/// </summary>
public static partial class RadioSlots
{
    private static readonly string[] Cardinal = [.. FinnishNumbers.Codes.Keys];
    private static readonly string[] Rank = [.. FinnishNumbers.Codes.Keys, "card"];
    private static readonly Dictionary<string, string[]> CasesByKind = new(StringComparer.Ordinal)
    {
        ["racer"] = ["nom"], ["bib"] = Cardinal, ["rank"] = Rank, ["totalrank"] = Rank, ["prevrank"] = Rank,
        ["time"] = ["nom"], ["total"] = ["nom"], ["prevtime"] = ["nom"], ["diff"] = ["nom"], ["totaldiff"] = ["nom"],
        ["status"] = ["verb", "noun"], ["num"] = Cardinal, ["ord"] = [.. FinnishNumbers.Codes.Keys]
    };
    public const int MaxLineLength = 400;
    private static readonly string[] NumberWords = ["kaksi", "kahde", "kahta", "kolm", "nelj", "viisi", "viide", "viitt", "kuusi", "kuude", "kuutt",
        "seitsem", "kahdeks", "yhdeks", "kymmen", "sata", "sada", "tuhat", "tuhan", "nolla", "pilkku", "sekun", "minuut", "sadasos"];

    [GeneratedRegex(@"\{(?<kind>[a-z]+):(?<ref>[a-z0-9]{1,12})(?::(?<case>[a-z]{3,4}))?\}")]
    private static partial Regex Pattern();
    [GeneratedRegex(@"[\{\}]")]
    private static partial Regex Braces();

    public static IReadOnlyList<SlotToken> Parse(string text) => Pattern().Matches(text ?? "")
        .Select(m => new SlotToken(m.Groups["kind"].Value, m.Groups["ref"].Value, m.Groups["case"].Success ? m.Groups["case"].Value : null, m.Value)).ToArray();

    /// <summary>The slots the model may use for one racer, given which facts that racer currently has.</summary>
    public static string[] Available(FactSheet facts, int bib)
    {
        ArgumentNullException.ThrowIfNull(facts);
        if (!facts.Racers.TryGetValue(bib, out var racer)) { return []; }
        var list = new List<string> { $"{{racer:{bib}}}", $"{{bib:{bib}:<case>}}", $"{{status:{bib}:verb|noun}}" };
        if (racer.Result?.Hundredths is not null && racer.Result.Status == LiveStatus.Finished) { list.Add($"{{time:{bib}}}"); }
        if (racer.Result?.Rank is not null) { list.Add($"{{rank:{bib}:<case>|card}}"); }
        if (racer.Result?.Difference is not null) { list.Add($"{{diff:{bib}}}"); }
        if (racer.Result?.TotalHundredths is not null) { list.AddRange([$"{{total:{bib}}}", $"{{totalrank:{bib}:<case>|card}}", $"{{totaldiff:{bib}}}"]); }
        if (racer.Previous?.Hundredths is not null && racer.Previous.Status == LiveStatus.Finished) { list.Add($"{{prevtime:{bib}}}"); }
        if (racer.Previous?.Rank is not null) { list.Add($"{{prevrank:{bib}:<case>|card}}"); }
        return [.. list];
    }

    /// <summary>Returns why a line must not be spoken, or null when it is valid.</summary>
    public static string? Problem(string text, FactSheet facts, LineRules rules)
    {
        ArgumentNullException.ThrowIfNull(facts); ArgumentNullException.ThrowIfNull(rules);
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaxLineLength || text.Any(char.IsControl)) { return "empty or oversized line"; }
        foreach (var slot in Parse(text))
        {
            if (!CasesByKind.TryGetValue(slot.Kind, out var cases)) { return "unknown slot"; }
            if (!cases.Contains(slot.Case ?? cases[0])) { return "disallowed case"; }
            if (Value(slot, facts) is null) { return "unknown slot"; }
        }
        var stripped = Pattern().Replace(text, "§");
        if (Braces().IsMatch(stripped)) { return "malformed slot"; }
        if (stripped.Any(char.IsDigit)) { return "digits outside slots"; }
        var words = LineRules.WordPattern.Matches(stripped);
        foreach (Match word in words)
        {
            var value = word.Value;
            var parts = value.Split('-', StringSplitOptions.RemoveEmptyEntries);
            // Counts, places and times come only from slots; "yksi", "ensimmäinen" and "toinen" stay usable as ordinary words.
            if (NumberWords.Any(n => value.StartsWith(n, StringComparison.OrdinalIgnoreCase))) { return "number words outside slots"; }
            // Eleven in any form (yksitoista, yhdentoista, yhdestoista), without rejecting "yhdessä" (together).
            if (value.StartsWith('y') || value.StartsWith('Y'))
            { if (value.Contains("toist", StringComparison.OrdinalIgnoreCase)) { return "number words outside slots"; } }
            // Racer names come only from slots. Lower-case common words (mäki, kivi) are not names.
            if (parts.Any(p => rules.RacerNames.Contains(p) && char.IsUpper(p[0]))) { return "racer name outside slots"; }
            if (!char.IsUpper(value[0]) || SentenceStart(stripped, word.Index)) { continue; }
            var head = parts[0];
            if (rules.Allowed.Contains(value) || rules.Allowed.Contains(head) || rules.Stems.Any(s => value.StartsWith(s, StringComparison.OrdinalIgnoreCase))) { continue; }
            if (head.Length <= 4 && head.All(char.IsUpper)) { continue; } // Abbreviations such as FIS or SM-kisat.
            return "unknown proper name";
        }
        return null;
    }
    private static bool SentenceStart(string text, int index)
    {
        for (var i = index - 1; i >= 0; i--)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c) || c is '"' or '\'' or '–' or '—' or '-' or '(' or '«' or '»') { continue; }
            return c is '.' or '!' or '?' or ':' or '…';
        }
        return true;
    }

    public static string Render(string text, FactSheet facts, IReadOnlyDictionary<string, string> lexicon)
    {
        ArgumentNullException.ThrowIfNull(facts); ArgumentNullException.ThrowIfNull(lexicon);
        var rendered = Pattern().Replace(text, m =>
        {
            var slot = new SlotToken(m.Groups["kind"].Value, m.Groups["ref"].Value, m.Groups["case"].Success ? m.Groups["case"].Value : null, m.Value);
            var value = Value(slot, facts) ?? throw new InvalidOperationException("Validate lines before rendering.");
            var code = slot.Case ?? CasesByKind[slot.Kind][0];
            return slot.Kind switch
            {
                "racer" => FinnishVerbalizer.Name(facts.Racers[int.Parse(slot.Reference, CultureInfo.InvariantCulture)].Competitor, lexicon),
                "bib" or "num" => FinnishNumbers.Cardinal(value.Number, FinnishNumbers.Codes[code]),
                "ord" => FinnishNumbers.Ordinal(value.Number, FinnishNumbers.Codes[code]),
                "rank" or "totalrank" or "prevrank" => code == "card" ? FinnishNumbers.Cardinal(value.Number) : FinnishNumbers.Ordinal(value.Number, FinnishNumbers.Codes[code]),
                "time" or "total" or "prevtime" => FinnishVerbalizer.Time(value.Number),
                "diff" or "totaldiff" => FinnishVerbalizer.Difference(value.Number),
                _ => FinnishVerbalizer.Status(value.Status, code == "noun")
            };
        });
        return Tidy(rendered);
    }
    // A sentence that starts with a rendered numeral or status gets its capital back; spacing is normalized.
    private static string Tidy(string text)
    {
        var builder = new StringBuilder(text.Length);
        var capitalize = true;
        foreach (var c in string.Join(' ', text.Split(' ', StringSplitOptions.RemoveEmptyEntries)))
        {
            builder.Append(capitalize && char.IsLetter(c) ? char.ToUpper(c, CultureInfo.GetCultureInfo("fi-FI")) : c);
            if (char.IsLetter(c)) { capitalize = false; }
            else if (c is '.' or '!' or '?') { capitalize = true; }
        }
        return builder.ToString();
    }

    /// <summary>Facts a rendered line states. The clip is stale once any of them no longer holds.</summary>
    public static IEnumerable<RadioCondition> Conditions(string text, FactSheet facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        var run = facts.Run;
        foreach (var slot in Parse(text))
        {
            if (Value(slot, facts) is not { } value || slot.Kind is "racer" or "bib") { continue; }
            if (slot.Kind is "num" or "ord")
            {
                // Counts by status change as the race goes on; the run number is covered by the current-run condition.
                if (CountStatus(slot.Reference) is { } counted) { yield return new(RadioConditionKind.StatusCount, run, Status: counted, Value: value.Number); }
                else if (slot.Reference == "starters") { yield return new(RadioConditionKind.Starters, run, Value: value.Number); }
                continue;
            }
            var bib = int.Parse(slot.Reference, CultureInfo.InvariantCulture);
            switch (slot.Kind)
            {
                case "rank": yield return new(RadioConditionKind.Rank, run, bib, Value: value.Number); break;
                case "totalrank": yield return new(RadioConditionKind.TotalRank, run, bib, Value: value.Number); break;
                case "prevrank": yield return new(RadioConditionKind.Rank, run - 1, bib, Value: value.Number); break;
                case "time": yield return new(RadioConditionKind.Time, run, bib, Value: value.Number); break;
                case "total": yield return new(RadioConditionKind.Total, run, bib, Value: value.Number); break;
                case "prevtime": yield return new(RadioConditionKind.Time, run - 1, bib, Value: value.Number); break;
                case "status": yield return new(RadioConditionKind.Status, run, bib, value.Status); break;
                case "diff" or "totaldiff":
                    // A gap holds while this time and the leader's time are unchanged.
                    var total = slot.Kind == "totaldiff";
                    var r = facts.Racers[bib].Result!;
                    yield return total ? new(RadioConditionKind.Total, run, bib, Value: r.TotalHundredths) : new(RadioConditionKind.Time, run, bib, Value: r.Hundredths);
                    var leaders = facts.State.Runs.First(x => x.Number == run).Results.Where(x => (total ? x.TotalRank : x.Rank) == 1);
                    foreach (var leader in leaders)
                    {
                        yield return total ? new(RadioConditionKind.Total, run, leader.Bib, Value: leader.TotalHundredths) : new(RadioConditionKind.Time, run, leader.Bib, Value: leader.Hundredths);
                        yield return new(total ? RadioConditionKind.TotalRank : RadioConditionKind.Rank, run, leader.Bib, Value: 1);
                    }
                    break;
            }
        }
    }

    private static LiveStatus? CountStatus(string key) => key switch
    {
        "finished" => LiveStatus.Finished, "oncourse" => LiveStatus.OnCourse, "remaining" => LiveStatus.Ready,
        "dnf" => LiveStatus.DNF, "dsq" => LiveStatus.DSQ, "dns" => LiveStatus.DNS, _ => null
    };
    private readonly record struct SlotValue(long Number, LiveStatus Status);
    private static SlotValue? Value(SlotToken slot, FactSheet facts)
    {
        if (slot.Kind is "num" or "ord")
        { return facts.Counts.TryGetValue(slot.Reference, out var count) && (slot.Kind == "num" || count >= 1) ? new SlotValue(count, default) : null; }
        if (!int.TryParse(slot.Reference, NumberStyles.None, CultureInfo.InvariantCulture, out var bib) || !facts.Racers.TryGetValue(bib, out var racer)) { return null; }
        var r = racer.Result; var p = racer.Previous;
        long? number = slot.Kind switch
        {
            "racer" or "status" => 0, "bib" => bib,
            "rank" => r?.Rank, "totalrank" => r?.TotalRank, "prevrank" => p?.Rank,
            "time" => r?.Status == LiveStatus.Finished ? r.Hundredths : null, "total" => r?.TotalHundredths,
            "prevtime" => p?.Status == LiveStatus.Finished ? p.Hundredths : null,
            "diff" => r?.Difference, "totaldiff" => r?.TotalDifference, _ => null
        };
        return number is null ? null : new SlotValue(number.Value, r?.Status ?? LiveStatus.Ready);
    }
}
