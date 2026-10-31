using System.Globalization;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OpenSkiTime.Application;
using OpenSkiTime.Desktop;
using OpenSkiTime.Devices;
using OpenSkiTime.Persistence;
using OpenSkiTime.Tests.FullRace;
using Xunit;

namespace OpenSkiTime.Tests;

public partial class DesktopWorkflowTests
{
    // Renders every main view of the completed synthetic 100-athlete race at the two audited window sizes.
    // Opt-in: set OPENSKITIME_VIEW_COVERAGE to an output directory.
    [AvaloniaFact]
    public async Task GenerateViewCoverageScreenshotsFromFullRace()
    {
        var output = Environment.GetEnvironmentVariable("OPENSKITIME_VIEW_COVERAGE");
        if (string.IsNullOrWhiteSpace(output)) { return; }
        var root = Path.Combine(Path.GetTempPath(), "openskitime-view-coverage", Guid.NewGuid().ToString("N"));
        var race = new SyntheticRace();
        try
        {
            var outcome = await new FullRaceScenario(race, root).RunAsync();
            Assert.Empty(outcome.Discrepancies);
            var cache = new FisLocalStore(root);
            await cache.SaveListAsync(race.PointsListArchive());
            await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore(), new AlgeDecoderFactory());
            using var vm = new MainViewModel(workspace, new FileDialogsStub { OpenPath = outcome.FilePath, NewPath = outcome.FilePath, BackupPath = outcome.FilePath + ".backup" },
                fisStore: cache, recentSeriesStore: new RecentSeriesStore(root),
                categoryRulePresetStore: new CategoryRulePresetStore(root), timingPreferencesStore: new TimingPreferencesStore(root),
                reportDefaultsStore: new TimingReportDefaultsStore(root), timingDeviceCache: new FisTimingDeviceCache(root));
            foreach (var (width, height) in CoverageSizes())
            {
                var folder = Path.Combine(output, $"{width}x{height}");
                var window = new MainWindow { DataContext = vm, WindowState = WindowState.Normal, Width = width, Height = height };
                window.Show();
                try
                {
                    if (workspace.IsOpen) { await vm.CloseSeriesCommand.ExecuteAsync(null); }
                    CaptureDraw(window, folder, "00-no-series.png");
                    await vm.OpenSeriesCommand.ExecuteAsync(null);
                    vm.FileLabel = @"C:\Race office\Synthetic Alpine Cup 2026.ost";
                    vm.ShowSeriesCommand.Execute(null);
                    CaptureDraw(window, folder, "01-event-series.png");
                    vm.ShowCompetitionsCommand.Execute(null);
                    vm.SelectedCompetition = vm.Competitions[0];
                    CaptureDraw(window, folder, "02-competitions.png");
                    vm.ShowCompetitorsCommand.Execute(null);
                    CaptureDraw(window, folder, "03-competitors.png");
                    vm.IsFisPanelOpen = true;
                    CaptureDraw(window, folder, "04-competitors-fis-update.png");
                    vm.IsFisPanelOpen = false;
                    var competition = vm.Competitions[0];
                    await vm.OpenDrawRunCommand.ExecuteAsync(new DrawDestination(competition, 1));
                    CaptureDraw(window, folder, "05-start-list-run1.png");
                    await vm.OpenDrawRunCommand.ExecuteAsync(new DrawDestination(competition, 2));
                    CaptureDraw(window, folder, "06-start-list-run2.png");
                    vm.ShowRunInputCommand.Execute(null);
                    CaptureDraw(window, folder, "07-start-list-run2-run1-results.png");
                    vm.ShowDrawListCommand.Execute(null);
                    await vm.OpenTimingRunCommand.ExecuteAsync(new DrawDestination(competition, 1));
                    CaptureDraw(window, folder, "08-timing-run1.png");
                    vm.SelectTimingBibs([outcome.Run1Order[33].Bib], outcome.Run1Order[33].Bib);
                    vm.ShowTimingClassificationEditor = true;
                    Dispatcher.UIThread.RunJobs();
                    var timingView = window.GetVisualDescendants().OfType<TimingView>().Single();
                    CaptureDialog(timingView.ClassificationDialog!, folder, "09-timing-classification-editor.png");
                    vm.ShowTimingClassificationEditor = false;
                    await vm.OpenTimingRunCommand.ExecuteAsync(new DrawDestination(competition, 2));
                    CaptureDraw(window, folder, "10-timing-run2.png");
                    vm.ShowForerunnerEditor = true;
                    vm.TimingForerunners[0].FirstName = "Synthetic"; vm.TimingForerunners[0].LastName = "Forerunner";
                    vm.AddTimingForerunnerCommand.Execute(null);
                    vm.TimingForerunners[1].FirstName = "Synthetic"; vm.TimingForerunners[1].LastName = "Second";
                    Dispatcher.UIThread.RunJobs();
                    CaptureDialog(timingView.ForerunnerDialog!, folder, "10a-timing-forerunner-editor.png");
                    await vm.SaveTimingForerunnersCommand.ExecuteAsync(null);
                    Assert.False(vm.IsError, vm.StatusMessage);
                    CaptureDraw(window, folder, "10b-timing-forerunners-at-start.png");
                    await vm.ShowResultsCommand.ExecuteAsync(null);
                    await vm.LoadResultsCommand.ExecuteAsync(null);
                    CaptureDraw(window, folder, "11-results.png");
                    await vm.ShowTimingReportCommand.ExecuteAsync(null);
                    await vm.SelectReportCompetitionAsync(competition);
                    var tabs = window.GetVisualDescendants().OfType<TimingReportView>().Single().FindControl<TabControl>("TimingReportTabs")!;
                    tabs.SelectedIndex = 0;
                    CaptureDraw(window, folder, "12-timing-report-overview.png");
                    tabs.SelectedIndex = 1;
                    CaptureDraw(window, folder, "13-timing-report-times.png");
                    await vm.ShowPdfFactoryCommand.ExecuteAsync(null);
                    CaptureDraw(window, folder, "14-pdf-factory.png");
                    vm.ShowSettingsCommand.Execute(null);
                    var settings = window.GetVisualDescendants().OfType<SettingsView>().Single().FindControl<TabControl>("SettingsTabs")!;
                    string[] names = ["fis", "timing-devices", "timing-report", "live-timing", "about"];
                    for (var i = 0; i < names.Length; i++)
                    {
                        settings.SelectedIndex = i;
                        Dispatcher.UIThread.RunJobs();
                        CaptureDraw(window, folder, $"15-settings-{i + 1}-{names[i]}.png");
                    }
                    var dialog = new TimingReceiptDialog { DataContext = vm, Width = width, Height = height };
                    dialog.Show(window);
                    try { CaptureDraw(dialog, folder, "16-receipt-dialog.png"); }
                    finally { dialog.Close(); }
                }
                finally { window.Close(); }
            }
            await workspace.CloseAsync();
        }
        finally
        {
            if (Directory.Exists(root))
            {
                foreach (var item in new DirectoryInfo(root).EnumerateFileSystemInfos("*", SearchOption.AllDirectories)) { item.Attributes &= ~FileAttributes.ReadOnly; }
                new DirectoryInfo(root).Attributes &= ~FileAttributes.ReadOnly;
                Directory.Delete(root, recursive: true);
            }
        }
    }

    // Timing dialogs size their height to the content; capture them at their laid-out size.
    private static void CaptureDialog(Window dialog, string folder, string name)
    {
        dialog.UpdateLayout();
        if (double.IsNaN(dialog.Height)) { dialog.Height = Math.Ceiling(dialog.Bounds.Height); }
        CaptureDraw(dialog, folder, name);
    }

    private static (int Width, int Height)[] CoverageSizes()
    {
        // Optional OPENSKITIME_VIEW_COVERAGE_SIZES, e.g. "1366x768,1280x720": window sizes in device-independent units,
        // so 1920×1080 at 150 % display scaling is 1280x720.
        var sizes = Environment.GetEnvironmentVariable("OPENSKITIME_VIEW_COVERAGE_SIZES");
        if (string.IsNullOrWhiteSpace(sizes)) { return [(1280, 800), (1920, 1080)]; }
        return sizes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(size => size.Split('x', 'X')).Select(parts => (int.Parse(parts[0], CultureInfo.InvariantCulture), int.Parse(parts[1], CultureInfo.InvariantCulture))).ToArray();
    }
}
