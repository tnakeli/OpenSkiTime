using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OpenSkiTime.Application;
using OpenSkiTime.Desktop;
using OpenSkiTime.Domain;
using OpenSkiTime.Persistence;
using OpenSkiTime.Reporting;
using Xunit;

namespace OpenSkiTime.Tests;

public partial class DesktopWorkflowTests
{
    [AvaloniaFact]
    public async Task PdfFactoryNavigatesGeneratesOpensAndRefreshesAfterSourceAndProfileChanges()
    {
        var root = Path.Combine(Path.GetTempPath(), "OpenSkiTime-PdfUI-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "race.ost");
            await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore());
            var date = new DateOnly(2026, 12, 12);
            await workspace.CreateAsync(path, new("Synthetic series", "Levi", "Test club", date, date, "FIN", "2026/27"),
                [new("Test race", "Levi FIS", date, Discipline.Slalom, RaceType.Club, 3, 0)]);
            var opener = new RecordingPdfOpener();
            using var vm = new MainViewModel(workspace, new FileDialogsStub { OpenPath = path, NewPath = path, BackupPath = path + ".bak" }, recentSeriesStore: new RecentSeriesStore(root), pdfOpener: opener);
            await vm.OpenSeriesCommand.ExecuteAsync(null);
            var window = new MainWindow { DataContext = vm, WindowState = WindowState.Normal, Width = 1280, Height = 820 };
            window.Show();
            var navigation = window.FindControl<Button>("PdfFactoryButton")!;
            PressSettingsControl(window, navigation);
            await vm.ShowPdfFactoryCommand.ExecutionTask!;
            for (var attempt = 0; attempt < 100 && navigation.Flyout is not MenuFlyout { IsOpen: true }; attempt++)
            { Dispatcher.UIThread.RunJobs(); await Task.Delay(10); }
            var competitionMenu = Assert.IsType<MenuFlyout>(navigation.Flyout);
            Assert.True(competitionMenu.IsOpen);
            var clubChoice = Assert.Single(competitionMenu.Items.OfType<MenuItem>());
            Assert.Equal("Levi FIS  ·  ACTIVE", clubChoice.Header);
            Assert.Same(vm.SelectActiveCompetitionCommand, clubChoice.Command);
            competitionMenu.Hide();
            Dispatcher.UIThread.RunJobs();
            Assert.True(vm.IsPdfFactorySection);
            Assert.False(vm.IsFormSection);
            Assert.Equal(15, vm.PdfReports.Count);
            var grid = window.GetVisualDescendants().OfType<DataGrid>().Single(x => x.Name == "PdfReportGrid");
            Assert.Equal(4, grid.Columns.Count);
            Assert.True(grid.Bounds.Width > 800);
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<Button>(), x => Equals(x.Content, "Refresh"));
            var factoryView = window.GetVisualDescendants().OfType<PdfFactoryView>().Single();
            Assert.Empty(factoryView.GetVisualDescendants().OfType<ComboBox>());
            var profile = factoryView.GetVisualDescendants().OfType<Expander>().Single(x => Equals(x.Header, "PDF settings"));
            profile.IsExpanded = true;
            Dispatcher.UIThread.RunJobs();
            var margin = window.GetVisualDescendants().OfType<NumericUpDown>().Single(x => x.Name == "PdfTopMarginInput");
            var input = margin.GetVisualDescendants().OfType<TextBox>().Single();
            Assert.Equal("15", input.Text);
            Assert.True(input.Bounds.Width >= 48, "Margin values must remain visible beside the spinner buttons.");
            var preview = window.GetVisualDescendants().OfType<Button>().Single(x => Equals(x.Content, "Preview"));
            Assert.Same(vm.PreviewPdfProfileCommand, preview.Command);
            vm.PdfTopMargin = 28;
            vm.PdfBottomMargin = 25;
            vm.PdfLeftMargin = 18;
            vm.PdfRightMargin = 22;
            var settingsBeforePreview = await workspace.ReadPdfFactoryAsync();
            var seriesBeforePreview = await workspace.ReadAsync();
            await vm.PreviewPdfProfileCommand.ExecuteAsync(null);
            Assert.False(vm.IsError);
            Assert.False(vm.IsPdfBusy);
            var previewPath = Assert.Single(opener.Paths);
            Assert.Equal(Path.GetTempPath(), Path.GetDirectoryName(previewPath) + Path.DirectorySeparatorChar);
            Assert.Equal(settingsBeforePreview.Profile, (await workspace.ReadPdfFactoryAsync()).Profile);
            Assert.Empty((await workspace.ReadPdfFactoryAsync()).Reports);
            Assert.Equal(seriesBeforePreview.Revision, (await workspace.ReadAsync()).Revision);
            Assert.Equal(28, vm.PdfTopMargin);
            opener.Paths.Clear();
            vm.PdfTopMargin = 15; vm.PdfBottomMargin = 15; vm.PdfLeftMargin = 12; vm.PdfRightMargin = 12;
            profile.IsExpanded = false;
            Assert.Equal(3, vm.PdfReports.Count(x => x.Descriptor.Type == PdfReportType.RefereeReport));
            Assert.All(vm.PdfReports.Where(x => x.Descriptor.Type == PdfReportType.StartList), x => Assert.False(x.CanGenerate));
            var entry = vm.PdfReports[0];
            await entry.GenerateCommand.ExecuteAsync(null);
            Assert.Equal("Generated", vm.PdfReports[0].Status);
            vm.PdfReports[0].OpenCommand.Execute(null);
            Assert.Equal(Path.Combine(root, entry.FileName), Assert.Single(opener.Paths));
            var current = await workspace.ReadAsync();
            await workspace.SaveSeriesAsync(current.Values with { Organizer = "Edited club" }, current.Revision);
            vm.ShowSeriesCommand.Execute(null);
            await workspace.SavePrintProfileAsync(new(TopMm: 20));
            await vm.ShowPdfFactoryCommand.ExecuteAsync(null);
            Assert.Equal(20, vm.PdfTopMargin);
            Assert.Equal("Outdated", vm.PdfReports[0].Status);
            Assert.Equal("Open old", vm.PdfReports[0].OpenLabel);
            Assert.True(vm.PdfReports[0].CanOpen);
            await vm.PdfReports[0].GenerateCommand.ExecuteAsync(null);
            Assert.Equal("Generated", vm.PdfReports[0].Status);
            vm.PdfTopMargin = 25;
            await vm.SavePdfProfileCommand.ExecuteAsync(null);
            Assert.Equal(25, (await workspace.ReadPdfFactoryAsync()).Profile.TopMm);
            Assert.Equal("Outdated", vm.PdfReports[0].Status);
            await vm.GenerateAllPdfsCommand.ExecuteAsync(null);
            Assert.Contains("9 report(s) generated", vm.PdfSummary, StringComparison.Ordinal);
            Assert.Contains("6 unavailable skipped", vm.PdfSummary, StringComparison.Ordinal);
            Assert.False(vm.IsPdfBusy);
            var qa = Environment.GetEnvironmentVariable("OST_PDF_QA_DIRECTORY");
            if (!string.IsNullOrWhiteSpace(qa))
            {
                Dispatcher.UIThread.RunJobs();
                using var screenshot = window.CaptureRenderedFrame();
                screenshot?.Save(Path.Combine(qa, "pdf-factory-ui.png"));
            }
            window.Close();
            vm.Dispose();
            Assert.False(File.Exists(previewPath));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
    [AvaloniaFact]
    public async Task PdfFactoryWaitsForReportOperationsThenSavesPendingTimingMetadataBeforeGeneration()
    {
        var root = Path.Combine(Path.GetTempPath(), "OpenSkiTime-PdfReportFlush-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "race.ost");
            var date = new DateOnly(2026, 12, 12);
            await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore());
            var series = await workspace.CreateAsync(path, new("Synthetic series", "Levi", "Test club", date, date, "FIN", "2026/27"),
                [new("Synthetic race", "Levi FIS", date, Discipline.Downhill, RaceType.Club, 1, 0)]);
            using var vm = new MainViewModel(workspace, new FileDialogsStub { OpenPath = path, NewPath = path, BackupPath = path + ".bak" },
                fisStore: new FisLocalStore(root), recentSeriesStore: new RecentSeriesStore(root), reportDefaultsStore: new(root));
            await vm.OpenSeriesCommand.ExecuteAsync(null);
            await vm.SelectReportCompetitionAsync(series.Competitions[0]);
            vm.IsReportBusy = true;
            vm.ReportLevel = 3;
            await vm.ShowPdfFactoryCommand.ExecuteAsync(null);
            Assert.False(vm.IsPdfFactorySection);
            Assert.True(vm.HasTimingReportEdits);
            Assert.True(vm.IsError);
            vm.IsReportBusy = false;
            await vm.ShowPdfFactoryCommand.ExecuteAsync(null);
            Assert.True(vm.IsPdfFactorySection);
            Assert.False(vm.HasTimingReportEdits);
            Assert.Equal(3, (await workspace.ReadTimingReportAsync(series.Competitions[0].Id))!.Values.Header.TimingLevel);
            var report = vm.PdfReports.Single(x => x.Descriptor.Type == PdfReportType.TimingReport);
            Assert.True(report.CanGenerate);
            await report.GenerateCommand.ExecuteAsync(null);
            Assert.Equal("Generated", vm.PdfReports.Single(x => x.Descriptor.Type == PdfReportType.TimingReport).Status);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
    [AvaloniaFact]
    public async Task OpenRefereeUsesGeneratedFileBesideSeriesOutsideTemporaryDirectory()
    {
        var root = Path.GetFullPath(Path.Combine("artifacts", "pdf-open-tests", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "synthetic.ost");
            var date = new DateOnly(2026, 12, 12);
            await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore());
            await workspace.CreateAsync(path, new("Synthetic PDF path", "Test slope", "Test club", date, date, "FIN", "2026/27"),
                [new("Synthetic race", "SL1", date, Discipline.Slalom, RaceType.Club, 1, 0)]);
            var opener = new RecordingPdfOpener();
            using var vm = new MainViewModel(workspace, new FileDialogsStub { OpenPath = path, NewPath = path, BackupPath = path + ".bak" },
                recentSeriesStore: new(root), pdfOpener: opener);
            await vm.OpenSeriesCommand.ExecuteAsync(null);
            await vm.ShowPdfFactoryCommand.ExecuteAsync(null);
            var referee = vm.PdfReports.Single(row => row.Descriptor.Type == PdfReportType.RefereeReport);
            await referee.GenerateCommand.ExecuteAsync(null);
            referee = vm.PdfReports.Single(row => row.Descriptor.Type == PdfReportType.RefereeReport);
            var expected = Path.Combine(root, referee.FileName);
            Assert.Equal(expected, referee.OutputPath);
            Assert.False(expected.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase));
            var generated = await File.ReadAllBytesAsync(expected);
            referee.OpenCommand.Execute(null);
            Assert.Equal(expected, Assert.Single(opener.Paths));
            Assert.Equal(generated, await File.ReadAllBytesAsync(opener.Paths[0]));
            Assert.Empty(Directory.GetFiles(root, "*.tmp"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
    [AvaloniaFact]
    public async Task PrintProfilePreviewRejectsInvalidMarginsAndRecoversAfterReaderFailure()
    {
        await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore());
        var opener = new RecordingPdfOpener { Fail = true };
        using var vm = new MainViewModel(workspace, new FileDialogsStub { OpenPath = "", NewPath = "", BackupPath = "" }, pdfOpener: opener);
        vm.PdfLeftMargin = 80;
        vm.PdfRightMargin = 80;
        await vm.PreviewPdfProfileCommand.ExecuteAsync(null);
        Assert.True(vm.IsError);
        Assert.Contains("margins", vm.StatusMessage, StringComparison.Ordinal);
        Assert.Empty(opener.Paths);
        Assert.False(vm.IsPdfBusy);
        vm.PdfLeftMargin = 12; vm.PdfRightMargin = 12;
        await vm.PreviewPdfProfileCommand.ExecuteAsync(null);
        Assert.True(vm.IsError);
        Assert.Contains("default PDF reader", vm.StatusMessage, StringComparison.Ordinal);
        Assert.False(vm.IsPdfBusy);
        opener.Fail = false;
        await vm.PreviewPdfProfileCommand.ExecuteAsync(null);
        Assert.False(vm.IsError);
        Assert.False(vm.IsPdfBusy);
        Assert.Empty(vm.PdfReports);
        Assert.False(workspace.IsOpen);
    }
    [AvaloniaFact]
    public async Task UnsavedPdfFactoryShowsSaveInstructionWithoutThrowing()
    {
        await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore());
        using var vm = new MainViewModel(workspace, new FileDialogsStub { OpenPath = "", NewPath = "", BackupPath = "" });
        await vm.ShowPdfFactoryCommand.ExecuteAsync(null);
        Assert.True(vm.IsPdfFactorySection);
        Assert.Contains("Save", vm.PdfSummary, StringComparison.Ordinal);
        Assert.Empty(vm.PdfReports);
        await vm.GenerateAllPdfsCommand.ExecuteAsync(null);
        Assert.False(vm.IsPdfBusy);
    }
    private sealed class RecordingPdfOpener : IPdfOpener
    {
        public List<string> Paths { get; } = [];
        public bool Fail { get; set; }
        public void Open(string path)
        {
            Assert.True(File.Exists(path));
            if (Fail) { throw new InvalidOperationException("Synthetic PDF reader failure"); }
            Paths.Add(path);
        }
    }
}
