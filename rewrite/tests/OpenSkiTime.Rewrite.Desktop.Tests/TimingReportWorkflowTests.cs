using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using OpenSkiTime.Rewrite.Application;
using OpenSkiTime.Rewrite.Desktop;
using OpenSkiTime.Rewrite.Devices;
using OpenSkiTime.Rewrite.Domain;
using OpenSkiTime.Rewrite.Persistence;
using OpenSkiTime.Rewrite.Timing;
using Xunit;

namespace OpenSkiTime.Rewrite.Tests;

public partial class DesktopWorkflowTests
{
    [AvaloniaFact]
    public async Task TimingReportImportsActualReceiptImagesRequiresReviewAndReopensExactAcceptedValues()
    {
        var folder = Path.Combine(Path.GetTempPath(), "openskitime-report-ui", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var file = Path.Combine(folder, "report.ost");
            var date = new DateOnly(2026, 10, 3);
            await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore(), new AlgeDecoderFactory());
            var values = new CompetitionValues("Synthetic downhill", "DH", date, Discipline.Downhill, RaceType.Fis, 1, 0, "9991");
            var series = await workspace.CreateAsync(file, new("Synthetic report", "Test slope", "Test club", date, date, "FIN", "2026/27"), [values]);
            var competition = series.Competitions[0];
            var revision = series.Revision;
            var entrants = new List<DrawEntrant>();
            for (var i = 1; i <= 2; i++)
            {
                var athlete = new CompetitorValues("SYNTHETIC" + i, "Racer", 2000, "90000" + i, "FIN", "Test", Gender.Male);
                var saved = await workspace.SaveDeskRowAsync(null, athlete, competition.Id, true, null, revision);
                revision = saved.Revision; entrants.Add(new(saved.Value.Id, athlete, i * 10));
            }
            var plan = FisStartOrder.FirstRun(competition.Id, values, Gender.Male, entrants, new("1327", date, date), new(), "report-ui");
            var list = Assert.Single((await workspace.SaveStartListAsync(new(plan, revision, "Operator", "Synthetic draw", DateTimeOffset.UtcNow))).Revisions);
            var timing = workspace.Timing!;
            await timing.SelectRunAsync(list.Id);
            var simulator = new SimulatorTimingSource();
            await timing.StartAsync(simulator, new("Synthetic", "Test", date, Simulation: true), "Operator");
            foreach (var entry in plan.Entries)
            {
                var start = TimeSpan.FromHours(12).Ticks + (entry.Position - 1) * 2 * TimeSpan.TicksPerMinute;
                await timing.ArmAsync(entry.Bib, null); await simulator.PulseAsync(0, start);
                await ReportUntil(() => timing.Snapshot!.Results.Single(x => x.Bib == entry.Bib).Status == TimingStatus.OnCourse);
                await timing.ArmAsync(null, entry.Bib); await simulator.PulseAsync(1, start + TimeSpan.TicksPerMinute);
                await ReportUntil(() => timing.Snapshot!.Results.Single(x => x.Bib == entry.Bib).Status == TimingStatus.Finished);
            }
            await timing.StopAsync();
            var originalResults = timing.Snapshot!.Results.Select(x => x.Hundredths).ToArray();
            using var vm = new MainViewModel(workspace, new FileDialogsStub { OpenPath = file, NewPath = file, BackupPath = file + ".backup" },
                fisStore: new FisLocalStore(folder), recentSeriesStore: new RecentSeriesStore(folder));
            await vm.OpenSeriesCommand.ExecuteAsync(null);
            vm.TimingOperator = "Synthetic operator";
            await vm.SelectReportCompetitionAsync(competition);
            Assert.False(vm.IsError, vm.StatusMessage); Assert.Equal(2, vm.ReportEvidence.Count);
            vm.ReportLevel = 3;
            vm.ReportEvidence[0].HandStart = "12:00:00.12";
            Assert.True(await vm.FlushTimingReportAsync(), vm.StatusMessage);
            Assert.False(vm.IsError, vm.StatusMessage);
            var fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "timing-receipt.png");
            vm.ReportImageRole = TimingReportImageRole.B; vm.ReportImportRun = 1; vm.ReportImageDevice = "Synthetic backup";
            await vm.ImportReportImagesAsync([fixture, fixture]);
            Assert.False(vm.IsError, vm.StatusMessage);
            Assert.Equal(2, vm.ReportImages.Count); Assert.Equal(4, vm.ReportImportPreview.Count(x => x.CanAccept));
            Assert.All(vm.ReportEvidence, x => { Assert.Empty(x.BStart); Assert.Empty(x.BFinish); });
            Assert.Equal(2, (await workspace.ReadTimingReportImagesAsync(competition.Id)).Count);
            await vm.AcceptReportImageMatchesCommand.ExecuteAsync(null);
            Assert.True(vm.IsError); Assert.Contains("confirm", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
            Assert.Single((await workspace.ReadTimingReportAsync(competition.Id))!.Values.Associations);
            vm.ReportImportPreview.Last(x => x.Channel == 1).Accept = false;
            vm.ReportImportVerified = true;

            var window = new Window { Width = 1280, Height = 850, Content = new TimingReportView { DataContext = vm } };
            window.Show(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            try
            {
                var view = (TimingReportView)window.Content;
                Assert.Same(vm.AcceptReportImageMatchesCommand, view.FindControl<Button>("AcceptReportImagesButton")!.Command);
                await CaptureReportTabs(window, view);
                view.FindControl<TabControl>("TimingReportTabs")!.SelectedIndex = 3;
                vm.ReportImageOriginalSize = true;
                window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
                var imageScroll = view.FindControl<ScrollViewer>("ReportImageScroll")!;
                Assert.True(imageScroll.Extent.Width > imageScroll.Viewport.Width);
                vm.ReportImageOriginalSize = false;
                await vm.AcceptReportImageMatchesCommand.ExecuteAsync(null);
                Assert.False(vm.IsError, vm.StatusMessage);
                var accepted = (await workspace.ReadTimingReportAsync(competition.Id))!.Values;
                Assert.Equal(4, accepted.Associations.Length);
                Assert.Equal(3, accepted.Associations.Count(x => x.Role == TimingReportImageRole.B));
                Assert.All(accepted.Associations, x => Assert.True(x.Stamp.Verified));
                Assert.All(accepted.Associations.Where(x => x.Role == TimingReportImageRole.B), x => Assert.StartsWith("image:", x.Stamp.SourceReference, StringComparison.Ordinal));
                Assert.False(accepted.Reviewed); Assert.False(accepted.CertifyFis);
                Assert.Equal(originalResults, TimingReplay.Restore(await workspace.ReadTimingAsync(list.Id), new AlgeDecoderFactory()).Results.Select(x => x.Hundredths));
                await vm.LoadTimingReportCommand.ExecuteAsync(null);
                Assert.False(vm.IsError, vm.StatusMessage);
                Assert.Equal(2, vm.ReportEvidence.Count(x => x.BStart.Length > 0));
                Assert.Single(vm.ReportEvidence, x => x.BFinish.Length > 0);
                Assert.Equal("12:00:00.12", vm.ReportEvidence[0].HandStart);
                Assert.Equal(2, vm.ReportImages.Count);
                await vm.PreviewSavedReportImagesCommand.ExecuteAsync(null);
                vm.ReportImportVerified = true;
                await vm.AcceptReportImageMatchesCommand.ExecuteAsync(null);
                Assert.False(vm.IsError, vm.StatusMessage);
                Assert.Equal(5, (await workspace.ReadTimingReportAsync(competition.Id))!.Values.Associations.Length);
            }
            finally { window.Close(); }
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    private static async Task ReportUntil(Func<bool> condition)
    { for (var i = 0; i < 200 && !condition(); i++) { await Task.Delay(10); } Assert.True(condition()); }

    private static Task CaptureReportTabs(Window window, TimingReportView view)
    {
        var output = Environment.GetEnvironmentVariable("OPENSKITIME_REPORT_VISUAL_DIR");
        if (string.IsNullOrWhiteSpace(output)) { return Task.CompletedTask; }
        Directory.CreateDirectory(output);
        var tabs = view.FindControl<TabControl>("TimingReportTabs")!;
        foreach (var size in new[] { new PixelSize(1280, 850), new PixelSize(980, 680) })
        {
         window.Width = size.Width; window.Height = size.Height;
         for (var i = 0; i < 4; i++)
         {
            tabs.SelectedIndex = i; window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            using var bitmap = new RenderTargetBitmap(size, new Vector(96, 96));
            bitmap.Render(window); bitmap.Save(Path.Combine(output, $"timing-report-{size.Width}-{i}.png"));
         }
        }
        tabs.SelectedIndex = 3;
        return Task.CompletedTask;
    }
}
