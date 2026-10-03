using Avalonia.Headless.XUnit;
using Avalonia.Controls;
using Avalonia.VisualTree;
using OpenSkiTime.Application;
using OpenSkiTime.Desktop;
using OpenSkiTime.Domain;
using OpenSkiTime.Persistence;
using Xunit;

namespace OpenSkiTime.Tests;

public partial class DesktopWorkflowTests
{
    [AvaloniaFact]
    public async Task RaceInformationAutosavesPartialRowsAndInvalidValuesBlockFileChanges()
    {
        var folder = Path.Combine(Path.GetTempPath(), "openskitime-information-autosave-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, "test.ost"); var date = new DateOnly(2026, 10, 2);
            await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore());
            var series = await workspace.CreateAsync(path, new("Test", "Test place", "Club", date, date, "FIN", "2026/27"));
            series = await workspace.SaveCompetitionAsync(null, new("Test", "SL W", date, Discipline.Slalom, RaceType.Fis, 1, 0, "0034"), series.Revision);
            var id = series.Competitions[0].Id;
            using var vm = new MainViewModel(workspace, new FileDialogsStub { NewPath = path, OpenPath = path, BackupPath = path + ".bak" },
                fisStore: new FisLocalStore(folder), recentSeriesStore: new RecentSeriesStore(folder));
            await vm.OpenSeriesCommand.ExecuteAsync(null); await vm.ShowResultsCommand.ExecuteAsync(null); await vm.LoadResultsCommand.ExecuteAsync(null);
            vm.ResultsRuns[0].Snow = "Hard"; vm.ResultsRuns[0].AddForerunnerCommand.Execute(null);
            SavedRaceInformation? saved = null;
            for (var i = 0; i < 30 && saved is null; i++) { await Task.Delay(100); saved = await workspace.ReadRaceInformationAsync(id); }
            Assert.NotNull(saved); Assert.Equal("Hard", saved.Values.Runs[0].Weather!.Snow);
            Assert.Equal("", Assert.Single(saved.Values.Runs[0].Forerunners!).Person.FirstName);
            vm.ResultsRuns[0].Gates = "invalid"; vm.ResultsRuns[0].Snow = "Soft";
            Assert.False(await vm.FlushRaceInformationAsync()); Assert.Contains("Not saved", vm.RaceInformationStatus);
            Assert.Equal("Hard", (await workspace.ReadRaceInformationAsync(id))!.Values.Runs[0].Weather!.Snow);
            await vm.OpenSeriesCommand.ExecuteAsync(null);
            Assert.True(vm.IsError); Assert.Equal("invalid", vm.ResultsRuns[0].Gates);
            vm.ResultsRuns[0].Gates = "45"; Assert.True(await vm.FlushRaceInformationAsync());
            await workspace.BackupAsync(path + ".bak"); await workspace.CloseAsync(); await workspace.OpenAsync(path + ".bak");
            saved = await workspace.ReadRaceInformationAsync(id);
            Assert.Equal("Soft", saved!.Values.Runs[0].Weather!.Snow); Assert.Equal(45, saved.Values.Runs[0].Gates);
        }
        finally { Directory.Delete(folder, true); }
    }

    [AvaloniaFact]
    public async Task SubmissionResponseAndResumeIdStayWithTheSelectedApproval()
    {
        await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore());
        var window = new Window();
        using var vm = new MainViewModel(workspace, new AvaloniaFileDialogs(window));
        var first = new ApprovedResult(Guid.NewGuid(), Guid.NewGuid(), 1, Guid.NewGuid(), null,
            "synthetic", DateTimeOffset.UnixEpoch, "Synthetic TD", "FIN9991.xml", [1], 0m, 0m);
        var second = first with { Id = Guid.NewGuid(), CompetitionId = Guid.NewGuid(), XmlFileName = "FIN9992.xml" };
        Assert.False(vm.CanSendResultsXml);
        vm.SelectedResultApproval = first;
        vm.ResultSubmissionId = "00000000-0000-0000-0000-000000000123";
        vm.ResultSubmissionStatus = "First upload pending"; vm.ResultSubmissionResponse = "First response";
        Assert.True(vm.CanCheckResultsSubmission); Assert.True(vm.CanSendResultsXml);
        vm.IsSubmissionBusy = true; Assert.False(vm.CanSendResultsXml); Assert.False(vm.CanCheckResultsSubmission);
        vm.IsSubmissionBusy = false;
        vm.SelectedResultApproval = second;
        Assert.Equal("", vm.ResultSubmissionId); Assert.Equal("", vm.ResultSubmissionResponse);
        Assert.False(vm.CanCheckResultsSubmission);
        vm.SelectedResultApproval = first;
        Assert.Equal("First upload pending", vm.ResultSubmissionStatus); Assert.Equal("First response", vm.ResultSubmissionResponse);
        Assert.True(vm.CanCheckResultsSubmission);
        vm.SelectedResultApproval = null; Assert.False(vm.CanSendResultsXml);
        Assert.Equal("", vm.ResultSubmissionResponse);
    }

    [AvaloniaFact]
    public async Task RaceInformationCanBePreparedBeforeTimingAndDraftsStayWithTheirCompetition()
    {
        var folder = Path.Combine(Path.GetTempPath(), "openskitime-race-information-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, "test.ost");
            await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore());
            var date = new DateOnly(2026, 10, 2);
            var series = await workspace.CreateAsync(path, new("Test", "Test location", "Test club", date, date, "FIN", "2026/27"));
            series = await workspace.SaveCompetitionAsync(null, new("First", "SL1", date, Discipline.Slalom, RaceType.Fis, 2, 0, "0034", CourseName: "Existing course"), series.Revision);
            series = await workspace.SaveCompetitionAsync(null, new("Second", "SL2", date, Discipline.Slalom, RaceType.Fis, 1, 0, "0035"), series.Revision);
            using var vm = new MainViewModel(workspace, new FileDialogsStub { NewPath = path, OpenPath = path, BackupPath = path + ".bak" },
                fisStore: new FisLocalStore(folder), recentSeriesStore: new RecentSeriesStore(folder));
            await vm.OpenSeriesCommand.ExecuteAsync(null); await vm.ShowResultsCommand.ExecuteAsync(null);
            await vm.LoadResultsCommand.ExecuteAsync(null);
            Assert.Equal(2, vm.ResultsRuns.Count); Assert.Equal("Test location", vm.WeatherLocation);
            var first = vm.ResultsCompetition!;
            vm.ResultsCategory = "FIS";
            var window = new Window { Width = 1366, Height = 900, Content = new ResultsView { DataContext = vm } };
            window.Show(); window.UpdateLayout();
            var jury = window.GetVisualDescendants().OfType<DataGrid>().Single(x => ReferenceEquals(x.ItemsSource, vm.ResultsJury));
            jury.SelectedIndex = 0; jury.CurrentColumn = jury.Columns[2];
            Assert.True(jury.BeginEdit()); window.UpdateLayout();
            jury.GetVisualDescendants().OfType<TextBox>().Single().Text = "Delegate";
            Assert.True(jury.CommitEdit()); Assert.Equal("Delegate", vm.ResultsJury[0].LastName);
            window.Close();
            vm.ResultsJury[0].FirstName = "Test"; vm.ResultsJury[0].Nation = "FIN";
            vm.ResultsRuns[0].AddForerunnerCommand.Execute(null);
            vm.ResultsRuns[0].Forerunners[0].FirstName = "Test"; vm.ResultsRuns[0].Forerunners[0].LastName = "RUNNER";
            vm.ResultsRuns[0].Forerunners[0].Nation = "FIN";
            vm.ResultsRuns[1].Snow = "Hard";
            vm.ResultsCompetition = vm.FisCompetitions.Single(x => x.Id != first.Id);
            await vm.LoadResultsCommand.ExecuteAsync(null);
            Assert.Single(vm.ResultsRuns); Assert.Equal("", vm.ResultsJury[0].LastName);
            vm.ResultsCompetition = first; await vm.LoadResultsCommand.ExecuteAsync(null);
            Assert.Equal("Delegate", vm.ResultsJury[0].LastName); Assert.Equal("Hard", vm.ResultsRuns[1].Snow);
            Assert.True(await vm.FlushRaceInformationAsync());
            Assert.Contains("saved", vm.RaceInformationStatus, StringComparison.OrdinalIgnoreCase);
            var saved = (await workspace.ReadRaceInformationAsync(first.Id))!;
            Assert.Single(saved.Values.Runs[0].Forerunners!); Assert.Equal("Existing course", saved.Values.Runs[0].Course);
            using var fresh = new MainViewModel(workspace, new FileDialogsStub { NewPath = path, OpenPath = path, BackupPath = path + ".bak" },
                fisStore: new FisLocalStore(folder), recentSeriesStore: new RecentSeriesStore(folder));
            await fresh.OpenSeriesCommand.ExecuteAsync(null); await fresh.ShowResultsCommand.ExecuteAsync(null);
            await fresh.LoadResultsCommand.ExecuteAsync(null);
            Assert.Equal("Hard", fresh.ResultsRuns[1].Snow); Assert.Equal("DELEGATE", fresh.ResultsJury[0].LastName);
        }
        finally { Directory.Delete(folder, true); }
    }

    [AvaloniaFact]
    public void ForecastSelectionAndFisSuggestionsPreserveManualValues()
    {
        var run = new RaceRunEditor(new(1, new("Test", "Setter", "FIN"), Course: "Manual course", Weather: new("Manual condition", "Hard", 0m, -1m)));
        run.FillEmpty("Course", "Imported course"); run.FillEmpty("Start altitude", "498 m");
        run.ApplyForecast(new(new DateTime(2026, 10, 2, 10, 0, 0), "Europe/Helsinki", 3.5m, 3, "Synthetic source", DateTimeOffset.UnixEpoch));
        Assert.Equal("Manual course", run.Course); Assert.Equal("498", run.StartAltitude);
        Assert.Equal("Manual condition", run.Conditions); Assert.Equal("Hard", run.Snow);
        Assert.Equal(0m, run.Values.Weather!.StartTemperature); Assert.Equal(-1m, run.Values.Weather.FinishTemperature);
        Assert.Contains("3.5 °C", run.WeatherSource, StringComparison.Ordinal);
        run.AddForerunnerCommand.Execute(null); run.AddForerunnerCommand.Execute(null);
        Assert.Equal("A", run.Forerunners[0].Letter); Assert.Equal("B", run.Forerunners[1].Letter);
        run.SelectedForerunner = run.Forerunners[0]; run.RemoveForerunnerCommand.Execute(null);
        Assert.Equal("B", Assert.Single(run.Forerunners).Letter);
    }

    [AvaloniaFact]
    public async Task WeatherRunButtonUsesTheSelectedForecastHour()
    {
        await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore());
        var window = new Window { Width = 1366, Height = 900 };
        using var vm = new MainViewModel(workspace, new AvaloniaFileDialogs(window));
        var run = new RaceRunEditor(new(1, new("", "", "")));
        vm.ResultsRuns.Add(run);
        vm.SelectedWeatherHour = new(new DateTime(2026, 10, 2, 10, 0, 0), "Europe/Helsinki", 3.5m, 0, "Synthetic source", DateTimeOffset.UnixEpoch);
        window.Content = new RaceInformationView { DataContext = vm };
        window.Show(); window.UpdateLayout();
        window.GetVisualDescendants().OfType<Expander>().Single(x => Equals(x.Header, "Browse weather forecast (optional)")).IsExpanded = true;
        window.UpdateLayout();
        var button = window.GetVisualDescendants().OfType<Button>().Single(x => Equals(x.Content, "Apply forecast to Run 1"));
        Assert.NotNull(button.Command); Assert.Same(run, button.CommandParameter);
        button.Command.Execute(button.CommandParameter);
        Assert.Equal("Clear", run.Conditions); Assert.Contains("3.5 °C", run.WeatherSource, StringComparison.Ordinal);
        Assert.Equal("", run.StartTemperature); Assert.Equal("", run.FinishTemperature);
        window.Close();
    }
}
