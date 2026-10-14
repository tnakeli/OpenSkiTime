using Avalonia.Headless.XUnit;
using OpenSkiTime.Application;
using OpenSkiTime.Desktop;
using OpenSkiTime.Domain;
using OpenSkiTime.Persistence;
using Xunit;

namespace OpenSkiTime.Tests;

public partial class DesktopWorkflowTests
{
    // A series moved to a computer without the FIS list: a changed FIS code must not keep the previous athlete's points.
    [AvaloniaFact]
    public async Task ChangingAFisCodeWithoutALoadedListClearsThePreviousAthletesPoints()
    {
        var root = Path.Combine(Path.GetTempPath(), "openskitime-offline-code", Guid.NewGuid().ToString("N"));
        var offline = Path.Combine(root, "offline");
        Directory.CreateDirectory(offline);
        try
        {
            var file = Path.Combine(root, "Synthetic.ost");
            var cache = new FisLocalStore(root);
            await cache.SaveListAsync(SyntheticFisArchive());
            await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore());
            var date = new DateOnly(2026, 9, 27);
            var series = await workspace.CreateAsync(file, new("Synthetic race", "Test slope", "Test club", date, date, "FIN", "2026/27"));
            series = await workspace.SaveCompetitionAsync(null, new("Synthetic Slalom", "SL1", date, Discipline.Slalom, RaceType.Fis, 2, 0, "1234"), series.Revision);
            await workspace.SaveDeskRowAsync(null, new("TEST00", "Athlete", 2000, "123456", "FIN", "Synthetic club", Gender.Female),
                series.Competitions[0].Id, true, null, series.Revision);
            await SaveCachedFisPointsAsync(workspace, cache);
            using var vm = new MainViewModel(workspace, new FileDialogsStub { NewPath = file, OpenPath = file, BackupPath = file + ".backup" },
                new EntryExchangeStub(), new FisLocalStore(offline), recentSeriesStore: new RecentSeriesStore(offline));
            await vm.OpenSeriesCommand.ExecuteAsync(null);
            vm.ShowCompetitorsCommand.Execute(null);
            var row = vm.VisibleCompetitors.Single(x => !x.IsPlaceholder);
            var saved = row.FisPoints;
            Assert.NotNull(saved);

            row.FederationCode = "999999";
            Assert.Null(row.FisPoints);
            Assert.True(row.IsFisPointsChanged);
            Assert.Contains("previous points are cleared", vm.StatusMessage, StringComparison.Ordinal);
            // Back to the saved code: the saved points return.
            row.FederationCode = "123456";
            Assert.Equal(saved, row.FisPoints);
            Assert.False(row.IsFisPointsChanged);
            await workspace.CloseAsync();
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    // Saved points follow the athlete: a changed Code saves the new athlete's points even when they equal the old ones,
    // and undoing only the points never keeps the previous athlete's points under the new Code.
    [AvaloniaFact]
    public async Task ChangedFisCodesSaveOnlyPointsThatBelongToTheNewCode()
    {
        var root = Path.Combine(Path.GetTempPath(), "openskitime-code-points", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var file = Path.Combine(root, "Synthetic.ost");
            var cache = new FisLocalStore(root);
            await cache.SaveListAsync(SyntheticFisArchive());
            await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore());
            var date = new DateOnly(2026, 9, 27);
            var series = await workspace.CreateAsync(file, new("Synthetic race", "Test slope", "Test club", date, date, "FIN", "2026/27"));
            var revision = series.Revision;
            foreach (var (name, code) in new[] { ("TEST00", "123456"), ("TEST01", "123457") })
            { revision = (await workspace.SaveDeskRowAsync(null, new(name, "Athlete", 2000, code, "FIN", "Synthetic club", Gender.Female), null, false, null, revision)).Revision; }
            await SaveCachedFisPointsAsync(workspace, cache);
            using var vm = new MainViewModel(workspace, new FileDialogsStub { NewPath = file, OpenPath = file, BackupPath = file + ".backup" },
                new EntryExchangeStub(), cache, recentSeriesStore: new RecentSeriesStore(root));
            await vm.OpenSeriesCommand.ExecuteAsync(null);
            vm.ShowCompetitorsCommand.Execute(null);
            var undone = vm.VisibleCompetitors.Single(x => x.FederationCode == "123456");
            var equal = vm.VisibleCompetitors.Single(x => x.FederationCode == "123457");
            Assert.NotNull(undone.FisPoints);
            Assert.NotNull(equal.FisPoints);

            // New Code, then only the points change is undone: the previous athlete's points must not be saved for it.
            undone.FederationCode = "999991";
            undone.RestoreField(nameof(CompetitorGridRow.FisPoints));
            Assert.Equal(undone.SavedFisPoints, undone.FisPoints);
            // New Code whose athlete has exactly the saved points (as on a real list): those points are saved for it.
            equal.FederationCode = "999992";
            equal.SetFisPoints(equal.SavedFisPoints, "999992");
            Assert.False(equal.IsFisPointsChanged);
            await vm.CommitDeskChangesCommand.ExecuteAsync(null);
            Assert.False(vm.IsError, vm.StatusMessage);

            var desk = await workspace.ReadCompetitorDeskAsync();
            Assert.Null(desk.FisPointsOf(desk.Competitors.Single(x => x.Values.FederationCode == "999991").Id));
            Assert.Equal(equal.SavedFisPoints, desk.FisPointsOf(desk.Competitors.Single(x => x.Values.FederationCode == "999992").Id));
            await workspace.CloseAsync();
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
