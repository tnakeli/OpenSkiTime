using System.Globalization;
using Avalonia.Headless.XUnit;
using OpenSkiTime.Application;
using OpenSkiTime.Desktop;
using OpenSkiTime.Devices;
using OpenSkiTime.Domain;
using OpenSkiTime.LiveTiming;
using OpenSkiTime.Persistence;
using OpenSkiTime.Timing;
using Xunit;

namespace OpenSkiTime.Tests;

// The live browser view ranks Run 2 by the combined time, like the timing view's Run 2 ranking, not by the Run 2 time.
public sealed class LiveTotalsTests
{
    private static readonly DateTimeOffset s_at = new(2026, 10, 5, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void RunTwoCarriesCombinedTimesRankedExAequoAndOnlyForRacersWhoFinishedEveryRun()
    {
        var runs = LiveSnapshotMapper.WithTotals([
            Run(2, (1, LiveStatus.Finished, 5000), (2, LiveStatus.Finished, 4900), (3, LiveStatus.Finished, 5200),
                (4, LiveStatus.Finished, 4500), (5, LiveStatus.OnCourse, null)),
            Run(1, (1, LiveStatus.Finished, 5000), (2, LiveStatus.Finished, 5100), (3, LiveStatus.Finished, 4900),
                (4, LiveStatus.DSQ, null), (5, LiveStatus.Finished, 5200))]);
        Assert.Equal([1, 2], runs.Select(x => x.Number));
        Assert.All(runs[0].Results, x => { Assert.Null(x.TotalHundredths); Assert.Null(x.TotalRank); Assert.Null(x.TotalDifference); });
        // Bibs 1 and 2 share 1:40.00; bib 3 is third. Bib 4 was disqualified in Run 1 and bib 5 is still on course.
        Assert.Equal(
            [(1, 10000L, 1, 0L), (2, 10000L, 1, 0L), (3, 10100L, 3, 100L), (4, null, null, null), (5, null, null, null)],
            runs[1].Results.OrderBy(x => x.Bib).Select(x => (x.Bib, x.TotalHundredths, x.TotalRank, x.TotalDifference)));
        // The fastest Run 2 time (bib 4) does not lead the combined standings.
        Assert.Equal(1, runs[1].Results.Single(x => x.Bib == 4).Rank);
        Snapshot(runs).Validate();

        // A Run 1 correction during Run 2 changes the totals on the next projection.
        var corrected = runs[0] with { Results = runs[0].Results.Select(x => x.Bib == 3 ? x with { Hundredths = 4700 } : x).ToArray() };
        var again = LiveSnapshotMapper.WithTotals([corrected, runs[1]]);
        Assert.Equal([(3, 9900L, 1, 0L), (1, 10000L, 2, 100L), (2, 10000L, 2, 100L)],
            again[1].Results.Where(x => x.TotalRank is not null).OrderBy(x => x.TotalRank).ThenBy(x => x.Bib)
                .Select(x => (x.Bib, x.TotalHundredths!.Value, x.TotalRank!.Value, x.TotalDifference!.Value)));
    }

    // What the desktop publishes while Run 2 is timed: every Run 1 result next to Run 2, with combined times for finishers.
    [AvaloniaFact]
    public async Task RunTwoLiveStateCarriesEveryRunOneResultAndTheCombinedTimes()
    {
        var folder = Path.Combine(Path.GetTempPath(), "openskitime-live-totals-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var date = new DateOnly(2026, 10, 5);
            var path = Path.Combine(folder, "race.ost");
            var values = new CompetitionValues("Synthetic slalom", "SL1", date, Discipline.Slalom, RaceType.Club, 2, 0);
            CompetitionDetails competition; StartListRevision first;
            await using (var setup = new SeriesWorkspace(new SqliteSeriesFileStore(), new AlgeDecoderFactory()))
            {
                var series = await setup.CreateAsync(path, new("Synthetic", "Test", "Club", date, date, "FIN", "2026/27"));
                series = await setup.SaveCompetitionAsync(null, values, series.Revision);
                competition = series.Competitions[0];
                var revision = series.Revision; var entrants = new List<DrawEntrant>();
                for (var i = 1; i <= 3; i++)
                {
                    var athlete = new CompetitorValues("TOTAL" + i, "Racer", 2010, (960000 + i).ToString(CultureInfo.InvariantCulture), "FIN", "Club", Gender.Female);
                    var saved = await setup.SaveDeskRowAsync(null, athlete, competition.Id, true, null, revision);
                    revision = saved.Revision; entrants.Add(new(saved.Value.Id, athlete, i * 10m));
                }
                var plan = FisStartOrder.FirstRun(competition.Id, values, Gender.Female, entrants, new("1327", date, date), new(), "live-totals-seed");
                first = (await setup.SaveStartListAsync(new(plan, revision, "Test", "Draw", DateTimeOffset.UnixEpoch))).Revisions[0];
                first = (await setup.MarkRunStartedAsync(first.Id, (await setup.ReadAsync()).Revision, "Test", DateTimeOffset.UnixEpoch)).Revisions[0];
                var timing = setup.Timing!;
                await timing.SelectRunAsync(first.Id);
                await timing.StartAsync(new SimulatorTimingSource(), new("Test", "Synthetic", date, Simulation: true), "Test");
                await timing.StopAsync();
                foreach (var entry in first.Plan.Entries)
                { await timing.CorrectAsync(new(DecisionKind.Time, CompetitorId: entry.Entrant.CompetitorId, Hundredths: 5000 + entry.Bib * 10), "Test", "Synthetic Run 1 time"); }
                await setup.SaveStartListAsync(new(FisStartOrder.SecondRun(first, timing.Snapshot!.ToRunFinishes()), (await setup.ReadAsync()).Revision,
                    "Test", "Run 2", DateTimeOffset.UnixEpoch, TimingReplay.InputVersion(await setup.ReadTimingAsync(first.Id))));
            }
            await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore(), new AlgeDecoderFactory());
            using var vm = new MainViewModel(workspace, new Dialogs(path),
                fisStore: new FisLocalStore(folder), recentSeriesStore: new RecentSeriesStore(folder));
            await vm.OpenSeriesCommand.ExecuteAsync(null);
            await vm.OpenTimingRunCommand.ExecuteAsync(new DrawDestination(vm.Competitions.Single(x => x.Id == competition.Id), 2));
            Assert.False(vm.IsError, vm.StatusMessage);
            var runTwo = workspace.Timing!;
            await runTwo.StartAsync(new SimulatorTimingSource(), new("Test", "Synthetic", date, Simulation: true), "Test");
            await runTwo.StopAsync();
            var finisher = first.Plan.Entries.Single(x => x.Bib == runTwo.Snapshot!.StartOrder[0]);
            await runTwo.CorrectAsync(new(DecisionKind.Time, CompetitorId: finisher.Entrant.CompetitorId, Hundredths: 4900), "Test", "Synthetic Run 2 time");
            vm.LiveTimeZone = "UTC";
            await vm.BuildLiveSnapshotAsync();
            var state = vm.PublishedLiveSnapshot!;
            state.Validate();
            Assert.Equal(2, state.CurrentRun);
            Assert.Equal([1, 2], state.Runs.Select(x => x.Number));
            // Every Run 1 time stays in the published state for the Run 2 view.
            Assert.Equal(first.Plan.Entries.Select(x => (x.Bib, (long?)(5000 + x.Bib * 10))).OrderBy(x => x.Bib),
                state.Runs[0].Results.Where(x => x.Status == LiveStatus.Finished).Select(x => (x.Bib, x.Hundredths)).OrderBy(x => x.Bib));
            var total = state.Runs[1].Results.Single(x => x.Bib == finisher.Bib);
            Assert.Equal((4900L + 5000 + finisher.Bib * 10, 1, 0L), (total.TotalHundredths!.Value, total.TotalRank!.Value, total.TotalDifference!.Value));
            Assert.All(state.Runs[1].Results.Where(x => x.Bib != finisher.Bib), x => Assert.Null(x.TotalHundredths));
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void ContractRejectsInconsistentTotals()
    {
        var finished = new LiveResult(1, LiveStatus.Finished, 5000, 1, 0, s_at);
        LiveSnapshot.ValidateResult(finished with { TotalHundredths = 10000, TotalRank = 1, TotalDifference = 0 }, 0);
        foreach (var invalid in new[]
        {
            finished with { TotalHundredths = 10000 },
            finished with { TotalHundredths = 10000, TotalRank = 0, TotalDifference = 0 },
            finished with { TotalHundredths = 10000, TotalRank = 1, TotalDifference = -1 },
            finished with { TotalHundredths = 4000, TotalRank = 1, TotalDifference = 0 },
            finished with { Status = LiveStatus.DNF, Hundredths = null, Rank = null, Difference = null, TotalHundredths = 10000, TotalRank = 1, TotalDifference = 0 },
        })
        { Assert.Throws<LiveValidationException>(() => LiveSnapshot.ValidateResult(invalid, 0)); }
    }

    private sealed class Dialogs(string path) : IFileDialogs
    {
        public Task<string?> ChooseNewAsync(string suggestedName) => Task.FromResult<string?>(path);
        public Task<string?> ChooseOpenAsync() => Task.FromResult<string?>(path);
        public Task<string?> ChooseBackupAsync(string suggestedName) => Task.FromResult<string?>(path + ".backup");
        public Task<bool> ConfirmRemoveAsync(string competitionName) => Task.FromResult(false);
        public Task<bool> ConfirmDiscardChangesAsync(int changeCount) => Task.FromResult(false);
        public Task<string?> ChooseStartListExportAsync(string suggestedName, bool print) => Task.FromResult<string?>(null);
        public Task<string?> ChooseResultXmlExportAsync(string suggestedName) => Task.FromResult<string?>(null);
    }
    private static LiveRun Run(int number, params (int Bib, LiveStatus Status, long? Time)[] rows)
    {
        var best = rows.Where(x => x.Status == LiveStatus.Finished).Select(x => x.Time).Min();
        return new(number, s_at, rows.Select(x => x.Bib).ToArray(), rows.Select(x => new LiveResult(x.Bib, x.Status, x.Time,
            x.Status == LiveStatus.Finished ? rows.Count(y => y.Status == LiveStatus.Finished && y.Time < x.Time) + 1 : null,
            x.Status == LiveStatus.Finished ? x.Time - best : null, s_at)).ToArray());
    }
    private static LiveSnapshot Snapshot(LiveRun[] runs) => new(1, new("Synthetic", "Test slope", "GS", new(2026, 10, 5), false, "", "M", "", 0),
        Enumerable.Range(1, 5).Select(i => new LiveCompetitor(i, "TOTAL" + i, "Synthetic", "FIN", "Club", "")).ToArray(), 2, runs, s_at);
}
