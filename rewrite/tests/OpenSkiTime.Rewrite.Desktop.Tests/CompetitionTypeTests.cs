using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Avalonia;
using Avalonia.Media.Imaging;
using OpenSkiTime.Rewrite.Application;
using OpenSkiTime.Rewrite.Desktop;
using OpenSkiTime.Rewrite.Domain;
using OpenSkiTime.Rewrite.Persistence;
using Xunit;

namespace OpenSkiTime.Rewrite.Tests;

public partial class DesktopWorkflowTests
{
    [AvaloniaFact]
    public async Task CompetitionCalendarFieldsAreSharedAndPersistFromTheCompetitionEditor()
    {
        var folder = Path.Combine(Path.GetTempPath(), "openskitime-calendar-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, "test.ost"); var date = new DateOnly(2026, 10, 2);
            await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore());
            var series = await workspace.CreateAsync(path, new("Test", "Default place", "Club", date, date, "FIN", "2026/27"));
            await workspace.SaveCompetitionAsync(null, new("Synthetic race", "SL W", date, Discipline.Slalom, RaceType.Fis, 1, 0, "0034"), series.Revision);
            using var vm = new MainViewModel(workspace, new FileDialogsStub { NewPath = path, OpenPath = path, BackupPath = path + ".bak" },
                fisStore: new FisLocalStore(folder), recentSeriesStore: new RecentSeriesStore(folder));
            await vm.OpenSeriesCommand.ExecuteAsync(null); vm.ShowCompetitionsCommand.Execute(null); vm.SelectedCompetition = vm.Competitions.Single();
            vm.CompetitionLocation = "Competition place"; vm.CompetitionNation = "SWE";
            vm.CompetitionCalendarCategory = "NC"; vm.CompetitionGender = "W";
            vm.CompetitionTdLastName = "Testlastname"; vm.CompetitionTdFirstName = "Testfirst"; vm.CompetitionTdNation = "SWE"; vm.CompetitionTdNumber = "1047";
            await vm.SaveCompetitionCommand.ExecuteAsync(null);
            var saved = (await workspace.ReadAsync()).Competitions.Single().Values.Calendar!;
            Assert.Equal("NC", saved.Category); Assert.Equal("Competition place", saved.Location); Assert.Equal("W", saved.Gender);
            Assert.Equal("TESTLASTNAME", saved.TechnicalDelegate!.LastName); Assert.Equal("1047", saved.TechnicalDelegate.Number);
            var window = new MainWindow { DataContext = vm, Width = 1366, Height = 900, WindowState = WindowState.Normal };
            window.Show(); window.UpdateLayout();
            Assert.Same(vm.GetCompetitionDataCommand, window.FindControl<Button>("GetCompetitionDataButton")!.Command);
            var output = Environment.GetEnvironmentVariable("OPENSKITIME_M7_VISUAL_DIR");
            if (!string.IsNullOrWhiteSpace(output))
            {
                Directory.CreateDirectory(output);
                var scroll = window.FindControl<ScrollViewer>("WorkspaceScroll")!;
                scroll.Offset = new Vector(0, 250); window.UpdateLayout();
                using var bitmap = new RenderTargetBitmap(new PixelSize(1366, 900), new Vector(96, 96));
                bitmap.Render(window); bitmap.Save(Path.Combine(output, "competition-calendar.png"));
            }
            await vm.ShowResultsCommand.ExecuteAsync(null); await vm.LoadResultsCommand.ExecuteAsync(null);
            Assert.Equal("Competition place", vm.WeatherLocation);
            Assert.Contains("TESTLASTNAME", vm.ResultsTechnicalDelegateText); Assert.Contains("1047", vm.ResultsTechnicalDelegateText);
            Assert.DoesNotContain(vm.ResultsJury, x => x.Function == "TechnicalDelegate");
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<Button>(), x => Equals(x.Content, "Save race information") || Equals(x.Content, "Fetch FIS category"));
            window.Close();
        }
        finally { Directory.Delete(folder, true); }
    }

    [AvaloniaFact]
    public async Task CompetitionDefaultsToFisAndFieldsFollowTheCheckboxWithoutClearingValues()
    {
        var folder = Path.Combine(Path.GetTempPath(), "openskitime-type-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, "test.ost");
            await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore());
            var date = new DateOnly(2026, 10, 2);
            var series = await workspace.CreateAsync(path, new("Test", "Test", "Club", date, date, "FIN", "2026/27"));
            await workspace.SaveCompetitionAsync(null, new("Club slalom", "SL", date, Discipline.Slalom, RaceType.Club, 2, 0), series.Revision);
            using var vm = new MainViewModel(workspace, new FileDialogsStub { NewPath = path, OpenPath = path, BackupPath = path + ".bak" },
                fisStore: new FisLocalStore(folder), recentSeriesStore: new RecentSeriesStore(folder));
            Assert.Equal(RaceType.Fis, vm.CompetitionRaceType); Assert.True(vm.IsCompetitionFis);
            await vm.OpenSeriesCommand.ExecuteAsync(null); vm.ShowCompetitionsCommand.Execute(null);
            vm.SelectedCompetition = Assert.Single(vm.Competitions);
            var window = new MainWindow { DataContext = vm, WindowState = WindowState.Normal, Width = 1280, Height = 820 };
            window.Show(); window.UpdateLayout();
            var picker = window.FindControl<CheckBox>("CompetitionFisCheckBox")!;
            Assert.Equal(false, picker.IsChecked); Assert.Equal("FIS competition", picker.Content);
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<TextBlock>(), x => x.Text == "LOCAL RACE CODE" || x.Text == "RACE TYPE");
            Assert.False(window.FindControl<StackPanel>("CompetitionFisCodeField")!.IsVisible);
            picker.IsChecked = true; window.UpdateLayout();
            Assert.True(vm.IsCompetitionFis); Assert.True(window.FindControl<StackPanel>("CompetitionFisCodeField")!.IsVisible);
            vm.CompetitionFisCode = "5573"; vm.CompetitionHomologation = "123/10/26";
            await vm.SaveCompetitionCommand.ExecuteAsync(null);
            Assert.Equal(RaceType.Fis, Assert.Single((await workspace.ReadAsync()).Competitions).Values.RaceType);
            picker.IsChecked = false; window.UpdateLayout();
            Assert.False(window.FindControl<StackPanel>("CompetitionFisCodeField")!.IsVisible);
            Assert.False(window.FindControl<StackPanel>("CompetitionHomologationField")!.IsVisible);
            Assert.Equal("5573", vm.CompetitionFisCode); Assert.Equal("123/10/26", vm.CompetitionHomologation);
            await vm.SaveCompetitionCommand.ExecuteAsync(null);
            Assert.Equal("5573", Assert.Single((await workspace.ReadAsync()).Competitions).Values.FisCode);
            vm.NewCompetitionCommand.Execute(null); window.UpdateLayout();
            Assert.Equal(true, picker.IsChecked); Assert.True(vm.IsCompetitionFis);
            await vm.OpenDrawRunCommand.ExecuteAsync(new DrawDestination(Assert.Single(vm.Competitions), 1));
            window.UpdateLayout();
            Assert.False(window.GetVisualDescendants().OfType<DrawView>().Single().FindControl<DataGrid>("DrawStartListGrid")!.Columns.Single(x => Equals(x.Header, "FIS PTS")).IsVisible);
            await vm.ShowResultsCommand.ExecuteAsync(null); await vm.LoadResultsCommand.ExecuteAsync(null);
            Assert.False(vm.ResultsIsFis);
            var results = window.GetVisualDescendants().OfType<ResultsView>().Single();
            Assert.False(results.FindControl<Border>("ResultsFisInformation")!.IsVisible);
            window.Close();
        }
        finally { Directory.Delete(folder, true); }
    }
}
