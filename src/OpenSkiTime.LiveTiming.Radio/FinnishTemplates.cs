namespace OpenSkiTime.LiveTiming.Radio;

public sealed record ScriptLine(string Host, string Text, bool Interruptible);

/// <summary>
/// Deterministic Finnish phrases for the core events. They are used when the language model fails, returns invalid
/// output or the budget only allows speech synthesis. Lines use the same slots and validation as model output.
/// </summary>
public static class FinnishTemplates
{
    public static IReadOnlyList<ScriptLine> Lines(IReadOnlyList<RadioEvent> batch, FactSheet facts, int seed)
    {
        ArgumentNullException.ThrowIfNull(batch); ArgumentNullException.ThrowIfNull(facts);
        var lines = new List<ScriptLine>();
        // Most urgent first, but a run summary always closes the batch so it follows the last finish it summarizes.
        foreach (var e in batch.OrderBy(x => x.Kind == RadioEventKind.RunComplete).ThenBy(x => x.Priority).ThenBy(x => x.At))
        {
            var next = For(e, facts, seed + lines.Count).ToArray();
            var first = lines.Count;
            // Hosts alternate across the batch so a sequence of short events still sounds like a conversation.
            lines.AddRange(next.Select((x, i) => x with { Host = RadioPersonas.Host(first + i).Name }));
            if (lines.Count >= 8) { break; }
        }
        return lines.Count == 0 ? lines : [.. lines[..^1], lines[^1] with { Interruptible = true }];
    }

    private static IEnumerable<ScriptLine> For(RadioEvent e, FactSheet facts, int seed)
    {
        var bib = e.Bibs.FirstOrDefault();
        var racer = facts.Racers.GetValueOrDefault(bib);
        var result = racer?.Result;
        var totals = facts.Run > 1 && result?.TotalRank is not null;
        string Pick(params string[] variants) => variants[Math.Abs(seed) % variants.Length];
        ScriptLine Line(string text, bool interruptible = true) => new("", text, interruptible);
        switch (e.Kind)
        {
            case RadioEventKind.Finish or RadioEventKind.Podium or RadioEventKind.NewLeader when result?.Status == LiveStatus.Finished:
                if (e.Kind == RadioEventKind.NewLeader)
                { yield return Line(Pick($"Uusi johtaja! {{racer:{bib}}} nousee kärkeen.", $"Kärkipaikka vaihtuu: {{racer:{bib}}} johtaa nyt."), false); }
                else
                { yield return Line(Pick($"Maalissa {{racer:{bib}}}, aika {{time:{bib}}}.", $"{{racer:{bib}}} on maalissa ajalla {{time:{bib}}}."), false); }
                if (totals)
                {
                    var place = result.TotalRank!.Value;
                    yield return Line(place == 1 ? $"Yhteisaika {{total:{bib}}}, ja hän johtaa yhteistuloksia."
                        : $"Yhteisaika {{total:{bib}}}. Yhteistuloksissa hän on nyt {Place("totalrank", bib, place)}, eroa kärkeen {{totaldiff:{bib}}}.");
                }
                else if (e.Kind == RadioEventKind.NewLeader) { yield return Line($"Aika {{time:{bib}}}."); }
                else if (result.Rank is { } rank)
                { yield return Line(rank == 1 ? "Hän on nyt kärjessä." : $"Hän on nyt {Place("rank", bib, rank)}, eroa kärkeen {{diff:{bib}}}."); }
                break;
            // A DNF is the drama of an alpine run: said with feeling, never with a guessed cause.
            case RadioEventKind.DNF when racer is not null:
                yield return Line(Pick($"Voi ei! {{racer:{bib}}} on ulkona, {{status:{bib}:noun}}!", $"Ja nyt sattui! {{racer:{bib}}} {{status:{bib}}}!",
                    $"Voi harmi, {{racer:{bib}}} on ulkona! Lasku päättyy kesken."), false);
                yield return Line(Pick("Kova paikka, mutta tällaista alppihiihto on.", "Tämä rinne ei anna anteeksi pieniäkään virheitä.",
                    "Harmillista, mutta seuraava lasku on jo tulossa."));
                break;
            case RadioEventKind.DNS when racer is not null:
                yield return Line($"{{racer:{bib}}} {{status:{bib}}}.");
                break;
            case RadioEventKind.DSQ when racer is not null:
                yield return Line($"{{racer:{bib}}} {{status:{bib}}}. Tulokset ovat vielä epävirallisia.");
                break;
            case RadioEventKind.Correction when result?.Status == LiveStatus.Finished && e.Source == "total" && result.TotalHundredths is not null:
                yield return Line($"Yhteistulokseen on tehty korjaus: {{racer:{bib}}}, yhteisaika nyt {{total:{bib}}}.");
                break;
            case RadioEventKind.Correction when result?.Status == LiveStatus.Finished:
                yield return Line($"Tulokseen on tehty korjaus: {{racer:{bib}}}, aika nyt {{time:{bib}}}.");
                break;
            case RadioEventKind.TopStart when racer is not null:
                yield return Line(racer.Previous?.Rank is { } previous && facts.Run == 2
                    ? $"Radalla nyt {Who(facts, bib)}{Club(facts, bib)}. Ensimmäisen laskun jälkeen hän oli {Place("prevrank", bib, previous)}."
                    : $"Radalla nyt {Who(facts, bib)}{Club(facts, bib)}.");
                foreach (var line in Gate(e, facts)) { yield return line; }
                break;
            case RadioEventKind.Start when racer is not null:
                yield return Line(Pick($"Radalla nyt {Who(facts, bib)}{Club(facts, bib)}.", $"Nyt radalla {Who(facts, bib)}{Club(facts, bib)}."));
                foreach (var line in Gate(e, facts)) { yield return line; }
                break;
            case RadioEventKind.RunStarted:
                yield return Line(racer is null ? "{ord:run:nom} lasku on alkanut." : $"{{ord:run:nom}} lasku on alkanut. Radalla on nyt {Who(facts, bib)}{Club(facts, bib)}.");
                if (racer is not null) { foreach (var line in Gate(e, facts)) { yield return line; } }
                break;
            case RadioEventKind.RunComplete:
                yield return Line(facts.Counts["run"] >= facts.Counts["runs"] ? "Kilpailun viimeinen lasku on päättynyt." : "{ord:run:nom} lasku on päättynyt.", false);
                var leaders = e.Bibs.Where(facts.Racers.ContainsKey).Take(3).ToArray();
                if (leaders.Length > 0)
                {
                    var kind = facts.Run > 1 && facts.Racers[leaders[0]].Result?.TotalRank is not null ? "totalrank" : "rank";
                    yield return Line("Epävirallisten tulosten mukaan " + string.Join(", ", leaders.Select((x, i) => $"{{{kind}:{x}:ess}} {(i == 0 ? "on " : "")}{{racer:{x}}}")) + ".");
                }
                break;
            case RadioEventKind.Interrupted:
                yield return Line("Kilpailussa on tällä hetkellä tauko. Kerromme heti, kun laskut jatkuvat.");
                break;
            case RadioEventKind.Producer:
                foreach (var line in Producer(e, facts)) { yield return line; }
                break;
        }
    }
    /// <summary>The start-gate lines of a batch's start events, with the racer each one names.</summary>
    public static IEnumerable<(ScriptLine Line, int Bib)> GateLines(IReadOnlyList<RadioEvent> batch, FactSheet facts)
    {
        ArgumentNullException.ThrowIfNull(batch); ArgumentNullException.ThrowIfNull(facts);
        foreach (var e in batch.Where(x => x.Kind is RadioEventKind.Start or RadioEventKind.TopStart or RadioEventKind.RunStarted))
        { foreach (var line in Gate(e, facts)) { yield return (line, e.Bibs[1]); } }
    }
    private static IEnumerable<ScriptLine> Gate(RadioEvent e, FactSheet facts)
    {
        if (e.Bibs.Length > 1 && facts.Racers.ContainsKey(e.Bibs[1]))
        { yield return new("", $"Lähtöportilla seuraavana {Who(facts, e.Bibs[1])}.", true); }
    }
    // In an international field a racer from another country is introduced with the nationality: "ruotsalainen Saga Porttila".
    private static string Who(FactSheet facts, int bib) => facts.International && facts.Racers[bib].Competitor.Nation != facts.HomeNation
        && FinnishVerbalizer.Demonym(facts.Racers[bib].Competitor.Nation) is not null ? $"{{nation:{bib}}} {{racer:{bib}}}" : $"{{racer:{bib}}}";
    private static string Club(FactSheet facts, int bib) => facts.Racers[bib].Competitor.Club.Trim().Length > 0 ? $", {{club:{bib}}}" : "";

    // Ordinal words read naturally up to ten ("kolmantena"); later places use the cardinal ("sijalla kaksikymmentäkolme").
    private static string Place(string kind, int bib, int place) => place <= 10 ? $"{{{kind}:{bib}:ess}}" : $"sijalla {{{kind}:{bib}:card}}";

    private static IEnumerable<ScriptLine> Producer(RadioEvent e, FactSheet facts)
    {
        var bib = e.Bibs.FirstOrDefault();
        switch (e.Source)
        {
            case ProducerSources.NextStarter when facts.Racers.ContainsKey(bib):
                yield return new("", $"Seuraavana lähtövuorossa on {{racer:{bib}}}.", true);
                break;
            case ProducerSources.OnCourse when facts.Racers.ContainsKey(bib):
                yield return new("", $"Radalla on nyt {Who(facts, bib)}{Club(facts, bib)}.", true);
                if (facts.Racers[bib].Previous?.Rank is { } before)
                { yield return new("", $"Ensimmäisen laskun jälkeen hän oli {Place("prevrank", bib, before)}.", true); }
                break;
            case ProducerSources.Standings:
                var leaders = e.Bibs.Where(facts.Racers.ContainsKey).Take(3).ToArray();
                if (leaders.Length == 0) { yield break; }
                var first = facts.Racers[leaders[0]];
                var kind = first.Result?.TotalRank is not null ? "totalrank" : first.Result?.Rank is not null ? "rank" : "prevrank";
                yield return new("", "Epävirallinen tilanne: " + string.Join(", ", leaders.Select((x, i) => $"{{{kind}:{x}:ess}} {(i == 0 ? "on " : "")}{{racer:{x}}}")) + ".", true);
                break;
            case ProducerSources.RunSummary:
                var finished = facts.Counts["finished"]; var remaining = facts.Counts["remaining"];
                // Every count is a slot, also the singular ones, so the clip is stale as soon as a count changes.
                yield return new("", (finished == 1 ? "Maaliin on tullut {num:finished} laskija" : "Maaliin on tullut {num:finished} laskijaa")
                    + (remaining == 0 ? "." : ", ja lähtövuoroa odottaa vielä {num:remaining}."), true);
                break;
        }
    }
}
