using OpenSkiTime.Application;
using OpenSkiTime.Devices;
using OpenSkiTime.Domain;
using OpenSkiTime.Persistence;
using OpenSkiTime.Reporting;
using OpenSkiTime.Timing;
using UglyToad.PdfPig;
using Xunit;

namespace OpenSkiTime.Tests;

// A Club race has no penalty or official FIS results: its results come out per class.
public sealed class ClassResultsTests
{
    private static readonly DateOnly s_date = new(2026, 11, 7);
    private static readonly DateTimeOffset s_at = new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ClubRaceResultsAreRankedPerClassAndWrittenToPdf()
    {
        var root = Path.Combine(Path.GetTempPath(), "openskitime-class-results", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var race = new CompetitionValues("Class race", "CR", s_date, Discipline.GiantSlalom, RaceType.Club, 2, 0);
            await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore(), new AlgeDecoderFactory());
            var series = await workspace.CreateAsync(Path.Combine(root, "results.ost"), new("Synthetic", "Test", "Club", s_date, s_date, "FIN", "2026/27"));
            series = await workspace.SaveCompetitionAsync(null, race, series.Revision);
            var competition = series.Competitions[0];
            var revision = (await workspace.SaveCategoryRuleAsync(null, new("N10", 2016, 2016, Gender.Female, 0), series.Revision)).Revision;
            revision = (await workspace.SaveCategoryRuleAsync(null, new("M10", 2016, 2016, Gender.Male, 1), revision)).Revision;
            var entrants = new List<DrawEntrant>();
            for (var i = 0; i < 6; i++)
            {
                var athlete = new CompetitorValues("RESULT" + i, "Racer", 2016, null, "FIN", "Club " + i, i < 3 ? Gender.Female : Gender.Male);
                var saved = await workspace.SaveDeskRowAsync(null, athlete, competition.Id, true, null, revision);
                revision = saved.Revision; entrants.Add(new(saved.Value.Id, athlete, null, i < 3 ? "N10" : "M10"));
            }
            var blocks = new StartOrderSetup([new("N10", ["N10"], new(), new(), "a"), new("M10", ["M10"], new(), new(), "b")]);
            var plan = FlexStartOrder.FirstRun(competition.Id, race, null, entrants, PointsListSource.None, new(), blocks);
            var desk = await workspace.SaveStartListAsync(new(plan, revision, "Test", "Draw", s_at));
            var first = desk.Revisions.Single();
            desk = await workspace.MarkRunStartedAsync(first.Id, desk.SeriesRevision, "Test", s_at);
            await TimeAsync(workspace, first, bib => 5000 + bib * 10);
            var version = TimingReplay.InputVersion(await workspace.ReadTimingAsync(first.Id));
            var secondPlan = FlexStartOrder.LaterRun(desk.Revisions.Single(), [], blocks);
            desk = await workspace.SaveStartListAsync(new(secondPlan, desk.SeriesRevision, "Test", "Run 2", s_at, version));
            var second = desk.Revisions.Single(x => x.Plan.RunNumber == 2);
            // Run 2 is faster for the higher bibs, but not enough to change the order. In N10 bib 2 does not finish
            // and bib 3 is disqualified at gate 4; with both runs finished the race is complete.
            await TimeAsync(workspace, second, bib => bib is 2 or 3 ? null : 6000 - bib * 5);
            foreach (var (bib, status) in new[] { (2, TimingStatus.DNF), (3, TimingStatus.DSQ) })
            {
                var id = second.Plan.Entries.Single(x => x.Bib == bib).Entrant.CompetitorId;
                await workspace.Timing!.CorrectAsync(new(DecisionKind.Status, CompetitorId: id, Status: status,
                    Disqualification: status == TimingStatus.DSQ ? new DisqualificationDetails(4, "Missed gate", "Judge") : null), "Test", "Classified");
            }

            var source = await new PdfReportSourceBuilder(new AlgeDecoderFactory()).BuildAsync(workspace, competition.Id);
            var catalog = ReportCatalog.GetAvailableReports(source);
            Assert.DoesNotContain(catalog, x => x.Type is PdfReportType.OfficialResults or PdfReportType.PenaltyCalculation);
            var descriptor = catalog.Single(x => x.Type == PdfReportType.ClassResults);
            Assert.True(descriptor.CanGenerate, descriptor.UnavailableReason);
            Assert.EndsWith("_Results.pdf", descriptor.FileName, StringComparison.Ordinal);

            var groups = ClassResultsBuilder.Build(source);
            Assert.Equal(["N10", "M10"], groups.Select(x => x.Class));
            var women = groups[0];
            Assert.Equal([1], women.Classified.Select(x => x.Bib));
            Assert.Equal<int?>([1], women.Classified.Select(x => x.Rank));
            Assert.Equal<long?>([5010L + 5995], women.Classified.Select(x => x.TotalHundredths));
            var others = women.Others.ToArray();
            Assert.Equal([(TimingStatus.DNF, 2), (TimingStatus.DSQ, 2)], others.Select(x => (x.Key.Outcome!.Value, x.Key.Run)));
            Assert.Equal([2], others[0].Select(x => x.Bib));
            Assert.Equal("Gate 4", others[1].Single().Detail);
            var men = groups[1];
            Assert.Equal([4, 5, 6], men.Classified.Select(x => x.Bib).Order());
            Assert.Equal<int?>([1, 2, 3], men.Classified.Select(x => x.Rank));
            Assert.Equal(["+0:00.00", "+0:00.05", "+0:00.10"], men.Classified.Select(x => x.Difference));
            Assert.Empty(men.Others);
            Assert.True(ClassResultsBuilder.IsComplete(source));

            // A Club race also has its timing report as a PDF (it is not sent to FIS).
            var timingReport = catalog.Single(x => x.Type == PdfReportType.TimingReport);
            Assert.True(timingReport.CanGenerate, timingReport.UnavailableReason);
            Assert.True((await new ReportGenerationService(new QuestPdfRenderer()).GenerateAsync(source, timingReport, _ => Task.CompletedTask, s_at)).Success);
            Assert.True(File.Exists(Path.Combine(root, timingReport.RelativePath)));
            Assert.True((await new ReportGenerationService(new QuestPdfRenderer()).GenerateAsync(source, descriptor, _ => Task.CompletedTask, s_at)).Success);
            using var pdf = PdfDocument.Open(Path.Combine(root, descriptor.RelativePath));
            var text = string.Join(" ", pdf.GetPages().SelectMany(x => x.GetWords()).Select(x => x.Text));
            Assert.DoesNotContain("PRELIMINARY", text, StringComparison.Ordinal);
            Assert.Contains("N10", text, StringComparison.Ordinal);
            Assert.Contains("M10", text, StringComparison.Ordinal);
            Assert.Contains("RESULT0", text, StringComparison.Ordinal);
            Assert.Contains("Did Not Finish - Run 2 (1)", text, StringComparison.Ordinal);
            Assert.Contains("Disqualified - Run 2 (1)", text, StringComparison.Ordinal);
            Assert.Contains("Number of competitors 6", text, StringComparison.Ordinal);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task LaterRunOrderedWithoutResultsIsNotInvalidatedByTheTimingOfTheRunBefore()
    {
        var root = Path.Combine(Path.GetTempPath(), "openskitime-class-results", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var race = new CompetitionValues("Class race", "CR", s_date, Discipline.GiantSlalom, RaceType.Club, 2, 0);
            await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore(), new AlgeDecoderFactory());
            var series = await workspace.CreateAsync(Path.Combine(root, "independent.ost"), new("Synthetic", "Test", "Club", s_date, s_date, "FIN", "2026/27"));
            series = await workspace.SaveCompetitionAsync(null, race, series.Revision);
            var competition = series.Competitions[0];
            var revision = series.Revision;
            var entrants = new List<DrawEntrant>();
            for (var i = 0; i < 3; i++)
            {
                var athlete = new CompetitorValues("INDEP" + i, "Racer", 2016, null, "FIN", "Club", Gender.Female);
                var saved = await workspace.SaveDeskRowAsync(null, athlete, competition.Id, true, null, revision);
                revision = saved.Revision; entrants.Add(new(saved.Value.Id, athlete, null, "N10"));
            }
            var blocks = new StartOrderSetup([new("N10", ["N10"], new(), new(), "a")]);
            var plan = FlexStartOrder.FirstRun(competition.Id, race, null, entrants, PointsListSource.None, new(), blocks);
            var desk = await workspace.SaveStartListAsync(new(plan, revision, "Test", "Draw", s_at));
            var first = desk.Revisions.Single();
            desk = await workspace.MarkRunStartedAsync(first.Id, desk.SeriesRevision, "Test", s_at);
            await TimeAsync(workspace, first, bib => bib == 1 ? 5000 : null);
            // Run 2 is ordered by the start order only: it is saved without a timing version although Run 1 is unfinished.
            var secondPlan = FlexStartOrder.LaterRun(desk.Revisions.Single(), [], blocks);
            desk = await workspace.SaveStartListAsync(new(secondPlan, desk.SeriesRevision, "Test", "Run 2", s_at));
            var second = desk.Revisions.Single(x => x.Plan.RunNumber == 2);
            // Run 1 timing changes afterwards; starting Run 2 is still allowed.
            await workspace.Timing!.SelectRunAsync(first.Id);
            await workspace.Timing.StartAsync(new SimulatorTimingSource(), new("Test", "Synthetic", s_date, Simulation: true), "Test");
            await workspace.Timing.StopAsync();
            await workspace.Timing.CorrectAsync(new(DecisionKind.Time, CompetitorId: first.Plan.Entries[1].Entrant.CompetitorId, Hundredths: 5100), "Test", "Late time");
            desk = await workspace.MarkRunStartedAsync(second.Id, desk.SeriesRevision, "Test", s_at);
            Assert.NotNull(desk.Revisions.Single(x => x.Plan.RunNumber == 2).StartedAt);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static async Task TimeAsync(SeriesWorkspace workspace, StartListRevision list, Func<int, long?> time)
    {
        await workspace.Timing!.SelectRunAsync(list.Id);
        await workspace.Timing.StartAsync(new SimulatorTimingSource(), new("Test", "Synthetic", s_date, Simulation: true), "Test");
        await workspace.Timing.StopAsync();
        foreach (var entry in list.Plan.Entries)
        {
            if (time(entry.Bib) is not { } hundredths) { continue; }
            await workspace.Timing.CorrectAsync(new(DecisionKind.Time, CompetitorId: entry.Entrant.CompetitorId, Hundredths: hundredths), "Test", "Synthetic verified time");
        }
    }
}
