using System.Globalization;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OpenSkiTime.Application;
using OpenSkiTime.Desktop;
using OpenSkiTime.Devices;
using OpenSkiTime.Domain;
using OpenSkiTime.Persistence;
using Xunit;

namespace OpenSkiTime.Tests;

public partial class DesktopWorkflowTests
{
    // The ranking names the current run, shows each intermediate with its rank in this run and, in Run 2, the Run 1 time
    // with the Run 1 rank. Equal times share a rank (competition ranking, as the RK column).
    [AvaloniaFact]
    public async Task RankingShowsCurrentRunIntermediateAndPreviousRunTimesWithSharedRanks()
    {
        var root = Path.Combine(Path.GetTempPath(), "openskitime-ranking-columns", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var file = Path.Combine(root, "Synthetic.ost");
            var cache = new FisLocalStore(root);
            await cache.SaveListAsync(SyntheticFisArchive());
            await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore(), new AlgeDecoderFactory());
            var date = new DateOnly(2026, 9, 27);
            var series = await workspace.CreateAsync(file, new("Synthetic race", "Test slope", "Test club", date, date, "FIN", "2026/27"));
            series = await workspace.SaveCompetitionAsync(null, new("Slalom", "SL", date, Discipline.Slalom, RaceType.Fis, 2, 1, "1234"), series.Revision);
            var competition = series.Competitions[0];
            var revision = series.Revision;
            for (var i = 0; i < 6; i++)
            {
                revision = (await workspace.SaveDeskRowAsync(null, new($"TEST{i:00}", "Athlete", 2000, $"{123456 + i}", "FIN", "Test club", Gender.Female),
                    competition.Id, true, null, revision)).Revision;
            }
            using var vm = new MainViewModel(workspace, new FileDialogsStub { OpenPath = file, NewPath = file, BackupPath = file + ".backup" },
                fisStore: cache, recentSeriesStore: new RecentSeriesStore(root));
            await vm.OpenSeriesCommand.ExecuteAsync(null);
            await vm.OpenDrawRunCommand.ExecuteAsync(new DrawDestination(competition, 1));
            await vm.PrepareDrawCommand.ExecuteAsync(null);
            await vm.OpenTimingRunCommand.ExecuteAsync(new DrawDestination(competition, 1));
            Assert.False(vm.IsError, vm.StatusMessage);
            var window = new MainWindow { DataContext = vm, Width = 1366, Height = 850 };
            window.Show();
            try
            {
                var view = window.FindControl<TimingView>("TimingWorkspace")!;
                var ranking = view.FindControl<DataGrid>("RankingGrid")!;
                UseSimulatorTiming(vm, 2);
                await vm.ConnectTimingCommand.ExecuteAsync(null);
                await vm.OpenTimingRunCommand.ExecuteAsync(new DrawDestination(competition, 1));
                Assert.False(vm.IsError, vm.StatusMessage);
                async Task Resume()
                {
                    foreach (var channel in new[] { "start", "finish", "intermediate:1" }) { await vm.ToggleTimingChannelCommand.ExecuteAsync(channel); }
                }
                async Task Race(int bib, string start, string intermediate, string finish)
                {
                    vm.StartBibText = bib.ToString(CultureInfo.InvariantCulture); await vm.ArmStartCommand.ExecuteAsync(null);
                    vm.SimulationTime = start; await vm.SimulatePulseCommand.ExecuteAsync("start");
                    await WaitTimingAsync(vm, () => vm.OnCourseRows.Any(x => x.Bib == bib));
                    vm.SimulationTime = intermediate; await vm.SimulatePulseCommand.ExecuteAsync("intermediate:1");
                    await WaitTimingAsync(vm, () => vm.TimingRows.Single(x => x.Bib == bib).HasSplits);
                    vm.FinishBibText = bib.ToString(CultureInfo.InvariantCulture); await vm.ArmFinishCommand.ExecuteAsync(null);
                    vm.SimulationTime = finish; await vm.SimulatePulseCommand.ExecuteAsync("finish");
                    await WaitTimingAsync(vm, () => vm.TimingRows.Single(x => x.Bib == bib).Status == "Finished");
                }
                TimingGridRow Ranked(int bib) => vm.RankingRows.Single(x => x.Bib == bib);
                string[] HeaderTexts()
                {
                    window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
                    return ranking.GetVisualDescendants().OfType<DataGridColumnHeader>().Where(x => x.IsVisible)
                        .SelectMany(x => x.GetVisualDescendants().OfType<TextBlock>()).Select(x => x.Text ?? "").ToArray();
                }

                await Resume();
                var a = vm.TimingRows[0].Bib; var b = vm.TimingRows[1].Bib; var c = vm.TimingRows[2].Bib; var d = vm.TimingRows[3].Bib;
                await Race(a, "12:00:00.0000", "12:00:20.0000", "12:01:00.0000");
                await Race(b, "12:02:00.0000", "12:02:20.0000", "12:03:01.0000");
                await Race(c, "12:04:00.0000", "12:04:19.5000", "12:05:00.0000");
                await Race(d, "12:06:00.0000", "12:06:21.0000", "12:07:02.0000");
                Assert.Equal([(1, Math.Max(a, c)), (1, Math.Min(a, c)), (3, b), (4, d)], vm.RankingRows.Select(x => (x.DisplayRank!.Value, x.Bib)));
                Assert.Equal("0:20.00 (2)", Assert.Single(Ranked(a).RankedIntermediates));
                Assert.Equal("0:20.00 (2)", Assert.Single(Ranked(b).RankedIntermediates));
                Assert.Equal("0:19.50 (1)", Assert.Single(Ranked(c).RankedIntermediates));
                Assert.Equal("0:21.00 (4)", Assert.Single(Ranked(d).RankedIntermediates));
                Assert.Equal("1:00.00", Ranked(a).DisplayTime);
                var interm = ranking.Columns.Single(x => x.Tag is "intermediate");
                Assert.True(interm.IsVisible);
                Assert.Equal("INTERM 1", interm.Header);
                Assert.Equal("RUN 1", ranking.Columns.Single(x => x.Tag is "runTime").Header);
                Assert.False(ranking.Columns.Single(x => x.Tag is "previousRun").IsVisible);
                Assert.DoesNotContain(ranking.Columns, x => x.Header is string text && text.Contains("STATE", StringComparison.Ordinal));
                Assert.Contains("RUN 1", HeaderTexts());
                Assert.Contains("INTERM 1", HeaderTexts());
                Assert.Contains(ranking.GetVisualDescendants().OfType<TextBlock>(), x => x.Text == "0:19.50 (1)");
                // The new intermediate column has the same sort controls as the fixed columns; it sorts by time, not text.
                var sort = ranking.GetVisualDescendants().OfType<Button>().Single(x => x.Name == "RankingSort_RankedIntermediates[0]");
                sort.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Dispatcher.UIThread.RunJobs();
                Assert.Equal(c, vm.RankingView.OfType<TimingGridRow>().First().Bib);
                Assert.Equal(d, vm.RankingView.OfType<TimingGridRow>().Last(x => x.Result.Splits.Any(s => s.Hundredths is not null)).Bib);

                foreach (var row in vm.TimingRows.Where(x => x.Result.Status == Timing.TimingStatus.Ready).ToArray())
                {
                    vm.SelectedTimingRow = row; vm.TimingReason = "Synthetic no start";
                    await vm.ClassifyTimingCommand.ExecuteAsync("DNS");
                }
                Assert.True(vm.CanPrepareNextTimedRun);
                await vm.PrepareNextTimedRunCommand.ExecuteAsync(null);
                await vm.PrepareDrawCommand.ExecuteAsync(null);
                Assert.False(vm.IsError, vm.StatusMessage);
                await vm.OpenTimingRunCommand.ExecuteAsync(new DrawDestination(competition, 2));
                Assert.False(vm.IsError, vm.StatusMessage);
                await Resume();
                await Race(c, "13:00:00.0000", "13:00:21.0000", "13:01:01.0000");
                await Race(a, "13:02:00.0000", "13:02:21.0000", "13:03:00.5000");
                // Run 2 ranks by total; Run 1 and the intermediate keep their own ranks, shared on equal times.
                Assert.Equal([(1, a), (2, c)], vm.RankingRows.Where(x => x.DisplayRank is not null).Select(x => (x.DisplayRank!.Value, x.Bib)));
                Assert.Equal("1:00.00 (1)", Ranked(a).PreviousRunRanked);
                Assert.Equal("1:00.00 (1)", Ranked(c).PreviousRunRanked);
                Assert.Equal("0:21.00 (1)", Assert.Single(Ranked(a).RankedIntermediates));
                Assert.Equal("0:21.00 (1)", Assert.Single(Ranked(c).RankedIntermediates));
                Assert.Equal("1:00.50", Ranked(a).DisplayTime);
                var previous = ranking.Columns.Single(x => x.Tag is "previousRun");
                Assert.True(previous.IsVisible);
                Assert.Equal("RUN 1", previous.Header);
                Assert.Equal("RUN 2", ranking.Columns.Single(x => x.Tag is "runTime").Header);
                Assert.True(ranking.Columns.IndexOf(previous) < ranking.Columns.IndexOf(interm));
                Assert.True(ranking.Columns.IndexOf(interm) < ranking.Columns.IndexOf(ranking.Columns.Single(x => x.Tag is "runTime")));
                Assert.Contains("RUN 2", HeaderTexts());
                Assert.Contains("RUN 1", HeaderTexts());
                Assert.Contains(ranking.GetVisualDescendants().OfType<TextBlock>(), x => x.Text == "1:00.00 (1)");
            }
            finally
            {
                if (workspace.Timing?.IsActive == true) { await vm.DisconnectTimingCommand.ExecuteAsync(null); }
                window.Close();
            }
        }
        finally { Directory.Delete(root, true); }
    }
}
