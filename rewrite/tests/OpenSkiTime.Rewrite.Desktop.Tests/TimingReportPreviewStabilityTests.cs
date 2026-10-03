using Avalonia.Controls;
using Avalonia.Headless.XUnit;
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
    public async Task TimingReportKeepsImageAndReviewedPreviewVisibleWhenTimingSourcesChange()
    {
        var folder = Path.Combine(Path.GetTempPath(), "openskitime-preview-stability", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var file = Path.Combine(folder, "review.ost");
            var date = new DateOnly(2026, 10, 3);
            await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore(), new AlgeDecoderFactory());
            var series = await workspace.CreateAsync(file, new("Synthetic report", "Test slope", "Test club", date, date, "FIN", "2026/27"),
                [new("Synthetic downhill", "DH", date, Discipline.Downhill, RaceType.Fis, 1, 0, "9991")]);
            var competition = series.Competitions[0];
            var revision = series.Revision;
            var entrants = new List<DrawEntrant>();
            for (var i = 1; i <= 2; i++)
            {
                var athlete = new CompetitorValues("SYNTHETIC" + i, "Racer", 2000, "90000" + i, "FIN", "Test", Gender.Male);
                var saved = await workspace.SaveDeskRowAsync(null, athlete, competition.Id, true, null, revision);
                revision = saved.Revision; entrants.Add(new(saved.Value.Id, athlete, i * 10));
            }
            var plan = FisStartOrder.FirstRun(competition.Id, competition.Values, Gender.Male, entrants,
                new("1327", date, date), new(), "preview-stability");
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
            var png = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "timing-receipt.png"));
            // The first saved image intentionally belongs to the later racer. Selecting the
            // first proposal must not silently replace the operator's chosen first image.
            var first = new TimingReportImage(Guid.NewGuid(), competition.Id, TimingReportImageRole.B, "first.png", "image/png", png,
                "1 C0 12:02:00.01\n2 C1 12:03:00.01", "Synthetic recognized text", DateTimeOffset.UnixEpoch)
                { RunNumber = 1 };
            var second = first with { Id = Guid.NewGuid(), FileName = "second.png", ImportedAt = DateTimeOffset.UnixEpoch.AddSeconds(1),
                RecognizedText = "3 C0 12:00:00.01\n4 C1 12:01:00.01" };
            await workspace.SaveTimingReportImagesAsync([first, second], (await workspace.ReadAsync()).Revision);
            using var vm = new MainViewModel(workspace, new FileDialogsStub { OpenPath = file, NewPath = file, BackupPath = file + ".backup" },
                fisStore: new FisLocalStore(folder), recentSeriesStore: new RecentSeriesStore(folder));
            await vm.OpenSeriesCommand.ExecuteAsync(null);
            vm.TimingOperator = "Synthetic operator";
            await vm.SelectReportCompetitionAsync(competition);
            vm.ReportLevel = 3;
            Assert.True(await vm.FlushTimingReportAsync(), vm.StatusMessage);
            await vm.ShowTimingReportCommand.ExecuteAsync(null);
            vm.ReportImportRun = 1;
            var view = new TimingReportView { DataContext = vm };
            var window = new Window { Width = 1280, Height = 850, Content = view };
            window.Show(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            try
            {
                view.FindControl<TabControl>("TimingReportTabs")!.SelectedIndex = 3;
                window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
                var picker = view.FindControl<ComboBox>("ReportImagePicker")!;
                Assert.NotNull(picker);
                picker.SelectedItem = vm.ReportImages.Single(x => x.Id == first.Id);
                Dispatcher.UIThread.RunJobs();
                await vm.PreviewSavedReportImagesCommand.ExecuteAsync(null);
                Assert.False(vm.IsError, vm.StatusMessage);
                Assert.Equal(first.Id, vm.SelectedReportImage!.Id);
                Assert.Equal(4, vm.ReportImportPreview.Count(x => x.CanAccept));
                var rows = vm.ReportImportPreview.ToArray();
                rows[0].Accept = false;
                vm.SelectedReportImportRow = rows[1];
                Assert.Equal(second.Id, vm.SelectedReportImage!.Id);
                picker.SelectedItem = vm.ReportImages.Single(x => x.Id == first.Id);
                Dispatcher.UIThread.RunJobs();
                var selectedRow = vm.SelectedReportImportRow;
                Assert.NotNull(selectedRow);
                Assert.Equal(first.Id, selectedRow.ImageId);
                vm.ReportImportVerified = true;
                var savedBefore = (await workspace.ReadTimingReportAsync(competition.Id))!;

                // An unused device impulse changes raw storage, but not the reviewed A
                // targets. Keep the proposals and verification usable while connected.
                var noise = new SimulatorTimingSource();
                await workspace.Timing!.SelectRunAsync(list.Id);
                await workspace.Timing.StartAsync(noise, new("Synthetic noise", "Test", date, Simulation: true), "Operator");
                var beforeNoise = workspace.Timing.SavedPackets;
                await noise.PulseAsync(0, TimeSpan.FromHours(13).Ticks);
                await ReportUntil(() => workspace.Timing.SavedPackets > beforeNoise);
                await vm.ShowTimingReportCommand.ExecuteAsync(null);
                Assert.False(vm.ReportImportPreviewStale);
                Assert.True(vm.ReportImportVerified);
                Assert.Same(selectedRow, vm.SelectedReportImportRow);
                Assert.Equal(first.Id, vm.SelectedReportImage!.Id);
                Assert.False(rows[0].Accept);

                await workspace.Timing!.SelectRunAsync(list.Id);
                await workspace.Timing.CorrectAsync(new(DecisionKind.Time, CompetitorId: plan.Entries[0].Entrant.CompetitorId, Hundredths: 6005),
                    "Synthetic operator", "Verified synthetic correction during image review");
                var auxiliary = new SimulatorTimingSource();
                await workspace.Auxiliary!.StartAsync(list.Id, AuxiliaryTimingRole.B, auxiliary, new("Synthetic B", "Test B", date), "Operator");
                await auxiliary.PulseAsync(0, TimeSpan.FromHours(13).Ticks);
                await ReportUntil(() => workspace.Auxiliary.State(AuxiliaryTimingRole.B).SavedPackets == 1);
                await workspace.Auxiliary.StopAsync(AuxiliaryTimingRole.B);
                // Exceed the former one-second polling interval while the report stays
                // visible. Background timing must not replace the reviewer's snapshot.
                await Task.Delay(1500);
                Dispatcher.UIThread.RunJobs();
                Assert.Equal("1:00.00", vm.ReportEvidence.Single(x => x.Bib == plan.Entries[0].Bib).Net);
                Assert.False(vm.ReportImportPreviewStale);
                Assert.True(vm.ReportImportVerified);
                Assert.Equal(rows.Length, vm.ReportImportPreview.Count);
                for (var i = 0; i < rows.Length; i++) { Assert.Same(rows[i], vm.ReportImportPreview[i]); }
                Assert.False(rows[0].Accept);
                Assert.Same(selectedRow, vm.SelectedReportImportRow);
                Assert.Equal(first.Id, vm.SelectedReportImage!.Id);
                await vm.ShowTimingReportCommand.ExecuteAsync(null);
                await ReportUntil(() => vm.ReportImportPreviewStale);
                Assert.Equal("1:00.05", vm.ReportEvidence.Single(x => x.Bib == plan.Entries[0].Bib).Net);
                Assert.Equal(rows.Length, vm.ReportImportPreview.Count);
                for (var i = 0; i < rows.Length; i++) { Assert.Same(rows[i], vm.ReportImportPreview[i]); }
                Assert.False(rows[0].Accept);
                Assert.Same(selectedRow, vm.SelectedReportImportRow);
                Assert.Equal(first.Id, vm.SelectedReportImage!.Id);
                Assert.Equal(first.Id, Assert.IsType<TimingReportImage>(picker.SelectedItem).Id);
                vm.ReportImportVerified = true;
                await vm.AcceptReportImageMatchesCommand.ExecuteAsync(null);
                Assert.True(vm.IsError);
                var rejected = (await workspace.ReadTimingReportAsync(competition.Id))!;
                Assert.Equal(savedBefore.Revision, rejected.Revision);
                Assert.Empty(rejected.Values.Associations);
                Assert.Equal(rows.Length, vm.ReportImportPreview.Count);

                await vm.PreviewSavedReportImagesCommand.ExecuteAsync(null);
                Assert.False(vm.IsError, vm.StatusMessage);
                Assert.False(vm.ReportImportPreviewStale);
                Assert.Equal(first.Id, vm.SelectedReportImage!.Id);
                Assert.Equal(4, vm.ReportImportPreview.Count(x => x.CanAccept));
                var expander = view.FindControl<Expander>("ReportRecognizedTextExpander")!;
                Assert.NotNull(expander); expander.IsExpanded = true;
                window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
                var text = view.FindControl<TextBox>("ReportRecognizedText")!;
                Assert.NotNull(text);
                Assert.Equal(first.RecognizedText, text.Text);
                Assert.True(text.Bounds.Width > 300, $"Recognized text should use the image panel width, actual {text.Bounds.Width}.");
                vm.ReportImportVerified = true;
                await vm.AcceptReportImageMatchesCommand.ExecuteAsync(null);
                Assert.False(vm.IsError, vm.StatusMessage);
                Assert.Equal(4, (await workspace.ReadTimingReportAsync(competition.Id))!.Values.Associations.Length);
            }
            finally { window.Close(); }
        }
        finally { Directory.Delete(folder, recursive: true); }
    }
}
