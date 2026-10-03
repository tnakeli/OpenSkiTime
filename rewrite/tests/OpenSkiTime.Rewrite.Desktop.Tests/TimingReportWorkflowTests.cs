using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
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
                fisStore: new FisLocalStore(folder), recentSeriesStore: new RecentSeriesStore(folder),
                reportDefaultsStore: new TimingReportDefaultsStore(folder));
            await vm.OpenSeriesCommand.ExecuteAsync(null);
            vm.TimingOperator = "Synthetic operator";
            await vm.SelectReportCompetitionAsync(competition);
            Assert.False(vm.IsError, vm.StatusMessage); Assert.Equal(2, vm.ReportEvidence.Count);
            vm.ReportLevel = 3;
            vm.ReportEvidence[0].HandStart = "12:00:00.12";
            Assert.True(await vm.FlushTimingReportAsync(), vm.StatusMessage);
            Assert.False(vm.IsError, vm.StatusMessage);
            var oldReport = (await workspace.ReadTimingReportAsync(competition.Id))!.Values;
            await workspace.SaveTimingReportAsync(oldReport with
            {
                Runs = oldReport.Runs.Select(r => r with { AllResultsA = true,
                    MissedA = [new TimingReportMissed(999, "Synthetic historical row", "Manual")] }).ToArray()
            }, (await workspace.ReadAsync()).Revision, "Synthetic operator", "Synthetic historical draft", DateTimeOffset.UtcNow);
            await vm.LoadTimingReportCommand.ExecuteAsync(null);
            Assert.Empty(vm.ReportMissed);
            Assert.False(vm.CanAddReportReplacement);
            vm.AddReportReplacementCommand.Execute(null);
            Assert.Empty(vm.ReportMissed);
            var fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "timing-receipt.png");
            var middle = new TimingReportEvidenceRow { Run = 1, Bib = 999,
                AStartStamp = vm.ReportEvidence[0].AStartStamp, AFinishStamp = vm.ReportEvidence[0].AFinishStamp };
            // An unrelated racer with identical A timestamps must not make report
            // first/last receipt matches ambiguous or appear in the dialog.
            vm.ReportEvidence.Add(middle);
            vm.ReportImageRole = TimingReportImageRole.B; vm.ReportImportRun = 1;
            await vm.ImportReportImagesAsync([fixture, fixture]);
            Assert.False(vm.IsError, vm.StatusMessage);
            Assert.Equal(2, vm.ReportImages.Count); Assert.Equal(4, vm.ReportImportPreview.Count(x => x.CanAccept));
            Assert.DoesNotContain(vm.ReportImportPreview, x => x.Bib == 999);
            vm.ReportEvidence.Remove(middle);
            Assert.All(vm.ReportEvidence, x => { Assert.Empty(x.BStart); Assert.Empty(x.BFinish); });
            Assert.Empty(await workspace.ReadTimingReportImagesAsync(competition.Id));
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
                Assert.Null(view.FindControl<Button>("AcceptReportImagesButton"));
                await CaptureReportTabs(window, view);
                view.FindControl<TabControl>("TimingReportTabs")!.SelectedIndex = 1;
                vm.ReportImageOriginalSize = true;
                window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
                var dialog = new TimingReceiptDialog { DataContext = vm };
                dialog.Show(window); dialog.UpdateLayout(); Dispatcher.UIThread.RunJobs();
                var imageScroll = dialog.FindControl<ScrollViewer>("ReportImageScroll")!;
                Assert.True(imageScroll.Extent.Width > imageScroll.Viewport.Width);
                vm.ReportImageOriginalSize = false;
                vm.ReportImportVerified = false;
                dialog.FindControl<Button>("AcceptReportImagesButton")!
                    .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                await ReportUntil(() => !vm.IsReportBusy && vm.ReportImportPreview.Count == 0);
                Assert.False(vm.IsError, vm.StatusMessage);
                Assert.Empty(vm.ReportImages); Assert.Null(vm.ReportImagePreview);
                var accepted = (await workspace.ReadTimingReportAsync(competition.Id))!.Values;
                Assert.Equal(4, accepted.Associations.Length);
                Assert.Equal(3, accepted.Associations.Count(x => x.Role == TimingReportImageRole.B));
                Assert.All(accepted.Associations, x => Assert.True(x.Stamp.Verified));
                Assert.All(accepted.Associations.Where(x => x.Role == TimingReportImageRole.B), x => Assert.Equal("manual", x.Stamp.SourceReference));
                Assert.False(accepted.Reviewed); Assert.False(accepted.CertifyFis);
                Assert.Equal(originalResults, TimingReplay.Restore(await workspace.ReadTimingAsync(list.Id), new AlgeDecoderFactory()).Results.Select(x => x.Hundredths));
                await vm.LoadTimingReportCommand.ExecuteAsync(null);
                Assert.False(vm.IsError, vm.StatusMessage);
                Assert.Equal(2, vm.ReportEvidence.Count(x => x.BStart.Length > 0));
                Assert.Single(vm.ReportEvidence, x => x.BFinish.Length > 0);
                Assert.Equal("12:00:00.12", vm.ReportEvidence[0].HandStart);
                vm.ResetReportImport();
                Assert.Empty(vm.ReportImages); Assert.Null(vm.ReportImagePreview);
                Assert.Empty(vm.ReportImportPreview);
                Assert.Empty(await workspace.ReadTimingReportImagesAsync(competition.Id));
                await vm.ImportReportImagesAsync([fixture]);
                await vm.RebuildReportReceiptMatchesAsync();
                vm.ReportImportVerified = true;
                await vm.AcceptReportImageMatchesCommand.ExecuteAsync(null);
                Assert.False(vm.IsError, vm.StatusMessage);
                Assert.Equal(5, (await workspace.ReadTimingReportAsync(competition.Id))!.Values.Associations.Length);
                var beforeCancel = (await workspace.ReadTimingReportAsync(competition.Id))!.Revision;
                await vm.ImportReportImagesAsync([fixture]);
                var cancelled = new TimingReceiptDialog { DataContext = vm };
                cancelled.Show(window);
                cancelled.Close();
                Assert.Empty(vm.ReportImages); Assert.Empty(vm.ReportImportPreview);
                Assert.Null(vm.ReportImagePreview);
                Assert.Empty(await workspace.ReadTimingReportImagesAsync(competition.Id));
                Assert.Equal(beforeCancel, (await workspace.ReadTimingReportAsync(competition.Id))!.Revision);

                // Clicking a clock opens an empty dialog without a file picker first.
                var readButton = view.GetVisualDescendants().OfType<Button>().First(x => Equals(x.Tag, "B"));
                readButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                Dispatcher.UIThread.RunJobs();
                var emptyDialog = Assert.IsType<TimingReceiptDialog>(Assert.Single(window.OwnedWindows));
                Assert.Empty(vm.ReportImages); Assert.Empty(vm.ReportImportPreview);
                Assert.NotNull(emptyDialog.FindControl<Button>("OpenReceiptImagesButton"));
                Assert.DoesNotContain(emptyDialog.GetVisualDescendants().OfType<TextBox>(),
                    x => x.Watermark is "Operator" or "Reason for change");

                await vm.ImportReportImageBytesAsync(await File.ReadAllBytesAsync(fixture), "Clipboard.png");
                Assert.Equal(4, vm.ReportImportPreview.Count);
                Assert.All(vm.ReportImportPreview, x => Assert.NotEmpty(x.Sample));
                Assert.DoesNotContain(emptyDialog.FindControl<DataGrid>("ReportImportGrid")!.Columns,
                    x => Equals(x.Header, "BIB"));
                var all = emptyDialog.FindControl<CheckBox>("SelectAllReceiptMatches")!;
                all.IsChecked = true; all.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(CheckBox.ClickEvent));
                Assert.All(vm.ReportImportPreview, x => Assert.Equal(x.CanAccept, x.Accept));
                emptyDialog.Close();
                Assert.Empty(vm.ReportImages); Assert.Empty(await workspace.ReadTimingReportImagesAsync(competition.Id));

                vm.ReportRuns[0].AllResultsA = false;
                vm.AddReportReplacementCommand.Execute(null);
                var replacement = Assert.Single(vm.ReportMissed);
                replacement.Bib = plan.Entries[0].Bib; replacement.Reason = "Synthetic photocell alignment";
                replacement.TimeFrom = "System B";
                view.FindControl<TabControl>("TimingReportTabs")!.SelectedIndex = 0;
                window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
                var rowControls = view.FindControl<ItemsControl>("ReportReplacementRows")!;
                var reasonPicker = rowControls.GetVisualDescendants().OfType<ComboBox>()
                    .Single(x => x.ItemsSource == replacement.Reasons);
                reasonPicker.IsDropDownOpen = true; Dispatcher.UIThread.RunJobs();
                Assert.True(reasonPicker.IsDropDownOpen);
                reasonPicker.SelectedItem = "Batteries"; reasonPicker.IsDropDownOpen = false;
                Assert.Equal("Batteries", replacement.Reason);
                replacement.Reason = "Synthetic photocell alignment";
                var sourcePicker = rowControls.GetVisualDescendants().OfType<ComboBox>()
                    .Single(x => Equals(x.SelectedItem, "System B"));
                sourcePicker.SelectedItem = "Manual"; Dispatcher.UIThread.RunJobs();
                Assert.Equal("Manual", replacement.TimeFrom);
                var visualOutput = Environment.GetEnvironmentVariable("OPENSKITIME_REPORT_VISUAL_DIR");
                if (!string.IsNullOrWhiteSpace(visualOutput))
                {
                    using var bitmap = new RenderTargetBitmap(new PixelSize(980, 680), new Vector(96, 96));
                    bitmap.Render(window); bitmap.Save(Path.Combine(visualOutput, "timing-report-replacement.png"));
                }
                Assert.True(await vm.FlushTimingReportAsync(), vm.StatusMessage);
                await vm.LoadTimingReportCommand.ExecuteAsync(null);
                Assert.False(vm.ReportRuns[0].AllResultsA);
                Assert.Equal("Synthetic photocell alignment", Assert.Single(vm.ReportMissed).Reason);
                window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
                rowControls.GetVisualDescendants().OfType<Button>().Single(x => Equals(x.Content, "Remove"))
                    .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                Assert.Empty(vm.ReportMissed);
                vm.AddReportReplacementCommand.Execute(null);
                Assert.Single(vm.ReportMissed);
                vm.ReportRuns[0].AllResultsA = true;
                Assert.Empty(vm.ReportMissed); Assert.False(vm.CanAddReportReplacement);
                Assert.True(await vm.FlushTimingReportAsync(), vm.StatusMessage);
                await vm.LoadTimingReportCommand.ExecuteAsync(null);
                Assert.Empty(vm.ReportMissed);

                vm.EnsureReportSettingsLoaded();
                vm.ReportDefaultChief.LastName = "SYNTHETIC CHIEF";
                vm.SaveTimingReportDefaultsCommand.Execute(null);
                Assert.Contains("SYNTHETIC CHIEF", vm.ReportEquipmentSummary, StringComparison.Ordinal);
                Assert.True(await vm.FlushTimingReportAsync(), vm.StatusMessage);
                Assert.Equal("SYNTHETIC CHIEF", (await workspace.ReadTimingReportAsync(competition.Id))!.Values.Defaults.ChiefOfTiming.LastName);
                vm.ShowTimingReportSettingsCommand.Execute(null);
                Assert.True(vm.IsSettingsSection); Assert.Equal(2, vm.SelectedSettingsTab);
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
         for (var i = 0; i < 2; i++)
         {
            tabs.SelectedIndex = i; window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            using var bitmap = new RenderTargetBitmap(size, new Vector(96, 96));
            bitmap.Render(window); bitmap.Save(Path.Combine(output, $"timing-report-{size.Width}-{i}.png"));
         }
        }
        tabs.SelectedIndex = 1;
        return Task.CompletedTask;
    }
}
