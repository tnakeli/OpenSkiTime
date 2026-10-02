using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using OpenSkiTime.Rewrite.Application;
using OpenSkiTime.Rewrite.Domain;
using OpenSkiTime.Rewrite.Persistence;
using Xunit;

namespace OpenSkiTime.Rewrite.Tests;

public sealed class CompetitionCalendarTests
{
    private static readonly FisCompetitionInformation s_summary = new(123, 34, 2026, new(2026, 3, 21), "SL", "FIS", "FIN", false, "Test place", "W", 456);

    internal static byte[] CalendarZip(string name = "Testlastname Testfirst (FIN)", string number = "1047", string category = "FIS", bool duplicate = false)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
        {
            var header = "Raceid\tEventid\tSeasoncode\tRacecodex\tDisciplinecode\tCatcode\tGender\tRacedate\tNationcode\tTd1name\tTd1nation\tTd1code\n";
            var row = $"123\t456\t2026\t0034\tSL\t{category}\tW\t\0" + $"2026-03-21 00:00:00\0\tFIN\t\0{name}\0\tFIN\t{number}\n";
            using (var writer = new StreamWriter(zip.CreateEntry("A_raceal.csv").Open(), new UTF8Encoding(false))) { writer.Write(header + row + (duplicate ? row : "")); }
            using (var writer = new StreamWriter(zip.CreateEntry("A_event.csv").Open(), new UTF8Encoding(false)))
            { writer.Write("Eventid\tSeasoncode\tSectorcode\tEventname\n456\t2026\tAL\tSynthetic FIS event\n"); }
        }
        return stream.ToArray();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CodexAndDatedCalendarProvideAllSharedFieldsWithoutOauth(bool cancelled)
    {
        var calls = new List<string>();
        using var handler = new Handler(request =>
        {
            calls.Add(request.RequestUri!.ToString());
            Assert.Null(request.Headers.Authorization);
            Assert.Equal("synthetic-key", Assert.Single(request.Headers.GetValues("X-Api-Key")));
            return new(HttpStatusCode.OK) { Content = calls.Count == 1
                ? new StringContent(JsonSerializer.Serialize(s_summary with { IsCancelled = cancelled }), Encoding.UTF8, "application/json") : new ByteArrayContent(CalendarZip()) };
        });
        using var http = new HttpClient(handler);
        var data = await new FisRaceInformationClient(http).GetCompetitionAsync(2026, "0034", "synthetic-key", new(2026, 10, 2));
        Assert.EndsWith("/AL/34?season=2026", calls[0]); Assert.EndsWith("/calendar?date=2026-03-21", calls[1]);
        Assert.Equal("Test place", data.Competition.PlaceName); Assert.Equal("SL", data.Competition.EventCode);
        Assert.Equal("FIN", data.Competition.PlaceNationCode); Assert.Equal("W", data.Competition.GenderCode);
        Assert.Equal("Synthetic FIS event", data.EventName); Assert.Equal("FIS", data.Competition.CategoryCode);
        Assert.Equal(new CompetitionTechnicalDelegateInfo("Testlastname", "Testfirst", "FIN", "1047"), data.TechnicalDelegate);
        Assert.Equal(cancelled, data.Competition.IsCancelled);
        Assert.Equal(cancelled, data.Note.Contains("cancelled", StringComparison.Ordinal));
    }

    [Fact]
    public void CalendarRejectsAmbiguousRacesAndDisagreementInsteadOfMixingMetadata()
    {
        Assert.Throws<DomainValidationException>(() => FisRaceInformationClient.ReadCalendar(CalendarZip(category: "NC"), s_summary, "synthetic"));
        Assert.Throws<DomainValidationException>(() => FisRaceInformationClient.ReadCalendar(CalendarZip(duplicate: true), s_summary, "synthetic"));
        Assert.Throws<DomainValidationException>(() => FisRaceInformationClient.ReadCalendar([1, 2, 3], s_summary, "synthetic"));
    }

    [Fact]
    public void MissingTdIsPreservedAndCompoundTdNameRequiresReview()
    {
        var partial = FisRaceInformationClient.ReadCalendar(CalendarZip(number: ""), s_summary, "synthetic");
        Assert.Equal("Testlastname", partial.TechnicalDelegate!.LastName); Assert.Equal("", partial.TechnicalDelegate.Number);
        Assert.Contains("no TD number", partial.Note, StringComparison.Ordinal);
        Assert.Null(FisRaceInformationClient.ReadCalendar(CalendarZip(name: "", number: ""), s_summary, "synthetic").TechnicalDelegate);
        var td = FisRaceInformationClient.ReadCalendar(CalendarZip("Test de Example (FIN)"), s_summary, "synthetic").TechnicalDelegate!;
        Assert.Equal("Test de Example", td.OriginalName); Assert.Equal("", td.LastName); Assert.Equal("", td.FirstName);
        Assert.Equal("1047", td.Number);
    }

    [Fact]
    public async Task CalendarFieldsTravelInPortableDatabase()
    {
        var folder = Path.Combine(Path.GetTempPath(), "openskitime-calendar-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, "test.ost"); var transfer = Path.Combine(folder, "transfer.ost");
            var calendar = new CompetitionCalendarData(2026, "Other location", "SWE", "NC", "W", new("Testlastname", "Testfirst", "SWE", "1047"), "Synthetic source").Validated();
            await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore());
            var series = await workspace.CreateAsync(path, new("Test", "Default place", "Club", s_summary.Date, s_summary.Date, "FIN", "2025/26"));
            series = await workspace.SaveCompetitionAsync(null, new("Calendar name", "SL W", s_summary.Date, Discipline.Slalom, RaceType.Fis, 2, 0, "0034", Calendar: calendar), series.Revision);
            await workspace.BackupAsync(transfer); await workspace.CloseAsync(); series = await workspace.OpenAsync(transfer);
            Assert.Equal(calendar, Assert.Single(series.Competitions).Values.Calendar);
            Assert.Equal("Default place", series.Values.Location); Assert.Equal("FIN", series.Values.Nation);
        }
        finally { Directory.Delete(folder, true); }
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(response(request));
    }
}
