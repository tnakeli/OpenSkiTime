using Avalonia.Headless.XUnit;
using OpenSkiTime.Application;
using OpenSkiTime.Desktop;
using OpenSkiTime.Domain;
using OpenSkiTime.Persistence;
using Xunit;

namespace OpenSkiTime.Tests;

public partial class DesktopWorkflowTests
{
    // A FIS draw with a points list that is not valid on the race date is allowed, and the list is stated clearly
    // before the draw, in the status after it and on the saved start list.
    [AvaloniaFact]
    public async Task DrawWithAPointsListNotValidOnTheRaceDateStatesTheListUsed()
    {
        var root = Path.Combine(Path.GetTempPath(), "openskitime-stale-list", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var file = Path.Combine(root, "Synthetic.ost");
            var cache = new FisLocalStore(root);
            await cache.SaveListAsync(SyntheticFisArchive()); // List 1327, valid 2026-09-24 – 2026-09-30.
            await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore());
            var date = new DateOnly(2026, 11, 19);
            var series = await workspace.CreateAsync(file, new("Synthetic race weekend", "Test slope", "Test club", date, date, "FIN", "2026/27"));
            series = await workspace.SaveCompetitionAsync(null, new("Synthetic Slalom", "SL1", date, Discipline.Slalom, RaceType.Fis, 2, 0, "1234"), series.Revision);
            var competition = series.Competitions[0];
            var revision = series.Revision;
            for (var i = 0; i < 3; i++)
            {
                revision = (await workspace.SaveDeskRowAsync(null, new($"TEST{i:00}", "Athlete", 2000, $"{123456 + i}", "FIN", "Synthetic club", Gender.Female),
                    competition.Id, true, null, revision)).Revision;
            }
            await SaveCachedFisPointsAsync(workspace, cache);
            using var vm = new MainViewModel(workspace, new FileDialogsStub { NewPath = file, OpenPath = file, BackupPath = file + ".backup" },
                new EntryExchangeStub(), cache, recentSeriesStore: new RecentSeriesStore(root));
            await vm.OpenSeriesCommand.ExecuteAsync(null);
            await vm.OpenDrawRunCommand.ExecuteAsync(new DrawDestination(vm.Competitions[0], 1));

            Assert.True(vm.CanPrepareDraw, vm.DrawBlockReason);
            Assert.True(vm.DrawPointsListWarning);
            Assert.Contains("1327", vm.DrawPointsListText, StringComparison.Ordinal);
            Assert.Contains("valid 2026-09-24–2026-09-30: NOT VALID on the competition date 2026-11-19", vm.DrawPointsListText, StringComparison.Ordinal);

            await vm.PrepareDrawCommand.ExecuteAsync(null);
            Assert.False(vm.IsError, vm.StatusMessage);
            var saved = Assert.Single((await workspace.ReadStartListsAsync(competition.Id)).Revisions);
            Assert.Equal(new PointsListSource("1327", new(2026, 9, 24), new(2026, 9, 30)), saved.Plan.PointsList with { PenaltyRules = null });
            Assert.True(vm.DrawPointsListWarning);
            Assert.StartsWith("Drawn with FIS points list 1327", vm.DrawPointsListText, StringComparison.Ordinal);
            Assert.Contains("NOT VALID", vm.DrawPointsListText, StringComparison.Ordinal);
            Assert.Contains("NOT VALID on the competition date", vm.StatusMessage, StringComparison.Ordinal);
            await workspace.CloseAsync();
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
