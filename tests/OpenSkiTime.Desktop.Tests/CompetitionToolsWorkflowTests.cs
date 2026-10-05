using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
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
    public async Task SharedCourseCheckboxNamesDisciplineSharesAndResetsAfterSaveAndSelectionChange()
    {
        var folder = Path.Combine(Path.GetTempPath(), "openskitime-shared-course-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, "test.ost"); var date = new DateOnly(2026, 10, 2);
            await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore());
            var race = new CompetitionValues("Synthetic race", "SL1 W", date, Discipline.Slalom, RaceType.Fis, 2, 0, "0034");
            await workspace.CreateAsync(path, new("Test", "Test place", "Club", date, date, "FIN", "2026/27"),
                [race, race with { ShortLabel = "SL2 W", FisCode = "0035" }]);
            using var vm = new MainViewModel(workspace, new FileDialogsStub { NewPath = path, OpenPath = path, BackupPath = path + ".bak" },
                fisStore: new FisLocalStore(folder), recentSeriesStore: new RecentSeriesStore(folder));
            await vm.OpenSeriesCommand.ExecuteAsync(null); vm.ShowCompetitionsCommand.Execute(null);
            vm.SelectedCompetition = vm.Competitions[0];
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 1000, WindowState = WindowState.Normal };
            window.Show(); window.UpdateLayout();
            var checkbox = window.FindControl<CheckBox>("SaveCourseToAllRacesCheckBox")!;
            var tdCheckbox = window.FindControl<CheckBox>("SaveTdToAllRacesCheckBox")!;
            Assert.False(tdCheckbox.IsChecked);
            Assert.True(tdCheckbox.Bounds.X > checkbox.Bounds.X);
            Assert.Equal("Save Course & Homologation to all Slalom races", checkbox.Content);
            vm.CompetitionDiscipline = Discipline.GiantSlalom;
            Assert.Equal("Save Course & Homologation to all Giant Slalom races", checkbox.Content);
            vm.CompetitionDiscipline = Discipline.Slalom;
            Assert.Equal("Save TD to all races", tdCheckbox.Content);
            Assert.False(checkbox.IsChecked); checkbox.IsChecked = true;
            Assert.True(vm.SaveCourseToAllRaces);
            vm.SelectedCompetition = vm.Competitions[1]; Assert.False(vm.SaveCourseToAllRaces);
            vm.CompetitionCourseName = "Shared slope"; vm.CompetitionHomologation = "123/10/26";
            vm.CompetitionStartAltitude = "500"; vm.CompetitionFinishAltitude = "300";
            vm.CompetitionVerticalDrop = "200"; vm.CompetitionCourseLength = "640";
            checkbox.IsChecked = true;
            tdCheckbox.IsChecked = true;
            vm.CompetitionTdLastName = "TESTLAST"; vm.CompetitionTdFirstName = "Testfirst";
            vm.CompetitionTdNation = "FIN"; vm.CompetitionTdNumber = "1047";
            var save = window.GetVisualDescendants().OfType<Button>().Single(x => Equals(x.Content, "Save competition"));
            Assert.Same(vm.SaveCompetitionCommand, save.Command);
            await vm.SaveCompetitionCommand.ExecuteAsync(null);
            Assert.False(vm.IsError); Assert.False(vm.SaveCourseToAllRaces); Assert.False(checkbox.IsChecked);
            Assert.False(vm.SaveTdToAllRaces); Assert.False(tdCheckbox.IsChecked);
            Assert.Contains("all Slalom races", vm.StatusMessage); Assert.Contains("TD saved to all races", vm.StatusMessage);
            Assert.All((await workspace.ReadAsync()).Competitions, x =>
            {
                Assert.Equal("Shared slope", x.Values.CourseName); Assert.Equal("123/10/26", x.Values.HomologationNumber);
                Assert.Equal(500, x.Values.StartAltitudeMeters); Assert.Equal(640, x.Values.CourseLengthMeters);
                Assert.Equal("1047", x.Values.Calendar!.TechnicalDelegate!.Number);
            });
            tdCheckbox.IsChecked = true;
            vm.SelectedCompetition = vm.Competitions.First(x => x.Id != vm.SelectedCompetition!.Id);
            Assert.False(vm.SaveTdToAllRaces);
            window.Close();
        }
        finally { Directory.Delete(folder, true); }
    }

    [AvaloniaFact]
    public async Task LateHomologationResponseCannotFillAnotherLocation()
    {
        var folder = Path.Combine(Path.GetTempPath(), "openskitime-late-course-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, "test.ost"); var date = new DateOnly(2026, 3, 21);
            await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore());
            await workspace.CreateAsync(path, new("Test", "Test place", "Club", date, date, "FIN", "2025/26"));
            var response = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var handler = new DelayedCourseHandler(response.Task); using var http = new HttpClient(handler);
            using var vm = new MainViewModel(workspace, new FileDialogsStub { NewPath = path, OpenPath = path, BackupPath = path + ".bak" },
                fisStore: new FisLocalStore(folder), recentSeriesStore: new RecentSeriesStore(folder), informationHttp: http);
            await vm.OpenSeriesCommand.ExecuteAsync(null); vm.ShowCompetitionsCommand.Execute(null);
            vm.CompetitionCourseName = "Manual course";
            var lookup = vm.BrowseCompetitionHomologationsCommand.ExecuteAsync(null);
            Assert.True(vm.IsHomologationBusy); vm.CompetitionLocation = "Other place";
            response.SetResult(new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new {
                data = new[] { new FisCourseHomologation(1, "123/10/26", "Test slope", "Test place", "FIN", "AL", "SL", "A", 500, 300, 200, 640, new(2025, 1, 1), new(2030, 1, 1)) } }), Encoding.UTF8, "application/json") });
            await lookup;
            Assert.False(vm.IsHomologationBusy); Assert.Equal("Manual course", vm.CompetitionCourseName); Assert.Empty(vm.CompetitionHomologations);
            Assert.Contains("Browse again", vm.CompetitionHomologationStatus, StringComparison.Ordinal);
        }
        finally { Directory.Delete(folder, true); }
    }

    [AvaloniaFact]
    public async Task HomologationBrowserAppliesSelectionAndManualChangesReachResults()
    {
        var folder = Path.Combine(Path.GetTempPath(), "openskitime-homologation-ui-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, "test.ost"); var date = new DateOnly(2026, 3, 21);
            await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore());
            await workspace.CreateAsync(path, new("Test", "Test place", "Club", date, date, "FIN", "2025/26"));
            var first = new FisCourseHomologation(1, "123/10/26", "Test slope", "Test place", "FIN", "AL", "SL", "A", 500, 300, 200, 640, new(2025, 1, 1), new(2030, 1, 1));
            using var handler = new CourseHandler(_ => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new {
                data = new[] { first, first with { HomologationId = 2, HomologationCode = "234/10/26", CourseName = "Other slope", StartAltitude = 510 } } }), Encoding.UTF8, "application/json") });
            using var http = new HttpClient(handler);
            using var vm = new MainViewModel(workspace, new FileDialogsStub { NewPath = path, OpenPath = path, BackupPath = path + ".bak" },
                fisStore: new FisLocalStore(folder), recentSeriesStore: new RecentSeriesStore(folder), informationHttp: http);
            await vm.OpenSeriesCommand.ExecuteAsync(null); vm.ShowCompetitionsCommand.Execute(null);
            vm.CompetitionName = "Synthetic race"; vm.CompetitionShortLabel = "SL W"; vm.CompetitionFisCode = "0034";
            vm.CompetitionCourseName = "Manual before browse";
            var window = new MainWindow { DataContext = vm, Width = 1366, Height = 900, WindowState = WindowState.Normal }; window.Show(); window.UpdateLayout();
            await vm.BrowseCompetitionHomologationsCommand.ExecuteAsync(null);
            Assert.Equal(2, vm.CompetitionHomologations.Count); Assert.Equal("Manual before browse", vm.CompetitionCourseName);
            var picker = window.FindControl<ComboBox>("CompetitionHomologationPicker")!; picker.SelectedItem = vm.CompetitionHomologations.Single(x => x.HomologationId == 2);
            vm.ApplyCompetitionHomologationCommand.Execute(null);
            Assert.Equal("Other slope", vm.CompetitionCourseName); Assert.Equal("510", vm.CompetitionStartAltitude); Assert.Equal("640", vm.CompetitionCourseLength);
            vm.CompetitionStartAltitude = "505"; await vm.SaveCompetitionCommand.ExecuteAsync(null);
            Assert.False(vm.IsError); await vm.ShowResultsCommand.ExecuteAsync(null); await vm.LoadResultsCommand.ExecuteAsync(null);
            Assert.All(vm.ResultsRuns, x => Assert.Equal("505", x.StartAltitude)); Assert.All(vm.ResultsRuns, x => Assert.Equal("234/10/26", x.Homologation));
            window.Close();
        }
        finally { Directory.Delete(folder, true); }
    }

    [AvaloniaFact]
    public async Task EmptyCompetitionsOpenNewEditorAndUseSeriesSeasonInsteadOfRaceDate()
    {
        var folder = Path.Combine(Path.GetTempPath(), "openskitime-new-race-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, "test.ost");
            await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore());
            using var vm = new MainViewModel(workspace, new FileDialogsStub { NewPath = path, OpenPath = path, BackupPath = path + ".bak" },
                fisStore: new FisLocalStore(folder), recentSeriesStore: new RecentSeriesStore(folder));
            Assert.Equal(FisSeason.SeriesLabel(DateOnly.FromDateTime(DateTime.Today)), vm.Season);
            vm.Name = "Test"; vm.Location = "Test place"; vm.Organizer = "Club";
            vm.StartDateText = vm.EndDateText = "21.03.2026"; vm.Season = "2026/27";
            await vm.CreateSeriesCommand.ExecuteAsync(null);
            Assert.True(vm.IsCompetitionsSection); Assert.True(vm.IsCompetitionEditing); Assert.Equal("New competition", vm.CompetitionEditorTitle);
            Assert.Equal("2027", vm.CompetitionSeason); Assert.Equal("21.03.2026", vm.CompetitionDateText);
            vm.CompetitionName = "Keep draft"; vm.ShowSeriesCommand.Execute(null); vm.ShowCompetitionsCommand.Execute(null);
            Assert.Equal("Keep draft", vm.CompetitionName);
            vm.NewSeriesCommand.Execute(null);
            Assert.Equal(FisSeason.SeriesLabel(DateOnly.FromDateTime(DateTime.Today)), vm.Season);
        }
        finally { Directory.Delete(folder, true); }
    }

    [AvaloniaFact]
    public async Task CompetitionGridAndResultsOverridesWorkThroughTheUi()
    {
        var folder = Path.Combine(Path.GetTempPath(), "openskitime-tools-ui-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, "test.ost"); var date = new DateOnly(2026, 3, 21);
            await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore());
            var series = await workspace.CreateAsync(path, new("Test", "Test place", "Club", date, date, "FIN", "2025/26"));
            var source = new CompetitionValues("Synthetic source", "SL1 W", date, Discipline.Slalom, RaceType.Fis, 2, 0, "0034",
                CourseName: "Test slope", StartAltitudeMeters: 500, FinishAltitudeMeters: 300, VerticalDropMeters: 200,
                HomologationNumber: "123/10/26", Calendar: new(2026, "Test place", "FIN", "FIS", "W", new("TESTLASTNAME", "Testfirst", "FIN", "1047")), CourseLengthMeters: 640);
            series = await workspace.SaveCompetitionAsync(null, source, series.Revision);
            series = await workspace.SaveCompetitionAsync(null, source with { Name = "Synthetic target", ShortLabel = "SL2 W", FisCode = "0035" }, series.Revision);
            using var vm = new MainViewModel(workspace, new FileDialogsStub { NewPath = path, OpenPath = path, BackupPath = path + ".bak" },
                fisStore: new FisLocalStore(folder), recentSeriesStore: new RecentSeriesStore(folder));
            await vm.OpenSeriesCommand.ExecuteAsync(null); vm.ShowCompetitionsCommand.Execute(null);
            var target = vm.Competitions.Single(x => x.Values.ShortLabel == "SL2 W");
            var window = new MainWindow { DataContext = vm, Width = 1854, Height = 1100, WindowState = WindowState.Normal };
            window.Show(); window.UpdateLayout();
            var grid = window.FindControl<DataGrid>("CompetitionsGrid")!;
            Assert.Equal(DataGridSelectionMode.Single, grid.SelectionMode);
            Assert.Contains(grid.Columns, x => Equals(x.Header, "CODEX")); Assert.Contains(grid.Columns, x => Equals(x.Header, "HOMOLOGATION"));
            Assert.Contains(grid.GetVisualDescendants().OfType<TextBlock>(), x => x.Text == "123/10/26 500m - 300m");
            grid.SelectedItem = target; window.UpdateLayout();
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<TextBlock>(), x => x.Text == "COPY FROM COMPETITION");
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<Button>(), x => Equals(x.Content, "Copy to selected competitions"));
            Assert.Equal("", vm.CompetitionCalendarStatus); Assert.Equal("", vm.CompetitionHomologationStatus);
            Assert.Equal("Test slope", vm.CompetitionCourseName);
            Assert.Same(vm.BrowseCompetitionHomologationsCommand, window.FindControl<Button>("BrowseCompetitionHomologationsButton")!.Command);
            Assert.Equal(Avalonia.Layout.HorizontalAlignment.Left, window.FindControl<Button>("BrowseCompetitionHomologationsButton")!.HorizontalAlignment);
            Assert.DoesNotContain(window.FindControl<StackPanel>("CompetitionCourseDetails")!.GetVisualAncestors(), x => x is Expander);
            await vm.ShowResultsCommand.ExecuteAsync(null); vm.ResultsCompetition = vm.FisCompetitions.Single(x => x.Id == target.Id); await vm.LoadResultsCommand.ExecuteAsync(null);
            Assert.Equal("640", vm.ResultsRuns[0].Length); Assert.Equal("500", vm.ResultsRuns[1].StartAltitude);
            vm.ResultsRuns[1].StartAltitude = "490"; vm.ResultsRuns[1].Drop = "190"; Assert.True(await vm.FlushRaceInformationAsync());
            vm.ShowCompetitionsCommand.Execute(null); vm.SelectedCompetition = vm.Competitions.Single(x => x.Id == target.Id);
            vm.CompetitionStartAltitude = "510"; vm.CompetitionCourseLength = "650"; await vm.SaveCompetitionCommand.ExecuteAsync(null);
            await vm.ShowResultsCommand.ExecuteAsync(null); vm.ResultsCompetition = vm.FisCompetitions.Single(x => x.Id == target.Id); await vm.LoadResultsCommand.ExecuteAsync(null);
            Assert.Equal("510", vm.ResultsRuns[0].StartAltitude); Assert.Equal("490", vm.ResultsRuns[1].StartAltitude);
            Assert.Equal("650", vm.ResultsRuns[1].Length); Assert.Contains("1047", vm.ResultsTechnicalDelegateText, StringComparison.Ordinal);
            vm.ShowCompetitionsCommand.Execute(null); vm.SelectedCompetition = vm.Competitions.Single(x => x.Id == target.Id); window.UpdateLayout();
            var output = Environment.GetEnvironmentVariable("OPENSKITIME_M7_VISUAL_DIR");
            if (!string.IsNullOrWhiteSpace(output))
            {
                Directory.CreateDirectory(output);
                using var bitmap = new RenderTargetBitmap(new PixelSize(1854, 1100), new Vector(96, 96)); bitmap.Render(window);
                bitmap.Save(Path.Combine(output, "competition-tools.png"));
                window.FindControl<ScrollViewer>("WorkspaceScroll")!.Offset = new Vector(0, 550); window.UpdateLayout();
                using var courseBitmap = new RenderTargetBitmap(new PixelSize(1854, 1100), new Vector(96, 96)); courseBitmap.Render(window);
                courseBitmap.Save(Path.Combine(output, "competition-course.png"));
            }
            window.Close();
        }
        finally { Directory.Delete(folder, true); }
    }

    [AvaloniaFact]
    public async Task EveryDatePickerDisplaysMonthNumberAndNameWhenNavigatingMonths()
    {
        var folder = Path.Combine(Path.GetTempPath(), "openskitime-date-picker-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, "test.ost"); var date = new DateOnly(2026, 3, 21);
            await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore());
            await workspace.CreateAsync(path, new("Test", "Test", "Club", date, date, "FIN", "2025/26"));
            using var vm = new MainViewModel(workspace, new FileDialogsStub { NewPath = path, OpenPath = path, BackupPath = path + ".bak" },
                fisStore: new FisLocalStore(folder), recentSeriesStore: new RecentSeriesStore(folder));
            await vm.OpenSeriesCommand.ExecuteAsync(null);
            var window = new MainWindow { DataContext = vm, Width = 1366, Height = 900, WindowState = WindowState.Normal }; window.Show(); window.UpdateLayout();
            foreach (var name in new[] { "SeriesStartDatePickerButton", "SeriesEndDatePickerButton", "CompetitionDatePickerButton", "FisDatePickerButton" })
            {
                if (name.StartsWith("Series", StringComparison.Ordinal)) { vm.ShowSeriesCommand.Execute(null); }
                else if (name.StartsWith("Competition", StringComparison.Ordinal)) { vm.ShowCompetitionsCommand.Execute(null); }
                else { vm.ShowCompetitorsCommand.Execute(null); vm.IsFisPanelOpen = true; }
                window.UpdateLayout(); var button = window.FindControl<Button>(name)!;
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var flyout = Assert.IsType<Flyout>(button.Flyout); var panel = Assert.IsType<StackPanel>(flyout.Content);
                var label = panel.Children.OfType<TextBlock>().Single(); var calendar = panel.Children.OfType<Avalonia.Controls.Calendar>().Single();
                Assert.Equal(calendar.DisplayDate.ToString("MM MMMM yyyy", CultureInfo.CurrentCulture), label.Text);
                calendar.DisplayDate = new DateTime(2026, 12, 1);
                Assert.Equal(new DateTime(2026, 12, 1).ToString("MM MMMM yyyy", CultureInfo.CurrentCulture), label.Text);
                flyout.Hide();
            }
            window.Close();
        }
        finally { Directory.Delete(folder, true); }
    }

    private sealed class CourseHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(response(request));
    }
    private sealed class DelayedCourseHandler(Task<HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => response;
    }
}
