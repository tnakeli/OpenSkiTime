using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using OpenSkiTime.Application;
using OpenSkiTime.Domain;
using OpenSkiTime.Reporting;
using OpenSkiTime.Timing;
using UglyToad.PdfPig;
using Xunit;

namespace OpenSkiTime.Tests;

// Equal values are ordinary race data. FIS ICR Book IV (Edition July 2026):
// 617.3.3 equal time or points are ex aequo and the higher start number is listed first;
// 621.11 the first 30 (or 15) of the Run 1 result list start Run 2 in reverse order, the lowest
// start number first among competitors ranked 30th, then the rest follow the result list;
// 621.3 the first draw group grows when competitors share the boundary points.
public sealed class TieHandlingTests
{
    private static readonly DateOnly s_date = TimingRulesTests.Date;
    private static readonly DateTimeOffset s_at = TimingRulesTests.At;
    private static readonly long s_dayStart = s_date.ToDateTime(TimeOnly.MinValue).Ticks;
    private static readonly CompetitionValues s_race = new("Synthetic Slalom", "SL1", s_date, Discipline.Slalom, RaceType.Fis, 2, 0, "1234",
        CourseName: "Slope", StartAltitudeMeters: 1100, FinishAltitudeMeters: 900, HomologationNumber: "12345/01/26");
    private static readonly PointsListSource s_points = new("1327", s_date.AddDays(-5), s_date.AddDays(5));
    private static readonly (int Bib, int Rank)[] s_expectedFinalRanks = [(5, 1), (6, 1), (1, 3), (2, 3), (3, 3), (9, 6), (10, 6)];
    private static readonly int[] s_unclassifiedBibs = [4, 7, 8];

    // ---------- Engine: equal elapsed hundredths are one sporting rank ----------

    [Theory]
    [InlineData(2, 1)]
    [InlineData(3, 1)]
    [InlineData(5, 1)]
    [InlineData(2, 15)]
    [InlineData(3, 14)]
    [InlineData(6, 13)]
    [InlineData(2, 30)]
    [InlineData(4, 29)]
    [InlineData(12, 25)]
    [InlineData(40, 1)]
    public void ReplayRanksEqualHundredthsExAequoAtEveryBoundary(int tied, int firstTiedRank)
    {
        const int count = 40;
        var list = TimingRulesTests.List(count);
        var plannedRanks = PlannedRanks(count, tied, firstTiedRank);
        var (observations, audit) = Race(list, bib => plannedRanks[bib].Hundredths, bib => plannedRanks[bib].SubHundredthTicks);
        var snapshot = TimingEngine.Replay(list, observations, audit, 0, 1);

        Assert.Equal(count, snapshot.Results.Count);
        Assert.Equal(count, snapshot.Results.Select(x => x.CompetitorId).Distinct().Count());
        Assert.All(snapshot.Results, x =>
        {
            Assert.Equal(TimingStatus.Finished, x.Status);
            Assert.Equal(plannedRanks[x.Bib].Hundredths, x.Hundredths);
            Assert.Equal(plannedRanks[x.Bib].Rank, x.Rank);
        });
        var tiedRows = snapshot.Results.Where(x => x.Rank == firstTiedRank).ToArray();
        Assert.Equal(tied, tiedRows.Length);
        Assert.Single(tiedRows.Select(x => x.Hundredths).Distinct());
        if (firstTiedRank - 1 + tied < count)
        { Assert.Equal(firstTiedRank + tied, snapshot.Results.Where(x => x.Rank > firstTiedRank).Min(x => x.Rank)); }
        Assert.DoesNotContain(snapshot.Results, x => x.Rank == firstTiedRank + 1 && tied > 1);

        // Input order does not choose a winner among tied competitors.
        var reordered = TimingEngine.Replay(list, observations.Reverse().ToArray(), audit.Reverse().ToArray(), 0, 1);
        Assert.Equal(snapshot.Results.Select(x => (x.Bib, x.Hundredths, x.Rank)), reordered.Results.Select(x => (x.Bib, x.Hundredths, x.Rank)));
        var again = TimingEngine.Replay(list, observations, audit, 0, 1);
        Assert.Equal(snapshot.Results.Select(x => (x.Bib, x.Status, x.Hundredths, x.Rank)), again.Results.Select(x => (x.Bib, x.Status, x.Hundredths, x.Rank)));
    }

    [Fact]
    public void SubHundredthDifferencesDoNotBreakATieAndCrossingAHundredthDoes()
    {
        var list = TimingRulesTests.List(4);
        // Same truncated hundredth (61.23) from different raw ticks; then 61.2399999 vs 61.2400000.
        var raw = new Dictionary<int, long> { [1] = 61_230_0000, [2] = 61_239_9999, [3] = 61_239_9999, [4] = 61_240_0000 };
        var (observations, audit) = Race(list, bib => raw[bib] / TimingTime.TicksPerHundredth, bib => raw[bib] % TimingTime.TicksPerHundredth);
        var snapshot = TimingEngine.Replay(list, observations, audit, 0, 1);
        Assert.Equal([6123, 6123, 6123, 6124], snapshot.Results.OrderBy(x => x.Bib).Select(x => x.Hundredths));
        Assert.Equal([1, 1, 1, 4], snapshot.Results.OrderBy(x => x.Bib).Select(x => x.Rank));
    }

    [Fact]
    public void CorrectedTimeJoiningAndLeavingATieRecalculatesRanksDeterministically()
    {
        var list = TimingRulesTests.List(4);
        var times = new Dictionary<int, long> { [1] = 6000, [2] = 6010, [3] = 6020, [4] = 6030 };
        var (observations, audit) = Race(list, bib => times[bib], _ => 0);
        var bib4 = list.Plan.Entries.Single(x => x.Bib == 4).Entrant.CompetitorId;
        TimingAudit Correction(long id, long? hundredths) => new(id, list.Id, s_at, "Operator", "Verified backup time",
            new(DecisionKind.Time, CompetitorId: bib4), new(DecisionKind.Time, CompetitorId: bib4, Hundredths: hundredths));
        var joined = TimingEngine.Replay(list, observations, audit.Append(Correction(100, 6010)).ToArray(), 0, 1);
        Assert.Equal([1, 2, 4, 2], joined.Results.OrderBy(x => x.Bib).Select(x => x.Rank));
        var threeWay = TimingEngine.Replay(list, observations, audit.Append(Correction(100, 6000)).ToArray(), 0, 1);
        Assert.Equal([1, 3, 4, 1], threeWay.Results.OrderBy(x => x.Bib).Select(x => x.Rank));
        // Clearing the correction appends history and restores the measured time and rank.
        var cleared = TimingEngine.Replay(list, observations, audit.Append(Correction(100, 6000)).Append(Correction(101, null)).ToArray(), 0, 1);
        Assert.Equal([1, 2, 3, 4], cleared.Results.OrderBy(x => x.Bib).Select(x => x.Rank));
        Assert.Equal(6030, cleared.Results.Single(x => x.Bib == 4).Hundredths);
    }

    [Fact]
    public void EqualIntermediateTimesShareASplitValueAndDoNotChangeFinishRanking()
    {
        var race = s_race with { IntermediateCount = 2 };
        var list = TimingRulesTests.List(4);
        list = list with { Plan = list.Plan with { Competition = race } };
        var session = Guid.NewGuid();
        var observations = new List<TimingObservation>();
        var audit = new List<TimingAudit>();
        // Splits (I1, I2) and finishes: equal I1 for three, equal I2 leading to different finishes, different I1 leading to equal finish.
        var plan = new Dictionary<int, (long I1, long I2, long Finish)>
        {
            [1] = (2000, 4000, 6000), [2] = (2000, 4000, 6050), [3] = (2000, 4100, 6000), [4] = (2050, 4100, 6100)
        };
        foreach (var entry in list.Plan.Entries)
        {
            var start = s_dayStart + TimeSpan.FromHours(10).Ticks + entry.Bib * TimeSpan.TicksPerMinute;
            var values = plan[entry.Bib];
            Add(0, start); Add(2, start + values.I1 * TimingTime.TicksPerHundredth);
            Add(3, start + values.I2 * TimingTime.TicksPerHundredth); Add(1, start + values.Finish * TimingTime.TicksPerHundredth);
            void Add(int channel, long ticks)
            {
                var observation = Impulse(session, observations.Count + 1, channel, ticks);
                observations.Add(observation);
                audit.Add(Assign(list, audit.Count + 1, observation.Key, entry.Bib));
            }
        }
        var snapshot = TimingEngine.Replay(list, observations, audit, 0, 1);
        var rows = snapshot.Results.OrderBy(x => x.Bib).ToArray();
        Assert.Equal([2000, 2000, 2000, 2050], rows.Select(x => x.Splits[0].Hundredths));
        Assert.Equal([4000, 4000, 4100, 4100], rows.Select(x => x.Splits[1].Hundredths));
        Assert.Equal([1, 3, 1, 4], rows.Select(x => x.Rank));
        Assert.All(rows, x => Assert.All(x.Splits, s => Assert.Equal("", s.Detail)));
    }

    // ---------- Combined and final results ----------

    [Fact]
    public void CombinedAndFinalResultsKeepEqualTotalsExAequoAcrossDifferentRunSplits()
    {
        var (first, firstTiming) = FirstRunWithTimes(new()
        {
            [1] = 5000, [2] = 5050, [3] = 5000, [4] = 5100, [5] = 5020, [6] = 5030, [7] = 5040, [8] = null, [9] = 5060, [10] = 5070
        }, missing: new() { [8] = TimingStatus.DNF });
        var secondPlan = FisStartOrder.SecondRun(first, firstTiming.ToRunFinishes());
        var second = first with { Id = Guid.NewGuid(), Revision = 1, Plan = secondPlan };
        // Totals: bib 1 = 10100, bib 2 = 10100 (different split), bib 3 = 10100, bib 4 DSQ, bib 5 = 10000, bib 6 = 10000,
        // bib 7 DNS, bib 9 = 10161, bib 10 = 10161.
        var secondTimes = new Dictionary<int, long?> { [1] = 5100, [2] = 5050, [3] = 5100, [4] = null, [5] = 4980, [6] = 4970, [7] = null, [9] = 5101, [10] = 5091 };
        var secondStatus = new Dictionary<int, TimingStatus> { [4] = TimingStatus.DSQ, [7] = TimingStatus.DNS };
        var secondTiming = new TimingSnapshot(second.Id, 0, secondPlan.Entries.Select(entry => new TimingResult(entry,
            secondStatus.GetValueOrDefault(entry.Bib, TimingStatus.Finished), secondTimes[entry.Bib], null, null, null, "")).ToArray(), [], []);

        var combined = TimingEngine.Combined(secondTiming, firstTiming).ToDictionary(x => x.Result.Bib);
        Assert.Equal(1, combined[5].Rank); Assert.Equal(1, combined[6].Rank);
        Assert.Equal(3, combined[1].Rank); Assert.Equal(3, combined[2].Rank); Assert.Equal(3, combined[3].Rank);
        Assert.Equal(6, combined[9].Rank); Assert.Equal(6, combined[10].Rank);
        Assert.Null(combined[4].Rank); Assert.Null(combined[7].Rank);

        var race = FisRaceResults.Assemble(first, firstTiming, second, secondTiming);
        Assert.Equal(10, race.Rows.Count);
        var final = race.Rows.ToDictionary(x => x.Entry.Bib);
        foreach (var (bib, rank) in s_expectedFinalRanks)
        { Assert.Equal(rank, final[bib].Rank); Assert.Equal(combined[bib].Total, final[bib].TotalHundredths); }
        Assert.All(s_unclassifiedBibs, bib => Assert.Null(final[bib].Rank));
        Assert.Equal((TimingStatus.DNF, 1), (final[8].Status, final[8].StatusRun));
        Assert.Equal((TimingStatus.DSQ, 2), (final[4].Status, final[4].StatusRun));
        Assert.Equal((TimingStatus.DNS, 2), (final[7].Status, final[7].StatusRun));
    }

    [Fact]
    public void OfficialListingOrdersEqualRanksByHigherStartNumberAndIsIndependentOfInputOrder()
    {
        var rows = new (int Bib, long? Time)[] { (3, 100), (8, 100), (5, 90), (12, 100), (1, null), (7, 110), (2, 90), (4, null) };
        var expected = new[] { 5, 2, 12, 8, 3, 7 };
        foreach (var input in new[] { rows, rows.Reverse().ToArray(), rows.OrderBy(x => x.Bib).ToArray() })
        {
            var listed = input.OrderByOfficialResult(x => x.Time, x => x.Bib).ToArray();
            Assert.Equal(expected, listed.Take(6).Select(x => x.Bib));
            Assert.Equal(rows.Length, listed.Length);
            Assert.All(listed.Skip(6), x => Assert.Null(x.Time));
        }
        Assert.Equal(1, ResultOrder.Rank(90, rows.Select(x => x.Time)));
        Assert.Equal(3, ResultOrder.Rank(100, rows.Select(x => x.Time)));
        Assert.Equal(6, ResultOrder.Rank(110, rows.Select(x => x.Time)));
        Assert.Null(ResultOrder.Rank(null, rows.Select(x => x.Time)));
    }

    [Fact]
    public void ResultXmlListsTiedCompetitorsWithEqualRankAndHigherBibFirst()
    {
        var (list, timing) = OneRunRace(new() { [1] = 5000, [2] = 5100, [3] = 5000, [4] = 5100, [5] = 5100, [6] = 5300, [7] = 5000 });
        var race = FisRaceResults.Assemble(list, timing);
        var penalty = FisPenalty.Calculate(Discipline.Downhill, race.PenaltyCompetitors, new(0, 999, 0));
        var details = new FisXmlDetails("FIS", new("T", "Delegate", "FIN"), new("C", "Chief", "FIN"), [new(45, 43, "10:00", new("S", "Setter", "FIN"))]);
        var doc = XDocument.Parse(Encoding.UTF8.GetString(FisResultXml.Create(race,
            new("Series", "Test", "Club", s_date, s_date, "FIN", "2026/27"), penalty, details)));
        var ranked = doc.Descendants("AL_ranked").Select(x => (Rank: x.Element("Rank")!.Value, Bib: x.Element("Bib")!.Value)).ToArray();
        Assert.Equal([("1", "7"), ("1", "3"), ("1", "1"), ("4", "5"), ("4", "4"), ("4", "2"), ("7", "6")], ranked);
        Assert.Equal(["00:00:00", "00:00:00", "00:00:00", "00:01:00", "00:01:00", "00:01:00", "00:03:00"],
            doc.Descendants("AL_ranked").Select(x => x.Element("AL_result")!.Element("Diff")!.Value));
        Assert.Equal(penalty.RacePoints[7], penalty.RacePoints[1]);
    }

    [Fact]
    public void OfficialResultsPdfListsTiedCompetitorsWithEqualRankAndHigherBibFirst()
    {
        var (list, timing) = OneRunRace(new() { [1] = 5000, [2] = 5100, [3] = 5000, [4] = 5100, [5] = 5100, [6] = 5300, [7] = 5000 });
        var race = FisRaceResults.Assemble(list, timing);
        var penalty = FisPenalty.Calculate(Discipline.Downhill, race.PenaltyCompetitors, new(0, 999, 0));
        var folder = Path.Combine(Path.GetTempPath(), "OpenSkiTime-TieTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var competition = new CompetitionDetails(list.Plan.CompetitionId, list.Plan.Competition);
            var series = new SeriesDetails(Guid.NewGuid(), new("Series", "Test", "Club", s_date, s_date, "FIN", "2026/27"), 1, [competition]);
            var desk = new CompetitorDeskDetails(series.Id, 1, [], [], []);
            var approval = new ApprovedResult(Guid.NewGuid(), competition.Id, 1, list.Id, null, "fingerprint", s_at, "TD",
                "FIN1234.xml", [1], penalty.Calculated, penalty.Applied);
            var source = new PdfReportSource(Path.Combine(folder, "race.ost"), series, competition, desk, new(new(), []), [],
                [new FinalReportData(race, penalty, approval, null)], null, null, "version-1");
            var descriptor = new PdfReportDescriptor("official", PdfReportType.OfficialResults, "Official Results", competition.Id, null,
                "official.pdf", true, null);
            var path = Path.Combine(folder, descriptor.FileName);
            new QuestPdfRenderer().Render(new PdfReportData(descriptor, source, [], s_at), path);
            using var pdf = PdfDocument.Open(path);
            var lines = pdf.GetPages().SelectMany(page => page.GetWords().GroupBy(x => Math.Round(x.BoundingBox.Bottom, 0))
                .OrderByDescending(x => x.Key).Select(x => x.OrderBy(w => w.BoundingBox.Left).Select(w => w.Text).ToArray()))
                .Where(x => x.Length > 3 && x[2].StartsWith("5000", StringComparison.Ordinal)).ToArray();
            Assert.Equal([("1", "7"), ("1", "3"), ("1", "1"), ("4", "5"), ("4", "4"), ("4", "2"), ("7", "6")], lines.Select(x => (x[0], x[1])));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    // ---------- Run 2 starting order (ICR 621.11) ----------

    [Fact]
    public void SecondRunTiesInsideReversedGroupStartLowestBibFirstAndOutsideFollowTheResultList()
    {
        // Ranks 5 (bibs 5, 6) tied inside the reversed group; ranks 33 (bibs 33, 34, 35) tied outside it.
        var times = Enumerable.Range(1, 36).ToDictionary(bib => bib, bib => (long?)(6000 + bib));
        times[6] = times[5]; times[34] = times[33]; times[35] = times[33];
        var second = SecondRun(times, 30);
        Assert.Equal(30, second.Entries.Count(x => x.Group == "Reversed group"));
        var bibs = second.Entries.Select(x => x.Bib).ToArray();
        Assert.Equal(30, bibs[0]);
        Assert.True(Array.IndexOf(bibs, 5) < Array.IndexOf(bibs, 6));
        Assert.Equal([35, 34, 33, 36], bibs.Skip(32));
        AssertSecondRunInvariants(times, 30, second);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    public void SecondRunCompetitorsRankedThirtiethAllStartFirstLowestBibFirst(int tied)
    {
        var times = Enumerable.Range(1, 40).ToDictionary(bib => bib, bib => (long?)(6000 + bib * 10));
        // Bibs 30, 31, ... share the 30th time; an unrelated higher bib (40) is also tied to prove bib rather than position decides.
        foreach (var bib in Enumerable.Range(31, tied - 2)) { times[bib] = times[30]; }
        times[40] = times[30];
        var second = SecondRun(times, 30);
        var tiedBibs = times.Where(x => x.Value == times[30]).Select(x => x.Key).Order().ToArray();
        Assert.Equal(29 + tied, second.Entries.Count(x => x.Group == "Reversed group"));
        Assert.Equal(tiedBibs, second.Entries.Take(tied).Select(x => x.Bib));
        Assert.Equal(29, second.Entries[tied].Bib);
        Assert.Equal(1, second.Entries[28 + tied].Bib);
        AssertSecondRunInvariants(times, 30, second);
    }

    [Theory]
    [InlineData(28, 5, 32)] // five tied at rank 28 span the boundary
    [InlineData(29, 3, 31)] // three tied at rank 29: nobody is ranked 30th, all are within the first 30 ranks
    [InlineData(29, 2, 30)] // tie immediately above the cutoff
    [InlineData(31, 2, 30)] // tie immediately below the cutoff
    [InlineData(1, 2, 30)]  // tied winners start last, the lower bib first
    [InlineData(1, 45, 45)] // everyone tied
    public void SecondRunReversalBoundaryFollowsSportingRank(int firstTiedRank, int tied, int reversedCount)
    {
        const int count = 45;
        var scrambled = Enumerable.Range(1, count).OrderBy(bib => bib * 17 % 47).ToArray();
        var times = new Dictionary<int, long?>();
        for (var i = 0; i < count; i++)
        { times[scrambled[i]] = 6000 + (i >= firstTiedRank - 1 && i < firstTiedRank - 1 + tied ? firstTiedRank - 1 : i) * 10; }
        var second = SecondRun(times, 30);
        Assert.Equal(reversedCount, second.Entries.Count(x => x.Group == "Reversed group"));
        AssertSecondRunInvariants(times, 30, second);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void ReducedFifteenReversalIncludesEveryoneRankedFifteenth(int tied)
    {
        var times = Enumerable.Range(1, 25).ToDictionary(bib => bib, bib => (long?)(5000 + bib * 3));
        foreach (var bib in Enumerable.Range(16, tied - 1)) { times[bib] = times[15]; }
        var second = SecondRun(times, 15);
        Assert.Equal(14 + tied, second.Entries.Count(x => x.Group == "Reversed group"));
        Assert.Equal(Enumerable.Range(15, tied), second.Entries.Take(tied).Select(x => x.Bib));
        AssertSecondRunInvariants(times, 15, second);
    }

    [Fact]
    public void SecondRunGapsFromNonFinishersDoNotCountTowardTheReversal()
    {
        var times = Enumerable.Range(1, 40).ToDictionary(bib => bib, bib => (long?)(6000 + bib));
        var status = new Dictionary<int, FinishStatus> { [2] = FinishStatus.DNS, [10] = FinishStatus.DNF, [20] = FinishStatus.DSQ, [25] = FinishStatus.NPS };
        foreach (var bib in status.Keys) { times[bib] = null; }
        times[35] = times[34]; // the 30th and 31st classified (bibs 34, 35) are tied
        var second = SecondRun(times, 30, status);
        Assert.Equal(36, second.Entries.Count);
        Assert.DoesNotContain(second.Entries, x => status.ContainsKey(x.Bib));
        Assert.Equal(31, second.Entries.Count(x => x.Group == "Reversed group"));
        Assert.Equal([34, 35, 33], second.Entries.Take(3).Select(x => x.Bib));
        AssertSecondRunInvariants(times, 30, second);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(42)]
    [InlineData(2026)]
    public void SecondRunInvariantsHoldForRandomLargeTieGroups(int seed)
    {
        var random = new Random(seed);
        foreach (var reversal in new[] { 30, 15 })
        {
            var count = random.Next(10, 70);
            // Few distinct values produce large tie groups across every boundary.
            var distinct = random.Next(1, 12);
            var times = Enumerable.Range(1, count).ToDictionary(bib => bib, _ => random.Next(10) == 0 ? (long?)null : 6000 + random.Next(distinct) * 7);
            if (times.Values.All(x => x is null)) { times[1] = 6000; }
            var second = SecondRun(times, reversal);
            AssertSecondRunInvariants(times, reversal, second);
        }
    }

    [Fact]
    public async Task SecondRunTiesAtTheBoundarySurviveSaveReloadAndStartValidation()
    {
        var root = Path.Combine(Path.GetTempPath(), "openskitime-tie-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var workspace = new SeriesWorkspace(new OpenSkiTime.Persistence.SqliteSeriesFileStore());
            var series = await workspace.CreateAsync(Path.Combine(root, "ties.ost"), new("Synthetic", "Test", "Test", s_date, s_date, "FIN", "2026/27"));
            series = await workspace.SaveCompetitionAsync(null, s_race, series.Revision);
            var competitionId = series.Competitions[0].Id;
            var revision = series.Revision;
            var entrants = new List<DrawEntrant>();
            for (var i = 1; i <= 34; i++)
            {
                var athlete = new CompetitorValues($"TIE{i:00}", "Athlete", 2000, (300000 + i).ToString(CultureInfo.InvariantCulture), "FIN", "Synthetic", Gender.Female);
                var saved = await workspace.SaveDeskRowAsync(null, athlete, competitionId, true, null, revision);
                revision = saved.Revision;
                entrants.Add(new(saved.Value.Id, athlete, i <= 16 ? 5m : 100m + i));
            }
            var firstPlan = FisStartOrder.FirstRun(competitionId, s_race, Gender.Female, entrants, s_points, new(), "tie-seed");
            Assert.Equal(16, firstPlan.Entries.Count(x => x.Group == "First group"));
            var desk = await workspace.SaveStartListAsync(new(firstPlan, revision, "Operator", "Draw", s_at));
            var first = (await workspace.MarkRunStartedAsync(desk.Revisions[0].Id, desk.SeriesRevision, "Operator", s_at)).Revisions[0];
            var results = first.Plan.Entries.Select(x => new RunFinish(x.Entrant.CompetitorId, FinishStatus.Finished,
                x.Bib is 29 or 30 or 31 or 34 ? 6029 : 6000 + x.Bib)).ToArray();
            var secondPlan = FisStartOrder.SecondRun(first, results);
            var saved2 = await workspace.SaveStartListAsync(new(secondPlan, (await workspace.ReadAsync()).Revision, "Operator", "Run 2", s_at));
            var stored = saved2.Revisions.Single(x => x.Plan.RunNumber == 2);
            await workspace.CloseAsync();
            await workspace.OpenAsync(Path.Combine(root, "ties.ost"));
            var reopened = (await workspace.ReadStartListsAsync(competitionId)).Revisions;
            var reloaded = reopened.Single(x => x.Plan.RunNumber == 2);
            Assert.Equal(secondPlan.Entries, reloaded.Plan.Entries);
            Assert.Equal([29, 30, 31, 34], reloaded.Plan.Entries.Take(4).Select(x => x.Bib));
            Assert.Equal(firstPlan.Entries, reopened.Single(x => x.Plan.RunNumber == 1).Plan.Entries);
            Assert.Equal(16, reopened.Single(x => x.Plan.RunNumber == 1).Plan.Entries.Count(x => x.Group == "First group"));
            Assert.Equal(JsonSerializer.Serialize(secondPlan.Entries), JsonSerializer.Serialize(FisStartOrder.SecondRun(reopened.Single(x => x.Plan.RunNumber == 1),
                reloaded.Plan.SourceResults, reloaded.Plan.Options.ReverseCount).Entries));
            var started = await workspace.MarkRunStartedAsync(stored.Id, (await workspace.ReadAsync()).Revision, "Operator", s_at);
            Assert.NotNull(started.Revisions.Single(x => x.Id == stored.Id).StartedAt);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    // ---------- First-run draw groups (ICR 621.3) ----------

    [Theory]
    [InlineData(2, 15, 16)]  // two share the 15th rank
    [InlineData(3, 15, 17)]  // three share the 15th rank
    [InlineData(9, 12, 20)]  // a large group spanning the boundary
    [InlineData(2, 14, 15)]  // tie immediately before the boundary
    [InlineData(2, 16, 15)]  // tie immediately after the boundary
    [InlineData(1, 1, 15)]   // all different points
    [InlineData(40, 1, 40)]  // everyone has the same points
    public void FirstGroupIncludesEveryCompetitorSharingTheBoundaryPoints(int tied, int firstTiedPosition, int expectedFirstGroup)
    {
        var entrants = Entrants(40, position => position >= firstTiedPosition && position < firstTiedPosition + tied ? firstTiedPosition : position);
        var plan = Draw(entrants, "boundary-seed");
        AssertDrawInvariants(entrants, plan, 15);
        Assert.Equal(expectedFirstGroup, plan.Entries.Count(x => x.Group == "First group"));
    }

    [Fact]
    public void ManyEqualPointsGroupsAreDrawnWithinTheirOwnGroupAndCompetitorsWithoutPointsStayLast()
    {
        var entrants = Entrants(60, position => position switch
        {
            <= 5 => 10m, <= 12 => 12.5m, <= 20 => 15m, <= 30 => 20m, <= 50 => 25m, _ => null
        });
        // A different decimal scale is the same FIS points value.
        entrants[25] = entrants[25] with { Points = 20.00m };
        var plan = Draw(entrants, "many-ties");
        AssertDrawInvariants(entrants, plan, 15);
        Assert.Equal(20, plan.Entries.Count(x => x.Group == "First group"));
        Assert.Equal(30, plan.Entries.Count(x => x.Group == "Points order"));
        Assert.Equal(10, plan.Entries.Count(x => x.Group == "No points"));
        Assert.All(plan.Entries.Take(50), x => Assert.NotNull(x.Entrant.Points));
        Assert.All(plan.Entries.Skip(50), x => Assert.Null(x.Entrant.Points));
        var equal = FisStartOrder.EqualPointsCompetitors(plan.Entries);
        Assert.Equal(50, equal.Count);
        Assert.DoesNotContain(plan.Entries.Skip(50), x => equal.Contains(x.Entrant.CompetitorId));
    }

    [Fact]
    public void FewerThanFifteenWithPointsKeepsCompetitorsWithoutPointsOutOfTheFirstGroup()
    {
        var entrants = Entrants(20, position => position <= 9 ? (position <= 3 ? 30m : position) : null);
        var plan = Draw(entrants, "small-field");
        AssertDrawInvariants(entrants, plan, 15);
        Assert.Equal(9, plan.Entries.Count(x => x.Group == "First group"));
        Assert.Equal(11, plan.Entries.Count(x => x.Group == "No points"));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(99)]
    public void DrawInvariantsHoldForRandomPointsWithLargeTieGroups(int seed)
    {
        var random = new Random(seed);
        for (var round = 0; round < 20; round++)
        {
            var count = random.Next(1, 80);
            var distinct = random.Next(1, 10);
            var entrants = Entrants(count, _ => random.Next(8) == 0 ? null : 5m + random.Next(distinct) * 2.5m);
            var plan = Draw(entrants, "random-" + round.ToString(CultureInfo.InvariantCulture));
            AssertDrawInvariants(entrants, plan, 15);
            var permuted = entrants.OrderBy(_ => random.Next()).ToArray();
            Assert.Equal(plan.Entries, Draw(permuted, "random-" + round.ToString(CultureInfo.InvariantCulture)).Entries);
        }
    }

    [Theory]
    [InlineData(new int[0])]
    [InlineData(new[] { 2 })]
    [InlineData(new[] { 3 })]
    [InlineData(new[] { 2, 3, 7 })]
    public void EqualPointsIndicatorFindsWholeGroupsRegardlessOfRowOrder(int[] groupSizes)
    {
        ArgumentNullException.ThrowIfNull(groupSizes);
        var points = new List<decimal?>();
        var value = 1m;
        foreach (var size in groupSizes) { points.AddRange(Enumerable.Repeat<decimal?>(value, size)); value += 10; }
        points.AddRange([100m, 101m, null, null, null]);
        var entries = points.Select((p, i) => new StartListEntry(i + 1, i + 1, new DrawEntrant(new Guid(i + 1, 0, 0, new byte[8]),
            new("EQ" + i.ToString(CultureInfo.InvariantCulture), "Athlete", 2005, "40000" + i.ToString(CultureInfo.InvariantCulture), "FIN", "Club", Gender.Female), p), "x")).ToArray();
        var expected = entries.Take(groupSizes.Sum()).Select(x => x.Entrant.CompetitorId).ToHashSet();
        Assert.Equal(expected, FisStartOrder.EqualPointsCompetitors(entries).ToHashSet());
        Assert.Equal(expected, FisStartOrder.EqualPointsCompetitors(entries.Reverse().ToArray()).ToHashSet());
        Assert.Equal(expected, FisStartOrder.EqualPointsCompetitors(entries.OrderBy(x => x.Entrant.CompetitorId.ToString("N", CultureInfo.InvariantCulture)[^4..], StringComparer.Ordinal).ToArray()).ToHashSet());
    }

    // ---------- helpers ----------

    private static Dictionary<int, (long Hundredths, long SubHundredthTicks, int Rank)> PlannedRanks(int count, int tied, int firstTiedRank)
    {
        // Ranks do not follow bibs: a fixed scramble spreads tied competitors over non-consecutive bibs.
        var order = Enumerable.Range(1, count).OrderBy(bib => bib * 37 % 41).ToArray();
        var result = new Dictionary<int, (long, long, int)>();
        for (var i = 0; i < count; i++)
        {
            var inTie = i >= firstTiedRank - 1 && i < firstTiedRank - 1 + tied;
            var place = inTie ? firstTiedRank - 1 : i;
            result[order[i]] = (6000 + place * 13, i % 7 * 14_285, inTie ? firstTiedRank : i + 1);
        }
        return result;
    }

    private static TimingObservation Impulse(Guid session, long sequence, int channel, long ticks) =>
        new($"{session:N}:{sequence}:0", session, sequence, "Timy", $"fingerprint:{sequence}", ObservationKind.Impulse,
            channel, ticks, 4, null, false, "clock", "Synthetic impulse");

    private static TimingAudit Assign(StartListRevision list, long id, string key, int bib) => new(id, list.Id, s_at, "Operator", "Assign",
        new(DecisionKind.Assignment, key), new(DecisionKind.Assignment, key, Bib: bib));

    private static (TimingObservation[] Observations, TimingAudit[] Audit) Race(StartListRevision list, Func<int, long> hundredths, Func<int, long> extraTicks)
    {
        var session = Guid.NewGuid();
        var observations = new List<TimingObservation>();
        var audit = new List<TimingAudit>();
        foreach (var entry in list.Plan.Entries)
        {
            var start = s_dayStart + TimeSpan.FromHours(10).Ticks + entry.Bib * TimeSpan.TicksPerMinute + entry.Bib * 37;
            var startImpulse = Impulse(session, observations.Count + 1, 0, start);
            observations.Add(startImpulse);
            audit.Add(Assign(list, audit.Count + 1, startImpulse.Key, entry.Bib));
            var finish = Impulse(session, observations.Count + 1, 1, start + hundredths(entry.Bib) * TimingTime.TicksPerHundredth + extraTicks(entry.Bib));
            observations.Add(finish);
            audit.Add(Assign(list, audit.Count + 1, finish.Key, entry.Bib));
        }
        return (observations.ToArray(), audit.ToArray());
    }

    private static (StartListRevision List, TimingSnapshot Timing) FirstRunWithTimes(Dictionary<int, long?> times, Dictionary<int, TimingStatus>? missing = null)
    {
        var entries = times.Keys.Order().Select(bib => new StartListEntry(bib, bib, new DrawEntrant(new Guid(bib, 0, 0, new byte[8]),
            new("TIE" + bib.ToString(CultureInfo.InvariantCulture), "Athlete", 2000, (500000 + bib).ToString(CultureInfo.InvariantCulture), "FIN", "Club", Gender.Male),
            bib <= 6 ? bib * 10 : null), "Points order")).ToArray();
        var plan = new StartListPlan(Guid.NewGuid(), s_race, Gender.Male, 1, "test", "seed", new(), s_points, null, [], entries);
        var list = new StartListRevision(Guid.NewGuid(), 1, s_at, null, "op", "draw", plan);
        var results = entries.Select(entry => new TimingResult(entry,
            missing is not null && missing.TryGetValue(entry.Bib, out var status) ? status : TimingStatus.Finished,
            times[entry.Bib], null, null, null, "")).ToArray();
        return (list, new TimingSnapshot(list.Id, 0, results, [], []));
    }

    private static (StartListRevision List, TimingSnapshot Timing) OneRunRace(Dictionary<int, long?> times)
    {
        var (list, timing) = FirstRunWithTimes(times);
        var downhill = list.Plan.Competition with { Discipline = Discipline.Downhill, RunCount = 1 };
        list = list with { Plan = list.Plan with { Competition = downhill } };
        var rows = timing.Results.Select(x => x with { Entry = x.Entry }).ToArray();
        return (list, timing with { ListId = list.Id, Results = rows });
    }

    private static StartListPlan SecondRun(Dictionary<int, long?> times, int reversal, Dictionary<int, FinishStatus>? status = null)
    {
        var count = times.Count;
        var entrants = Enumerable.Range(1, count).Select(i => new DrawEntrant(new Guid(i, 0, 0, new byte[8]),
            new("RUN" + i.ToString(CultureInfo.InvariantCulture), "Athlete", 2000, (600000 + i).ToString(CultureInfo.InvariantCulture), "FIN", "Club", Gender.Female), i)).ToArray();
        var plan = FisStartOrder.FirstRun(Guid.NewGuid(), s_race, Gender.Female, entrants, s_points, new(), "second-run-seed");
        var first = new StartListRevision(Guid.NewGuid(), 1, s_at, null, "op", "draw", plan);
        var results = first.Plan.Entries.Select(x => new RunFinish(x.Entrant.CompetitorId,
            times[x.Bib] is null ? status?.GetValueOrDefault(x.Bib, FinishStatus.DNF) ?? FinishStatus.DNF : FinishStatus.Finished, times[x.Bib])).ToArray();
        var second = FisStartOrder.SecondRun(first, results, reversal);
        // The input order of results cannot change the starting order.
        Assert.Equal(second.Entries, FisStartOrder.SecondRun(first, results.Reverse().ToArray(), reversal).Entries);
        return second;
    }

    private static void AssertSecondRunInvariants(Dictionary<int, long?> times, int reversal, StartListPlan second)
    {
        var classified = times.Where(x => x.Value is not null).Select(x => (Bib: x.Key, Time: x.Value!.Value)).ToArray();
        var listing = classified.OrderByOfficialResult(x => x.Time, x => x.Bib).ToArray();
        var reversed = listing.Where(x => ResultOrder.Rank(x.Time, classified.Select(c => (long?)c.Time)) <= reversal).ToArray();
        var expected = reversed.Reverse().Concat(listing.Skip(reversed.Length)).Select(x => x.Bib).ToArray();
        Assert.Equal(expected, second.Entries.Select(x => x.Bib));
        Assert.Equal(classified.Length, second.Entries.Select(x => x.Entrant.CompetitorId).Distinct().Count());
        Assert.Equal(Enumerable.Range(1, classified.Length), second.Entries.Select(x => x.Position));
        Assert.All(second.Entries.Take(reversed.Length), x => Assert.Equal("Reversed group", x.Group));
        Assert.All(second.Entries.Skip(reversed.Length), x => Assert.Equal("Result order", x.Group));
        Assert.True(reversed.Length >= Math.Min(reversal, classified.Length));
    }

    private static DrawEntrant[] Entrants(int count, Func<int, decimal?> pointsForPosition) => Enumerable.Range(1, count).Select(i =>
        new DrawEntrant(new Guid(i, 7, 7, new byte[8]), new("DRAW" + i.ToString("D3", CultureInfo.InvariantCulture), "Athlete", 2008,
            (700000 + i).ToString(CultureInfo.InvariantCulture), "FIN", "Club", Gender.Female), pointsForPosition(i))).ToArray();

    private static StartListPlan Draw(DrawEntrant[] entrants, string seed) =>
        FisStartOrder.FirstRun(Guid.Parse("10000000-0000-0000-0000-000000000077"), s_race, Gender.Female, entrants, s_points, new(), seed);

    private static void AssertDrawInvariants(DrawEntrant[] entrants, StartListPlan plan, int firstGroup)
    {
        Assert.Equal(entrants.Length, plan.Entries.Count);
        Assert.Equal(entrants.Select(x => x.CompetitorId).Order(), plan.Entries.Select(x => x.Entrant.CompetitorId).Order());
        Assert.Equal(Enumerable.Range(1, entrants.Length), plan.Entries.Select(x => x.Position));
        Assert.Equal(Enumerable.Range(1, entrants.Length), plan.Entries.Select(x => x.Bib));
        var withPoints = entrants.Where(x => x.Points is not null).OrderBy(x => x.Points).ToArray();
        var boundary = withPoints.Length == 0 ? null : withPoints[Math.Min(firstGroup, withPoints.Length) - 1].Points;
        var expectedFirst = withPoints.Where(x => x.Points <= boundary).Select(x => x.CompetitorId).Order().ToArray();
        Assert.Equal(expectedFirst, plan.Entries.Where(x => x.Group == "First group").Select(x => x.Entrant.CompetitorId).Order());
        Assert.Equal(expectedFirst.Length, plan.Entries.TakeWhile(x => x.Group == "First group").Count());
        // After the first group, points never decrease and every equal-points group is contiguous.
        var rest = plan.Entries.Skip(expectedFirst.Length).Where(x => x.Entrant.Points is not null).ToArray();
        Assert.Equal(rest.Select(x => x.Entrant.Points).Order(), rest.Select(x => x.Entrant.Points));
        Assert.All(rest, x => Assert.Equal("Points order", x.Group));
        Assert.All(plan.Entries.Where(x => x.Entrant.Points is null), x => Assert.Equal("No points", x.Group));
        Assert.Equal(plan.Entries.Count(x => x.Entrant.Points is null), plan.Entries.Reverse().TakeWhile(x => x.Entrant.Points is null).Count());
        Assert.Equal(plan.Entries, FisStartOrder.FirstRun(plan.CompetitionId, plan.Competition, plan.Gender,
            plan.Entries.Select(x => x.Entrant).Reverse().ToArray(), plan.PointsList, plan.Options, plan.Seed).Entries);
    }
}
