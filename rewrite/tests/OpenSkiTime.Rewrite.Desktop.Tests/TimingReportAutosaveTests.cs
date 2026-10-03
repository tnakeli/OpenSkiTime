using Avalonia.Controls;
using Avalonia.Headless.XUnit;
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
    public async Task TimingReportAutosavesAcrossCompetitionsAndReadsEachRacesCommittedATiming()
    {
        var folder = Path.Combine(Path.GetTempPath(), "openskitime-report-autosave", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var file = Path.Combine(folder, "autosave.ost");
            var date = new DateOnly(2026, 10, 3);
            await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore(), new AlgeDecoderFactory());
            var series = await workspace.CreateAsync(file, new("Synthetic reports", "Test slope", "Test club", date, date, "FIN", "2026/27"),
                [new("Synthetic A", "DH-A", date, Discipline.Downhill, RaceType.Fis, 1, 0, "9991"),
                 new("Synthetic B", "DH-B", date, Discipline.Downhill, RaceType.Fis, 1, 0, "9992")]);
            var first = series.Competitions.Single(x => x.Values.ShortLabel == "DH-A");
            var second = series.Competitions.Single(x => x.Values.ShortLabel == "DH-B");
            var lists = new List<StartListRevision>();
            var revision = series.Revision;
            foreach (var race in new[] { first, second })
            {
                var index = race.Id == first.Id ? 0 : 1;
                var athlete = new CompetitorValues(index == 0 ? "ALPHARACER" : "BETARACER", "Synthetic", 2000,
                    index == 0 ? "900101" : "900102", "FIN", "Test", Gender.Male);
                var saved = await workspace.SaveDeskRowAsync(null, athlete, race.Id, true, null, revision);
                var plan = FisStartOrder.FirstRun(race.Id, race.Values, Gender.Male, [new(saved.Value.Id, athlete, 10)],
                    new("1327", date, date), new(), "autosave-test");
                var list = Assert.Single((await workspace.SaveStartListAsync(new(plan, saved.Revision, "Operator", "Synthetic draw", DateTimeOffset.UtcNow))).Revisions);
                lists.Add(list);
                var timing = workspace.Timing!;
                await timing.SelectRunAsync(list.Id);
                var simulator = new SimulatorTimingSource();
                await timing.StartAsync(simulator, new("Synthetic", "Test", date, Simulation: true), "Operator");
                var start = TimeSpan.FromHours(index == 0 ? 12 : 14).Ticks;
                await timing.ArmAsync(plan.Entries[0].Bib, null); await simulator.PulseAsync(0, start);
                await ReportUntil(() => timing.Snapshot!.Results[0].Status == TimingStatus.OnCourse);
                await timing.ArmAsync(null, plan.Entries[0].Bib);
                await simulator.PulseAsync(1, start + TimeSpan.FromSeconds(index == 0 ? 60 : 75).Ticks);
                await ReportUntil(() => timing.Snapshot!.Results[0].Status == TimingStatus.Finished);
                await timing.StopAsync();
                revision = (await workspace.ReadAsync()).Revision;
            }
            // The live workspace last displayed B. Report A must restore its own database sources.
            using var vm = new MainViewModel(workspace, new FileDialogsStub { OpenPath = file, NewPath = file, BackupPath = file + ".backup" },
                fisStore: new FisLocalStore(folder), recentSeriesStore: new RecentSeriesStore(folder));
            await vm.OpenSeriesCommand.ExecuteAsync(null);
            vm.TimingOperator = "Synthetic operator";
            await vm.SelectReportCompetitionAsync(first);
            Assert.False(vm.IsError, vm.StatusMessage);
            var a = Assert.Single(vm.ReportEvidence);
            Assert.Contains("ALPHARACER", a.Name, StringComparison.Ordinal);
            Assert.StartsWith("12:00:00", a.AStart, StringComparison.Ordinal); Assert.Equal("1:00.00", a.Net);
            vm.ReportLevel = 3; vm.ReportHandSync = "11:50:00.00";
            a.BStart = "12:00:00.01"; Assert.Single(vm.ReportRuns).Comment = "Alpha notes";
            // Observe the durable write without invoking the drain/save boundary.
            SavedTimingReport? savedA = null;
            for (var attempt = 0; attempt < 200; attempt++)
            {
                savedA = await workspace.ReadTimingReportAsync(first.Id);
                if (savedA?.Values.Runs[0].Comment == "Alpha notes" && !vm.HasTimingReportEdits) { break; }
                await Task.Delay(25);
            }
            Assert.NotNull(savedA);
            Assert.Equal("Alpha notes", savedA.Values.Runs[0].Comment);
            Assert.NotNull(savedA.Values.HandSync); Assert.Equal(3, savedA.Values.Header.TimingLevel);
            Assert.Single(savedA.Values.Associations);
            Assert.False(vm.HasTimingReportEdits);

            a.HandStart = "12:bad";
            Assert.False(await vm.FlushTimingReportAsync());
            Assert.True(vm.HasTimingReportEdits);
            Assert.Equal(savedA.Revision, (await workspace.ReadTimingReportAsync(first.Id))!.Revision);
            await vm.SelectReportCompetitionAsync(second);
            Assert.Equal(first.Id, vm.ReportCompetition!.Id);
            Assert.Equal("12:bad", Assert.Single(vm.ReportEvidence).HandStart);
            a.HandStart = "";
            Assert.True(await vm.FlushTimingReportAsync(), vm.StatusMessage);

            await vm.SelectReportCompetitionAsync(second);
            Assert.False(vm.IsError, vm.StatusMessage);
            var b = Assert.Single(vm.ReportEvidence);
            Assert.Contains("BETARACER", b.Name, StringComparison.Ordinal);
            Assert.StartsWith("14:00:00", b.AStart, StringComparison.Ordinal); Assert.Equal("1:15.00", b.Net);
            Assert.Empty(b.BStart); Assert.Empty(b.HandStart);
            b.HandFinish = "14:01:15.25"; Assert.Single(vm.ReportRuns).Comment = "Beta notes";
            // Switching itself must drain the pending edit; no explicit save or refresh button.
            await vm.SelectReportCompetitionAsync(first);
            Assert.False(vm.IsError, vm.StatusMessage);
            Assert.Equal("Beta notes", (await workspace.ReadTimingReportAsync(second.Id))!.Values.Runs[0].Comment);
            Assert.Equal("12:00:00.01", Assert.Single(vm.ReportEvidence).BStart);
            Assert.Equal("Alpha notes", Assert.Single(vm.ReportRuns).Comment);

            await vm.ShowTimingReportCommand.ExecuteAsync(null);
            Assert.False(vm.IsError, vm.StatusMessage);
            Assert.Equal("1:00.00", Assert.Single(vm.ReportEvidence).Net);
            await workspace.Timing!.SelectRunAsync(lists[0].Id);
            await workspace.Timing.CorrectAsync(new(DecisionKind.Time, CompetitorId: lists[0].Plan.Entries[0].Entrant.CompetitorId, Hundredths: 6100),
                "Synthetic operator", "Verified synthetic correction");
            // Reopening the view reloads committed timing; it is not polled in the background.
            await vm.ShowTimingReportCommand.ExecuteAsync(null);
            await ReportUntil(() => vm.ReportEvidence.Count == 1 && vm.ReportEvidence[0].Net == "1:01.00");
            Assert.False(vm.IsError, vm.StatusMessage);
            Assert.Equal("1:01.00", Assert.Single(vm.ReportEvidence).Net);
            // A saved all-System-A declaration never gains replacement rows automatically.
            Assert.Empty(vm.ReportMissed);
            Assert.True(Assert.Single(vm.ReportRuns).AllResultsA);
            Assert.Equal("12:00:00.01", Assert.Single(vm.ReportEvidence).BStart);
            Assert.True(await vm.FlushTimingReportAsync(), vm.StatusMessage);

            var view = new TimingReportView { DataContext = vm };
            var window = new Window { Width = 1100, Height = 800, Content = view };
            window.Show(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            try
            {
                Assert.DoesNotContain(view.GetVisualDescendants().OfType<Button>(), x => Equals(x.Content, "Save report") || Equals(x.Content, "Refresh A / B"));
                view.FindControl<TabControl>("TimingReportTabs")!.SelectedIndex = 1;
                window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
                var receipt = new TimingReceiptDialog { DataContext = vm }; receipt.Show(window); receipt.UpdateLayout();
                var picker = receipt.FindControl<ComboBox>("ReportImportRunPicker")!;
                Assert.NotNull(picker);
                picker.SelectedIndex = -1; Dispatcher.UIThread.RunJobs();
                Assert.Null(vm.ReportImportRun);
                Assert.False(DataValidationErrors.GetHasErrors(picker));
                vm.ReportImportRun = 1; Dispatcher.UIThread.RunJobs();
                Assert.Equal(1, picker.SelectedItem); receipt.Close();
                Assert.False(DataValidationErrors.GetHasErrors(picker));
            }
            finally { window.Close(); }
            Assert.True(await vm.FlushTimingReportAsync(), vm.StatusMessage);
            Assert.Equal("Alpha notes", (await workspace.ReadTimingReportAsync(first.Id))!.Values.Runs[0].Comment);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }
}
