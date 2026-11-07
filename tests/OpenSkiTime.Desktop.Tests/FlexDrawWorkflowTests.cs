using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OpenSkiTime.Application;
using OpenSkiTime.Desktop;
using OpenSkiTime.Devices;
using OpenSkiTime.Domain;
using OpenSkiTime.Persistence;
using Xunit;

namespace OpenSkiTime.Tests;

// A Club race: several classes, men and women in one list, first and later runs arranged in the Draw view.
public sealed class FlexDrawWorkflowTests
{
    private static readonly DateOnly s_date = new(2026, 11, 7);

    [AvaloniaFact]
    public async Task ClubRaceDrawsClassesAsBlocksAndThenReversesTheStartOrderForLaterRuns()
    {
        var folder = Path.Combine(Path.GetTempPath(), "openskitime-flex-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, "club.ost");
            var race = new CompetitionValues("Training race", "TR", s_date, Discipline.GiantSlalom, RaceType.Club, 3, 0);
            CompetitionDetails competition;
            await using (var setup = new SeriesWorkspace(new SqliteSeriesFileStore(), new AlgeDecoderFactory()))
            {
                var series = await setup.CreateAsync(path, new("Synthetic", "Test", "Club", s_date, s_date, "FIN", "2026/27"));
                series = await setup.SaveCompetitionAsync(null, race, series.Revision);
                competition = series.Competitions[0];
                var revision = (await setup.SaveCategoryRuleAsync(null, new("N10", 2016, 2016, Gender.Female, 0), series.Revision)).Revision;
                revision = (await setup.SaveCategoryRuleAsync(null, new("M10", 2016, 2016, Gender.Male, 1), revision)).Revision;
                revision = (await setup.SaveCategoryRuleAsync(null, new("N12", 2014, 2014, Gender.Female, 2), revision)).Revision;
                for (var i = 0; i < 12; i++)
                {
                    var (year, gender) = (i % 3) switch { 0 => (2016, Gender.Female), 1 => (2016, Gender.Male), _ => (2014, Gender.Female) };
                    var athlete = new CompetitorValues("RACER" + i, "Young", year, null, null, "Club", gender);
                    revision = (await setup.SaveDeskRowAsync(null, athlete, competition.Id, true, null, revision)).Revision;
                }
            }
            await using var viewWorkspace = new SeriesWorkspace(new SqliteSeriesFileStore(), new AlgeDecoderFactory());
            using var vm = new MainViewModel(viewWorkspace, new Dialogs(path), fisStore: new FisLocalStore(folder), recentSeriesStore: new RecentSeriesStore(folder));
            await vm.OpenSeriesCommand.ExecuteAsync(null);
            await vm.OpenDrawRunCommand.ExecuteAsync(new DrawDestination(vm.Competitions.Single(x => x.Id == competition.Id), 1));
            Assert.False(vm.IsError, vm.StatusMessage);
            Assert.True(vm.IsFlexDraw);
            Assert.Equal(["N10", "M10", "N12"], vm.DrawBlocks.Select(x => x.ClassesText));
            Assert.Equal([4, 4, 4], vm.DrawBlocks.Select(x => x.Starters));
            // Arrange: the 12-year-olds first, and the two 10-year-old classes drawn as one group.
            vm.SelectedDrawBlock = vm.DrawBlocks[2];
            vm.MoveDrawBlockUpCommand.Execute(null); vm.MoveDrawBlockUpCommand.Execute(null);
            vm.SelectedDrawBlock = vm.DrawBlocks[1];
            vm.MergeDrawBlockCommand.Execute(null);
            Assert.Equal(["N12", "N10 + M10"], vm.DrawBlocks.Select(x => x.ClassesText));
            // First bibs are worked out from the order and the draw settings; a typed first bib moves the blocks after it.
            Assert.Equal(["1–4", "5–12"], vm.DrawBlocks.Select(x => x.BibRange));
            Assert.Equal(["1", "5"], vm.DrawBlocks.Select(x => x.AutoFirstBib));
            vm.DrawFirstBib = 11; vm.DrawSkippedBibsText = "13";
            Assert.Equal(["11–15", "16–23"], vm.DrawBlocks.Select(x => x.BibRange));
            vm.DrawFirstBib = 1; vm.DrawSkippedBibsText = "";
            vm.DrawBlocks[1].FirstBib = "20";
            Assert.Equal(["1–4", "20–27"], vm.DrawBlocks.Select(x => x.BibRange));
            vm.DrawBlocks[1].FirstBib = "3";
            Assert.True(vm.DrawBlocks[1].BibConflict);
            vm.DrawBlocks[1].FirstBib = "11";
            await vm.PrepareDrawCommand.ExecuteAsync(null);
            Assert.False(vm.IsError, vm.StatusMessage);
            Assert.Equal(12, vm.DrawEntries.Count);
            Assert.All(vm.DrawEntries.Take(4), x => Assert.Equal("N12", x.Entrant.Class));
            Assert.Equal(Enumerable.Range(1, 4).Concat(Enumerable.Range(11, 8)), vm.DrawEntries.Select(x => x.Bib));
            Assert.Null(vm.DrawRevision!.Plan.Gender);
            // Later runs reverse the start order without any results; run 3 repeats the order of run 2.
            var runOne = vm.DrawEntries.Select(x => x.Bib).ToArray();
            var listId = vm.DrawRevision.Id;
            await viewWorkspace.MarkRunStartedAsync(listId, (await viewWorkspace.ReadAsync()).Revision, "Starter", DateTimeOffset.UtcNow);
            await vm.OpenDrawRunCommand.ExecuteAsync(new DrawDestination(vm.Competitions.Single(), 2));
            Assert.False(vm.IsError, vm.StatusMessage);
            Assert.True(vm.CanPrepareDraw, vm.DrawBlockReason);
            await vm.PrepareDrawCommand.ExecuteAsync(null);
            Assert.False(vm.IsError, vm.StatusMessage);
            Assert.Equal(runOne.Take(4).Reverse().Concat(runOne.Skip(4).Reverse()), vm.DrawEntries.Select(x => x.Bib));
            var window = new Window { Width = 1500, Height = 900, Content = new DrawView { DataContext = vm } };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            vm.IsDrawSetupOpen = true;
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            var grid = window.GetVisualDescendants().OfType<DataGrid>().Single(x => x.Name == "DrawSetupGrid");
            Assert.True(grid.IsEffectivelyVisible);
            Assert.Equal(2, grid.ItemsSource.Cast<object>().Count());
            Assert.Contains(grid.Columns, x => Equals(x.Header, "ORDER BY") && x.IsVisible);
            Assert.DoesNotContain(grid.Columns, x => Equals(x.Header, "FIRST RUN") && x.IsVisible);
            window.Close();
        }
        finally { Directory.Delete(folder, true); }
    }

    [AvaloniaFact]
    public async Task PointsTypedInTheGridOrderABlockByPointsAndWinOverDisciplinePoints()
    {
        var folder = Path.Combine(Path.GetTempPath(), "openskitime-flex-points-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, "points.ost");
            CompetitionDetails competition;
            await using (var setup = new SeriesWorkspace(new SqliteSeriesFileStore(), new AlgeDecoderFactory()))
            {
                var series = await setup.CreateAsync(path, new("Synthetic", "Test", "Club", s_date, s_date, "FIN", "2026/27"));
                series = await setup.SaveCompetitionAsync(null, new("Cup race", "CR", s_date, Discipline.Slalom, RaceType.Club, 2, 0), series.Revision);
                competition = series.Competitions[0];
                var revision = (await setup.SaveCategoryRuleAsync(null, new("M16", 2010, 2010, Gender.Male, 0), series.Revision)).Revision;
                for (var i = 0; i < 6; i++)
                {
                    revision = (await setup.SaveDeskRowAsync(null, new("CUP" + i, "Racer", 2010, null, null, "Club", Gender.Male), competition.Id, true, null, revision)).Revision;
                }
            }
            await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore(), new AlgeDecoderFactory());
            using var vm = new MainViewModel(workspace, new Dialogs(path), fisStore: new FisLocalStore(folder), recentSeriesStore: new RecentSeriesStore(folder));
            await vm.OpenSeriesCommand.ExecuteAsync(null);
            vm.ShowCompetitorsCommand.Execute(null);
            // Higher points are better: 0 and empty count as no points; one of the rows has only discipline points.
            var typed = new Dictionary<string, string> { ["CUP0"] = "10", ["CUP1"] = "250.5", ["CUP2"] = "80", ["CUP3"] = "0" };
            foreach (var (surname, points) in typed) { vm.VisibleCompetitors.Single(x => x.Surname == surname).PointsText = points; }
            vm.VisibleCompetitors.Single(x => x.Surname == "CUP4").PointsText = "not a number";
            Assert.Equal(string.Empty, vm.VisibleCompetitors.Single(x => x.Surname == "CUP4").PointsText);
            await vm.CommitDeskChangesCommand.ExecuteAsync(null);
            Assert.False(vm.IsError, vm.StatusMessage);
            var desk = await workspace.ReadCompetitorDeskAsync();
            Assert.Equal(250.5m, desk.FisPointsOf(desk.Competitors.Single(x => x.Values.Surname == "CUP1").Id)!.For("SL"));
            await vm.OpenDrawRunCommand.ExecuteAsync(new DrawDestination(vm.Competitions.Single(), 1));
            var block = Assert.Single(vm.DrawBlocks);
            block.FirstRun = DrawBlockRow.FirstRunChoices[1];
            block.BetterPoints = DrawBlockRow.BetterPointsChoices[1];
            block.ZeroIsNoPoints = true;
            block.FirstGroup = 2;
            await vm.PrepareDrawCommand.ExecuteAsync(null);
            Assert.False(vm.IsError, vm.StatusMessage);
            var order = vm.DrawEntries.Select(x => x.Entrant.Athlete.Surname).ToArray();
            Assert.Equal(["CUP1", "CUP2"], order.Take(2).Order(StringComparer.Ordinal).ToArray());
            Assert.Equal("CUP0", order[2]);
            Assert.Equal(3, vm.DrawEntries.Count(x => x.Group == "No points"));
        }
        finally { Directory.Delete(folder, true); }
    }

    [AvaloniaFact]
    public async Task DrawAgainKeepsTheScrollPositionOfTheStartList()
    {
        var folder = Path.Combine(Path.GetTempPath(), "openskitime-flex-scroll-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, "scroll.ost");
            await using (var setup = new SeriesWorkspace(new SqliteSeriesFileStore(), new AlgeDecoderFactory()))
            {
                var series = await setup.CreateAsync(path, new("Synthetic", "Test", "Club", s_date, s_date, "FIN", "2026/27"));
                series = await setup.SaveCompetitionAsync(null, new("Long race", "LR", s_date, Discipline.GiantSlalom, RaceType.Club, 1, 0), series.Revision);
                var revision = series.Revision;
                for (var i = 0; i < 60; i++)
                {
                    revision = (await setup.SaveDeskRowAsync(null, new("RACER" + i, "Young", 2014, null, null, "Club", Gender.Male), series.Competitions[0].Id, true, null, revision)).Revision;
                }
            }
            await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore(), new AlgeDecoderFactory());
            using var vm = new MainViewModel(workspace, new Dialogs(path), fisStore: new FisLocalStore(folder), recentSeriesStore: new RecentSeriesStore(folder));
            await vm.OpenSeriesCommand.ExecuteAsync(null);
            var window = new Window { Width = 1400, Height = 700, Content = new DrawView { DataContext = vm } };
            window.Show();
            await vm.OpenDrawRunCommand.ExecuteAsync(new DrawDestination(vm.Competitions.Single(), 1));
            vm.DrawBlocks[0].FirstRun = DrawBlockRow.FirstRunChoices[0];
            await vm.PrepareDrawCommand.ExecuteAsync(null);
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            var bar = () => window.GetVisualDescendants().OfType<DataGrid>().Single(x => x.Name == "DrawStartListGrid")
                .GetVisualDescendants().OfType<Avalonia.Controls.Primitives.ScrollBar>().First(x => x.Orientation == Avalonia.Layout.Orientation.Vertical);
            Assert.True(bar().Maximum > 100, "The list should be scrollable.");
            bar().Value = 400; window.UpdateLayout();
            Assert.Equal(400, bar().Value);
            await vm.PrepareDrawCommand.ExecuteAsync(null);
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            Assert.Equal(400, bar().Value, 1);
            window.Close();
        }
        finally { Directory.Delete(folder, true); }
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
