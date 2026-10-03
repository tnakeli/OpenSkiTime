using Avalonia.Controls;
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

public partial class DesktopWorkflowTests
{
    [AvaloniaFact]
    public async Task ResultsTimingReportAndPdfFactoryShareCompetitionOnlySelectorAndPreserveTimingRun()
    {
        var folder = Path.Combine(Path.GetTempPath(), "openskitime-active-competition", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var file = Path.Combine(folder, "synthetic.ost");
            var date = new DateOnly(2026, 10, 3);
            var at = new DateTimeOffset(2026, 10, 3, 10, 0, 0, TimeSpan.Zero);
            await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore(), new AlgeDecoderFactory());
            var firstValues = new CompetitionValues("Synthetic slalom one", "SL1", date, Discipline.Slalom, RaceType.Fis, 2, 0, "9991");
            var secondValues = firstValues with { Name = "Synthetic slalom two", ShortLabel = "SL2", FisCode = "9992" };
            var series = await workspace.CreateAsync(file, new("Synthetic selection", "Test slope", "Test club", date, date, "FIN", "2026/27"),
                [firstValues, secondValues]);
            var first = series.Competitions[0];
            var athlete = new CompetitorValues("SYNTHETIC", "Racer", 2000, "900001", "FIN", "Test", Gender.Male);
            var saved = await workspace.SaveDeskRowAsync(null, athlete, first.Id, true, null, series.Revision);
            var plan = FisStartOrder.FirstRun(first.Id, firstValues, Gender.Male, [new(saved.Value.Id, athlete, 10)],
                new("1327", date, date), new(), "active-competition");
            var lists = await workspace.SaveStartListAsync(new(plan, saved.Revision, "Operator", "Synthetic draw", at));
            var runOne = Assert.Single(lists.Revisions);
            lists = await workspace.MarkRunStartedAsync(runOne.Id, lists.SeriesRevision, "Operator", at);
            runOne = Assert.Single(lists.Revisions);
            var secondPlan = FisStartOrder.SecondRun(runOne, [new(saved.Value.Id, FinishStatus.Finished, 6000)]);
            await workspace.SaveStartListAsync(new(secondPlan, lists.SeriesRevision, "Operator", "Synthetic second run", at));
            using var vm = new MainViewModel(workspace, new FileDialogsStub { OpenPath = file, NewPath = file, BackupPath = file + ".backup" },
                fisStore: new FisLocalStore(folder), recentSeriesStore: new(folder), reportDefaultsStore: new(folder), timingDeviceCache: new(folder));
            await vm.OpenSeriesCommand.ExecuteAsync(null);
            await vm.OpenTimingRunCommand.ExecuteAsync(new DrawDestination(first, 2));
            Assert.False(vm.IsError, vm.StatusMessage);
            Assert.True(vm.IsActiveRaceDestination(first.Id, 2, WorkspaceSection.Timing));
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
            window.Show(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            try
            {
                var selector = window.FindControl<Button>("ActiveRaceButton")!;
                Assert.Contains("Run 2", vm.ActiveRaceLabel, StringComparison.Ordinal);
                (await OpenCompetitionNavigationAsync(window, vm, "ResultsMenuButton", "SL1")).Hide();
                Assert.True(vm.IsResultsSection);
                Assert.Equal(first.Id, vm.ResultsCompetition!.Id);
                AssertCompetitionOnlySelection(window, vm, selector, "SL1");
                Assert.DoesNotContain(window.GetVisualDescendants().OfType<ResultsView>().Single()
                    .GetVisualDescendants().OfType<ComboBox>(), box => ReferenceEquals(box.ItemsSource, vm.FisCompetitions));
                (await OpenCompetitionNavigationAsync(window, vm, "TimingReportMenuButton", "SL1")).Hide();
                Assert.True(vm.IsTimingReportSection);
                Assert.Equal(first.Id, vm.ReportCompetition!.Id);
                AssertCompetitionOnlySelection(window, vm, selector, "SL1");
                (await OpenCompetitionNavigationAsync(window, vm, "PdfFactoryButton", "SL1")).Hide();
                Assert.True(vm.IsPdfFactorySection);
                Assert.Equal(first.Id, vm.PdfCompetition!.Id);
                AssertCompetitionOnlySelection(window, vm, selector, "SL1");
                Assert.True(vm.IsActiveRaceDestination(first.Id, 2, WorkspaceSection.Timing));
                await vm.OpenTimingRunCommand.ExecuteAsync(vm.ActiveRaceDestination);
                Assert.Equal(2, vm.TimingRun);
                Assert.Contains("Run 2", vm.ActiveRaceLabel, StringComparison.Ordinal);

                var resultsMenu = await OpenCompetitionNavigationAsync(window, vm, "ResultsMenuButton", "SL1");
                var second = vm.FisCompetitions.Single(x => x.Values.ShortLabel == "SL2");
                var resultsChoice = resultsMenu.Items.OfType<MenuItem>().Single(item => Equals(item.CommandParameter, second));
                resultsChoice.Command!.Execute(resultsChoice.CommandParameter);
                await vm.SelectActiveCompetitionCommand.ExecutionTask!;
                resultsMenu.Hide();
                Assert.True(vm.IsResultsSection);
                Assert.Equal(second.Id, vm.ResultsCompetition!.Id);
                AssertCompetitionOnlySelection(window, vm, selector, "SL2");
                var reportMenu = await OpenCompetitionNavigationAsync(window, vm, "TimingReportMenuButton", "SL2");
                Assert.Equal(second.Id, vm.ReportCompetition!.Id);
                var reportChoice = reportMenu.Items.OfType<MenuItem>().Single(item => item.CommandParameter is CompetitionDetails race && race.Id == first.Id);
                reportChoice.Command!.Execute(reportChoice.CommandParameter);
                await vm.SelectActiveCompetitionCommand.ExecutionTask!;
                reportMenu.Hide();
                Assert.True(vm.IsTimingReportSection);
                Assert.Equal(first.Id, vm.ReportCompetition!.Id);
                var pdfMenu = await OpenCompetitionNavigationAsync(window, vm, "PdfFactoryButton", "SL1");
                var pdfChoice = pdfMenu.Items.OfType<MenuItem>().Single(item => Equals(item.CommandParameter, second));
                pdfChoice.Command!.Execute(pdfChoice.CommandParameter);
                await vm.SelectActiveCompetitionCommand.ExecutionTask!;
                pdfMenu.Hide();
                Assert.True(vm.IsPdfFactorySection);
                Assert.Equal(second.Id, vm.PdfCompetition!.Id);
                AssertCompetitionOnlySelection(window, vm, selector, "SL2");
                Assert.Contains(vm.PdfReports, row => row.FileName.StartsWith("SL2_", StringComparison.Ordinal));
                Assert.DoesNotContain(vm.PdfReports, row => row.FileName.StartsWith("SL1_", StringComparison.Ordinal));
                PressSettingsControl(window, selector);
                var activePdfMenu = Assert.IsType<MenuFlyout>(selector.Flyout);
                var firstChoice = activePdfMenu.Items.OfType<MenuItem>().Single(item => item.CommandParameter is CompetitionDetails race && race.Id == first.Id);
                firstChoice.Command!.Execute(firstChoice.CommandParameter);
                await vm.SelectActiveCompetitionCommand.ExecutionTask!;
                activePdfMenu.Hide();
                Assert.True(vm.IsPdfFactorySection);
                Assert.Equal(first.Id, vm.PdfCompetition!.Id);
                await vm.ShowResultsCommand.ExecuteAsync(null);
                Assert.Equal(first.Id, vm.ResultsCompetition!.Id);
            }
            finally { window.Close(); }
        }
        finally
        {
            if (Path.GetFullPath(folder).StartsWith(Path.Combine(Path.GetTempPath(), "openskitime-active-competition") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            { Directory.Delete(folder, true); }
        }
    }

    private static void AssertCompetitionOnlySelection(Window window, MainViewModel vm, Button selector, string label)
    {
        window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
        Assert.Equal(label + "  ▾", vm.ActiveRaceLabel);
        Assert.Equal(vm.ActiveRaceLabel, selector.Content);
        PressSettingsControl(window, selector);
        var menu = Assert.IsType<MenuFlyout>(selector.Flyout);
        AssertCompetitionMenu(menu, vm, label);
        menu.Hide();
    }

    private static async Task<MenuFlyout> OpenCompetitionNavigationAsync(Window window, MainViewModel vm, string buttonName, string label)
    {
        var button = window.FindControl<Button>(buttonName)!;
        PressSettingsControl(window, button);
        var opening = buttonName switch
        {
            "ResultsMenuButton" => vm.ShowResultsCommand.ExecutionTask,
            "PdfFactoryButton" => vm.ShowPdfFactoryCommand.ExecutionTask,
            _ => vm.ShowTimingReportCommand.ExecutionTask,
        };
        Assert.NotNull(opening);
        await opening;
        for (var attempt = 0; attempt < 100 && button.Flyout is not MenuFlyout { IsOpen: true }; attempt++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }
        window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
        Assert.Contains("active", button.Classes);
        var menu = Assert.IsType<MenuFlyout>(button.Flyout);
        AssertCompetitionMenu(menu, vm, label);
        return menu;
    }

    private static void AssertCompetitionMenu(MenuFlyout menu, MainViewModel vm, string label)
    {
        Assert.True(menu.IsOpen);
        var choices = menu.Items.OfType<MenuItem>().ToArray();
        Assert.Collection(choices, item => Assert.StartsWith("SL1", Assert.IsType<string>(item.Header), StringComparison.Ordinal),
            item => Assert.StartsWith("SL2", Assert.IsType<string>(item.Header), StringComparison.Ordinal));
        Assert.Equal(label + "  ·  ACTIVE", Assert.Single(choices, x => x.Icon is not null).Header);
        Assert.All(choices, item =>
        {
            Assert.Empty(item.Items);
            Assert.Same(vm.SelectActiveCompetitionCommand, item.Command);
            Assert.IsType<CompetitionDetails>(item.CommandParameter);
        });
    }
}
