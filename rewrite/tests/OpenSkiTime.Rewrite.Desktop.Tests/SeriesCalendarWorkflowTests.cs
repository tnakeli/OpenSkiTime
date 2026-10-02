using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using OpenSkiTime.Rewrite.Application;
using OpenSkiTime.Rewrite.Desktop;
using OpenSkiTime.Rewrite.Persistence;
using Xunit;

namespace OpenSkiTime.Rewrite.Tests;

public partial class DesktopWorkflowTests
{
    [AvaloniaFact]
    public async Task CalendarBrowserSelectionCreatesCancelledRacesAndCanRefreshWithoutDuplicates()
    {
        var folder = Path.Combine(Path.GetTempPath(), "openskitime-series-calendar-ui-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, "calendar.ost");
            var requests = new List<string>();
            using var handler = new CalendarUiHandler(request =>
            {
                Assert.Null(request.Headers.Authorization);
                Assert.Equal("synthetic-key", Assert.Single(request.Headers.GetValues("X-Api-Key")));
                requests.Add(request.RequestUri!.AbsoluteUri);
                if (request.RequestUri.AbsolutePath == "/data-feeds/calendar")
                { return new(HttpStatusCode.OK) { Content = new ByteArrayContent(CalendarUiZip()) }; }
                var codex = int.Parse(request.RequestUri.Segments[^1], System.Globalization.CultureInfo.InvariantCulture);
                return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(
                    new FisCompetitionInformation(codex + 100, codex, 2026, new(2026, 3, codex == 34 ? 21 : 22),
                        "SL", "FIS", "FIN", true, "Test place", "W", 456))) };
            });
            using var http = new HttpClient(handler);
            await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore());
            using var vm = new MainViewModel(workspace, new FileDialogsStub { NewPath = path, OpenPath = path, BackupPath = path + ".bak" },
                fisStore: new FisLocalStore(folder, new CalendarTestCredential()), recentSeriesStore: new RecentSeriesStore(folder), informationHttp: http);
            vm.Season = "2025/26";
            var window = new MainWindow { DataContext = vm, WindowState = WindowState.Normal, Width = 1366, Height = 1100 }; window.Show(); window.UpdateLayout();
            window.Width = 1365; window.Height = 1099;
            window.Width = 1366; window.Height = 1100; window.UpdateLayout();
            Assert.Same(vm.BrowseSeriesCalendarCommand, window.FindControl<Button>("BrowseSeriesCalendarButton")!.Command);
            await vm.BrowseSeriesCalendarCommand.ExecuteAsync(null);
            Assert.True(vm.IsSeriesCalendarOpen); Assert.Equal("2026", vm.SeriesCalendarSeason); Assert.Single(requests);
            Assert.Single(vm.SeriesCalendarEvents);
            vm.ApplyCalendarFilter(CalendarColumn.Nation, []); Assert.Empty(vm.SeriesCalendarEvents);
            vm.ClearCalendarFilter(CalendarColumn.Nation); Assert.Single(vm.SeriesCalendarEvents);
            var grid = window.FindControl<DataGrid>("SeriesCalendarEventsGrid")!;
            grid.SelectedItem = vm.SeriesCalendarEvents.Single(); window.UpdateLayout();
            var selected = vm.SelectedSeriesCalendarEvent;
            vm.SortCalendar(CalendarColumn.Start, true);
            Assert.Same(selected, vm.SelectedSeriesCalendarEvent);
            var nationHeader = Assert.IsType<Button>(grid.Columns[3].Header);
            nationHeader.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            var filterPanel = Assert.IsType<StackPanel>(window.CalendarFilterMenu!.Content);
            var search = filterPanel.Children.OfType<TextBox>().Single();
            var selectAll = filterPanel.Children.OfType<CheckBox>().Single();
            Assert.True(selectAll.IsChecked);
            selectAll.IsChecked = false;
            var actions = filterPanel.Children.OfType<StackPanel>().Last();
            actions.Children.OfType<Button>().Single(x => x.Name == "CalendarFilterCancel").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Assert.Single(vm.SeriesCalendarEvents); Assert.Same(selected, vm.SelectedSeriesCalendarEvent);
            nationHeader.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            filterPanel = Assert.IsType<StackPanel>(window.CalendarFilterMenu!.Content);
            search = filterPanel.Children.OfType<TextBox>().Single(); search.Text = "FIN";
            selectAll = filterPanel.Children.OfType<CheckBox>().Single(); selectAll.IsChecked = false;
            actions = filterPanel.Children.OfType<StackPanel>().Last();
            actions.Children.OfType<Button>().Single(x => x.Name == "CalendarFilterApply").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Assert.Empty(vm.SeriesCalendarEvents); Assert.Null(vm.SelectedSeriesCalendarEvent); Assert.Empty(vm.SeriesCalendarCompetitions);
            Assert.Contains("●", nationHeader.Content!.ToString(), StringComparison.Ordinal);
            vm.ClearCalendarFiltersCommand.Execute(null); Assert.Single(vm.SeriesCalendarEvents);
            grid.SelectedItem = vm.SeriesCalendarEvents.Single(); window.UpdateLayout();
            Assert.Equal(2, vm.SeriesCalendarCompetitions.Count);
            Assert.Equal("SL1 W 21.3", vm.SeriesCalendarCompetitions[0].ShortLabel);
            Assert.Equal("SL2 W 22.3", vm.SeriesCalendarCompetitions[1].ShortLabel);
            Assert.Equal("Synthetic weekend - SL Women 0034", vm.SeriesCalendarCompetitions[0].Name);
            var output = Environment.GetEnvironmentVariable("OPENSKITIME_M7_VISUAL_DIR");
            if (!string.IsNullOrWhiteSpace(output))
            {
                Directory.CreateDirectory(output);
                using var bitmap = new RenderTargetBitmap(new PixelSize(1366, 1100), new Vector(96, 96));
                bitmap.Render(window); bitmap.Save(Path.Combine(output, "series-calendar.png"));
            }
            await vm.UseSeriesCalendarEventCommand.ExecuteAsync(null);
            Assert.False(vm.IsError); Assert.Equal(3, requests.Count);
            Assert.Equal("Synthetic weekend", vm.Name); Assert.Equal("Synthetic club", vm.Organizer); Assert.Equal("Test place", vm.Location);
            Assert.Equal("21.03.2026", vm.StartDateText); Assert.Equal("22.03.2026", vm.EndDateText); Assert.False(File.Exists(path));
            await vm.CreateSeriesCommand.ExecuteAsync(null);
            Assert.False(vm.IsError); var created = await workspace.ReadAsync(); Assert.Equal(2, created.Competitions.Count);
            Assert.Equal("SL1 W 21.3", created.Competitions[0].Values.ShortLabel);
            Assert.Equal("Synthetic weekend - SL Women 0034", created.Competitions[0].Values.Name);
            Assert.Equal("1047", created.Competitions[0].Values.Calendar!.TechnicalDelegate!.Number);
            Assert.Equal("FIN", created.Values.Nation); Assert.Equal("2025/26", created.Values.Season);
            var ids = created.Competitions.Select(x => x.Id).ToArray();
            vm.ShowSeriesCommand.Execute(null); await vm.BrowseSeriesCalendarCommand.ExecuteAsync(null);
            vm.SelectedSeriesCalendarEvent = vm.SeriesCalendarEvents.Single();
            await vm.UseSeriesCalendarEventCommand.ExecuteAsync(null);
            Assert.False(vm.IsError); Assert.Equal(6, requests.Count);
            var refreshed = await workspace.ReadAsync(); Assert.Equal(ids, refreshed.Competitions.Select(x => x.Id));
            Assert.Equal(created.Revision + 1, refreshed.Revision);
            window.Close();
        }
        finally { Directory.Delete(folder, true); }
    }

    [AvaloniaFact]
    public async Task LateCalendarResponseCannotPopulateANewSeries()
    {
        var folder = Path.Combine(Path.GetTempPath(), "openskitime-late-calendar-ui-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        try
        {
            var response = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var handler = new DelayedCourseHandler(response.Task); using var http = new HttpClient(handler);
            await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore());
            using var vm = new MainViewModel(workspace, new FileDialogsStub { NewPath = Path.Combine(folder, "new.ost"), OpenPath = "", BackupPath = "" },
                fisStore: new FisLocalStore(folder, new CalendarTestCredential()), recentSeriesStore: new RecentSeriesStore(folder), informationHttp: http);
            vm.Season = "2025/26";
            var loading = vm.BrowseSeriesCalendarCommand.ExecuteAsync(null);
            Assert.True(vm.IsSeriesCalendarBusy); vm.NewSeriesCommand.Execute(null);
            response.SetResult(new(HttpStatusCode.OK) { Content = new ByteArrayContent(CalendarUiZip()) }); await loading;
            Assert.Empty(vm.SeriesCalendarEvents); Assert.False(vm.IsSeriesCalendarOpen); Assert.Equal("", vm.Name);
        }
        finally { Directory.Delete(folder, true); }
    }

    [AvaloniaFact]
    public async Task CalendarHeaderSearchCombinesFiltersSortsNumericallyAndPreservesVisibleSelection()
    {
        var folder = Path.Combine(Path.GetTempPath(), "openskitime-calendar-filters-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, "test.ost");
            using var handler = new CalendarUiHandler(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(CalendarUiZip(true)) });
            using var http = new HttpClient(handler);
            await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore());
            using var vm = new MainViewModel(workspace, new FileDialogsStub { NewPath = path, OpenPath = path, BackupPath = path + ".bak" },
                fisStore: new FisLocalStore(folder, new CalendarTestCredential()), recentSeriesStore: new RecentSeriesStore(folder), informationHttp: http);
            vm.Season = "2025/26"; vm.Nation = "";
            var window = new MainWindow { DataContext = vm, Width = 1366, Height = 1100 }; window.Show();
            try
            {
                await vm.BrowseSeriesCalendarCommand.ExecuteAsync(null); Assert.Equal(2, vm.SeriesCalendarEvents.Count);
                vm.SortCalendar(CalendarColumn.Races, true); Assert.Equal(12, vm.SeriesCalendarEvents[0].CompetitionCount);
                vm.SortCalendar(CalendarColumn.Races, false); Assert.Equal(2, vm.SeriesCalendarEvents[0].CompetitionCount);
                vm.SortCalendar(CalendarColumn.Start, false); Assert.Equal(new DateOnly(2026, 2, 1), vm.SeriesCalendarEvents[0].StartDate);
                var grid = window.FindControl<DataGrid>("SeriesCalendarEventsGrid")!;
                grid.SelectedItem = vm.SeriesCalendarEvents.Single(x => x.Id == 456);
                var selected = vm.SelectedSeriesCalendarEvent;
                vm.ApplyCalendarFilter(CalendarColumn.Location, ["Test place"]);
                Assert.Same(selected, vm.SelectedSeriesCalendarEvent); Assert.Equal(2, vm.SeriesCalendarCompetitions.Count);
                vm.ApplyCalendarFilter(CalendarColumn.Nation, ["SWE"]); Assert.Empty(vm.SeriesCalendarEvents); Assert.Null(vm.SelectedSeriesCalendarEvent);
                vm.ClearCalendarFiltersCommand.Execute(null); Assert.Equal(2, vm.SeriesCalendarEvents.Count);
                var nation = Assert.IsType<Button>(grid.Columns[3].Header);
                nation.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                var panel = Assert.IsType<StackPanel>(window.CalendarFilterMenu!.Content);
                panel.Children.OfType<TextBox>().Single().Text = "swe";
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                var list = Assert.IsType<StackPanel>(panel.Children.OfType<ScrollViewer>().Single().Content);
                Assert.Equal("SWE", Assert.Single(list.Children.OfType<CheckBox>()).Content);
                panel.Children.OfType<StackPanel>().Last().Children.OfType<Button>().Single(x => x.Name == "CalendarFilterApply")
                    .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                Assert.Equal("SWE", Assert.Single(vm.SeriesCalendarEvents).Nation);
                Assert.Contains("●", nation.Content!.ToString(), StringComparison.Ordinal);
                grid.SelectedItem = vm.SeriesCalendarEvents.Single(); selected = vm.SelectedSeriesCalendarEvent;
                await vm.BrowseSeriesCalendarCommand.ExecuteAsync(null);
                Assert.Equal(selected!.Id, vm.SelectedSeriesCalendarEvent!.Id); Assert.Equal(12, vm.SeriesCalendarCompetitions.Count);
                nation.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                panel = Assert.IsType<StackPanel>(window.CalendarFilterMenu!.Content);
                panel.Children.OfType<StackPanel>().Last().Children.OfType<Button>().Single(x => x.Name == "CalendarFilterClear")
                    .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(2, vm.SeriesCalendarEvents.Count); Assert.DoesNotContain("●", nation.Content!.ToString(), StringComparison.Ordinal);
            }
            finally { window.Close(); }
        }
        finally { Directory.Delete(folder, true); }
    }

    private static byte[] CalendarUiZip(bool additionalEvent = false)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            using (var writer = new StreamWriter(zip.CreateEntry("A_event.csv").Open(), new UTF8Encoding(false)))
            { writer.Write("Eventid\tSeasoncode\tSectorcode\tEventname\tStartdate\tEnddate\tPlace\tNationcodeplace\tOrgaddressL1\n456\t2026\tAL\tSynthetic weekend\t2026-03-21\t2026-03-22\tTest place\tFIN\tSynthetic club\n");
              if (additionalEvent) { writer.Write("457\t2026\tAL\tOther synthetic event\t2026-02-01\t2026-02-01\tOther slope\tSWE\tSynthetic club\n"); } }
            using (var writer = new StreamWriter(zip.CreateEntry("A_raceal.csv").Open(), new UTF8Encoding(false)))
            { writer.Write("Raceid\tEventid\tSeasoncode\tRacecodex\tDisciplinecode\tCatcode\tGender\tRacedate\tPlace\tNationcode\tTd1name\tTd1nation\tTd1code\n134\t456\t2026\t0034\tSL\tFIS\tW\t2026-03-21\tTest place\tFIN\tTestlast Testfirst (FIN)\tFIN\t1047\n135\t456\t2026\t0035\tSL\tFIS\tW\t2026-03-22\tTest place\tFIN\tTestlast Testfirst (FIN)\tFIN\t1047\n");
              if (additionalEvent) { for (var i = 0; i < 12; i++) { writer.Write($"{200 + i}\t457\t2026\t{1000 + i}\tSL\tFIS\tM\t2026-02-01\tOther slope\tSWE\t\t\t\n"); } } }
        }
        return output.ToArray();
    }
    private sealed class CalendarUiHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(response(request)); }
    private sealed class CalendarTestCredential : ICredentialStore
    {
        public bool Exists() => true;
        public string Read() => "synthetic-key";
        public void Save(string key) => throw new NotSupportedException();
        public void Remove() => throw new NotSupportedException();
    }
}
