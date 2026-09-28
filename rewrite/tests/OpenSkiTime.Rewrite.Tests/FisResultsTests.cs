using System.Text;
using System.Xml.Linq;
using OpenSkiTime.Rewrite.Application;
using OpenSkiTime.Rewrite.Domain;
using OpenSkiTime.Rewrite.Timing;
using Xunit;

namespace OpenSkiTime.Rewrite.Tests;

public sealed class FisResultsTests
{
    private static readonly CompetitionValues s_race = new("Public race", "SL1", new(2026, 9, 28), Discipline.Slalom,
        RaceType.Fis, 1, 0, "1234", CourseName: "Slope", StartAltitudeMeters: 1100, FinishAltitudeMeters: 900,
        HomologationNumber: "12345/01/26");

    private static (StartListRevision List, TimingSnapshot Timing) Fixture(int count = 10)
    {
        var entries = Enumerable.Range(1, count).Select(i => new StartListEntry(i, i,
            new DrawEntrant(Guid.NewGuid(), new CompetitorValues("RACER" + i, "Test", 2000, (100000 + i).ToString(System.Globalization.CultureInfo.InvariantCulture),
                "FIN", "Club", Gender.Male), i <= 5 ? i * 10 : null), "Points order")).ToArray();
        var plan = new StartListPlan(Guid.NewGuid(), s_race, Gender.Male, 1, "test", "seed", new(),
            new("1327", new(2026, 9, 22), new(2026, 10, 10)), null, [], entries);
        var list = new StartListRevision(Guid.NewGuid(), 1, DateTimeOffset.UtcNow, null, "op", "draw", plan);
        var results = entries.Select((entry, i) => new TimingResult(entry, TimingStatus.Finished,
            5000 + i * 100, i + 1, null, null, "")).ToArray();
        return (list, new TimingSnapshot(list.Id, 0, results, [], []));
    }

    [Fact]
    public void PenaltyUsesOriginalPointsAndFisRaceFormula()
    {
        var (list, timing) = Fixture();
        var race = FisRaceResults.Assemble(list, timing);
        var penalty = FisPenalty.Calculate(Discipline.Slalom, race.PenaltyCompetitors, new(0, 999, 0));
        Assert.Equal(730, penalty.FValue);
        Assert.Equal(150, penalty.SumA);
        Assert.Equal(150, penalty.SumB);
        Assert.Equal(146.00m, penalty.SumC); // 0, 14.60, 29.20, 43.80, 58.40
        Assert.Equal(15.40m, penalty.Calculated);
        Assert.Equal(330m, penalty.Applied);
        Assert.Equal(0, penalty.RacePoints[1]);
    }

    [Fact]
    public void MissingPointsRequireDoubleMaximumMinimum()
    {
        var (list, timing) = Fixture();
        var race = FisRaceResults.Assemble(list, timing);
        // Six classified racers lack listed points; this invokes the FIS double-maximum floor.
        var penalty = FisPenalty.Calculate(Discipline.Slalom, race.PenaltyCompetitors, new(0, 999, 0));
        Assert.True(penalty.DoubleMinimum);
        Assert.Equal(330m, penalty.Applied);
    }

    [Fact]
    public void TieAtTenthIsEligibleAndFifthPointsTieUsesHigherRacePoints()
    {
        var (list, timing) = Fixture(11);
        var rows = FisRaceResults.Assemble(list, timing).PenaltyCompetitors.ToArray();
        rows[9] = rows[9] with { Rank = 10 };
        rows[10] = rows[10] with { Rank = 10, Entry = rows[10].Entry with
        { Entrant = rows[10].Entry.Entrant with { Points = 50 } } };
        var result = FisPenalty.Calculate(Discipline.Slalom, rows, new(0, 999, 0));
        Assert.Contains(result.BestClassified, x => x.Competitor.Bib == 11);
        Assert.DoesNotContain(result.BestClassified, x => x.Competitor.Bib == 5);
    }

    [Fact]
    public void NonStarterDoesNotEnterBestStartedAndMaximumSubstitutesMissingPoints()
    {
        var (list, timing) = Fixture();
        var rows = FisRaceResults.Assemble(list, timing).PenaltyCompetitors.ToArray();
        rows[0] = rows[0] with { Started = false, Status = TimingStatus.DNS,
            TotalHundredths = null, Rank = null };
        var result = FisPenalty.Calculate(Discipline.Slalom, rows, new(0, 999, 0));
        Assert.DoesNotContain(result.BestStarted, x => x.Competitor.Bib == 1);
        Assert.Contains(result.BestStarted, x => x.Competitor.Bib == 6 && x.UsedPoints == 165m && x.SubstitutedMaximum);
    }

    [Fact]
    public void StarterWithoutTimeOrClassificationBlocksResultCreation()
    {
        var (list, timing) = Fixture();
        var exception = Assert.Throws<DomainValidationException>(() => FisRaceResults.Assemble(list,
            timing with { Results = timing.Results.Select((x, index) => index == 1
                ? x with { Status = TimingStatus.Ready, Hundredths = null } : x).ToArray() }));
        Assert.Contains("Bib 2", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CompleteTimesRemainPublishableWithAnExtraUnassignedPulse()
    {
        var (list, timing) = Fixture();
        var extra = new TimingObservation("extra", Guid.NewGuid(), 11, "test", "extra",
            ObservationKind.Impulse, 1, TimeSpan.FromHours(12).Ticks, 3, null, false, "clock", "False finish");
        var withExtra = timing with { Observations = [new ObservationReview(extra, null, false, null, "Unassigned")] };
        Assert.True(withExtra.Complete);
        Assert.Equal(1, withExtra.Unresolved);
        Assert.Equal(10, FisRaceResults.Assemble(list, withExtra).Rows.Count);
    }

    [Fact]
    public void TwoRunNonFinisherRetainsRunAndFirstBibInXml()
    {
        var (oneRunList, firstTiming) = Fixture();
        var first = oneRunList with { Plan = oneRunList.Plan with
            { Competition = oneRunList.Plan.Competition with { RunCount = 2 } } };
        var secondPlan = FisStartOrder.SecondRun(first, firstTiming.ToRunFinishes());
        var second = first with { Id = Guid.NewGuid(), Plan = secondPlan };
        var secondTiming = new TimingSnapshot(second.Id, 0, secondPlan.Entries.Select(entry =>
            new TimingResult(entry, entry.Bib == 1 ? TimingStatus.DNS : TimingStatus.Finished,
                entry.Bib == 1 ? null : 5100 + entry.Bib * 100, null, null, null, "")).ToArray(), [], []);
        var race = FisRaceResults.Assemble(first, firstTiming with { ListId = first.Id }, second, secondTiming);
        Assert.Equal(TimingStatus.DNS, race.Rows.Single(x => x.Entry.Bib == 1).Status);
        Assert.Equal(2, race.Rows.Single(x => x.Entry.Bib == 1).StatusRun);
        var penalty = FisPenalty.Calculate(Discipline.Slalom, race.PenaltyCompetitors, new(0, 999, 0));
        var details = new FisXmlDetails("FIS", new("T", "Delegate", "FIN"), new("C", "Chief", "FIN"),
            [new(45, 43, "10:00", new("S", "Setter", "FIN")),
             new(44, 42, "13:00", new("S", "Setter", "FIN"))]);
        var doc = XDocument.Parse(Encoding.UTF8.GetString(FisResultXml.Create(race,
            new("Series", "Ruka", "Club", s_race.Date, s_race.Date, "FIN", "2026-27"), penalty, details)));
        var notRanked = Assert.Single(doc.Descendants("AL_notranked"));
        Assert.Equal("DNS2", notRanked.Attribute("Status")?.Value);
        Assert.Equal("1", notRanked.Element("Bib")?.Value);
        Assert.Equal("2", notRanked.Element("Run")?.Value);
        Assert.Equal("00:50:00", notRanked.Element("AL_result")?.Element("Timerun1")?.Value);
        Assert.Equal(9, doc.Descendants("AL_ranked").Count());
    }

    [Fact]
    public void XmlContainsOfficialRankedResultsPenaltyAndRunMetadata()
    {
        var (list, timing) = Fixture();
        var race = FisRaceResults.Assemble(list, timing);
        var penalty = FisPenalty.Calculate(Discipline.Slalom, race.PenaltyCompetitors, new(0, 999, 0));
        var metadata = new FisXmlDetails("FIS", new("T", "Delegate", "FIN"), new("C", "Chief", "FIN"),
            [new(45, 43, "10:00", new("S", "Setter", "FIN"))]);
        var xml = FisResultXml.Create(race, new("Series", "Ruka", "Club", s_race.Date, s_race.Date, "FIN", "2026-27"),
            penalty, metadata);
        Assert.StartsWith("<?xml", Encoding.UTF8.GetString(xml));
        var doc = XDocument.Parse(Encoding.UTF8.GetString(xml));
        Assert.Equal("2027", doc.Root!.Element("Raceheader")!.Element("Season")!.Value);
        Assert.Equal("330.00", doc.Descendants("Appliedpenalty").Single().Value);
        Assert.Equal("13", doc.Descendants("Usedfislist").Single().Value);
        Assert.Equal(10, doc.Descendants("AL_ranked").Count());
        Assert.Equal("00:50:00", doc.Descendants("Totaltime").First().Value);
        Assert.Equal("FIN1234.xml", FisResultXml.FileName(race, new("Series", "Ruka", "Club", s_race.Date, s_race.Date, "FIN", "2026-27")));
    }
}
