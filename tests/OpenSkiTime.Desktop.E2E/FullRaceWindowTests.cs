using System.Globalization;
using System.Text;
using OpenSkiTime.Application;
using OpenSkiTime.Devices;
using OpenSkiTime.Persistence;
using OpenSkiTime.Tests.FullRace;
using OpenSkiTime.Timing;
using Xunit;

namespace OpenSkiTime.Desktop.E2E;

public sealed class FullRaceWindowTests
{
    internal static string Output(string name)
    {
        var configured = Environment.GetEnvironmentVariable("OPENSKITIME_DESKTOP_E2E_OUTPUT");
        return Path.Combine(string.IsNullOrWhiteSpace(configured) ? Path.Combine(DesktopApp.RepositoryRoot(), "artifacts", "ux-e2e", "e2e") : configured, name);
    }

    // The hand-timed finish and the photocell double impulse of the synthetic plan are exercised at the application
    // boundary (FullRaceScenarioTests); on the real window every other impulse is sent through the simulator buttons.
    internal static IReadOnlyDictionary<string, SyntheticRun> WindowPlan(IReadOnlyDictionary<string, SyntheticRun> plan)
        => plan.ToDictionary(x => x.Key, x => x.Value with { MissingFinish = false, DuplicateFinish = false });

    // The complete two-run slalom operated through the real OpenSkiTime window: series, competition, paste import of
    // 100 athletes, draw, simulator timing of both runs with DNS/DNF/DSQ, Run 2 order, results. The saved series file
    // is then verified against independent expectations.
    [DesktopE2EFact]
    public async Task OperatorRunsTheFullRaceThroughTheRealWindow()
    {
        var root = Path.Combine(Path.GetTempPath(), "openskitime-e2e", Guid.NewGuid().ToString("N"));
        var race = new SyntheticRace();
        var steps = Output("steps");
        var file = Path.Combine(root, "Synthetic Alpine Cup 2026.ost");
        var log = new StringBuilder();
        var plan1 = WindowPlan(race.Run1);
        var plan2 = WindowPlan(race.Run2);
        IReadOnlyList<(int Position, int Bib, string Code)> order1, order2;
        var resultsState = "";
        using (var app = DesktopApp.Launch(root, local => File.WriteAllBytes(Path.Combine(local, "fis-points-list.zip"), race.PointsListArchive())))
        {
            var office = new RaceOffice(app, race) { FastImpulses = Environment.GetEnvironmentVariable("OPENSKITIME_DESKTOP_E2E_TYPED_IMPULSES") != "1" };
            try
            {
                office.CreateSeries(file);
                app.Screenshot(steps, "01-series-created.png");
                office.CreateCompetition();
                app.Screenshot(steps, "02-competition.png");
                office.ImportCompetitors();
                app.Screenshot(steps, "03-competitors-imported.png");
                office.DrawFirstRun();
                app.Screenshot(steps, "04-run1-draw.png");
                order1 = SeriesFileEvidence.StartOrder(file, 1);
                Assert.Equal(SyntheticRace.AthleteCount, order1.Count);
                office.OpenRun("5  Timing  ▾", 1);
                office.ConnectSimulator();
                app.Screenshot(steps, "05-timing-connected.png");
                office.TimeRun(1, order1, plan1, (done, total) =>
                { if (done == total / 2) { app.Screenshot(steps, "06-run1-live.png"); } });
                app.Screenshot(steps, "07-run1-complete.png");
                var dsq1 = order1.Single(x => x.Code == race.Athletes[SyntheticRace.Run1Dsq].Code).Bib;
                office.Disqualify(dsq1, race.Run1[race.Athletes[SyntheticRace.Run1Dsq].Code]);
                app.Screenshot(steps, "08-run1-dsq.png");
                office.PrepareSecondRun();
                app.Screenshot(steps, "09-run2-start-list.png");
                order2 = SeriesFileEvidence.StartOrder(file, 2);
                office.OpenRun("5  Timing  ▾", 2);
                office.EnsureConnected();
                office.TimeRun(2, order2, plan2, (done, total) =>
                { if (done == total / 2) { app.Screenshot(steps, "10-run2-live.png"); } });
                var dsq2 = order2.Single(x => x.Code == race.Athletes[SyntheticRace.Run2Dsq].Code).Bib;
                office.Disqualify(dsq2, race.Run2[race.Athletes[SyntheticRace.Run2Dsq].Code]);
                app.Screenshot(steps, "11-run2-complete.png");
                office.OpenResults();
                app.Screenshot(steps, "12-results.png");
                resultsState = app.Window.FindAllDescendants(app.By.ByControlType(FlaUI.Core.Definitions.ControlType.Text))
                    .Select(x => x.Name).FirstOrDefault(x => x.Contains(" classified · ", StringComparison.Ordinal)) ?? "";
                Assert.StartsWith("91 classified · 9 not classified.", resultsState, StringComparison.Ordinal);
            }
            catch
            {
                File.WriteAllText(Output("uia-failure.txt"), app.Tree());
                app.Screenshot(steps, "failure.png");
                throw;
            }
        }

        // Independent verification of what the real window saved.
        var expected1 = ExpectedResults.Run(1, order1, plan1);
        Assert.Equal(ExpectedResults.SecondRunOrder(expected1, 30), order2.Select(x => x.Bib));
        var expected2 = ExpectedResults.Run(2, order2, plan2);
        var expectedFinal = ExpectedResults.Final(expected1, expected2);
        await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore(), new AlgeDecoderFactory());
        var series = await workspace.OpenAsync(file);
        var competition = Assert.Single(series.Competitions);
        var lists = (await workspace.ReadStartListsAsync(competition.Id)).Revisions;
        var list1 = lists.Where(x => x.Plan.RunNumber == 1).MaxBy(x => x.Revision)!;
        var list2 = lists.Where(x => x.Plan.RunNumber == 2).MaxBy(x => x.Revision)!;
        await workspace.Timing!.SelectRunAsync(list1.Id);
        var timing1 = workspace.Timing.Snapshot!;
        await workspace.Timing.SelectRunAsync(list2.Id);
        var timing2 = workspace.Timing.Snapshot!;
        var result = FisRaceResults.Assemble(list1, timing1, list2, timing2);
        var mismatches = new List<string>();
        foreach (var (expected, actual) in new[] { (expected1, timing1), (expected2, timing2) })
        {
            foreach (var row in expected)
            {
                var r = actual.Results.Single(x => x.Bib == row.Bib);
                var split = r.Splits.Count > 0 ? r.Splits[0].Hundredths : null;
                if (r.Status.ToString() != row.Outcome.ToString() || r.Hundredths != row.Hundredths || (row.Outcome == RunOutcome.Finished && r.Rank != row.Rank)
                    || (row.Outcome == RunOutcome.Finished && split != row.IntermediateHundredths))
                { mismatches.Add($"Bib {row.Bib}: expected {row.Outcome} {row.Hundredths} split {row.IntermediateHundredths} rank {row.Rank}, actual {r.Status} {r.Hundredths} split {split} rank {r.Rank}"); }
            }
        }
        foreach (var row in expectedFinal)
        {
            var r = result.Rows.Single(x => x.Entry.Bib == row.Bib);
            if (r.Status.ToString() != row.Status || r.TotalHundredths != row.Total || r.Rank != row.Rank)
            { mismatches.Add($"Final bib {row.Bib}: expected {row.Status} {row.Total} rank {row.Rank}, actual {r.Status} {r.TotalHundredths} rank {r.Rank}"); }
        }
        log.AppendLine(CultureInfo.InvariantCulture, $"Real-window race {DateTimeOffset.UtcNow:O}: input mode {(DesktopApp.PhysicalInput ? "mouse/keyboard" : "UI Automation patterns")}");
        log.AppendLine(CultureInfo.InvariantCulture, $"Run 1: {timing1.Results.Count} on list, {timing1.Results.Count(x => x.Status == TimingStatus.Finished)} finished, {timing1.Results.Count(x => x.Status == TimingStatus.DNS)} DNS, {timing1.Results.Count(x => x.Status == TimingStatus.DNF)} DNF, {timing1.Results.Count(x => x.Status == TimingStatus.DSQ)} DSQ, {(await workspace.ReadTimingAsync(list1.Id)).Packets.Count} raw packets");
        log.AppendLine(CultureInfo.InvariantCulture, $"Run 2: {timing2.Results.Count} on list, {timing2.Results.Count(x => x.Status == TimingStatus.Finished)} finished, {timing2.Results.Count(x => x.Status == TimingStatus.DNF)} DNF, {timing2.Results.Count(x => x.Status == TimingStatus.DSQ)} DSQ, {(await workspace.ReadTimingAsync(list2.Id)).Packets.Count} raw packets");
        log.AppendLine(CultureInfo.InvariantCulture, $"Results view: {resultsState}");
        foreach (var (name, snapshot) in new[] { ("Run 1", timing1), ("Run 2", timing2) })
        {
            foreach (var open in snapshot.Observations.Where(x => x.State is "Unassigned" or "Review"))
            {
                log.AppendLine(CultureInfo.InvariantCulture,
                    $"{name} unresolved impulse: channel {open.Observation.Channel} at {new TimeOnly(open.Observation.DeviceTicks.GetValueOrDefault() % TimeSpan.TicksPerDay):HH:mm:ss.ffff} state {open.State}");
            }
        }
        log.AppendLine(CultureInfo.InvariantCulture, $"Final: {result.Rows.Count(x => x.Rank is not null)} classified; mismatches: {mismatches.Count}");
        foreach (var m in mismatches) { log.AppendLine(m); }
        await File.WriteAllTextAsync(Output("real-window-race.log"), log.ToString());
        await workspace.CloseAsync();
        Assert.Empty(mismatches);
        Assert.Equal(91, result.Rows.Count(x => x.Rank is not null));
    }
}
