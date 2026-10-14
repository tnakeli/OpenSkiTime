using Avalonia.Headless.XUnit;
using OpenSkiTime.Application;
using OpenSkiTime.Desktop;
using OpenSkiTime.Domain;
using OpenSkiTime.Persistence;
using Xunit;

namespace OpenSkiTime.Tests;

public partial class DesktopWorkflowTests
{
    // Before the draw every entrant is listed with saved points (best first) and points that differ from the loaded
    // FIS list are marked; the draw skips missing bibs; afterwards positions and bibs can be changed until the run starts.
    [AvaloniaFact]
    public async Task DrawPreviewSkippedBibsMovesAndBibReassignment()
    {
        var root = Path.Combine(Path.GetTempPath(), "openskitime-draw-edit", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var file = Path.Combine(root, "Synthetic.ost");
            var cache = new FisLocalStore(root);
            await cache.SaveListAsync(SyntheticFisArchive()); // List 1327: 123456 has SL 12.34.
            await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore());
            var date = new DateOnly(2026, 9, 27);
            var series = await workspace.CreateAsync(file, new("Synthetic race weekend", "Test slope", "Test club", date, date, "FIN", "2026/27"));
            series = await workspace.SaveCompetitionAsync(null, new("Synthetic Slalom", "SL1", date, Discipline.Slalom, RaceType.Fis, 2, 0, "1234"), series.Revision);
            var competition = series.Competitions[0];
            var revision = series.Revision;
            for (var i = 0; i < 5; i++)
            {
                revision = (await workspace.SaveDeskRowAsync(null, new($"TEST{i:00}", "Athlete", 2000, $"{123456 + i}", "FIN", "Synthetic club", Gender.Female),
                    competition.Id, true, null, revision)).Revision;
            }
            await SaveCachedFisPointsAsync(workspace, cache);
            // 123457 has points saved that the loaded list does not have.
            var desk = await workspace.ReadCompetitorDeskAsync();
            var other = desk.Competitors.Single(x => x.Values.FederationCode == "123457");
            await workspace.ApplyDeskBatchAsync(new DeskBatch(desk.SeriesId, desk.Revision,
                [new DeskBatchRow(other.Id, other.Values, [], new CompetitorFisPoints("1327", null, null, 30m, null, null), true)], []));
            using var vm = new MainViewModel(workspace, new FileDialogsStub { NewPath = file, OpenPath = file, BackupPath = file + ".backup" },
                new EntryExchangeStub(), cache, recentSeriesStore: new RecentSeriesStore(root));
            await vm.OpenSeriesCommand.ExecuteAsync(null);
            await vm.OpenDrawRunCommand.ExecuteAsync(new DrawDestination(vm.Competitions[0], 1));

            Assert.True(vm.ShowDrawPreview);
            Assert.Equal(["123456", "123457", "123458", "123459", "123460"], vm.DrawPreviewRows.Select(x => x.Code));
            Assert.Equal([12.34m, 30m, null, null, null], vm.DrawPreviewRows.Select(x => x.Points));
            Assert.Equal([false, true, false, false, false], vm.DrawPreviewRows.Select(x => x.DiffersFromList));
            Assert.Contains("1 differ from loaded list 1327", vm.DrawPreviewInfo, StringComparison.Ordinal);

            vm.DrawFirstBib = 1;
            vm.DrawSkippedBibsText = "2, 4";
            await vm.PrepareDrawCommand.ExecuteAsync(null);
            Assert.False(vm.IsError, vm.StatusMessage);
            Assert.False(vm.ShowDrawPreview);
            Assert.Equal([1, 3, 5, 6, 7], vm.DrawStartListRows.Select(x => x.Entry.Bib));
            Assert.True(vm.CanEditStartList);

            // A penalty move keeps the bib; the revision records operator and reason.
            var penalised = vm.DrawStartListRows[0].Entry;
            vm.SelectedDrawRow = vm.DrawStartListRows[0];
            vm.DrawMoveToPosition = "5";
            vm.DrawMoveReason = "Start penalty";
            await vm.MoveDrawEntryCommand.ExecuteAsync(null);
            Assert.False(vm.IsError, vm.StatusMessage);
            Assert.Equal(penalised.Entrant.CompetitorId, vm.DrawStartListRows[^1].Entry.Entrant.CompetitorId);
            Assert.Equal([3, 5, 6, 7, 1], vm.DrawStartListRows.Select(x => x.Entry.Bib));
            var lists = await workspace.ReadStartListsAsync(competition.Id);
            var latest = lists.Revisions.OrderByDescending(x => x.Revision).First();
            Assert.Contains("moved from position 1 to 5: Start penalty", latest.Reason, StringComparison.Ordinal);

            // Bibs again in the current order, any number of times.
            vm.DrawFirstBib = 10;
            vm.DrawSkippedBibsText = "13";
            await vm.AssignBibsCommand.ExecuteAsync(null);
            Assert.Equal([10, 11, 12, 14, 15], vm.DrawStartListRows.Select(x => x.Entry.Bib));
            vm.DrawSkippedBibsText = "";
            await vm.AssignBibsCommand.ExecuteAsync(null);
            Assert.Equal([10, 11, 12, 13, 14], vm.DrawStartListRows.Select(x => x.Entry.Bib));
            Assert.Equal("", vm.DrawSkippedBibsText);
            Assert.Equal(10, vm.DrawFirstBib);
            // A long skipped range is accepted; the recorded reason keeps the compact notation.
            vm.DrawSkippedBibsText = "11-90";
            await vm.AssignBibsCommand.ExecuteAsync(null);
            Assert.False(vm.IsError, vm.StatusMessage);
            Assert.Equal([10, 91, 92, 93, 94], vm.DrawStartListRows.Select(x => x.Entry.Bib));
            vm.DrawSkippedBibsText = "";
            await vm.AssignBibsCommand.ExecuteAsync(null);
            Assert.Equal([10, 11, 12, 13, 14], vm.DrawStartListRows.Select(x => x.Entry.Bib));

            // Once the run has started, the list is locked and says why.
            lists = await workspace.ReadStartListsAsync(competition.Id);
            latest = lists.Revisions.OrderByDescending(x => x.Revision).First();
            await workspace.MarkRunStartedAsync(latest.Id, lists.SeriesRevision, "Starter", DateTimeOffset.UnixEpoch);
            await vm.OpenDrawRunCommand.ExecuteAsync(new DrawDestination(vm.Competitions[0], 1));
            Assert.False(vm.CanEditStartList);
            await vm.AssignBibsCommand.ExecuteAsync(null);
            Assert.True(vm.IsError);
            Assert.Contains("locked", vm.StatusMessage, StringComparison.Ordinal);
            await workspace.CloseAsync();
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
