using System.Globalization;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using OpenSkiTime.Application;
using OpenSkiTime.Desktop;
using OpenSkiTime.Devices;
using OpenSkiTime.Domain;
using OpenSkiTime.Persistence;
using OpenSkiTime.Timing;
using Xunit;

namespace OpenSkiTime.Tests;

// Keyboard and drag actions must change the competitor or impulse the operator saw. Impulses processed between screen
// refreshes, and grid changes caused by a refresh, must never redirect an action to another competitor.
public sealed class TimingActionTargetTests
{
    private static readonly DateOnly s_date = new(2026, 9, 27);
    private static long At(int hour, int minute, int second) => s_date.ToDateTime(new TimeOnly(hour, minute, second)).Ticks
        - s_date.ToDateTime(TimeOnly.MinValue).Ticks;

    [AvaloniaFact]
    public async Task NextStartDnsClassifiesOnlyTheStarterTheScreenShowed()
    {
        var race = await RaceAsync(3);
        try
        {
            var (vm, timing, source, order) = (race.Vm, race.Timing, race.Source, race.Order);
            await timing.ExpectAsync(0, null);
            vm.RefreshTiming();
            Assert.StartsWith(order[0].ToString(CultureInfo.InvariantCulture) + " ·", vm.NextStartLabel, StringComparison.Ordinal);
            // The first starter leaves before the screen refreshes: the queue already expects the second.
            await source.PulseAsync(0, At(12, 0, 0));
            await UntilAsync(() => timing.Snapshot!.Results.Single(x => x.Bib == order[0]).Status == TimingStatus.OnCourse);
            await vm.NextStartDnsCommand.ExecuteAsync(null);
            Assert.True(vm.IsError, vm.StatusMessage);
            Assert.DoesNotContain(timing.Snapshot!.Results, x => x.Status == TimingStatus.DNS);
            vm.RefreshTiming();
            Assert.StartsWith(order[1].ToString(CultureInfo.InvariantCulture) + " ·", vm.NextStartLabel, StringComparison.Ordinal);
            await vm.NextStartDnsCommand.ExecuteAsync(null);
            Assert.False(vm.IsError, vm.StatusMessage);
            Assert.Equal(TimingStatus.DNS, timing.Snapshot!.Results.Single(x => x.Bib == order[1]).Status);
        }
        finally { await race.DisposeAsync(); }
    }

    [AvaloniaFact]
    public async Task IgnoreLastFinishIgnoresTheFinishTheScreenShowed()
    {
        var race = await RaceAsync(2);
        try
        {
            var (vm, timing, source, order) = (race.Vm, race.Timing, race.Source, race.Order);
            await timing.ExpectAsync(0, null);
            await timing.ExpectAsync(1, null);
            await source.PulseAsync(0, At(12, 0, 0));
            await UntilAsync(() => timing.Snapshot!.Results.Single(x => x.Bib == order[0]).Status == TimingStatus.OnCourse);
            await source.PulseAsync(0, At(12, 0, 30));
            await UntilAsync(() => timing.Snapshot!.Results.Single(x => x.Bib == order[1]).Status == TimingStatus.OnCourse);
            await source.PulseAsync(1, At(12, 0, 40)); // a false finish, given to the first racer on course
            await UntilAsync(() => timing.Snapshot!.Results.Single(x => x.Bib == order[0]).Status == TimingStatus.Finished);
            vm.RefreshTiming();
            var shown = timing.Snapshot!.Results.Single(x => x.Bib == order[0]).FinishKey!;
            Assert.StartsWith($"Last finish · Bib {order[0]}", vm.LastFinishLabel, StringComparison.Ordinal);
            // The first racer's real finish arrives before the screen refreshes and goes to the next expected finisher.
            await source.PulseAsync(1, At(12, 1, 0));
            await UntilAsync(() => timing.Snapshot!.Results.Single(x => x.Bib == order[1]).Status == TimingStatus.Finished);
            var later = timing.Snapshot!.Results.Single(x => x.Bib == order[1]).FinishKey!;
            await vm.IgnoreLastFinishCommand.ExecuteAsync(null);
            Assert.False(vm.IsError, vm.StatusMessage);
            var snapshot = timing.Snapshot!;
            Assert.Equal("Ignored", snapshot.Observations.Single(x => x.Observation.Key == shown).State);
            Assert.Equal(order[1], snapshot.Observations.Single(x => x.Observation.Key == later).Bib);
            Assert.Equal(TimingStatus.OnCourse, snapshot.Results.Single(x => x.Bib == order[0]).Status);
        }
        finally { await race.DisposeAsync(); }
    }

    [AvaloniaFact]
    public async Task EndingADragKeepsTheOperatorsSelection()
    {
        var race = await RaceAsync(3);
        try
        {
            var (vm, timing, source, order) = (race.Vm, race.Timing, race.Source, race.Order);
            await timing.ExpectAsync(0, null);
            await source.PulseAsync(0, At(12, 0, 0));
            await UntilAsync(() => timing.Snapshot!.Results.Single(x => x.Bib == order[0]).Status == TimingStatus.OnCourse);
            vm.RefreshTiming();
            var view = new TimingView { DataContext = vm };
            var window = new Window { Width = 1366, Height = 850, Content = view };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var grid = view.FindControl<DataGrid>("TimestampsGrid")!;
            grid.SelectedItem = vm.TimestampRows.Single(x => x.Bib == order[0]);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal([order[0]], vm.SelectedTimingBibs);
            // While dragging, timestamp rows are deferred; new input reorders them when the drag ends.
            vm.IsTimingDragging = true;
            await source.PulseAsync(0, At(12, 0, 30));
            await UntilAsync(() => timing.Snapshot!.Results.Single(x => x.Bib == order[1]).Status == TimingStatus.OnCourse);
            vm.RefreshTiming();
            vm.IsTimingDragging = false;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal([order[0]], vm.SelectedTimingBibs);
            Assert.Equal(order[0], vm.SelectedTimingRow?.Bib);
            window.Close();
        }
        finally { await race.DisposeAsync(); }
    }

    [AvaloniaFact]
    public async Task RefreshDoesNotSelectACompetitorAfterTheOperatorClearedTheSelection()
    {
        var race = await RaceAsync(2);
        try
        {
            var (vm, timing, source, order) = (race.Vm, race.Timing, race.Source, race.Order);
            await timing.ExpectAsync(0, null);
            vm.RefreshTiming();
            vm.SelectTimingBibs([], null); // for example an unassigned impulse row selected in Timestamps
            Assert.Null(vm.SelectedTimingRow);
            await source.PulseAsync(0, At(12, 0, 0));
            await UntilAsync(() => timing.Snapshot!.Results.Single(x => x.Bib == order[0]).Status == TimingStatus.OnCourse);
            vm.RefreshTiming();
            Assert.Null(vm.SelectedTimingRow);
            Assert.Empty(vm.SelectedTimingBibs);
            await vm.ClassifyTimingCommand.ExecuteAsync("DNS");
            Assert.DoesNotContain(timing.Snapshot!.Results, x => x.Status == TimingStatus.DNS);
        }
        finally { await race.DisposeAsync(); }
    }

    [AvaloniaFact]
    public async Task ClassificationEditorRefusesSeveralSelectedCompetitors()
    {
        var race = await RaceAsync(3);
        try
        {
            var (vm, timing, order) = (race.Vm, race.Timing, race.Order);
            vm.RefreshTiming();
            vm.SelectTimingBibs([order[0], order[1]], order[0]);
            vm.TimingClassification = "DNF";
            await vm.SaveTimingClassificationCommand.ExecuteAsync(null);
            Assert.True(vm.IsError, vm.StatusMessage);
            Assert.DoesNotContain(timing.Snapshot!.Results, x => x.Status == TimingStatus.DNF);
        }
        finally { await race.DisposeAsync(); }
    }

    [Fact]
    public void ManualRunOneStatusAcceptsNamesOnly()
    {
        var entry = new StartListEntry(1, 1, new DrawEntrant(Guid.NewGuid(), new("INPUT", "Racer", 2010, "940001", "FIN", "Club", Gender.Female), 10m), "Points order");
        Assert.Throws<DomainValidationException>(() => new ResultInputRow(entry) { Status = "1" }.Read());
        Assert.Throws<DomainValidationException>(() => new ResultInputRow(entry) { Status = " 3 " }.Read());
        Assert.Equal(FinishStatus.DNS, new ResultInputRow(entry) { Status = "dns" }.Read().Status);
        Assert.Equal(5234, new ResultInputRow(entry) { Status = "Finished", Time = "52.34" }.Read().Hundredths);
    }

    [AvaloniaFact]
    public async Task Run2ListStaysExportableAfterARun1ChangeThatLeavesResultsUnchanged()
    {
        var folder = Path.Combine(Path.GetTempPath(), "openskitime-export-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, "race.ost");
            var values = new CompetitionValues("Synthetic slalom", "SL1", s_date, Discipline.Slalom, RaceType.Club, 2, 0);
            CompetitionDetails competition;
            await using (var setup = new SeriesWorkspace(new SqliteSeriesFileStore(), new AlgeDecoderFactory()))
            {
                var series = await setup.CreateAsync(path, new("Synthetic", "Test", "Club", s_date, s_date, "FIN", "2026/27"));
                series = await setup.SaveCompetitionAsync(null, values, series.Revision);
                competition = series.Competitions[0];
                var revision = series.Revision;
                var entrants = new List<DrawEntrant>();
                for (var i = 1; i <= 3; i++)
                {
                    var athlete = new CompetitorValues("EXPORT" + i, "Racer", 2010, (930000 + i).ToString(CultureInfo.InvariantCulture), "FIN", "Club", Gender.Female);
                    var saved = await setup.SaveDeskRowAsync(null, athlete, competition.Id, true, null, revision);
                    revision = saved.Revision; entrants.Add(new(saved.Value.Id, athlete, i * 10m));
                }
                var plan = FisStartOrder.FirstRun(competition.Id, values, Gender.Female, entrants, new("1327", s_date, s_date), new(), "export-seed");
                var first = (await setup.SaveStartListAsync(new(plan, revision, "Test", "Draw", DateTimeOffset.UnixEpoch))).Revisions[0];
                first = (await setup.MarkRunStartedAsync(first.Id, (await setup.ReadAsync()).Revision, "Test", DateTimeOffset.UnixEpoch)).Revisions[0];
                var timing = setup.Timing!;
                await timing.SelectRunAsync(first.Id);
                var source = new SimulatorTimingSource();
                await timing.StartAsync(source, new("Test", "Synthetic", s_date, Simulation: true), "Test");
                await source.PulseAsync(1, At(12, 0, 0)); // a stray finish beam interruption, never assigned
                await UntilAsync(() => timing.Snapshot!.Observations.Count == 1);
                await timing.StopAsync();
                var stray = Assert.Single(timing.Snapshot!.Observations).Observation.Key;
                foreach (var entry in first.Plan.Entries)
                { await timing.CorrectAsync(new(DecisionKind.Time, CompetitorId: entry.Entrant.CompetitorId, Hundredths: 6000 + entry.Bib), "Test", "Synthetic time"); }
                await setup.SaveStartListAsync(new(FisStartOrder.SecondRun(first, timing.Snapshot!.ToRunFinishes()), (await setup.ReadAsync()).Revision,
                    "Test", "Run 2", DateTimeOffset.UnixEpoch, TimingReplay.InputVersion(await setup.ReadTimingAsync(first.Id))));
                await timing.CorrectAsync(new(DecisionKind.Assignment, stray, Ignored: true), "Test", "Stray beam interruption");
            }
            await using var viewWorkspace = new SeriesWorkspace(new SqliteSeriesFileStore(), new AlgeDecoderFactory());
            using var vm = new MainViewModel(viewWorkspace, new Dialogs(path), fisStore: new FisLocalStore(folder), recentSeriesStore: new RecentSeriesStore(folder));
            await vm.OpenSeriesCommand.ExecuteAsync(null);
            await vm.OpenDrawRunCommand.ExecuteAsync(new DrawDestination(vm.Competitions.Single(x => x.Id == competition.Id), 2));
            Assert.False(vm.IsError, vm.StatusMessage);
            Assert.NotNull(vm.DrawRevision);
            Assert.True(vm.CanExportDraw);
        }
        finally { Directory.Delete(folder, true); }
    }
    private static async Task UntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (!condition()) { await Task.Delay(10, timeout.Token); }
    }

    private sealed record Race(string Folder, SeriesWorkspace Workspace, MainViewModel Vm, TimingWorkspace Timing,
        SimulatorTimingSource Source, int[] Order) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Timing.StopAsync();
            Vm.Dispose();
            await Workspace.DisposeAsync();
            Directory.Delete(Folder, true);
        }
    }

    private static async Task<Race> RaceAsync(int count)
    {
        var folder = Path.Combine(Path.GetTempPath(), "openskitime-action-targets-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "race.ost");
        var competitionValues = new CompetitionValues("Synthetic slalom", "SL1", s_date, Discipline.Slalom, RaceType.Club, 2, 0);
        CompetitionDetails competition;
        await using (var setup = new SeriesWorkspace(new SqliteSeriesFileStore(), new AlgeDecoderFactory()))
        {
            var series = await setup.CreateAsync(path, new("Synthetic", "Test", "Club", s_date, s_date, "FIN", "2026/27"));
            series = await setup.SaveCompetitionAsync(null, competitionValues, series.Revision);
            competition = series.Competitions[0];
            var revision = series.Revision;
            var entrants = new List<DrawEntrant>();
            for (var i = 1; i <= count; i++)
            {
                var athlete = new CompetitorValues("TARGET" + i, "Racer", 2010, (950000 + i).ToString(CultureInfo.InvariantCulture), "FIN", "Club", Gender.Female);
                var saved = await setup.SaveDeskRowAsync(null, athlete, competition.Id, true, null, revision);
                revision = saved.Revision; entrants.Add(new(saved.Value.Id, athlete, i * 10m));
            }
            var plan = FisStartOrder.FirstRun(competition.Id, competitionValues, Gender.Female, entrants, new("1327", s_date, s_date), new(), "target-seed");
            await setup.SaveStartListAsync(new(plan, revision, "Test", "Synthetic draw", DateTimeOffset.UnixEpoch));
        }
        var workspace = new SeriesWorkspace(new SqliteSeriesFileStore(), new AlgeDecoderFactory());
        var vm = new MainViewModel(workspace, new Dialogs(path), fisStore: new FisLocalStore(folder), recentSeriesStore: new RecentSeriesStore(folder));
        await vm.OpenSeriesCommand.ExecuteAsync(null);
        await vm.OpenTimingRunCommand.ExecuteAsync(new DrawDestination(vm.Competitions.Single(x => x.Id == competition.Id), 1));
        Assert.False(vm.IsError, vm.StatusMessage);
        // The periodic screen refresh is stopped so each test decides exactly when the screen was last refreshed.
        (typeof(MainViewModel).GetField("_timingTimer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(vm) as DispatcherTimer)?.Stop();
        var timing = workspace.Timing!;
        var source = new SimulatorTimingSource();
        await timing.StartAsync(source, new("Test", "Synthetic", s_date, Simulation: true), "Test");
        await timing.FollowStartOrderAsync(true);
        return new(folder, workspace, vm, timing, source, timing.Snapshot!.StartOrder.ToArray());
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
