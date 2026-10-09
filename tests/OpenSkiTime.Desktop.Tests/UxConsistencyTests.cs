using System.Globalization;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OpenSkiTime.Application;
using OpenSkiTime.Desktop;
using OpenSkiTime.Devices;
using OpenSkiTime.Persistence;
using OpenSkiTime.Tests.FullRace;
using Xunit;

namespace OpenSkiTime.Tests;

// Regression coverage for the shared UI conventions documented in docs/ux-e2e/UI_AUDIT.md, exercised on every main
// view of the completed synthetic 100-athlete race at the smallest audited window size.
public partial class DesktopWorkflowTests
{
    private static readonly string[] s_actionClasses =
        ["primaryAction", "secondaryAction", "toolbarAction", "dangerAction", "dateAction", "sectionNav", "statusDrop", "activeRace"];

    [AvaloniaFact]
    public async Task EveryMainViewFollowsTheSharedButtonHeaderAndAccentConventions()
    {
        var root = Path.Combine(Path.GetTempPath(), "openskitime-ux-conventions", Guid.NewGuid().ToString("N"));
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
            var window = new MainWindow { DataContext = vm, WindowState = WindowState.Normal, Width = 1280, Height = 800 };
            window.Show();
            try
            {
                // Fluent's accent (check boxes, toggles) is the OpenSkiTime accent, not the Windows blue.
                var app = Avalonia.Application.Current!;
                var accent = ((ISolidColorBrush)app.FindResource("AccentBrush")!).Color;
                Assert.Equal(accent, (Color)app.FindResource("SystemAccentColor")!);

                // Without a series, every series-dependent step is disabled, PDF Factory included.
                CaptureDraw(window, null, "");
                Assert.False(window.FindControl<Button>("PdfFactoryButton")!.IsEffectivelyEnabled);

                await vm.OpenSeriesCommand.ExecuteAsync(null);
                Assert.True(window.FindControl<Button>("PdfFactoryButton")!.IsEffectivelyEnabled);
                var competition = vm.Competitions[0];
                var views = new (string Name, Func<Task> Show)[]
                {
                    ("event series", () => { vm.ShowSeriesCommand.Execute(null); return Task.CompletedTask; }),
                    ("competitions", () => { vm.ShowCompetitionsCommand.Execute(null); vm.SelectedCompetition = competition; return Task.CompletedTask; }),
                    ("competitors", () => { vm.ShowCompetitorsCommand.Execute(null); return Task.CompletedTask; }),
                    ("start list run 1", () => vm.OpenDrawRunCommand.ExecuteAsync(new DrawDestination(competition, 1))),
                    ("start list run 2", () => vm.OpenDrawRunCommand.ExecuteAsync(new DrawDestination(competition, 2))),
                    ("timing run 1", () => vm.OpenTimingRunCommand.ExecuteAsync(new DrawDestination(competition, 1))),
                    ("timing classification", () =>
                    {
                        vm.SelectTimingBibs([outcome.Run1Order[0].Bib], outcome.Run1Order[0].Bib);
                        vm.ShowTimingClassificationEditor = true;
                        return Task.CompletedTask;
                    }),
                    ("results", async () => { vm.ShowTimingClassificationEditor = false; await vm.ShowResultsCommand.ExecuteAsync(null); await vm.LoadResultsCommand.ExecuteAsync(null); }),
                    ("timing report", async () => { await vm.ShowTimingReportCommand.ExecuteAsync(null); await vm.SelectReportCompetitionAsync(competition); }),
                    ("pdf factory", () => vm.ShowPdfFactoryCommand.ExecuteAsync(null)),
                    ("settings", () => { vm.ShowSettingsCommand.Execute(null); return Task.CompletedTask; }),
                };
                foreach (var (name, show) in views)
                {
                    await show();
                    CaptureDraw(window, null, "");
                    AssertConventions(window, name);
                }

                // Timing: ranking headers fit on one line next to their icons; the classification editor's save is primary.
                await vm.OpenTimingRunCommand.ExecuteAsync(new DrawDestination(competition, 1));
                CaptureDraw(window, null, "");
                var timing = window.GetVisualDescendants().OfType<TimingView>().Single();
                var ranking = timing.FindControl<DataGrid>("RankingGrid")!;
                foreach (var column in ranking.Columns.Where(x => x.IsVisible && x.Header is string { Length: > 0 }))
                {
                    var label = column.Header!.ToString()!;
                    Assert.True(column.ActualWidth + 0.5 >= ColumnHeaderControls.RequiredWidth(label),
                        $"Ranking column {label} is {column.ActualWidth}px, needs {ColumnHeaderControls.RequiredWidth(label)}px");
                }
                foreach (var header in ranking.GetVisualDescendants().OfType<TextBlock>().Where(x => x.Text is "RK" or "BIB" or "NAT"))
                { Assert.True(header.Bounds.Height < 20, $"Header {header.Text} wraps ({header.Bounds.Height}px)"); }
                Assert.Equal(2, timing.GetVisualDescendants().OfType<GridSplitter>().Count(x => x.Classes.Contains("paneSplitter") && x.IsEffectivelyVisible));
                Assert.True(ranking.Bounds.Height >= 3 * ranking.RowHeight, "The ranking shows fewer than three rows at 1280×800");
                Assert.Contains("primaryAction", timing.FindControl<Button>("SaveTimingClassification")!.Classes);

                // FIS points use the FIS decimal point regardless of the Windows number format.
                var previous = CultureInfo.CurrentCulture;
                try
                {
                    CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fi-FI");
                    await vm.OpenDrawRunCommand.ExecuteAsync(new DrawDestination(competition, 1));
                    Assert.All(vm.DrawStartListRows.Where(x => x.Entry.Entrant.Points is not null), row =>
                        Assert.Equal(row.Entry.Entrant.Points!.Value.ToString("0.00", CultureInfo.InvariantCulture), row.Points));
                }
                finally { CultureInfo.CurrentCulture = previous; }
            }
            finally { window.Close(); }
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

    private static void AssertConventions(Window window, string view)
    {
        Dispatcher.UIThread.RunJobs();
        // Every visible action button of the views uses one of the shared action styles. Excluded: buttons that belong
        // to a control template (date pickers, spinners) and the compact icon/inline buttons that set their own size.
        var buttons = window.GetVisualDescendants().OfType<Button>()
            .Where(x => x.GetType() == typeof(Button) && x.IsEffectivelyVisible && x.TemplatedParent is null
                && !(x.IsSet(Layoutable.MinHeightProperty) && x.MinHeight < 25)
                && x.FindAncestorOfType<DataGridColumnHeader>() is null)
            .ToArray();
        Assert.NotEmpty(buttons);
        foreach (var button in buttons)
        {
            Assert.True(button.Classes.Any(c => s_actionClasses.Contains(c)),
                $"{view}: button '{button.Content}' ({button.Name}) has no shared action style");
        }
        foreach (var tab in window.GetVisualDescendants().OfType<TabItem>().Where(x => x.IsEffectivelyVisible))
        { Assert.True(tab.FontSize <= 16, $"{view}: tab '{tab.Header}' uses {tab.FontSize}px text"); }
    }
}
