using Avalonia.Headless.XUnit;
using OpenSkiTime.Application;
using OpenSkiTime.Desktop;
using OpenSkiTime.Devices;
using OpenSkiTime.Domain;
using OpenSkiTime.Persistence;
using OpenSkiTime.Timing;
using Xunit;

namespace OpenSkiTime.Tests;

public partial class DesktopWorkflowTests
{
    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PenaltyLoadsAutomaticallyFromPortableDrawRulesAndMissingRulesBlockApproval(bool includeRules)
    {
        var folder = Path.Combine(Path.GetTempPath(), "openskitime-penalty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, "race.ost"); var date = new DateOnly(2026, 9, 28);
            await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore(), new AlgeDecoderFactory());
            var series = await workspace.CreateAsync(path, new("Synthetic", "Test", "Club", date, date, "FIN", "2026/27"));
            var race = new CompetitionValues("Synthetic race", "SG1 W", date, Discipline.SuperG, RaceType.Fis, 1, 0, "9991",
                Calendar: new(2027, "Test", "FIN", "TEST", "W", new("TEST", "Delegate", "FIN", "1047")));
            series = await workspace.SaveCompetitionAsync(null, race, series.Revision);
            var competition = series.Competitions[0].Id; var revision = series.Revision;
            var entrants = new List<DrawEntrant>();
            for (var i = 1; i <= 6; i++)
            {
                var athlete = new CompetitorValues("TEST" + i, "Synthetic", 2002, (990000 + i).ToString(System.Globalization.CultureInfo.InvariantCulture), "FIN", "Club", Gender.Female);
                var saved = await workspace.SaveDeskRowAsync(null, athlete, competition, true, null, revision);
                revision = saved.Revision; entrants.Add(new(saved.Value.Id, athlete, i * 10m));
            }
            var rules = new FisPenaltyListRules(2027, [new("TEST", 3, 29m, 888m)],
                [new("SG", Gender.Female, 731, 166m, 0m, [0m, 1m, 2m, 7m, 9m])]);
            var plan = FisStartOrder.FirstRun(competition, race, Gender.Female, entrants,
                new("1327", new(2026, 9, 24), new(2026, 9, 30), includeRules ? rules : null), new(), "synthetic-seed");
            var list = Assert.Single((await workspace.SaveStartListAsync(new(plan, revision, "Test", "Synthetic draw", DateTimeOffset.UnixEpoch))).Revisions);
            await workspace.Timing!.SelectRunAsync(list.Id);
            await workspace.Timing.StartAsync(new SimulatorTimingSource(), new("Test", "Synthetic", date, Simulation: true), "Test");
            await workspace.Timing.StopAsync();
            foreach (var entry in list.Plan.Entries)
            {
                await workspace.Timing.CorrectAsync(entry.Position == 6
                    ? new(DecisionKind.Status, CompetitorId: entry.Entrant.CompetitorId, Status: TimingStatus.DNF)
                    : new(DecisionKind.Time, CompetitorId: entry.Entrant.CompetitorId, Hundredths: 5000 + entry.Position * 100),
                    "Test", "Synthetic backup result");
            }
            await workspace.BackupAsync(path + ".bak"); await workspace.CloseAsync(); await workspace.OpenAsync(path + ".bak");
            using var vm = new MainViewModel(workspace, new FileDialogsStub { NewPath = path, OpenPath = path + ".bak", BackupPath = path + ".second.bak" },
                fisStore: new FisLocalStore(folder), recentSeriesStore: new RecentSeriesStore(folder));
            await vm.OpenSeriesCommand.ExecuteAsync(null); await vm.ShowResultsCommand.ExecuteAsync(null); await vm.LoadResultsCommand.ExecuteAsync(null);
            Assert.False(vm.IsError, vm.StatusMessage);
            Assert.Equal(includeRules, vm.ResultsReady);
            if (includeRules)
            {
                Assert.Contains("F 731", vm.ResultsRuleValues); Assert.Contains("Category adder 7.00", vm.ResultsRuleValues);
                Assert.Contains("Minimum 29.00", vm.ResultsRuleValues); Assert.Contains("1327", vm.ResultsRuleSource);
                Assert.Equal(5, vm.ResultTopTen.Count); Assert.Equal(5, vm.ResultBestStarted.Count);
                Assert.Contains("(A + B − C) / 10", vm.ResultsPenaltySummary);
                Assert.All(vm.ResultRows.Where(x => x.Status == "Finished"), x => Assert.NotEmpty(x.RacePoints));
            }
            else
            {
                Assert.Contains("rules are missing", vm.ResultsRuleSource);
                await vm.ApproveFisResultsCommand.ExecuteAsync(null);
                Assert.True(vm.IsError); Assert.Empty(await workspace.ReadApprovedResultsAsync(competition));
            }
        }
        finally { Directory.Delete(folder, true); }
    }
}
