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

// Forerunners entered in Timing are the run's forerunners in Results; they wait before the competitors and are
// published live with their progress only.
public partial class DesktopWorkflowTests
{
    [AvaloniaFact]
    public async Task ForerunnersEnteredInTimingAppearInResultsQueueFirstAndCopyToRunTwo()
    {
        var root = Path.Combine(Path.GetTempPath(), "openskitime-forerunner-ui", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var file = Path.Combine(root, "Synthetic.ost");
            var cache = new FisLocalStore(root);
            await cache.SaveListAsync(SyntheticFisArchive());
            await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore(), new AlgeDecoderFactory());
            var date = new DateOnly(2026, 9, 27);
            var series = await workspace.CreateAsync(file, new("Synthetic race", "Test slope", "Test club", date, date, "FIN", "2026/27"));
            series = await workspace.SaveCompetitionAsync(null, new("Slalom", "SL", date, Discipline.Slalom, RaceType.Fis, 2, 0, "1234"), series.Revision);
            var competition = series.Competitions[0];
            var revision = series.Revision;
            for (var i = 0; i < 3; i++)
            {
                revision = (await workspace.SaveDeskRowAsync(null, new($"TEST{i:00}", "Athlete", 2000, $"{123456 + i}", "FIN", "Test club", Gender.Female),
                    competition.Id, true, null, revision)).Revision;
            }
            await SaveCachedFisPointsAsync(workspace, cache);
            using var vm = new MainViewModel(workspace, new FileDialogsStub { OpenPath = file, NewPath = file, BackupPath = file + ".backup" },
                fisStore: cache, recentSeriesStore: new RecentSeriesStore(root));
            await vm.OpenSeriesCommand.ExecuteAsync(null);
            await vm.OpenDrawRunCommand.ExecuteAsync(new DrawDestination(competition, 1));
            await vm.PrepareDrawCommand.ExecuteAsync(null);
            await vm.OpenTimingRunCommand.ExecuteAsync(new DrawDestination(competition, 1));
            Assert.False(vm.IsError, vm.StatusMessage);
            Assert.Equal("No forerunners", vm.ForerunnerSummary);

            // The quick entry opens with forerunner A ready to fill in.
            vm.ShowForerunnerEditor = true;
            var a = Assert.Single(vm.TimingForerunners);
            Assert.Equal("A", a.Letter);
            a.FirstName = "Ada"; a.LastName = "Synthrunner"; a.Nation = "fin";
            vm.AddTimingForerunnerCommand.Execute(null); // new forerunners default to the series nation
            vm.TimingForerunners[1].FirstName = "Bo"; vm.TimingForerunners[1].LastName = "Synthsecond";
            Assert.Equal("B", vm.TimingForerunners[1].Letter);
            await vm.SaveTimingForerunnersCommand.ExecuteAsync(null);
            Assert.False(vm.IsError, vm.StatusMessage);
            Assert.False(vm.ShowForerunnerEditor);
            Assert.Equal("Forerunners: A, B", vm.ForerunnerSummary);

            // They start first (before the competitors), marked as forerunners, and are never ranked.
            // AT START lists the next starter at the bottom.
            Assert.Equal([false, false, false, true, true], vm.AtStartRows.Select(x => x.IsForerunner));
            Assert.Equal("F · A", vm.AtStartRows[^1].BibText);
            Assert.DoesNotContain(vm.RankingRows, x => x.IsForerunner);
            Assert.Equal(3, workspace.Timing!.Snapshot!.Results.Count);

            // The same forerunners are the run's forerunners in Results.
            var saved = (await workspace.ReadRaceInformationAsync(competition.Id))!.Values;
            Assert.Equal([("A", "SYNTHRUNNER", "FIN"), ("B", "SYNTHSECOND", "FIN")],
                saved.Runs.Single(x => x.Number == 1).Forerunners!.Select(x => (x.Letter, x.Person.LastName, x.Person.Nation)));
            await vm.ShowResultsCommand.ExecuteAsync(null);
            await vm.LoadResultsCommand.ExecuteAsync(null);
            Assert.Equal(["A", "B"], vm.ResultsRuns.Single(x => x.Number == 1).Forerunners.Select(x => x.Letter));

            // A wrong identifier is refused without changing the saved forerunners.
            await vm.OpenTimingRunCommand.ExecuteAsync(new DrawDestination(competition, 1));
            vm.ShowForerunnerEditor = true;
            vm.TimingForerunners[1].Letter = "a";
            await vm.SaveTimingForerunnersCommand.ExecuteAsync(null);
            Assert.True(vm.IsError);
            await vm.CancelTimingForerunnersCommand.ExecuteAsync(null);
            Assert.Equal(["A", "B"], vm.TimingForerunners.Select(x => x.Letter));

            // Run 1: both forerunners and one competitor run, the rest do not start; then Run 2 reuses Run 1's forerunners.
            UseSimulatorTiming(vm, 2);
            await vm.ConnectTimingCommand.ExecuteAsync(null);
            Assert.True(vm.IsTimingConnected, vm.StatusMessage);
            foreach (var channel in new[] { "start", "finish" }) { await vm.ToggleTimingChannelCommand.ExecuteAsync(channel); }
            // The first start impulse goes to forerunner A, who runs without a time or a rank.
            vm.SimulationTime = "12:00:00.0000"; await vm.SimulatePulseCommand.ExecuteAsync("start");
            await WaitTimingAsync(vm, () => vm.OnCourseRows.Any(x => x.IsForerunner));
            Assert.Matches(@"^\d+:\d{2}\.\d{2}", vm.OnCourseRows.Single().Clock.Time); // the running time, as for a competitor
            vm.SimulationTime = "12:00:45.0000"; await vm.SimulatePulseCommand.ExecuteAsync("finish");
            await WaitTimingAsync(vm, () => workspace.Timing!.Snapshot!.Forerunners[0].Status == TimingStatus.Finished);
            // The Timing view shows the forerunner's time below the competitors, never with a rank.
            Assert.Equal(4500, workspace.Timing!.Snapshot!.Forerunners[0].Hundredths);
            var timedA = Assert.Single(vm.RankingRows, x => x.IsForerunner);
            Assert.Equal(("F · A", (int?)null, "0:45.00"), (timedA.BibText, timedA.DisplayRank, timedA.DisplayTime));
            // Forerunner B, then the first competitor, who gets a time and the rank.
            async Task Pulse(string time, string channel, Func<bool> done)
            { vm.SimulationTime = time; await vm.SimulatePulseCommand.ExecuteAsync(channel); await WaitTimingAsync(vm, done); }
            var forerunners = () => workspace.Timing!.Snapshot!.Forerunners;
            await Pulse("12:01:00.0000", "start", () => forerunners()[1].Status == TimingStatus.OnCourse);
            await Pulse("12:01:45.0000", "finish", () => forerunners()[1].Status == TimingStatus.Finished);
            await Pulse("12:02:00.0000", "start", () => vm.OnCourseRows.Count == 1);
            await Pulse("12:02:50.0000", "finish", () => vm.RankingRows.Count(x => !x.IsForerunner) == 1);
            Assert.Equal((1, "0:50.00"), (vm.RankingRows[0].DisplayRank, vm.RankingRows[0].DisplayTime));
            Assert.Equal([false, true, true], vm.RankingRows.Select(x => x.IsForerunner));
            Assert.All(workspace.Timing!.Snapshot!.Forerunners, x => Assert.Equal(TimingStatus.Finished, x.Status));
            // A forerunner can be selected and dragged like a competitor to correct its impulses.
            var aKey = workspace.Timing.Snapshot.Forerunners[0].Bib;
            vm.SelectTimingBibs([aKey], aKey);
            Assert.True(vm.SelectedTimingRow?.IsForerunner);
            Assert.NotNull(vm.CreateTimingDrag(aKey));
            // Results cannot remove a forerunner that has impulses: the change is not saved and stays visible.
            await vm.ShowResultsCommand.ExecuteAsync(null);
            await vm.LoadResultsCommand.ExecuteAsync(null);
            var resultsRun = vm.ResultsRuns.Single(x => x.Number == 1);
            var removed = resultsRun.Forerunners.Single(x => x.Letter == "A");
            resultsRun.Forerunners.Remove(removed);
            Assert.False(await vm.FlushRaceInformationAsync());
            Assert.Contains("Not saved", vm.RaceInformationStatus, StringComparison.Ordinal);
            Assert.Equal(["A", "B"], (await workspace.ReadRaceInformationAsync(competition.Id))!.Values.Runs[0].Forerunners!.Select(x => x.Letter));
            resultsRun.Forerunners.Insert(0, removed);
            Assert.True(await vm.FlushRaceInformationAsync());
            await vm.OpenTimingRunCommand.ExecuteAsync(new DrawDestination(competition, 1));
            Assert.Equal(TimingStatus.Finished, workspace.Timing.Snapshot!.Forerunners[0].Status);
            // A forerunner added in Results while the Timing editor is open reaches timing at once; the editor keeps its draft.
            vm.ShowForerunnerEditor = true;
            vm.TimingForerunners[1].FirstName = "Draft";
            await vm.ShowResultsCommand.ExecuteAsync(null);
            await vm.LoadResultsCommand.ExecuteAsync(null);
            vm.ResultsRuns.Single(x => x.Number == 1).Forerunners.Add(new ForerunnerEditor { Letter = "C", FirstName = "Cy", LastName = "Synththird", Nation = "FIN" });
            Assert.True(await vm.FlushRaceInformationAsync());
            Assert.Contains(workspace.Timing.Snapshot!.Forerunners, x => x.ForerunnerLetter == "C");
            Assert.Equal("Draft", vm.TimingForerunners[1].FirstName);
            await vm.CancelTimingForerunnersCommand.ExecuteAsync(null);
            Assert.Equal(["A", "B", "C"], vm.TimingForerunners.Select(x => x.Letter));
            // An invalid unsaved Results edit stops a forerunner save; the Results edit stays and can be corrected.
            var resultsRunOne = vm.ResultsRuns.Single(x => x.Number == 1);
            resultsRunOne.Gates = "not a number";
            vm.ShowForerunnerEditor = true;
            vm.TimingForerunners[2].FirstName = "Unsaved";
            // Reopening the same run (the Timing navigation button) keeps the open draft.
            await vm.OpenTimingRunCommand.ExecuteAsync(new DrawDestination(competition, 1));
            Assert.True(vm.ShowForerunnerEditor);
            Assert.Equal("Unsaved", vm.TimingForerunners[2].FirstName);
            await vm.SaveTimingForerunnersCommand.ExecuteAsync(null);
            Assert.True(vm.IsError);
            Assert.True(vm.ShowForerunnerEditor);
            Assert.Equal("Cy", (await workspace.ReadRaceInformationAsync(competition.Id))!.Values.Runs[0].Forerunners!.Single(x => x.Letter == "C").Person.FirstName);
            resultsRunOne.Gates = "";
            Assert.True(await vm.FlushRaceInformationAsync());
            await vm.CancelTimingForerunnersCommand.ExecuteAsync(null);
            foreach (var row in vm.TimingRows.Where(x => x.Result.Status == TimingStatus.Ready && !x.IsForerunner).ToArray())
            {
                vm.SelectedTimingRow = row; vm.TimingReason = "Synthetic no start";
                await vm.ClassifyTimingCommand.ExecuteAsync("DNS");
            }
            Assert.True(vm.CanPrepareNextTimedRun, vm.StatusMessage);
            await vm.PrepareNextTimedRunCommand.ExecuteAsync(null);
            await vm.PrepareDrawCommand.ExecuteAsync(null);
            Assert.False(vm.IsError, vm.StatusMessage);
            await vm.OpenTimingRunCommand.ExecuteAsync(new DrawDestination(competition, 2));
            Assert.False(vm.IsError, vm.StatusMessage);
            Assert.True(vm.CanCopyForerunnersFromRunOne);
            Assert.Empty(vm.TimingForerunners);
            await vm.CopyForerunnersFromRunOneCommand.ExecuteAsync(null);
            vm.TimingForerunners.Single(x => x.Letter == "B").FirstName = "Changed";
            await vm.SaveTimingForerunnersCommand.ExecuteAsync(null);
            Assert.False(vm.IsError, vm.StatusMessage);
            saved = (await workspace.ReadRaceInformationAsync(competition.Id))!.Values;
            Assert.Equal("Bo", saved.Runs.Single(x => x.Number == 1).Forerunners!.Single(x => x.Letter == "B").Person.FirstName);
            Assert.Equal("Changed", saved.Runs.Single(x => x.Number == 2).Forerunners!.Single(x => x.Letter == "B").Person.FirstName);
            if (workspace.Timing?.IsActive == true) { await vm.DisconnectTimingCommand.ExecuteAsync(null); }
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void GenderLabelFiltersMatchOnlyThemselves()
    {
        Assert.False(ColumnGridController<object>.Matches("Women", "Men"));
        Assert.True(ColumnGridController<object>.Matches("Men", "men"));
        Assert.True(ColumnGridController<object>.Matches("Women", "wom"));
        Assert.True(ColumnGridController<object>.Matches("Synthetic Women's Slalom", "Men"));
    }

    [Fact]
    public void LiveTimingPublishesForerunnerProgressWithoutATime()
    {
        var date = new DateOnly(2026, 9, 27);
        var race = new CompetitionValues("Synthetic slalom", "SL1", date, Discipline.Slalom, RaceType.Fis, 2, 0, "9993");
        var entrants = Enumerable.Range(1, 2).Select(i => new DrawEntrant(new Guid(i, 0, 0, new byte[8]),
            new("LIVE" + i, "Athlete", 2000, (960000 + i).ToString(System.Globalization.CultureInfo.InvariantCulture), "FIN", "Club", Gender.Female), i)).ToArray();
        var plan = FisStartOrder.FirstRun(Guid.NewGuid(), race, Gender.Female, entrants, new("1327", date, date), new(), "live-seed");
        var list = new StartListRevision(Guid.NewGuid(), 1, DateTimeOffset.UnixEpoch, null, "op", "draw", plan);
        var forerunner = new TimingForerunner("A", "Ada", "SYNTHRUNNER", "FIN");
        var session = Guid.NewGuid();
        var observation = new TimingObservation($"{session:N}:1:0", session, 1, "Timy", "fingerprint:1", ObservationKind.Impulse, 0,
            date.ToDateTime(TimeOnly.MinValue).Ticks + TimeSpan.FromHours(10).Ticks, 4, null, false, "clock", "Synthetic impulse");
        TimingAudit[] audit = [new(1, list.Id, DateTimeOffset.UnixEpoch, "op", "assign", new(DecisionKind.Assignment, observation.Key),
            new(DecisionKind.Assignment, observation.Key, Bib: forerunner.Key))];
        var snapshot = TimingEngine.Replay(list, [observation], audit, 0, 1, forerunners: [forerunner]);
        var live = LiveSnapshotMapper.MapRun(list, snapshot, TimeZoneInfo.Utc, DateTimeOffset.UnixEpoch);
        var published = Assert.Single(live.Forerunners!);
        Assert.Equal(new LiveForerunner("A", "SYNTHRUNNER", "Ada", "FIN", LiveStatus.OnCourse), published);
        Assert.Equal(2, live.Results.Length);
        Assert.DoesNotContain(live.Results, x => x.Bib == forerunner.Key);
        Assert.DoesNotContain(forerunner.Key, live.StartOrder);
    }

    [Fact]
    public void AForerunnerClassifiedBeforeAnyImpulseStaysSelectableInTimestamps()
    {
        var date = new DateOnly(2026, 9, 27);
        var race = new CompetitionValues("Synthetic slalom", "SL1", date, Discipline.Slalom, RaceType.Fis, 2, 0, "9993");
        var entrants = Enumerable.Range(1, 2).Select(i => new DrawEntrant(new Guid(i, 0, 0, new byte[8]),
            new("LIVE" + i, "Athlete", 2000, (960000 + i).ToString(System.Globalization.CultureInfo.InvariantCulture), "FIN", "Club", Gender.Female), i)).ToArray();
        var plan = FisStartOrder.FirstRun(Guid.NewGuid(), race, Gender.Female, entrants, new("1327", date, date), new(), "live-seed");
        var list = new StartListRevision(Guid.NewGuid(), 1, DateTimeOffset.UnixEpoch, null, "op", "draw", plan);
        var forerunner = new TimingForerunner("A", "Ada", "SYNTHRUNNER", "FIN");
        var status = new TimingDecision(DecisionKind.Status, CompetitorId: TimingForerunner.IdOf(forerunner.Key));
        TimingAudit[] audit = [new(1, list.Id, DateTimeOffset.UnixEpoch, "op", "Forerunner did not run", status, status with { Status = TimingStatus.DNS })];
        var snapshot = TimingEngine.Replay(list, [], audit, 0, 1, forerunners: [forerunner]);
        Assert.Equal(TimingStatus.DNS, Assert.Single(snapshot.Forerunners).Status);
        var row = Assert.Single(TimingTimestampRow.Create(snapshot, 0));
        Assert.Equal((forerunner.Key, "F · A"), (row.Bib, row.BibText));
        Assert.Contains("DNS", row.Name, StringComparison.Ordinal);
    }
}
