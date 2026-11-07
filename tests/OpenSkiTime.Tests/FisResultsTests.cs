using System.Text;
using System.Xml.Linq;
using OpenSkiTime.Application;
using OpenSkiTime.Domain;
using OpenSkiTime.Timing;
using Xunit;

namespace OpenSkiTime.Tests;

public sealed class FisResultsTests
{
    [Fact]
    public void PenaltyUsesListFactorCapAdderAndBounds()
    {
        var (list, timing) = Fixture();
        var race = FisRaceResults.Assemble(list, timing);
        var entrants = race.PenaltyCompetitors.Select(x => x with { Entry = x.Entry with { Entrant = x.Entry.Entrant with { Points = 10m } } }).ToArray();
        var profile = new FisPenaltyProfile("TEST", 3, 1000, 20, 0, 7, 29, 888);
        var penalty = FisPenalty.Calculate(profile, entrants);
        Assert.Equal(1000, penalty.FValue); Assert.Equal(20m, penalty.MaximumPoints);
        Assert.Equal(20m, penalty.RacePoints[2]); Assert.Equal(100m, penalty.SumC);
        Assert.Equal(0m, penalty.Calculated); Assert.Equal(29m, penalty.Applied);
        var ceiling = FisPenalty.Calculate(profile with { Minimum = 0, Maximum = 5 }, entrants);
        Assert.Equal(5m, ceiling.Applied);
    }
    [Fact]
    public void XmlUsesSharedCompetitionCalendarAndTdNumberInsteadOfSeriesDefaults()
    {
        var (list, timing) = Fixture();
        var calendar = new CompetitionCalendarData(2027, "Competition place", "SWE", "NC", "M",
            new("TESTLASTNAME", "Testfirst", "SWE", "1047"));
        list = list with { Plan = list.Plan with { Competition = list.Plan.Competition with { Calendar = calendar } } };
        var race = FisRaceResults.Assemble(list, timing);
        var series = new SeriesValues("Series", "Default place", "Club", s_race.Date, s_race.Date, "FIN", "2026/27");
        var penalty = FisPenalty.Calculate(Discipline.Slalom, race.PenaltyCompetitors, new(0, 999, 0));
        var details = new FisXmlDetails("FIS", new("Old", "Delegate", "FIN"), new("Test", "Chief", "FIN"), [new(45, 43, "10:00", new("Test", "Setter", "FIN"))], CourseChief: new("Course", "Chief", "FIN"));
        var doc = XDocument.Parse(Encoding.UTF8.GetString(FisResultXml.Create(race, series, penalty, details)));
        Assert.Equal("Competition place", doc.Descendants("Place").Single().Value);
        Assert.Equal("NC", doc.Descendants("Category").Single().Value);
        Assert.Equal("SWE", doc.Descendants("Raceheader").Single().Element("Nation")!.Value);
        var td = doc.Descendants("Jury").Single(x => x.Attribute("Function")?.Value == "TechnicalDelegate");
        Assert.Equal("1047", td.Element("Number")!.Value); Assert.Equal("TESTLASTNAME", td.Element("Lastname")!.Value);
        Assert.Equal("SWE1234.xml", FisResultXml.FileName(race, series));
        var differentGender = race with { FirstList = list with { Plan = list.Plan with { Competition = list.Plan.Competition with { Calendar = calendar with { Gender = "W" } } } } };
        Assert.Throws<DomainValidationException>(() => FisResultXml.Create(differentGender, series, penalty, details));
    }

    private static RaceInformation Information(CompetitionValues competition)
    {
        var information = RaceInformation.Empty(competition);
        return information with
        {
            Jury = information.Jury.Select(x => x.Function == "TechnicalDelegate" ? x : x with { Person = new("Synthetic", x.Function.ToUpperInvariant(), "FIN") }).ToArray(),
            Runs = information.Runs.Select(r => r with
            {
                CourseSetter = new("Course", "SETTER", "FIN"), Gates = 45, TurningGates = 43, StartTime = "10:00",
                Forerunners = [new("A", new("Fore", "RUNNER", "FIN"))], Weather = new("Clear", "Hard", StartTemperature: -1m, FinishTemperature: 0m),
            }).ToArray(),
        };
    }

    private static XDocument TwoRunXml(TimingStatus status, bool withInformation = true, FisPerson? courseChief = null)
    {
        var (oneRunList, firstTiming) = Fixture();
        var first = oneRunList with { Plan = oneRunList.Plan with { Competition = oneRunList.Plan.Competition with { RunCount = 2 } } };
        var secondPlan = FisStartOrder.SecondRun(first, firstTiming.ToRunFinishes());
        var second = first with { Id = Guid.NewGuid(), Plan = secondPlan };
        var secondTiming = new TimingSnapshot(second.Id, 0, secondPlan.Entries.Select(entry =>
            new TimingResult(entry, entry.Bib == 1 ? status : TimingStatus.Finished,
                entry.Bib == 1 ? null : 5100 + entry.Bib * 100, null, null, null, "")
            { Disqualification = entry.Bib == 1 && status == TimingStatus.DSQ ? new(19, "ICR 629.3", "Judge") : null }).ToArray(), [], []);
        var race = FisRaceResults.Assemble(first, firstTiming with { ListId = first.Id }, second, secondTiming);
        var penalty = FisPenalty.Calculate(Discipline.Slalom, race.PenaltyCompetitors, new(0, 999, 0));
        var details = new FisXmlDetails("FIS", new("T", "Delegate", "FIN", "718"), new("C", "Chief", "FIN"),
            [new(45, 43, "10:00", new("S", "Setter", "FIN")), new(44, 42, "13:00", new("S", "Setter", "FIN"))],
            withInformation ? Information(first.Plan.Competition) : null, courseChief);
        return XDocument.Parse(Encoding.UTF8.GetString(FisResultXml.Create(race,
            new("Series", "Ruka", "Club", s_race.Date, s_race.Date, "FIN", "2026-27"), penalty, details)), LoadOptions.None);
    }

    // The element tables of the Alpine Result XML specification are checked as a whole; see FisResultXmlSpec.
    [Theory]
    [InlineData(TimingStatus.DNS)]
    [InlineData(TimingStatus.DNF)]
    [InlineData(TimingStatus.DSQ)]
    public void GeneratedXmlConformsToTheAlpineResultXmlSpecification(TimingStatus status)
        => Assert.Empty(FisResultXmlSpec.Validate(TwoRunXml(status), "FIN1234.xml"));

    [Fact]
    public void OneRunXmlConformsToTheAlpineResultXmlSpecification()
    {
        var (list, timing) = Fixture();
        var race = FisRaceResults.Assemble(list, timing);
        var penalty = FisPenalty.Calculate(Discipline.Slalom, race.PenaltyCompetitors, new(0, 999, 0));
        var details = new FisXmlDetails("FIS", new("T", "Delegate", "FIN"), new("C", "Chief", "FIN"), [new(45, 43, "10:00", new("S", "Setter", "FIN"))],
            CourseChief: new("Course", "Chief", "FIN"));
        var doc = XDocument.Parse(Encoding.UTF8.GetString(FisResultXml.Create(race,
            new("Series", "Ruka", "Club", s_race.Date, s_race.Date, "FIN", "2026-27"), penalty, details)));
        Assert.Empty(FisResultXmlSpec.Validate(doc, "FIN1234.xml"));
    }

    [Fact]
    public void OfficialsAreSplitIntoJuryAndMembersAndTheSoftwareIsDescribed()
    {
        var doc = TwoRunXml(TimingStatus.DNF);
        var raceInfo = doc.Descendants("AL_raceinfo").Single();
        // Jury: technical delegate, chief of race and referees. Members: the other officials (2.3.1-2.3.3).
        Assert.Equal(["TechnicalDelegate", "ChiefRace", "Referee"], raceInfo.Elements("Jury").Select(x => x.Attribute("Function")!.Value));
        Assert.Equal(["ChiefCourse", "Startreferee", "Finishreferee", "ChiefTiming"], raceInfo.Elements("Member").Select(x => x.Attribute("Function")!.Value));
        Assert.True(raceInfo.Elements("Jury").Last().IsBefore(raceInfo.Elements("Member").First()));
        Assert.Equal("718", raceInfo.Elements("Jury").First().Element("Number")!.Value);
        Assert.Equal(["Course", "Starttime"], raceInfo.Elements("Runinfo").First().Elements().Select(x => x.Name.LocalName).Where(x => x is "Course" or "Starttime"));
        Assert.Equal("Weather", raceInfo.Elements("Runinfo").First().Elements().ElementAt(1).Name.LocalName);
        Assert.Equal("Starttime", raceInfo.Elements("Runinfo").First().Elements().Last().Name.LocalName);
        Assert.Equal(["Softwarecompany", "Softwarename", "Softwareversion"], raceInfo.Elements().Where(x => x.Name.LocalName.StartsWith("Software", StringComparison.Ordinal)).Select(x => x.Name.LocalName));
        Assert.Equal(ProductInfo.Version, raceInfo.Element("Softwareversion")!.Value);
        Assert.All(raceInfo.Elements("Runinfo"), x => Assert.NotNull(x.Attribute("No")));
    }

    [Fact]
    public void AMemberIsRequiredSoXmlWithoutAnyOtherOfficialIsRefused()
    {
        var ex = Assert.Throws<DomainValidationException>(() => TwoRunXml(TimingStatus.DNF, withInformation: false));
        Assert.Contains("race official", ex.Message, StringComparison.Ordinal);
        // A course chief given with the details satisfies the requirement when the race information lists none.
        var doc = TwoRunXml(TimingStatus.DNF, withInformation: false, courseChief: new("Course", "Chief", "FIN"));
        Assert.Equal("ChiefCourse", Assert.Single(doc.Descendants("Member")).Attribute("Function")!.Value);
    }

    [Fact]
    public void NonFinishersCarryNoPartialResultBecauseRacepointsWouldBeRequired()
    {
        var doc = TwoRunXml(TimingStatus.DNF);
        Assert.Empty(doc.Descendants("AL_notranked").Elements("AL_result"));
    }

    [Fact]
    public void TheSpecificationCheckerFlagsRealDeviations()
    {
        string[] Check(Action<XDocument> change) { var doc = TwoRunXml(TimingStatus.DSQ); change(doc); return [.. FisResultXmlSpec.Validate(doc, "FIN1234.xml")]; }
        Assert.Contains(Check(d => d.Descendants("Member").Remove()), x => x.Contains("required element Member", StringComparison.Ordinal));
        Assert.Contains(Check(d => d.Descendants("Timerun1").First().Value = "48.01"), x => x.Contains("Timerun1", StringComparison.Ordinal));
        Assert.Contains(Check(d => d.Descendants("Runinfo").First().Attribute("No")!.Value = "x"), x => x.Contains("@No", StringComparison.Ordinal));
        Assert.Contains(Check(d => { var run = d.Descendants("Runinfo").First(); var start = run.Element("Starttime")!; start.Remove(); run.AddFirst(start); }),
            x => x.Contains("out of order", StringComparison.Ordinal));
        Assert.Contains(Check(d => d.Descendants("AL_notranked").First().Add(new XElement("AL_result", new XElement("Timerun1", "00:50:00")))),
            x => x.Contains("Racepoints is missing", StringComparison.Ordinal) || x.Contains("required element Racepoints", StringComparison.Ordinal));
        Assert.Contains(Check(d => d.Descendants("AL_notranked").First().SetAttributeValue("Status", "DNF9")), x => x.Contains("Status", StringComparison.Ordinal));
        Assert.Contains(Check(d => d.Descendants("Gender").First().Value = "W"), x => x.Contains("Gender", StringComparison.Ordinal));
        Assert.Contains(Check(d => d.Root!.Add(new XElement("Surprise"))), x => x.Contains("Surprise", StringComparison.Ordinal));
    }

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

    [Theory]
    [InlineData(TimingStatus.DNS)]
    [InlineData(TimingStatus.DNF)]
    [InlineData(TimingStatus.DSQ)]
    public void TwoRunNonFinisherRetainsRunAndFirstBibInXml(TimingStatus status)
    {
        var (oneRunList, firstTiming) = Fixture();
        var first = oneRunList with { Plan = oneRunList.Plan with
            { Competition = oneRunList.Plan.Competition with { RunCount = 2 } } };
        var secondPlan = FisStartOrder.SecondRun(first, firstTiming.ToRunFinishes());
        var second = first with { Id = Guid.NewGuid(), Plan = secondPlan };
        var secondTiming = new TimingSnapshot(second.Id, 0, secondPlan.Entries.Select(entry =>
            new TimingResult(entry, entry.Bib == 1 ? status : TimingStatus.Finished,
                entry.Bib == 1 ? null : 5100 + entry.Bib * 100, null, null, null, "")
            { Disqualification = entry.Bib == 1 && status == TimingStatus.DSQ ? new(19, "629.3", "Test judge") : null }).ToArray(), [], []);
        var race = FisRaceResults.Assemble(first, firstTiming with { ListId = first.Id }, second, secondTiming);
        Assert.Equal(status, race.Rows.Single(x => x.Entry.Bib == 1).Status);
        Assert.Equal(2, race.Rows.Single(x => x.Entry.Bib == 1).StatusRun);
        var penalty = FisPenalty.Calculate(Discipline.Slalom, race.PenaltyCompetitors, new(0, 999, 0));
        var details = new FisXmlDetails("FIS", new("T", "Delegate", "FIN"), new("C", "Chief", "FIN"),
            [new(45, 43, "10:00", new("S", "Setter", "FIN")),
             new(44, 42, "13:00", new("S", "Setter", "FIN"))], CourseChief: new("Course", "Chief", "FIN"));
        var doc = XDocument.Parse(Encoding.UTF8.GetString(FisResultXml.Create(race,
            new("Series", "Ruka", "Club", s_race.Date, s_race.Date, "FIN", "2026-27"), penalty, details)));
        var notRanked = Assert.Single(doc.Descendants("AL_notranked"));
        Assert.Equal(status + "2", notRanked.Attribute("Status")?.Value);
        Assert.Equal("1", notRanked.Element("Bib")?.Value);
        Assert.Equal("2", notRanked.Element("Run")?.Value);
        // A non-finisher has no race points, which AL_result requires (3.11), so it carries no AL_result.
        Assert.Null(notRanked.Element("AL_result"));
        Assert.Equal(status == TimingStatus.DSQ ? "19" : null, notRanked.Element("Gate")?.Value);
        Assert.Equal(status == TimingStatus.DSQ ? "629.3" : null, notRanked.Element("Reason")?.Value);
        Assert.DoesNotContain("Test judge", doc.ToString(), StringComparison.Ordinal);
        Assert.Equal(9, doc.Descendants("AL_ranked").Count());
    }

    [Fact]
    public void AuditedRun1DisqualificationAfterRun2KeepsSavedOrderAndRecalculatesFinalRanks()
    {
        var (oneRun, original) = Fixture();
        var first = oneRun with { Plan = oneRun.Plan with { Competition = oneRun.Plan.Competition with { RunCount = 2 } } };
        var secondPlan = FisStartOrder.SecondRun(first, original.ToRunFinishes());
        var second = first with { Id = Guid.NewGuid(), Plan = secondPlan };
        var secondTiming = new TimingSnapshot(second.Id, 0, secondPlan.Entries.Select(entry =>
            new TimingResult(entry, TimingStatus.Finished, 5000, null, null, null, "")).ToArray(), [], []);
        var id = original.Results[0].CompetitorId;
        var dsq = new DisqualificationDetails(12, "629.3", "Test judge");
        var reviewed = original with { Results = original.Results.Select(x => x.CompetitorId == id
            ? x with { Status = TimingStatus.DSQ, Hundredths = null, Rank = null, Disqualification = dsq } : x).ToArray(),
            Audit = [new(1, first.Id, first.CreatedAt, "Operator", "Post-run jury decision",
                new(DecisionKind.Status, CompetitorId: id), new(DecisionKind.Status, CompetitorId: id, Status: TimingStatus.DSQ, Disqualification: dsq))] };
        var result = FisRaceResults.Assemble(first, reviewed, second, secondTiming);
        var disqualified = result.Rows.Single(x => x.Entry.Bib == 1);
        Assert.Equal(TimingStatus.DSQ, disqualified.Status); Assert.Equal(1, disqualified.StatusRun);
        Assert.Equal(dsq, disqualified.Disqualification); Assert.Null(disqualified.TotalHundredths);
        Assert.Equal(1, result.Rows.Single(x => x.Entry.Bib == 2).Rank);
        Assert.Throws<DomainValidationException>(() => FisRaceResults.Assemble(first, original with {
            Results = original.Results.Select(x => x.CompetitorId == id ? x with { Hundredths = 9000 } : x).ToArray() }, second, secondTiming));
    }

    [Fact]
    public void XmlContainsOfficialRankedResultsPenaltyAndRunMetadata()
    {
        var (list, timing) = Fixture();
        var race = FisRaceResults.Assemble(list, timing);
        var penalty = FisPenalty.Calculate(Discipline.Slalom, race.PenaltyCompetitors, new(0, 999, 0));
        var metadata = new FisXmlDetails("FIS", new("T", "Delegate", "FIN"), new("C", "Chief", "FIN"),
            [new(45, 43, "10:00", new("S", "Setter", "FIN"))], CourseChief: new("Course", "Chief", "FIN"));
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

    [Fact]
    public void XmlDoesNotInventWeatherMeasurementPlace()
    {
        var (list, timing) = Fixture();
        var race = FisRaceResults.Assemble(list, timing);
        var penalty = FisPenalty.Calculate(Discipline.Slalom, race.PenaltyCompetitors, new(0, 999, 0));
        var empty = RaceInformation.Empty(s_race);
        var information = empty with { Runs = [empty.Runs[0] with
            { Weather = new("Clear", "Hard", UnlocatedTemperature: -2.5m) }] };
        var details = new FisXmlDetails("FIS", new("T", "Delegate", "FIN"), new("C", "Chief", "FIN"),
            [new(45, 43, "10:00", new("S", "Setter", "FIN"))], information, CourseChief: new("Course", "Chief", "FIN"));
        var doc = XDocument.Parse(Encoding.UTF8.GetString(FisResultXml.Create(race,
            new("Series", "Test", "Club", s_race.Date, s_race.Date, "FIN", "2026/27"), penalty, details)));
        var weather = Assert.Single(doc.Descendants("Runinfo").Single().Elements("Weather"));
        Assert.Null(weather.Element("Place")); Assert.Equal("-2.5", weather.Element("Temperatureair")!.Value);
        Assert.Equal("Clear", weather.Element("Weather")!.Value);
    }

    [Fact]
    public void XmlIncludesReviewedJuryForerunnersWeatherAndRunCourseOverrides()
    {
        var (list, timing) = Fixture();
        var race = FisRaceResults.Assemble(list, timing);
        var penalty = FisPenalty.Calculate(Discipline.Slalom, race.PenaltyCompetitors, new(0, 999, 0));
        var empty = RaceInformation.Empty(s_race);
        var information = empty with { Category = "FIS", Jury = [new("Referee", new("R", "Official", "FIN"))],
            Runs = [empty.Runs[0] with { Course = "Reviewed course", StartAltitude = 700, FinishAltitude = 400, Length = 640,
                Forerunners = [new("A", new("Test", "RUNNER", "FIN")), new("B", new("Other", "RUNNER", "SWE"))],
                Weather = new("Clear", "Hard", 0m, -1.2m) }] };
        var details = new FisXmlDetails("FIS", new("T", "Delegate", "FIN"), new("C", "Chief", "FIN"),
            [new(45, 43, "10:00", new("S", "Setter", "FIN"))], information, CourseChief: new("Course", "Chief", "FIN"));
        var doc = XDocument.Parse(Encoding.UTF8.GetString(FisResultXml.Create(race,
            new("Series", "Test", "Club", s_race.Date, s_race.Date, "FIN", "2026/27"), penalty, details)));
        Assert.Equal("Reviewed course", doc.Descendants("Course").Single().Element("Name")!.Value);
        Assert.Equal("700", doc.Descendants("Startelev").Single().Value);
        Assert.Equal("640", doc.Descendants("Length").Single().Value);
        Assert.Equal(["1", "2"], doc.Descendants("Forerunner").Select(x => x.Attribute("Order")!.Value));
        Assert.Contains(doc.Descendants("Jury"), x => x.Attribute("Function")!.Value == "Referee");
        var records = doc.Descendants("Runinfo").Single().Elements("Weather").ToArray();
        Assert.Equal("0", records[0].Element("Temperatureair")!.Value);
        Assert.Equal("-1.2", records[1].Element("Temperatureair")!.Value);
        Assert.Equal("Finish", records[1].Element("Place")!.Value);
        Assert.Equal("C", doc.Descendants("Tempunit").Single().Value);
    }
}
