using OpenSkiTime.LiveTiming.Radio;
using Xunit;

namespace OpenSkiTime.LiveTiming.Tests;

// The language model may refer to facts only through slots; code validates every line before speech.
public sealed class RadioSlotTests
{
    private static readonly IReadOnlyDictionary<string, string> NoLexicon = FinnishVerbalizer.Lexicon("");

    private static (FactSheet Facts, LineRules Rules) Race()
    {
        var state = RadioRace.Set(RadioRace.Create(), 1, LiveStatus.Finished, 4701);
        state = RadioRace.Set(state, 2, LiveStatus.Finished, 4790);
        state = RadioRace.Set(state, 3, LiveStatus.OnCourse);
        state = RadioRace.Set(state, 4, LiveStatus.DNF);
        state = RadioRace.Set(state, 3, LiveStatus.Review);
        return (FactSheet.Build(state, [2, 3, 4]), new LineRules(state, ["Aino", "Kalle"], "Sponsorina Lumiklubi."));
    }

    [Fact]
    public void ValidLineRendersFinnishFromTheTimingValues()
    {
        var (facts, rules) = Race();
        const string line = "Maalissa {racer:2}, aika {time:2}. Hän on nyt {rank:2:ess}, eroa kärkeen {diff:2}.";
        Assert.Null(RadioSlots.Problem(line, facts, rules));
        Assert.Equal("Maalissa Veeti Hankiranta, aika neljäkymmentäseitsemän pilkku yhdeksänkymmentä sekuntia. Hän on nyt toisena, eroa kärkeen nolla pilkku kahdeksankymmentäyhdeksän sekuntia.",
            RadioSlots.Render(line, facts, NoLexicon));
        Assert.Equal("Ilona Kurvinen keskeytti.", RadioSlots.Render("{racer:4} {status:4}.", facts, NoLexicon));
        Assert.Equal("Keskeytys.", RadioSlots.Render("{status:4:noun}.", facts, NoLexicon));
        Assert.Equal("Ensimmäinen lasku, ja vuoroaan odottaa yksi.", RadioSlots.Render("{ord:run:nom} lasku, ja vuoroaan odottaa {num:remaining}.", facts, NoLexicon));
        Assert.Equal("Sijalla kaksi.", RadioSlots.Render("Sijalla {rank:2:card}.", facts, NoLexicon));
    }

    [Theory]
    [InlineData("Aika on 47,90 sekuntia.", "digits outside slots")]
    [InlineData("Radalla {time:3}.", "unknown slot")]          // Bib 3 has no time yet.
    [InlineData("Matkassa {racer:9}.", "unknown slot")]        // Bib 9 is not in the fact sheet.
    [InlineData("{racer:2:gen} lasku.", "disallowed case")]    // Names are never inflected by code.
    [InlineData("{name:2} maalissa.", "unknown slot")]
    [InlineData("{racer:3} {status:3}.", "unknown slot")]          // Bib 3 is under timing review.
    [InlineData("Hankiranta johtaa nyt.", "racer name outside slots")]
    [InlineData("Maalissa VEETI {racer:1}.", "racer name outside slots")]
    [InlineData("Hän on kolmas.", "number words outside slots")]
    [InlineData("Hieno lasku {racer:2}:lta.", "ending attached to a slot")]
    [InlineData("Kolmantena on {racer:2}.", "number words outside slots")]
    [InlineData("Hän on yhdestoista.", "number words outside slots")]
    [InlineData("Yksitoista laskijaa on maalissa.", "number words outside slots")]
    [InlineData("Eroa on sekunnin verran.", "number words outside slots")]
    [InlineData("Hän voitti Matti Virtasen.", "unknown proper name")]
    [InlineData("{racer:2} ja Pekka tuulettavat.", "unknown proper name")]
    [InlineData("Puuttuva {racer:2 lopetus.", "malformed slot")]
    [InlineData("", "empty or oversized line")]
    public void InvalidLinesAreRejectedBeforeSpeech(string line, string problem)
    {
        var (facts, rules) = Race();
        Assert.Equal(problem, RadioSlots.Problem(line, facts, rules));
    }

    [Fact]
    public void TheBibGoesBeforeTheFirstMentionOfEachRacer()
    {
        var lines = RadioSlots.AnnounceBibs([
            new("Aino", "Radalla nyt {nation:3} {racer:3}, {club:3}.", false),
            new("Kalle", "{racer:3} on vauhdissa, ja {racer:2} odottaa.", false),
            new("Aino", "Numero {bib:4:nom} eli {racer:4} on valmiina.", true)]);
        Assert.Equal(["Radalla nyt numero {bib:3:nom}, {nation:3} {racer:3}, {club:3}.", "{racer:3} on vauhdissa, ja numero {bib:2:nom}, {racer:2} odottaa.",
            "Numero {bib:4:nom} eli {racer:4} on valmiina."], lines.Select(x => x.Text));
    }

    [Theory]
    [InlineData("Jäljellä {num:remaining:part}.", "Jäljellä {num:remaining:par}.")]
    [InlineData("Numero {bib:3:lle} aukeaa portti.", "Numero {bib:3:all} aukeaa portti.")]
    [InlineData("{rank:2:ESS} on {racer:2}.", "{rank:2:ess} on {racer:2}.")]
    [InlineData("{rank:2:xyz} pysyy.", "{rank:2:xyz} pysyy.")]
    public void CommonAlternativeCaseCodesAreNormalizedBeforeValidation(string line, string normalized)
        => Assert.Equal(normalized, RadioSlots.Normalize(line));

    [Theory]
    [InlineData("Kiitos, Kalle. Täällä Testitunturilla riittää jännitystä.")]
    [InlineData("Rinneseuran laskija {racer:2} on nyt maalissa.")]
    [InlineData("Hän tulee Ruotsista, ja Suomen laskijat seuraavat perässä.")]
    [InlineData("FIS-kilpailuissa ja SM-tasolla tämä on tuttua, Lumiklubin väki kertoo.")]
    [InlineData("Mäki on jyrkkä, ja kivi ei ole tiellä.")]
    [InlineData("Seurataan laskua yhdessä.")]
    [InlineData("Täällä Harjoitusrinteellä riittää vauhtia, ja Testitunturilla on hyvä keli.")] // Inflected allowed names.
    public void OrdinaryWordsAndAllowedNamesPass(string line)
    {
        var (facts, rules) = Race();
        Assert.Null(RadioSlots.Problem(line, facts, rules));
    }

    [Fact]
    public void ConditionsCoverEveryStatedFactIncludingTheLeaderBehindAGap()
    {
        var (facts, _) = Race();
        var conditions = RadioSlots.Conditions("{rank:2:ess}, eroa {diff:2}, {racer:4} {status:4}.", facts).ToArray();
        Assert.Contains(new RadioCondition(RadioConditionKind.Rank, 1, 2, Value: 2), conditions);
        Assert.Contains(new RadioCondition(RadioConditionKind.Time, 1, 2, Value: 4790), conditions);
        Assert.Contains(new RadioCondition(RadioConditionKind.Time, 1, 1, Value: 4701), conditions);
        // A gap changes when someone else takes the lead, even though neither stated time changes.
        Assert.Contains(new RadioCondition(RadioConditionKind.Rank, 1, 1, Value: 1), conditions);
        Assert.Contains(new RadioCondition(RadioConditionKind.Status, 1, 4, LiveStatus.DNF), conditions);
        Assert.Equal(5, conditions.Length);
        var counts = RadioSlots.Conditions("Maalissa {num:finished}, matkaa jäljellä {num:remaining}, {ord:run:nom} lasku.", facts).ToArray();
        Assert.Equal([new RadioCondition(RadioConditionKind.StatusCount, 1, Status: LiveStatus.Finished, Value: 2),
            new RadioCondition(RadioConditionKind.StatusCount, 1, Status: LiveStatus.Ready, Value: 1)], counts);
        Assert.All(counts, x => Assert.True(RadioPlayback.Holds(facts.State, x)));
        Assert.False(RadioPlayback.Holds(RadioRace.Set(facts.State, 5, LiveStatus.OnCourse), counts[1]));
        var field = Assert.Single(RadioSlots.Conditions("Lähtölistalla on {num:starters} laskijaa.", facts));
        Assert.Equal(new RadioCondition(RadioConditionKind.Starters, 1, Value: 5), field);
        Assert.False(RadioPlayback.Holds(facts.State with { Runs = [facts.State.Runs[0] with { StartOrder = [1, 2, 3, 4] }] }, field));
        var leaderBeaten = RadioRace.Set(RadioRace.Set(facts.State, 5, LiveStatus.OnCourse), 5, LiveStatus.Finished, 4600);
        Assert.False(RadioPlayback.Holds(leaderBeaten, new RadioCondition(RadioConditionKind.Rank, 1, 1, Value: 1)));
    }

    [Fact]
    public void FactSheetForTheModelHasNoNames()
    {
        var (facts, _) = Race();
        var prompt = System.Text.Json.JsonSerializer.Serialize(facts.ToPrompt());
        Assert.DoesNotContain("Hankiranta", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Veeti", prompt, StringComparison.Ordinal);
        Assert.Contains("{racer:2}", prompt, StringComparison.Ordinal);
        Assert.Contains("Rinneseura", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void TemplatesSpeakEveryCoreEventAndPassValidation()
    {
        var (facts, rules) = Race();
        var at = RadioRace.Start;
        RadioEvent Event(RadioEventKind kind, params int[] bibs) => new(kind, 2, 1, bibs, at, TimeSpan.FromMinutes(1), [], [], 1);
        foreach (var e in new[] { Event(RadioEventKind.NewLeader, 2), Event(RadioEventKind.Finish, 2), Event(RadioEventKind.DNF, 4), Event(RadioEventKind.DSQ, 4),
            Event(RadioEventKind.RunStarted, 1), Event(RadioEventKind.RunComplete, 1, 2), Event(RadioEventKind.Interrupted), Event(RadioEventKind.Correction, 2) })
        {
            var lines = FinnishTemplates.Lines([e], facts, 0);
            Assert.NotEmpty(lines);
            Assert.All(lines, x => Assert.Null(RadioSlots.Problem(x.Text, facts, rules)));
            Assert.True(lines[^1].Interruptible);
        }
        var finish = FinnishTemplates.Lines([Event(RadioEventKind.Finish, 2)], facts, 0);
        Assert.Equal(["Aino", "Kalle"], finish.Select(x => x.Host));
        Assert.Equal("Hän on nyt toisena, eroa kärkeen nolla pilkku kahdeksankymmentäyhdeksän sekuntia.", RadioSlots.Render(finish[1].Text, facts, NoLexicon));
        var complete = FinnishTemplates.Lines([Event(RadioEventKind.RunComplete, 1, 2)], facts, 0);
        Assert.Equal("Epävirallisten tulosten mukaan ensimmäisenä on Aada Lumivaara, toisena Veeti Hankiranta.", RadioSlots.Render(complete[1].Text, facts, NoLexicon));
    }
}
