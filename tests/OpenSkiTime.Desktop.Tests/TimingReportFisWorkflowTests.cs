using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
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

// The Timing report tab sends like Results: no separate approval, normal mode by default, a confirmed real send, test mode
// with the reserved-codex simulations, and the FIS answer as a verdict with the raw text behind a button.
public partial class DesktopWorkflowTests
{
    [AvaloniaFact]
    public async Task TimingReportNeverSendsAnIncompleteReportAndKeepsNormalModeAsTheDefault()
    {
        var folder = Path.Combine(Path.GetTempPath(), "openskitime-report-fis", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var file = Path.Combine(folder, "report.ost"); var date = new DateOnly(2026, 10, 3);
            using var handler = new FisHandler(_ => throw new InvalidOperationException("An incomplete report must never be sent."));
            using var http = new HttpClient(handler);
            var token = new ReportSettingsCredential(); token.Save(FisMemberToken);
            await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore(), new AlgeDecoderFactory());
            var values = new CompetitionValues("Synthetic downhill", "DH", date, Discipline.Downhill, RaceType.Fis, 1, 0, "9991");
            var series = await workspace.CreateAsync(file, new("Synthetic report", "Test slope", "Test club", date, date, "FIN", "2026/27"), [values]);
            using var vm = new MainViewModel(workspace, new FileDialogsStub { OpenPath = file, NewPath = file, BackupPath = file + ".backup" },
                fisStore: new FisLocalStore(folder, new ReportSettingsCredential(), token), recentSeriesStore: new RecentSeriesStore(folder),
                reportDefaultsStore: new TimingReportDefaultsStore(folder), submissionHttp: http);
            await vm.OpenSeriesCommand.ExecuteAsync(null);
            vm.TimingOperator = "Synthetic operator";
            await vm.SelectReportCompetitionAsync(series.Competitions[0]);
            Assert.False(vm.IsError, vm.StatusMessage);

            // Normal mode is the default and the file name is known before anything is created.
            Assert.False(vm.FisUploadTestMode); Assert.Equal("Send to FIS", vm.SendToFisLabel);
            Assert.Equal("FIN9991.xml", vm.ReportXmlFileName);
            Assert.Empty(await workspace.ReadApprovedTimingReportsAsync(series.Competitions[0].Id));

            // A real send is confirmed first; after the confirmation the missing fields are listed and nothing leaves.
            await vm.SendTimingReportToFisCommand.ExecuteAsync(null);
            Assert.True(vm.IsReportSendConfirmationOpen); Assert.Empty(handler.Calls);
            vm.CancelSendTimingReportCommand.Execute(null);
            Assert.False(vm.IsReportSendConfirmationOpen);

            // Switching test mode on cancels a pending real send; a confirmation never becomes a real send in test mode.
            await vm.SendTimingReportToFisCommand.ExecuteAsync(null);
            Assert.True(vm.IsReportSendConfirmationOpen);
            vm.FisUploadTestMode = true;
            Assert.False(vm.IsReportSendConfirmationOpen);
            vm.IsReportSendConfirmationOpen = true;
            await vm.ConfirmSendTimingReportCommand.ExecuteAsync(null);
            Assert.False(vm.IsReportSendConfirmationOpen); Assert.Empty(handler.Calls); Assert.Null(vm.ReportSubmissionOutcome);
            vm.FisUploadTestMode = false;
            await vm.SendTimingReportToFisCommand.ExecuteAsync(null);
            await vm.ConfirmSendTimingReportCommand.ExecuteAsync(null);
            var outcome = Assert.IsType<FisUploadOutcome>(vm.ReportSubmissionOutcome);
            Assert.True(outcome.IsError); Assert.Equal("XML not created", outcome.Title); Assert.False(outcome.IsTest); Assert.Equal("LIVE", outcome.Mode);
            Assert.NotEmpty(outcome.Problems); Assert.Equal(outcome.Problems.Count, vm.ReportValidation.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
            Assert.Contains("Nothing was sent", outcome.Hint, StringComparison.Ordinal);
            Assert.Empty(handler.Calls); Assert.False(vm.IsReportBusy); Assert.False(vm.IsReportSubmitting);
            Assert.Empty(await workspace.ReadApprovedTimingReportsAsync(series.Competitions[0].Id));

            // Test mode needs no confirmation but an incomplete report is still never sent.
            vm.FisUploadTestMode = true;
            await vm.SendTimingReportToFisCommand.ExecuteAsync(null);
            Assert.False(vm.IsReportSendConfirmationOpen);
            Assert.True(vm.ReportSubmissionOutcome!.IsTest); Assert.StartsWith("TEST", vm.ReportSubmissionOutcome.Mode);
            Assert.Empty(handler.Calls);

            // Save to XML says what is missing instead of writing a file.
            await vm.SaveTimingReportXmlCommand.ExecuteAsync(null);
            Assert.Contains("Complete the listed fields", vm.ReportStatus, StringComparison.Ordinal);

            // The view shows the verdict, the shared test mode card and the response button; the raw text is hidden.
            var window = new Window { Width = 1280, Height = 900, Content = new TimingReportView { DataContext = vm } };
            window.Show(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            try
            {
                Assert.Equal("XML not created", window.GetVisualDescendants().OfType<TextBlock>().Single(x => x.Name == "FisOutcomeTitle").Text);
                Assert.Same(vm.SaveTimingReportXmlCommand, window.GetVisualDescendants().OfType<Button>().Single(x => x.Name == "SaveTimingReportXmlButton").Command);
                var send = window.GetVisualDescendants().OfType<Button>().Single(x => x.Name == "SendTimingReportToFisButton");
                Assert.Same(vm.SendTimingReportToFisCommand, send.Command); Assert.Equal("Send to FIS · test", send.Content);
                Assert.True(window.GetVisualDescendants().OfType<ComboBox>().Single(x => x.Name == "FisTestScenarioPicker").IsEffectivelyVisible);
                Assert.False(window.GetVisualDescendants().OfType<TextBox>().Single(x => x.Name == "TimingReportFisResponseBox").IsEffectivelyVisible);
                Assert.DoesNotContain(window.GetVisualDescendants().OfType<Button>(), x => Equals(x.Content, "Create approved XML") || Equals(x.Content, "Export approved XML"));
                vm.FisUploadTestMode = false; window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
                Assert.Equal("Send to FIS", send.Content);

                // An answer from FIS reads as a verdict; the raw text appears on request.
                vm.ReportSubmissionOutcome = new FisUploadOutcome(FisUploadTone.Success, "Results accepted by FIS", "FIS has stored and processed the results.", "", false, "LIVE",
                    "00000000-0000-0000-0000-0000000000c3", "FIN9991.xml", "10:00:00", "1 file · 0 queued · 1 processed · 0 rejected",
                    [new("FIN9991.xml", "Processed", FisUploadTone.Success, "File successfully saved!", "AL 2026 · codex 9991", "10:00:30")], [], "{\"uuid\":\"x\"}");
                window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
                Assert.Contains("success", window.GetVisualDescendants().OfType<Border>().Single(x => x.Name == "FisOutcomeBanner").Classes);
                var raw = window.GetVisualDescendants().OfType<TextBox>().Single(x => x.Name == "TimingReportFisResponseBox");
                Assert.False(raw.IsEffectivelyVisible);
                Assert.Equal("View FIS response", vm.ReportFisResponseButtonLabel);
                vm.ToggleReportFisResponseCommand.Execute(null); window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
                Assert.True(raw.IsEffectivelyVisible); Assert.Equal("{\"uuid\":\"x\"}", raw.Text); Assert.Equal("Hide FIS response", vm.ReportFisResponseButtonLabel);
                if (Environment.GetEnvironmentVariable("OPENSKITIME_M7_VISUAL_DIR") is { Length: > 0 } output)
                {
                    Directory.CreateDirectory(output);
                    using var bitmap = new RenderTargetBitmap(new PixelSize(1280, 900), new Vector(96, 96));
                    bitmap.Render(window); bitmap.Save(Path.Combine(output, "timing-report-fis.png"), PngBitmapEncoderOptions.Default);
                }
            }
            finally { window.Close(); }
        }
        finally { Directory.Delete(folder, true); }
    }
}
