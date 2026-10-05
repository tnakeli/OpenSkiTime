using System.Globalization;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OpenSkiTime.Application;
using OpenSkiTime.Desktop;
using OpenSkiTime.Devices;
using OpenSkiTime.Domain;
using OpenSkiTime.Persistence;
using OpenSkiTime.Timing;
using Xunit;

namespace OpenSkiTime.Tests;

// Operator views must show the same sporting semantics as the official result: equal times are ex aequo and the higher
// start number is listed first (ICR 617.3.3); equal draw points are visible as complete groups (ICR 621.3).
public sealed class TieDisplayTests
{
    private static readonly DateOnly s_date = new(2026, 9, 28);
    // Bib → Run 1 time. Bibs 1, 3, 7 share the winning time; 2, 4, 5 share fourth place; 6 is seventh.
    private static readonly Dictionary<int, long> s_times = new() { [1] = 5000, [2] = 5100, [3] = 5000, [4] = 5100, [5] = 5100, [6] = 5300, [7] = 5000 };
    private static readonly (string Rank, int Bib)[] s_officialOrder = [("1", 7), ("1", 3), ("1", 1), ("4", 5), ("4", 4), ("4", 2), ("7", 6)];

    [AvaloniaFact]
    public async Task ResultsAndRankingListEqualTimesExAequoWithHigherBibFirst()
    {
        var folder = Path.Combine(Path.GetTempPath(), "openskitime-ties-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, "race.ost");
            var race = new CompetitionValues("Synthetic race", "SG1 W", s_date, Discipline.SuperG, RaceType.Fis, 1, 0, "9991",
                Calendar: new(2027, "Test", "FIN", "TEST", "W", new("TEST", "Delegate", "FIN", "1047")));
            var rules = new FisPenaltyListRules(2027, [new("TEST", 3, 29m, 888m)], [new("SG", Gender.Female, 731, 166m, 0m, [0m, 1m, 2m, 7m, 9m])]);
            CompetitionDetails competition;
            await using (var workspace = new SeriesWorkspace(new SqliteSeriesFileStore(), new AlgeDecoderFactory()))
            {
                var series = await workspace.CreateAsync(path, new("Synthetic", "Test", "Club", s_date, s_date, "FIN", "2026/27"));
                series = await workspace.SaveCompetitionAsync(null, race, series.Revision);
                competition = series.Competitions[0];
                var revision = series.Revision;
                var entrants = new List<DrawEntrant>();
                for (var i = 1; i <= s_times.Count; i++)
                {
                    var athlete = new CompetitorValues("TIE" + i, "Synthetic", 2002, (980000 + i).ToString(CultureInfo.InvariantCulture), "FIN", "Club", Gender.Female);
                    var saved = await workspace.SaveDeskRowAsync(null, athlete, competition.Id, true, null, revision);
                    revision = saved.Revision; entrants.Add(new(saved.Value.Id, athlete, i * 10m));
                }
                var plan = FisStartOrder.FirstRun(competition.Id, race, Gender.Female, entrants,
                    new("1327", s_date.AddDays(-4), s_date.AddDays(2), rules), new(), "tie-seed");
                var list = Assert.Single((await workspace.SaveStartListAsync(new(plan, revision, "Test", "Synthetic draw", DateTimeOffset.UnixEpoch))).Revisions);
                await workspace.Timing!.SelectRunAsync(list.Id);
                await workspace.Timing.StartAsync(new SimulatorTimingSource(), new("Test", "Synthetic", s_date, Simulation: true), "Test");
                await workspace.Timing.StopAsync();
                foreach (var entry in list.Plan.Entries)
                {
                    await workspace.Timing.CorrectAsync(new(DecisionKind.Time, CompetitorId: entry.Entrant.CompetitorId, Hundredths: s_times[entry.Bib]),
                        "Test", "Synthetic verified time");
                }
            }
            await using var viewWorkspace = new SeriesWorkspace(new SqliteSeriesFileStore(), new AlgeDecoderFactory());
            using var vm = new MainViewModel(viewWorkspace, new Dialogs(path),
                fisStore: new FisLocalStore(folder), recentSeriesStore: new RecentSeriesStore(folder));
            await vm.OpenSeriesCommand.ExecuteAsync(null);
            await vm.ShowResultsCommand.ExecuteAsync(null);
            await vm.LoadResultsCommand.ExecuteAsync(null);
            Assert.False(vm.IsError, vm.StatusMessage);
            Assert.Equal(s_officialOrder, vm.ResultRows.Select(x => (x.Rank, x.Bib)));
            Assert.Equal(s_officialOrder, vm.ResultTopTen.Select(x => (x.Rank, x.Bib)));

            await vm.OpenTimingRunCommand.ExecuteAsync(new DrawDestination(vm.Competitions.Single(x => x.Id == competition.Id), 1));
            Assert.False(vm.IsError, vm.StatusMessage);
            vm.RefreshTiming();
            Assert.Equal(s_officialOrder, vm.RankingRows.Select(x => (x.DisplayRank!.Value.ToString(CultureInfo.InvariantCulture), x.Bib)));
            Assert.Equal(vm.RankingRows.Select(x => x.DisplayRank), vm.RankingRows.Select(x => x.Rank));
        }
        finally { Directory.Delete(folder, true); }
    }

    [AvaloniaFact]
    public async Task DrawViewEmphasizesWholeEqualPointsGroupsButNotMissingPoints()
    {
        var folder = Path.Combine(Path.GetTempPath(), "openskitime-draw-ties-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, "draw.ost");
            // A local race is still drawn by FIS points, so the points column and emphasis must be visible there too.
            var race = new CompetitionValues("Synthetic slalom", "SL1 W", s_date, Discipline.Slalom, RaceType.Club, 2, 0);
            // Two at 12.5, three at 20 (one written as 20.00), a larger group of five at 31.25, unique values and two without points.
            decimal?[] points = [12.5m, 12.5m, 20m, 20.00m, 20m, 31.25m, 31.25m, 31.25m, 31.25m, 31.25m, 8m, 40m, 41m, null, null];
            CompetitionDetails competition;
            HashSet<Guid> expected;
            await using (var workspace = new SeriesWorkspace(new SqliteSeriesFileStore(), new AlgeDecoderFactory()))
            {
                var series = await workspace.CreateAsync(path, new("Synthetic", "Test", "Club", s_date, s_date, "FIN", "2026/27"));
                series = await workspace.SaveCompetitionAsync(null, race, series.Revision);
                competition = series.Competitions[0];
                var revision = series.Revision;
                var entrants = new List<DrawEntrant>();
                for (var i = 0; i < points.Length; i++)
                {
                    var athlete = new CompetitorValues("POINTS" + i, "Synthetic", 2012, (970000 + i).ToString(CultureInfo.InvariantCulture), "FIN", "Club", Gender.Female);
                    var saved = await workspace.SaveDeskRowAsync(null, athlete, competition.Id, true, null, revision);
                    revision = saved.Revision; entrants.Add(new(saved.Value.Id, athlete, points[i]));
                }
                expected = entrants.Take(10).Select(x => x.CompetitorId).ToHashSet();
                var plan = FisStartOrder.FirstRun(competition.Id, race, Gender.Female, entrants,
                    new("1327", s_date.AddDays(-4), s_date.AddDays(2)), new(FirstGroup: 3), "draw-tie-seed");
                await workspace.SaveStartListAsync(new(plan, revision, "Test", "Synthetic draw", DateTimeOffset.UnixEpoch));
            }
            await using var viewWorkspace = new SeriesWorkspace(new SqliteSeriesFileStore(), new AlgeDecoderFactory());
            using var vm = new MainViewModel(viewWorkspace, new Dialogs(path),
                fisStore: new FisLocalStore(folder), recentSeriesStore: new RecentSeriesStore(folder));
            await vm.OpenSeriesCommand.ExecuteAsync(null);
            for (var load = 0; load < 2; load++)
            {
                // The second load proves the indication is recalculated from the saved list after reloading.
                await vm.OpenDrawRunCommand.ExecuteAsync(new DrawDestination(vm.Competitions.Single(x => x.Id == competition.Id), 1));
                Assert.False(vm.IsError, vm.StatusMessage);
                Assert.Equal(points.Length, vm.DrawStartListRows.Count);
                Assert.Equal(expected, vm.DrawStartListRows.Where(x => x.SharesPoints).Select(x => x.Entry.Entrant.CompetitorId).ToHashSet());
                Assert.All(vm.DrawStartListRows.Where(x => x.Entry.Entrant.Points is null), x => Assert.False(x.SharesPoints));
                Assert.Contains("Bold: equal FIS points", vm.DrawListInfo, StringComparison.Ordinal);
                Assert.True(vm.ShowDrawPoints);
            }
            var window = new Window { Width = 1200, Height = 900, Content = new DrawView { DataContext = vm } };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var rows = window.GetVisualDescendants().OfType<DataGridRow>().Where(x => x.DataContext is DrawStartListRow).ToArray();
            Assert.NotEmpty(rows);
            foreach (var row in rows)
            {
                var item = (DrawStartListRow)row.DataContext!;
                var surname = row.GetVisualDescendants().OfType<TextBlock>().Single(x => x.Text == item.Entry.Entrant.Athlete.Surname);
                Assert.Equal(item.SharesPoints ? FontWeight.Bold : FontWeight.Normal, surname.FontWeight);
            }
            Assert.Contains(rows, x => ((DrawStartListRow)x.DataContext!).SharesPoints);
            Assert.Contains(rows, x => !((DrawStartListRow)x.DataContext!).SharesPoints);
            window.Close();
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void LiveRunCarriesTheAuthoritativeRankAndZeroDifferenceForTiedCompetitors()
    {
        var race = new CompetitionValues("Synthetic slalom", "SL1", s_date, Discipline.Slalom, RaceType.Fis, 2, 0, "9993");
        var entrants = Enumerable.Range(1, 5).Select(i => new DrawEntrant(new Guid(i, 0, 0, new byte[8]),
            new("LIVE" + i, "Athlete", 2000, (960000 + i).ToString(CultureInfo.InvariantCulture), "FIN", "Club", Gender.Female), i)).ToArray();
        var plan = FisStartOrder.FirstRun(Guid.NewGuid(), race, Gender.Female, entrants, new("1327", s_date, s_date), new(), "live-seed");
        var list = new StartListRevision(Guid.NewGuid(), 1, DateTimeOffset.UnixEpoch, null, "op", "draw", plan);
        var session = Guid.NewGuid();
        var day = s_date.ToDateTime(TimeOnly.MinValue).Ticks + TimeSpan.FromHours(10).Ticks;
        var times = new Dictionary<int, long> { [1] = 6000, [2] = 5900, [3] = 6000, [4] = 5900, [5] = 6100 };
        var observations = new List<TimingObservation>();
        var audit = new List<TimingAudit>();
        foreach (var entry in plan.Entries)
        {
            var start = day + entry.Bib * TimeSpan.TicksPerMinute;
            foreach (var (channel, ticks) in new[] { (0, start), (1, start + times[entry.Bib] * TimingTime.TicksPerHundredth) })
            {
                var observation = new TimingObservation($"{session:N}:{observations.Count + 1}:0", session, observations.Count + 1, "Timy",
                    "fingerprint:" + observations.Count, ObservationKind.Impulse, channel, ticks, 4, null, false, "clock", "Synthetic impulse");
                observations.Add(observation);
                audit.Add(new(audit.Count + 1, list.Id, DateTimeOffset.UnixEpoch, "op", "assign", new(DecisionKind.Assignment, observation.Key),
                    new(DecisionKind.Assignment, observation.Key, Bib: entry.Bib)));
            }
        }
        var snapshot = TimingEngine.Replay(list, observations, audit, 0, 1);
        var live = LiveSnapshotMapper.MapRun(list, snapshot, TimeZoneInfo.Utc, DateTimeOffset.UnixEpoch);
        Assert.Equal(snapshot.Results.Select(x => (x.Bib, x.Rank)), live.Results.Select(x => (x.Bib, x.Rank)));
        Assert.Equal([3, 1, 3, 1, 5], live.Results.OrderBy(x => x.Bib).Select(x => x.Rank));
        Assert.Equal([100L, 0, 100, 0, 200], live.Results.OrderBy(x => x.Bib).Select(x => x.Difference!.Value));
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
}
