using System.Globalization;
using System.Text;
using System.Xml.Linq;
using OpenSkiTime.Application;
using OpenSkiTime.Devices;
using OpenSkiTime.Domain;
using OpenSkiTime.Persistence;
using OpenSkiTime.Timing;
using Xunit;

namespace OpenSkiTime.Tests;

// Official output must be exact and reachable: race points round exact midpoints half up, audited Run 1 corrections
// after Run 2 still produce results, and text that XML cannot carry is a validation message, never a crash.
public sealed class ResultIntegrityTests
{
    private static readonly CompetitionValues s_race = new("Synthetic race", "SL1", new(2026, 12, 12), Discipline.Slalom,
        RaceType.Fis, 2, 0, "1234", CourseName: "Slope", StartAltitudeMeters: 1100, FinishAltitudeMeters: 900,
        HomologationNumber: "12345/01/26", Calendar: new(2027, "Levi", "FIN", "FIS", "M"));
    private static readonly SeriesValues s_series = new("Series", "Levi", "Club", s_race.Date, s_race.Date, "FIN", "2026/27");
    private static readonly FisXmlDetails s_details = new("FIS", new("T", "Delegate", "FIN"), new("C", "Chief", "FIN"),
        [new(45, 43, "10:00", new("S", "Setter", "FIN"))]);

    private static StartListEntry Entry(int bib, CompetitorValues? athlete = null) => new(bib, bib, new DrawEntrant(new Guid(bib, 3, 3, new byte[8]),
        athlete ?? new("RACER" + bib.ToString(CultureInfo.InvariantCulture), "Test", 2000, (800000 + bib).ToString(CultureInfo.InvariantCulture), "FIN", "Club", Gender.Male),
        10m * bib), "Points order");

    private static PenaltyCompetitor[] Field(params long[] totals) => totals.Select((total, i) => new PenaltyCompetitor(Entry(i + 1),
        true, TimingStatus.Finished, total, totals.Count(x => x < total) + 1)).ToArray();

    [Theory]
    [InlineData(8176, 8239, "5.63")] // (63 / 8176) × 730 = 5.625 exactly
    [InlineData(2336, 2354, "5.63")] // (18 / 2336) × 730 = 5.625 exactly
    public void RacePointsRoundAnExactMidpointHalfUp(long winner, long other, string expected)
    {
        var penalty = FisPenalty.Calculate(Discipline.Slalom, Field(winner, other, other + 100, other + 200, other + 300, other + 400), new(0, 999, 0));
        Assert.Equal(expected, penalty.RacePoints[2].ToString("0.00", CultureInfo.InvariantCulture));
    }

    [Fact]
    public void RacePointsEqualExactHalfUpRoundingForEveryTimeInARange()
    {
        foreach (var discipline in new[] { Discipline.Slalom, Discipline.GiantSlalom, Discipline.SuperG, Discipline.Downhill })
        {
            var f = FisPenalty.FValue(discipline);
            for (var winner = 4000L; winner < 4040; winner++)
            {
                var field = Field(Enumerable.Range(0, 601).Select(i => winner + i).ToArray());
                var points = FisPenalty.Calculate(discipline, field, new(0, 999, 0)).RacePoints;
                foreach (var row in field)
                {
                    // Integer half up of 100 × (Tx - To) × F / To, without any intermediate rounding.
                    var numerator = (row.TotalHundredths!.Value - winner) * f * 100;
                    var expected = (2 * numerator + winner) / (2 * winner) / 100m;
                    Assert.Equal(expected, points[row.Bib]);
                }
            }
        }
    }

    [Fact]
    public void AuditedRun1TimeCorrectionAfterRun2KeepsTheOrderAndIsReported()
    {
        var (first, firstTiming) = RunOne(new long[] { 5000, 5100, 5200, 5300, 5400, 5500 });
        var second = first with { Id = Guid.NewGuid(), Plan = FisStartOrder.SecondRun(first, firstTiming.ToRunFinishes()),
            SourceTimingVersion = "results/v2:audit=3" };
        var secondTiming = new TimingSnapshot(second.Id, 0, second.Plan.Entries.Select(entry =>
            new TimingResult(entry, TimingStatus.Finished, 6000, null, null, null, "")).ToArray(), [], []);
        var unchanged = FisRaceResults.Assemble(first, firstTiming with { AuditVersion = 3 }, second, secondTiming);
        Assert.Empty(unchanged.Run1TimesChangedAfterRun2Order);

        // A verified backup time replaces bib 3's Run 1 time after Run 2: audited, so the audit grew past the order.
        var corrected = firstTiming with { AuditVersion = 4, Results = firstTiming.Results.Select(x => x.Bib == 3 ? x with { Hundredths = 5050 } : x).ToArray() };
        var race = FisRaceResults.Assemble(first, corrected, second, secondTiming);
        Assert.Equal([3], race.Run1TimesChangedAfterRun2Order);
        var row = race.Rows.Single(x => x.Entry.Bib == 3);
        Assert.Equal((5050L, 11050L, 2), (row.Run1Hundredths!.Value, row.TotalHundredths!.Value, row.Rank!.Value));
        Assert.Equal(second.Plan.Entries, race.SecondList!.Plan.Entries);

        // A changed time without an audited change after the order was saved, or a newly eligible starter, still blocks.
        Assert.Throws<DomainValidationException>(() => FisRaceResults.Assemble(first, corrected with { AuditVersion = 3 }, second, secondTiming));
        Assert.Throws<DomainValidationException>(() => FisRaceResults.Assemble(first, corrected, second with { SourceTimingVersion = null }, secondTiming));
    }

    [Fact]
    public async Task Run1CorrectionAfterRun2IsTimedStillProducesFinalResults()
    {
        var folder = Path.Combine(Path.GetTempPath(), "openskitime-result-integrity", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var date = new DateOnly(2026, 12, 12);
            var at = new DateTimeOffset(2026, 12, 12, 12, 0, 0, TimeSpan.FromHours(2));
            var rules = new FisPenaltyListRules(2027, [new("FIS", 0, 0, 999)], [new("SL", Gender.Male, 730, 165, 0, [0, 0, 0, 0, 0])]);
            await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore(), new AlgeDecoderFactory());
            var series = await workspace.CreateAsync(Path.Combine(folder, "race.ost"), s_series, [s_race]);
            var competition = series.Competitions[0];
            var entrants = new List<DrawEntrant>();
            for (var i = 1; i <= 6; i++)
            {
                var athlete = new CompetitorValues("RACER" + i, "Synthetic", 2000, (900000 + i).ToString(CultureInfo.InvariantCulture), "FIN", "Club", Gender.Male);
                var saved = await workspace.SaveDeskRowAsync(null, athlete, competition.Id, true, null, (await workspace.ReadAsync()).Revision);
                entrants.Add(new(saved.Value.Id, athlete, i * 10));
            }
            var plan = FisStartOrder.FirstRun(competition.Id, s_race, Gender.Male, entrants, new("1327", date, date, rules), new(), "seed");
            var first = (await workspace.SaveStartListAsync(new(plan, (await workspace.ReadAsync()).Revision, "Operator", "Draw", at))).Revisions[0];
            first = (await workspace.MarkRunStartedAsync(first.Id, (await workspace.ReadAsync()).Revision, "Operator", at)).Revisions[0];
            await CompleteRunAsync(workspace, first, 5000, date);
            var firstReplay = await workspace.ReadTimingAsync(first.Id);
            var secondPlan = FisStartOrder.SecondRun(first, TimingReplay.Restore(firstReplay, new AlgeDecoderFactory()).ToRunFinishes());
            var second = (await workspace.SaveStartListAsync(new(secondPlan, (await workspace.ReadAsync()).Revision, "Operator", "Run 2", at,
                TimingReplay.InputVersion(firstReplay)))).Revisions.Single(x => x.Plan.RunNumber == 2);
            await CompleteRunAsync(workspace, second, 6000, date);

            var timing = workspace.Timing!;
            await timing.SelectRunAsync(first.Id);
            var correctedId = first.Plan.Entries.Single(x => x.Bib == 3).Entrant.CompetitorId;
            await timing.CorrectAsync(new(DecisionKind.Time, CompetitorId: correctedId, Hundredths: 5333), "Operator", "Verified backup time");

            var run1 = await workspace.ReadTimingAsync(first.Id); var run2 = await workspace.ReadTimingAsync(second.Id);
            var race = FisRaceResults.Assemble(run1.List, TimingReplay.Restore(run1, new AlgeDecoderFactory()), run2.List, TimingReplay.Restore(run2, new AlgeDecoderFactory()));
            Assert.Equal(5333, race.Rows.Single(x => x.Entry.Entrant.CompetitorId == correctedId).Run1Hundredths);
            Assert.Equal([3], race.Run1TimesChangedAfterRun2Order);
            // The Run 2 order stays the one that was raced.
            await Assert.ThrowsAsync<DomainValidationException>(async () => await workspace.SaveStartListAsync(new(
                FisStartOrder.SecondRun(run1.List, TimingReplay.Restore(run1, new AlgeDecoderFactory()).ToRunFinishes()),
                (await workspace.ReadAsync()).Revision, "Operator", "Redraw", at, TimingReplay.InputVersion(run1))));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Theory]
    [InlineData("Club \"A\"\tNorth\r\nLane", true)] // TSV round-trips quoted tabs and line breaks
    [InlineData("Äkäslompolo 🎿", true)]
    [InlineData("Word\u000Bbreak", false)]
    [InlineData("nul\0", false)]
    [InlineData("form\u000Cfeed", false)]
    [InlineData("non￿char", false)]
    public void PortableTextAllowsWhatEveryExportCanCarry(string text, bool portable)
    {
        Assert.Equal(portable, TextRules.IsPortable(text));
        // Built here: test-case serialization would replace an unpaired surrogate before it reached the method.
        Assert.False(TextRules.IsPortable("half" + (char)0xD83C));
        Assert.False(TextRules.IsPortable((char)0xDF7F + "low"));
    }

    [Fact]
    public void ControlCharactersAreValidationErrorsAtInputAndAtTheXmlBoundary()
    {
        Assert.Throws<DomainValidationException>(() => new DisqualificationDetails(12, "ICR 629.3\u000Bstraddled", "Judge").Validate());
        Assert.Throws<DomainValidationException>(() => new DisqualificationDetails(12, "ICR 629.3", "Judge\u0001").Validate());
        Assert.Throws<DomainValidationException>(() => new CompetitorValues("TEST\u000BNAME", "Racer", 2000, "123456", "FIN", "Club", Gender.Male).Validated(2026));
        Assert.Throws<DomainValidationException>(() => new CompetitorValues("TEST", "Racer", 2000, "123456", "FIN", "Club\u000CB", Gender.Male).Validated(2026));
        var information = RaceInformation.Empty(s_race);
        Assert.Throws<DomainValidationException>(() => (information with { Runs = [information.Runs[0] with { Course = "Slope\u000B2" }, information.Runs[1]] }).Validate(2));

        // Data saved before validation existed reaches the writer: a clear message, never an unhandled ArgumentException.
        var oneRun = s_race with { RunCount = 1 };
        var entries = Enumerable.Range(1, 6).Select(bib => Entry(bib, bib == 6
            ? new("OLD\u000BNAME", "Test", 2000, "800006", "FIN", "Club", Gender.Male) : null)).ToArray();
        var plan = new StartListPlan(Guid.NewGuid(), oneRun, Gender.Male, 1, "test", "seed", new(), new("1327", new(2026, 12, 1), new(2026, 12, 31)), null, [], entries);
        var list = new StartListRevision(Guid.NewGuid(), 1, DateTimeOffset.UnixEpoch, null, "op", "draw", plan);
        var race = FisRaceResults.Assemble(list, new TimingSnapshot(list.Id, 0, entries.Select(e =>
            new TimingResult(e, TimingStatus.Finished, 5000 + e.Bib * 10, null, null, null, "")).ToArray(), [], []));
        var penalty = FisPenalty.Calculate(Discipline.Slalom, race.PenaltyCompetitors, new(0, 999, 0));
        var error = Record.Exception(() => FisResultXml.Create(race, s_series, penalty, s_details));
        Assert.IsType<DomainValidationException>(error);
        var valid = race with { Rows = race.Rows.Where(x => x.Entry.Bib != 6).ToArray() };
        Assert.NotNull(XDocument.Parse(Encoding.UTF8.GetString(FisResultXml.Create(valid, s_series,
            FisPenalty.Calculate(Discipline.Slalom, valid.PenaltyCompetitors, new(0, 999, 0)), s_details))).Root);

        var draft = TimingReportDraftWithComment("Gate\u000Bcheck");
        Assert.IsType<DomainValidationException>(Record.Exception(() => TimingReportXml.Create(draft, "1.0.0")));
        Assert.NotEmpty(TimingReportXml.Create(TimingReportDraftWithComment("Gate check"), "1.0.0"));
    }

    private static (StartListRevision List, TimingSnapshot Timing) RunOne(long[] times)
    {
        var entries = times.Select((_, i) => Entry(i + 1)).ToArray();
        var plan = new StartListPlan(Guid.NewGuid(), s_race, Gender.Male, 1, "test", "seed", new(),
            new("1327", new(2026, 12, 1), new(2026, 12, 31)), null, [], entries);
        var list = new StartListRevision(Guid.NewGuid(), 1, DateTimeOffset.UnixEpoch, null, "op", "draw", plan);
        return (list, new TimingSnapshot(list.Id, 0, entries.Select((e, i) => new TimingResult(e, TimingStatus.Finished, times[i], null, null, null, "")).ToArray(), [], []));
    }

    private static async Task CompleteRunAsync(SeriesWorkspace workspace, StartListRevision list, long baseTime, DateOnly date)
    {
        var timing = workspace.Timing!;
        await timing.SelectRunAsync(list.Id);
        await timing.StartAsync(new SimulatorTimingSource(), new("Synthetic", "Local simulator", date, Simulation: true), "Operator");
        await timing.StopAsync();
        foreach (var entry in list.Plan.Entries)
        { await timing.CorrectAsync(new(DecisionKind.Time, CompetitorId: entry.Entrant.CompetitorId, Hundredths: baseTime + entry.Position * 100), "Operator", "Synthetic time"); }
        Assert.True(timing.Snapshot!.Complete);
    }

    private static TimingReportDraft TimingReportDraftWithComment(string comment)
    {
        var device = new TimingReportDevice("Synthetic", "Test", "SYNTH-1", "TEST.001");
        var start = new TimingReportStamp(TimeSpan.FromHours(12).Ticks + 1_234_567, 7, "synthetic");
        var finish = start with { Ticks = start.Ticks + TimeSpan.TicksPerMinute };
        var bib = new TimingReportBib { Bib = 1, AStart = start, AFinish = finish, BStart = start, BFinish = finish,
            HandStart = start with { Precision = 2, Ticks = TimeSpan.FromHours(12).Ticks + 1_200_000 },
            HandFinish = finish with { Precision = 2, Ticks = TimeSpan.FromHours(12).Ticks + TimeSpan.TicksPerMinute + 1_200_000 }, NetHundredths = 6000 };
        return new() { CompetitionId = Guid.NewGuid(), Header = new(2027, "9991", "FIN", "SL", "FIS", "M", "Synthetic", "Ruka", new(2026, 10, 3)) { TimingLevel = 3 },
            TechnicalDelegate = new("Test", "Delegate", "FIN", Number: "123"),
            Defaults = new() { TimerA = device, TimerB = device, StartDevice = device, FinishCellsA = device, FinishCellsB = device,
                Timekeeper = new("Test", "Keeper", "FIN", "test@example.invalid", "000") },
            Sync = new(TimeSpan.FromHours(11).Ticks, 4), HandSync = new(TimeSpan.FromHours(11).Ticks, 2),
            SyncCheckA = new(TimeSpan.FromHours(11).Ticks + TimeSpan.TicksPerMinute, 4),
            SyncCheckB = new(TimeSpan.FromHours(11).Ticks + TimeSpan.TicksPerMinute, 4),
            Runs = [new() { Run = 1, First = bib, Last = bib, BestBib = 1, BestHundredths = 6000, Comment = comment }], Reviewed = true, CertifyFis = true };
    }
}
