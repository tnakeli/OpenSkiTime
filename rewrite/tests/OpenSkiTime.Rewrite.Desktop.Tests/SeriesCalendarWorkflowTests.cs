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
    public async Task CalendarBrowserSelectionCreatesSeriesAndAllRacesAndCanRefreshWithoutDuplicates()
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
                        "SL", "FIS", "FIN", false, "Test place", "W", 456))) };
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
            vm.SeriesCalendarNation = "SWE"; Assert.Empty(vm.SeriesCalendarEvents);
            vm.SeriesCalendarNation = ""; vm.SeriesCalendarSearch = "Test place"; Assert.Single(vm.SeriesCalendarEvents);
            var grid = window.FindControl<DataGrid>("SeriesCalendarEventsGrid")!;
            grid.SelectedItem = vm.SeriesCalendarEvents.Single(); window.UpdateLayout();
            Assert.Equal(2, vm.SeriesCalendarCompetitions.Count);
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

    private static byte[] CalendarUiZip()
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            using (var writer = new StreamWriter(zip.CreateEntry("A_event.csv").Open(), new UTF8Encoding(false)))
            { writer.Write("Eventid\tSeasoncode\tSectorcode\tEventname\tStartdate\tEnddate\tPlace\tNationcodeplace\tOrgaddressL1\n456\t2026\tAL\tSynthetic weekend\t2026-03-21\t2026-03-22\tTest place\tFIN\tSynthetic club\n"); }
            using (var writer = new StreamWriter(zip.CreateEntry("A_raceal.csv").Open(), new UTF8Encoding(false)))
            { writer.Write("Raceid\tEventid\tSeasoncode\tRacecodex\tDisciplinecode\tCatcode\tGender\tRacedate\tPlace\tNationcode\tTd1name\tTd1nation\tTd1code\n134\t456\t2026\t0034\tSL\tFIS\tW\t2026-03-21\tTest place\tFIN\tTestlast Testfirst (FIN)\tFIN\t1047\n135\t456\t2026\t0035\tSL\tFIS\tW\t2026-03-22\tTest place\tFIN\tTestlast Testfirst (FIN)\tFIN\t1047\n"); }
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
