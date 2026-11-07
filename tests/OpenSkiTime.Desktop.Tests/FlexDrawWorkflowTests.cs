using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OpenSkiTime.Application;
using OpenSkiTime.Desktop;
using OpenSkiTime.Devices;
using OpenSkiTime.LiveTiming;
using OpenSkiTime.Domain;
using OpenSkiTime.Persistence;
using OpenSkiTime.Timing;
using Xunit;

namespace OpenSkiTime.Tests;

// A Club race: several classes, men and women in one list, first and later runs arranged in the Draw view.
public sealed class FlexDrawWorkflowTests
{
    private static readonly DateOnly s_date = new(2026, 11, 7);
    private static readonly int[] s_bibs = [1, 2, 3, 4];

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
            DataGrid Grid() => window.GetVisualDescendants().OfType<DataGrid>().Single(x => x.Name == "DrawStartListGrid");
            int FirstRow() { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); return Grid().GetVisualDescendants().OfType<DataGridRow>().Where(x => x.Bounds.Height > 0).Min(x => x.Index); }
            Assert.Equal(0, FirstRow());
            // Scroll down so that a later row is the first one in view.
            Grid().ScrollIntoView(vm.DrawStartListRows[^1], null); window.UpdateLayout();
            Grid().ScrollIntoView(vm.DrawStartListRows[10], null); window.UpdateLayout();
            Grid().ScrollIntoView(vm.DrawStartListRows[^1], null); window.UpdateLayout();
            var before = FirstRow();
            Assert.True(before > 10, $"The list should be scrolled, first row {before}.");
            await vm.PrepareDrawCommand.ExecuteAsync(null);
            for (var i = 0; i < 6; i++) { Dispatcher.UIThread.RunJobs(); await Task.Delay(30); window.UpdateLayout(); }
            Assert.Equal(before, FirstRow());
            window.Close();
        }
        finally { Directory.Delete(folder, true); }
    }

    [AvaloniaFact]
    public async Task RankingListsTheClassOfTheLatestFinisherFirst()
    {
        var folder = Path.Combine(Path.GetTempPath(), "openskitime-flex-ranking-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, "ranking.ost");
            var race = new CompetitionValues("Class race", "CL", s_date, Discipline.GiantSlalom, RaceType.Club, 1, 0);
            CompetitionDetails competition;
            await using (var setup = new SeriesWorkspace(new SqliteSeriesFileStore(), new AlgeDecoderFactory()))
            {
                var series = await setup.CreateAsync(path, new("Synthetic", "Test", "Club", s_date, s_date, "FIN", "2026/27"));
                series = await setup.SaveCompetitionAsync(null, race, series.Revision);
                competition = series.Competitions[0];
                var revision = (await setup.SaveCategoryRuleAsync(null, new("N10", 2016, 2016, Gender.Female, 0), series.Revision)).Revision;
                revision = (await setup.SaveCategoryRuleAsync(null, new("M10", 2016, 2016, Gender.Male, 1), revision)).Revision;
                var entrants = new List<DrawEntrant>();
                for (var i = 0; i < 6; i++)
                {
                    var gender = i < 3 ? Gender.Female : Gender.Male;
                    var athlete = new CompetitorValues("CLASS" + i, "Racer", 2016, null, null, "Club", gender);
                    var saved = await setup.SaveDeskRowAsync(null, athlete, competition.Id, true, null, revision);
                    revision = saved.Revision; entrants.Add(new(saved.Value.Id, athlete, null, i < 3 ? "N10" : "M10"));
                }
                var blocks = new StartOrderSetup([new("N10", ["N10"], new(), new(), "a"), new("M10", ["M10"], new(), new(), "b")]);
                var plan = FlexStartOrder.FirstRun(competition.Id, race, null, entrants, PointsListSource.None, new(), blocks);
                await setup.SaveStartListAsync(new(plan, revision, "Test", "Draw", DateTimeOffset.UnixEpoch));
            }
            await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore(), new AlgeDecoderFactory());
            using var vm = new MainViewModel(workspace, new Dialogs(path), fisStore: new FisLocalStore(folder), recentSeriesStore: new RecentSeriesStore(folder));
            await vm.OpenSeriesCommand.ExecuteAsync(null);
            await vm.OpenTimingRunCommand.ExecuteAsync(new DrawDestination(vm.Competitions.Single(), 1));
            Assert.False(vm.IsError, vm.StatusMessage);
            (typeof(MainViewModel).GetField("_timingTimer", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(vm) as DispatcherTimer)?.Stop();
            var timing = workspace.Timing!;
            var source = new SimulatorTimingSource();
            await timing.StartAsync(source, new("Test", "Synthetic", s_date, Simulation: true), "Test");
            await timing.FollowStartOrderAsync(true);
            await timing.ExpectAsync(0, null);
            await timing.ExpectAsync(1, null);
            var order = timing.Snapshot!.StartOrder.ToArray();
            static long At(int minute, int second) => new TimeOnly(12, 0, 0).Add(TimeSpan.FromSeconds(minute * 60 + second)).Ticks;
            for (var i = 0; i < 6; i++)
            {
                await source.PulseAsync(0, At(0, i * 20));
                var bib = order[i];
                await UntilAsync(() => timing.Snapshot!.Results.Single(x => x.Bib == bib).Status == TimingStatus.OnCourse);
            }
            // The first class finishes completely, then the first competitor of the second class: that class is the active one.
            for (var i = 0; i < 3; i++)
            {
                await source.PulseAsync(1, At(1, 10 + i * 10));
                var bib = order[i];
                await UntilAsync(() => timing.Snapshot!.Results.Single(x => x.Bib == bib).Status == TimingStatus.Finished);
            }
            vm.RefreshTiming();
            Assert.Equal(["N10", "N10", "N10"], vm.RankingRows.Select(x => x.Category));
            await source.PulseAsync(1, At(1, 40));
            await UntilAsync(() => timing.Snapshot!.Results.Single(x => x.Bib == order[3]).Status == TimingStatus.Finished);
            vm.RefreshTiming();
            Assert.Equal(["M10", "N10", "N10", "N10"], vm.RankingRows.Select(x => x.Category));
            await timing.StopAsync();
        }
        finally { Directory.Delete(folder, true); }
    }

    private static async Task UntilAsync(Func<bool> condition, [System.Runtime.CompilerServices.CallerLineNumber] int line = 0)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try { while (!condition()) { await Task.Delay(10, timeout.Token); } }
        catch (TaskCanceledException) { throw new TimeoutException("Condition at line " + line + " was not met."); }
    }

    [Fact]
    public void LiveTotalsAreRankedWithinTheClassAndTheSnapshotCarriesTheClass()
    {
        var at = new DateTimeOffset(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);
        LiveResult Result(int bib, long hundredths) => new(bib, LiveStatus.Finished, hundredths, null, null, at);
        // Bibs 1, 2 are in N10 and 3, 4 in M10. Over the whole race bib 3 has the best total, within N10 it is bib 1.
        var competitors = new[]
        {
            new LiveCompetitor(1, "A", "A", "FIN", "", "", "N10"), new LiveCompetitor(2, "B", "B", "FIN", "", "", "N10"),
            new LiveCompetitor(3, "C", "C", "FIN", "", "", "M10"), new LiveCompetitor(4, "D", "D", "FIN", "", "", "M10"),
        };
        var runs = new[]
        {
            new LiveRun(1, at, [1, 2, 3, 4], [Result(1, 6000), Result(2, 6100), Result(3, 5000), Result(4, 5100)]),
            new LiveRun(2, at, [4, 3, 2, 1], [Result(1, 6000), Result(2, 6000), Result(3, 5000), Result(4, 5500)]),
        };
        var totals = LiveSnapshotMapper.WithTotals(runs, competitors)[1].Results.ToDictionary(x => x.Bib);
        Assert.Equal([1, 2, 1, 2], s_bibs.Select(b => totals[b].TotalRank));
        Assert.Equal<long?>([0, 100, 0, 600], s_bibs.Select(b => totals[b].TotalDifference));
        // Without classes the ranks are those of the whole race.
        var whole = LiveSnapshotMapper.WithTotals(runs)[1].Results.ToDictionary(x => x.Bib);
        Assert.Equal([3, 4, 1, 2], s_bibs.Select(b => whole[b].TotalRank));
        var snapshot = new LiveSnapshot(1, new("Race", "Place", "GS", new(2026, 11, 7), false, "", "A", "", 0, "", 20), competitors, 20,
            Enumerable.Range(1, 20).Select(n => new LiveRun(n, at, [1, 2, 3, 4], [])).ToArray(), at);
        snapshot.Validate();
        Assert.Equal("N10", System.Text.Json.JsonSerializer.Deserialize<LiveSnapshot>(System.Text.Json.JsonSerializer.Serialize(snapshot, LiveJson.Options), LiveJson.Options)!.Competitors[0].Category);
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
