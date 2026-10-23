using OpenSkiTime.LiveTiming.Radio;
using Xunit;

namespace OpenSkiTime.LiveTiming.Tests;

// Speech fidelity: every number, time, place and status is rendered by code, so these tables are what listeners hear.
public sealed class RadioFinnishTests
{
    [Theory]
    [InlineData(0, "nom", "nolla")]
    [InlineData(1, "nom", "yksi")]
    [InlineData(3, "ill", "kolmeen")]
    [InlineData(7, "par", "seitsemää")]
    [InlineData(10, "ess", "kymmenenä")]
    [InlineData(11, "ade", "yhdellätoista")]
    [InlineData(12, "gen", "kahdentoista")]
    [InlineData(20, "nom", "kaksikymmentä")]
    [InlineData(21, "ine", "kahdessakymmenessäyhdessä")]
    [InlineData(45, "ela", "neljästäkymmenestäviidestä")]
    [InlineData(100, "nom", "sata")]
    [InlineData(123, "gen", "sadankahdenkymmenenkolmen")]
    [InlineData(200, "nom", "kaksisataa")]
    [InlineData(200, "ade", "kahdellasadalla")]
    [InlineData(1000, "tra", "tuhanneksi")]
    [InlineData(2026, "nom", "kaksituhattakaksikymmentäkuusi")]
    [InlineData(99999, "nom", "yhdeksänkymmentäyhdeksäntuhattayhdeksänsataayhdeksänkymmentäyhdeksän")]
    public void CardinalsInflectEveryComponent(long value, string code, string expected)
        => Assert.Equal(expected, FinnishNumbers.Cardinal(value, FinnishNumbers.Codes[code]));

    [Theory]
    [InlineData(1, "nom", "ensimmäinen")]
    [InlineData(1, "ess", "ensimmäisenä")]
    [InlineData(2, "ess", "toisena")]
    [InlineData(3, "ess", "kolmantena")]
    [InlineData(3, "tra", "kolmanneksi")]
    [InlineData(3, "par", "kolmatta")]
    [InlineData(4, "ine", "neljännessä")]
    [InlineData(5, "gen", "viidennen")]
    [InlineData(6, "ess", "kuudentena")]
    [InlineData(7, "nom", "seitsemäs")]
    [InlineData(8, "ade", "kahdeksannella")]
    [InlineData(9, "ill", "yhdeksänteen")]
    [InlineData(10, "ess", "kymmenentenä")]
    [InlineData(11, "nom", "yhdestoista")]
    [InlineData(12, "ess", "kahdentenatoista")]
    [InlineData(13, "gen", "kolmannentoista")]
    [InlineData(20, "nom", "kahdeskymmenes")]
    [InlineData(21, "nom", "kahdeskymmenesensimmäinen")]
    [InlineData(22, "ess", "kahdentenakymmenentenätoisena")]
    [InlineData(100, "nom", "sadas")]
    [InlineData(101, "nom", "sadasensimmäinen")]
    [InlineData(1000, "nom", "tuhannes")]
    [InlineData(2000, "gen", "kahdennentuhannennen")]
    public void OrdinalsInflectWithVowelHarmony(long value, string code, string expected)
        => Assert.Equal(expected, FinnishNumbers.Ordinal(value, FinnishNumbers.Codes[code]));

    [Theory]
    [InlineData(4812, "neljäkymmentäkahdeksan pilkku kaksitoista sekuntia")]
    [InlineData(5, "nolla pilkku nolla viisi sekuntia")]
    [InlineData(6503, "yksi minuutti viisi pilkku nolla kolme")]
    [InlineData(12000, "kaksi minuuttia nolla pilkku nolla nolla")]
    public void TimesSpeakThePublishedHundredths(long hundredths, string expected) => Assert.Equal(expected, FinnishVerbalizer.Time(hundredths));

    [Theory]
    [InlineData(34, "nolla pilkku kolmekymmentäneljä sekuntia")]
    [InlineData(0, "nolla pilkku nolla nolla sekuntia")]
    [InlineData(6100, "yksi minuutti yksi pilkku nolla nolla sekuntia")]
    public void DifferencesAlwaysEndInSeconds(long hundredths, string expected) => Assert.Equal(expected, FinnishVerbalizer.Difference(hundredths));

    [Fact]
    public void StatusesUseFixedAlpineTerms()
    {
        Assert.Equal("keskeytti", FinnishVerbalizer.Status(LiveStatus.DNF));
        Assert.Equal("hylättiin", FinnishVerbalizer.Status(LiveStatus.DSQ));
        Assert.Equal("ei lähtenyt matkaan", FinnishVerbalizer.Status(LiveStatus.DNS));
        Assert.Equal("keskeytys", FinnishVerbalizer.Status(LiveStatus.DNF, noun: true));
        Assert.Equal("hylkäys", FinnishVerbalizer.Status(LiveStatus.DSQ, noun: true));
    }

    [Fact]
    public void NamesAreVerbatimExceptAllCapitalSurnamesAndTheOperatorLexicon()
    {
        var none = FinnishVerbalizer.Lexicon("");
        Assert.Equal("Aino-Liisa Mäki-Kuusela", FinnishVerbalizer.Name(new(1, "MÄKI-KUUSELA", "Aino-Liisa", "FIN", "", ""), none));
        Assert.Equal("Jan von Berg", FinnishVerbalizer.Name(new(2, "von Berg", "Jan", "GER", "", ""), none));
        var lexicon = FinnishVerbalizer.Lexicon("Snøheim = Snööheim\nbroken line\nX = 12");
        Assert.Equal("Maja Snööheim", FinnishVerbalizer.Name(new(3, "SNØHEIM", "Maja", "NOR", "", ""), lexicon));
        Assert.Single(lexicon); // Lines without '=' and spoken forms with digits are ignored.
        var whole = FinnishVerbalizer.Lexicon("Maja Snøheim = Maija Snööhaim");
        Assert.Equal("Maija Snööhaim", FinnishVerbalizer.Name(new(3, "SNØHEIM", "Maja", "NOR", "", ""), whole));
    }
}
