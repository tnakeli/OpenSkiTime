using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia;
using OpenSkiTime.Application;
using OpenSkiTime.Desktop;
using OpenSkiTime.Devices;
using OpenSkiTime.Domain;
using OpenSkiTime.Persistence;
using OpenSkiTime.Timing;
using Xunit;

namespace OpenSkiTime.Tests;

public partial class DesktopWorkflowTests
{
    private static async Task UntilUiAsync(Func<bool> condition, Func<string>? describe = null)
    {
        for (var i = 0; i < 1000; i++)
        {
            Dispatcher.UIThread.RunJobs();
            if (condition()) { return; }
            await Task.Delay(10);
        }
        Assert.Fail("Timed out. " + describe?.Invoke());
    }

    [AvaloniaFact]
    public async Task EditResultEntersADeviceRecordFinishAndAnEetFromCapturedSystemB()
    {
        var root = Path.Combine(Path.GetTempPath(), "openskitime-edit-result-ui", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var file = Path.Combine(root, "Synthetic-Edit-Result.ost");
            var cache = new FisLocalStore(root);
            await cache.SaveListAsync(SyntheticFisArchive());
            await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore(), new AlgeDecoderFactory());
            var date = new DateOnly(2026, 9, 27);
            var series = await workspace.CreateAsync(file, new("Synthetic race weekend", "Test slope", "Test club", date, date, "FIN", "2026/27"));
            series = await workspace.SaveCompetitionAsync(null, new("Synthetic Slalom", "SL1", date, Discipline.Slalom, RaceType.Fis, 2, 0, "1234"), series.Revision);
            var competition = series.Competitions[0];
            var revision = series.Revision;
            revision = (await workspace.SaveCategoryRuleAsync(null, new("Test 2000", 2000, 2000, null, 0), revision)).Revision;
            for (var i = 0; i < 13; i++)
            {
                revision = (await workspace.SaveDeskRowAsync(null, new($"TEST{i:00}", "Athlete", 2000, $"{200000 + i}", "FIN", "Synthetic club", Gender.Female),
                    competition.Id, true, null, revision)).Revision;
            }
            await SaveCachedFisPointsAsync(workspace, cache);
            var dialogs = new FileDialogsStub { OpenPath = file, NewPath = file, BackupPath = file + ".backup" };
            using var vm = new MainViewModel(workspace, dialogs, fisStore: cache,
                recentSeriesStore: new RecentSeriesStore(root), timingPreferencesStore: new TimingPreferencesStore(root));
            await vm.OpenSeriesCommand.ExecuteAsync(null);
            await vm.OpenDrawRunCommand.ExecuteAsync(new DrawDestination(competition, 1));
            await vm.PrepareDrawCommand.ExecuteAsync(null);
            await vm.OpenTimingRunCommand.ExecuteAsync(new DrawDestination(competition, 1));
            Assert.False(vm.IsError, vm.StatusMessage);

            // Synthetic race: one racer at a time, System B reads every finish 0.0123 s after A. The last two finish
            // impulses never reach the computer; B still has the first of them.
            var timing = workspace.Timing!;
            var listId = timing.ListId!.Value;
            var noon = date.ToDateTime(TimeOnly.MinValue).Ticks + TimeSpan.FromHours(12).Ticks;
            var a = new SimulatorTimingSource();
            var b = new SimulatorTimingSource();
            (typeof(MainViewModel).GetField("_timingTimer", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(vm) as DispatcherTimer)?.Stop();
            await timing.StartAsync(a, new("Simulator", "A", date, Simulation: true), "Operator");
            await timing.FollowStartOrderAsync(true);
            await timing.ExpectAsync(0, null);
            await timing.ExpectAsync(1, null);
            await workspace.Auxiliary!.StartAsync(listId, AuxiliaryTimingRole.B, b, new("Simulator", "B", date, Simulation: true), "Operator");
            for (var i = 0; i < 13; i++)
            {
                await PulseAsync(timing, a, 0, noon + i * TimeSpan.TicksPerMinute);
                var finish = noon + i * TimeSpan.TicksPerMinute + 40 * TimeSpan.TicksPerSecond;
                if (i < 11) { await PulseAsync(timing, a, 1, finish); }
                if (i < 12) { await b.PulseAsync(1, new DateTime(finish + 123_000).TimeOfDay.Ticks); }
            }
            await workspace.Auxiliary.StopAsync(AuxiliaryTimingRole.B);
            await timing.StopAsync();
            vm.RefreshTiming();
            var order = timing.Snapshot!.StartOrder;
            var eetBib = order[11];
            var recordBib = order[12];

            var window = new MainWindow { DataContext = vm, WindowState = WindowState.Normal, Width = 1366, Height = 850 };
            window.Show();
            var view = window.FindControl<TimingView>("TimingWorkspace")!;
            var toggle = view.FindControl<ToggleButton>("EditClassificationToggle")!;
            Assert.Equal("Edit result…", toggle.Content);
            vm.SelectTimingBibs([eetBib], eetBib);
            toggle.IsChecked = true;
            Dispatcher.UIThread.RunJobs();
            var dialog = Assert.IsType<TimingClassificationDialog>(view.ClassificationDialog);
            Assert.Equal("Edit result", dialog.Title);

            // EET tab (Alt+3): the ten racers started before are paired with their B times; B also has the missing finish.
            dialog.KeyPressQwerty(PhysicalKey.Digit3, RawInputModifiers.Alt);
            await UntilUiAsync(() => vm.EetRows.Count == 13, () => vm.EetMessage);
            Assert.Equal(2, vm.TimingResultTab);
            Assert.True(dialog.FindControl<Button>("SaveEet")!.IsVisible);
            Assert.False(dialog.FindControl<Button>("SaveTimingClassification")!.IsVisible);
            // On a short screen the window height is capped: the EET content scrolls and the action bar stays in view.
            var fullHeight = dialog.Bounds.Height;
            dialog.MaxHeight = 560;
            dialog.UpdateLayout(); Dispatcher.UIThread.RunJobs(); dialog.UpdateLayout();
            Assert.True(fullHeight > 560, $"The EET tab is {fullHeight} px tall.");
            var saveEet = dialog.FindControl<Button>("SaveEet")!;
            var bottom = saveEet.TranslatePoint(new Point(0, saveEet.Bounds.Height), dialog)!.Value.Y;
            Assert.True(bottom <= dialog.Bounds.Height, $"Save EET ends at {bottom} px in a {dialog.Bounds.Height} px dialog.");
            Assert.True(dialog.FindControl<ScrollViewer>("EetScroll")!.Extent.Height > dialog.FindControl<ScrollViewer>("EetScroll")!.Viewport.Height);
            dialog.ClearValue(Window.MaxHeightProperty);
            dialog.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            Assert.Equal("12:11:40.0123000", vm.EetBackupText);
            Assert.Equal("10 / 10", vm.EetPairCount);
            Assert.Equal(order.Skip(1).Take(10), vm.EetRows.Where(x => x.Used).Select(x => x.Bib));
            Assert.Equal("+0.0123000", vm.EetCorrection);
            Assert.Equal("12:11:40.0000000", vm.EetResult);
            Assert.Equal("0:40.00", vm.EetNetTime);
            Assert.False(vm.CanSaveEet); // the timing report reason is still missing
            vm.EetReplacementReason = "Wire break";
            Assert.True(vm.CanSaveEet);

            // Leaving a racer out needs a reason; the next earlier racer takes the place.
            var left = vm.EetRows.Single(x => x.Bib == order[10]);
            left.Excluded = true;
            Assert.False(vm.CanSaveEet);
            Assert.Contains($"Bib {order[10]}", vm.EetMessage, StringComparison.Ordinal);
            left.ExcludeReason = "Synthetic: backup cell blocked";
            Assert.True(vm.CanSaveEet);
            // A left-out reference's backup text is not used, so an unreadable value does not block saving.
            left.BackupText = "unreadable";
            Assert.True(vm.CanSaveEet, vm.EetMessage);
            // A captured backup time is removed only by leaving the reference out with a reason, never by clearing it.
            var cleared = vm.EetRows.Single(x => x.Bib == order[9]);
            cleared.BackupText = "";
            Assert.False(vm.CanSaveEet);
            Assert.Contains("leave it out with a reason", vm.EetMessage, StringComparison.Ordinal);
            cleared.BackupText = cleared.PairedText;
            Assert.True(vm.CanSaveEet, vm.EetMessage);
            // Looking at another tab keeps the table and what was typed in it.
            dialog.KeyPressQwerty(PhysicalKey.Digit2, RawInputModifiers.Alt);
            dialog.KeyPressQwerty(PhysicalKey.Digit3, RawInputModifiers.Alt);
            Dispatcher.UIThread.RunJobs();
            Assert.Same(left, vm.EetRows.Single(x => x.Bib == order[10]));
            Assert.True(left.Excluded);
            Assert.True(vm.CanSaveEet, vm.EetMessage);
            // Optional rendered review of the dialog (set OPENSKITIME_EDIT_RESULT_VISUAL_DIR).
            var visual = Environment.GetEnvironmentVariable("OPENSKITIME_EDIT_RESULT_VISUAL_DIR");
            void Capture(string name)
            {
                if (string.IsNullOrWhiteSpace(visual)) { return; }
                Directory.CreateDirectory(visual);
                dialog.UpdateLayout(); Dispatcher.UIThread.RunJobs(); dialog.UpdateLayout();
                using var frame = dialog.CaptureRenderedFrame();
                frame?.Save(Path.Combine(visual, name), PngBitmapEncoderOptions.Default);
            }
            Capture("edit-result-eet.png");
            Assert.Equal(order.Take(10), vm.EetRows.Where(x => x.Used).Select(x => x.Bib));

            // Save PDF before saving the EET prints the calculation shown, marked as a draft.
            Assert.True(dialog.FindControl<Button>("SaveEetPdf")!.IsVisible);
            Assert.True(vm.SaveEetPdfCommand.CanExecute(null));
            dialogs.ExportPath = Path.Combine(root, "eet-draft.pdf");
            await vm.SaveEetPdfCommand.ExecuteAsync(null);
            Assert.False(vm.IsError, vm.StatusMessage);
            Assert.Equal(("Save EET calculation", $"EET-SL1-Run1-Bib{eetBib}-Finish-draft.pdf"), (dialogs.PdfTitle, dialogs.PdfSuggestedName));
            var draftPdf = PdfText(dialogs.ExportPath);
            Assert.Contains("DRAFT - NOT SAVED", draftPdf, StringComparison.Ordinal);
            Assert.Contains("12:11:40.0000000", draftPdf, StringComparison.Ordinal);

            await vm.SaveEetCommand.ExecuteAsync(null);
            Assert.False(vm.IsError, vm.StatusMessage);
            var eetRow = vm.TimingRows.Single(x => x.Bib == eetBib);
            Assert.Equal("0:40.00 e", eetRow.DisplayTime);
            vm.ShowTimingHistory = true;
            Assert.Contains(vm.TimingHistory, h => h.Summary.Contains("EET Finish", StringComparison.Ordinal)
                && h.Summary.Contains($"left out {order[10]}: Synthetic: backup cell blocked", StringComparison.Ordinal));
            var saved = await workspace.ReadTimingAsync(listId);
            var report = TimingReportProjection.FromTiming(saved, timing.Snapshot!);
            Assert.Equal(new TimingReportMissed(eetBib, "Wire break", "System B"), Assert.Single(report.MissedA));

            // After saving, Save PDF prints the saved calculation from the history, with operator and time.
            dialogs.ExportPath = Path.Combine(root, "eet.pdf");
            await vm.SaveEetPdfCommand.ExecuteAsync(null);
            Assert.False(vm.IsError, vm.StatusMessage);
            Assert.Equal($"EET-SL1-Run1-Bib{eetBib}-Finish.pdf", dialogs.PdfSuggestedName);
            if (!string.IsNullOrWhiteSpace(visual)) { File.Copy(dialogs.ExportPath, Path.Combine(visual, "edit-result-eet.pdf"), overwrite: true); }
            var savedPdf = PdfText(dialogs.ExportPath);
            Assert.DoesNotContain("DRAFT", savedPdf, StringComparison.Ordinal);
            foreach (var expected in new[] { "Synthetic race weekend", "Synthetic Slalom", "Codex 1234", "Run 1", "Saved to the result", $"by {vm.TimingOperator}.",
                "12:11:40.0123000", "12:11:40.0000000", "+0.0123000", "0:40.00", "Wire break", $"Left out: Synthetic: backup cell blocked", "System B" })
            { Assert.Contains(expected, savedPdf, StringComparison.Ordinal); }

            // Time of day tab (Alt+2): the finish read from the Timy printout is a System A time, shown without a mark.
            Assert.True(vm.ChooseTimingClassificationCompetitor(recordBib.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            dialog.KeyPressQwerty(PhysicalKey.Digit2, RawInputModifiers.Alt);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(1, vm.TimingResultTab);
            Assert.Equal(MainViewModel.DeviceRecordSourceLabel, vm.TimeEntrySource);
            Assert.Equal("missing", vm.TimeEntryFinish);
            var time = dialog.FindControl<TextBox>("TimeEntryTime")!;
            time.Text = "12:12:40.5";
            Assert.Equal("Invalid: too few decimals", vm.TimeEntryPreview);
            time.Text = "12:12:40.512";
            Assert.Equal("Net 0:40.51", vm.TimeEntryPreview);
            vm.TimeEntryReference = "Printout line 0025";
            Capture("edit-result-time-of-day.png");
            time.Focus();
            dialog.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            await UntilUiAsync(() => vm.TimingRows.Single(x => x.Bib == recordBib).Result.Status == TimingStatus.Finished, () => vm.StatusMessage);
            Assert.False(vm.IsError, vm.StatusMessage);
            Assert.Equal("0:40.51", vm.TimingRows.Single(x => x.Bib == recordBib).DisplayTime);
            Assert.Equal("12:12:40.512 · A · device record", vm.TimeEntryFinish);
            Assert.Contains(vm.TimingHistory, h => h.Summary.Contains("System A Finish", StringComparison.Ordinal)
                && h.Summary.Contains("Printout line 0025", StringComparison.Ordinal));
            dialog.Close();
            window.Close();
        }
        finally
        {
            if (Path.GetFullPath(root).StartsWith(Path.Combine(Path.GetTempPath(), "openskitime-edit-result-ui") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                Directory.Delete(root, true);
            }
        }

        static string PdfText(string path)
        {
            using var pdf = UglyToad.PdfPig.PdfDocument.Open(path);
            return string.Join(" ", pdf.GetPages().SelectMany(x => x.GetWords()).Select(x => x.Text));
        }

        static async Task PulseAsync(TimingWorkspace timing, SimulatorTimingSource source, int channel, long ticks)
        {
            var count = timing.Snapshot!.Observations.Count;
            await source.PulseAsync(channel, new DateTime(ticks).TimeOfDay.Ticks);
            await UntilUiAsync(() => timing.Snapshot!.Observations.Count > count && timing.Snapshot.Observations[^1].State != "Unassigned",
                () => string.Join(" | ", timing.Snapshot!.Observations.Select(x => $"{x.Observation.Channel} {x.State} {x.Observation.Message}")) + " · " + timing.Connection);
        }
    }
}
