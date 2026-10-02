using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using OpenSkiTime.Rewrite.Application;
using OpenSkiTime.Rewrite.Domain;
using OpenSkiTime.Rewrite.Persistence;
using Xunit;

namespace OpenSkiTime.Rewrite.Tests;

public sealed class SeriesCalendarTests
{
    private static readonly SeriesValues s_series = new("Synthetic weekend", "Test place", "Synthetic club", new(2026, 3, 21), new(2026, 3, 22), "FIN", "2025/26");
    private static CompetitionValues Race(string codex, Discipline discipline = Discipline.Slalom)
        => new("Synthetic race", "SL W " + codex, new(2026, 3, 21), discipline, RaceType.Fis, 2, 0, codex,
            Calendar: new(2026, "Test place", "FIN", "FIS", "W", new("TESTLAST", "Testfirst", "FIN", "1047")));

    private static byte[] Archive(bool duplicate = false)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            using (var writer = new StreamWriter(zip.CreateEntry("A_event.csv").Open(), new UTF8Encoding(false)))
            { writer.Write("Eventid\tSeasoncode\tSectorcode\tEventname\tStartdate\tEnddate\tPlace\tNationcodeplace\tOrgaddressL1\n456\t2026\tAL\tSynthetic weekend\t2026-03-21\t2026-03-22\tTest place\tFIN\tSynthetic club\n999\t2026\tCC\tOther discipline\t2026-03-21\t2026-03-21\tOther place\tSWE\tOther club\n"); }
            using (var writer = new StreamWriter(zip.CreateEntry("A_raceal.csv").Open(), new UTF8Encoding(false)))
            {
                var first = "123\t456\t2026\t0034\tSL\tFIS\tW\t\0" + "2026-03-21 00:00:00\0\tTest place\tFIN\tTestlast Testfirst (FIN)\tFIN\t1047\t123/10/26\n";
                writer.Write("Raceid\tEventid\tSeasoncode\tRacecodex\tDisciplinecode\tCatcode\tGender\tRacedate\tPlace\tNationcode\tTd1name\tTd1nation\tTd1code\tHomol\n" + first
                    + "124\t456\t2026\t0035\tDH\tMAS\tM\t2026-03-22\tTest place\tFIN\t\t\t\t\n"
                    + "125\t456\t2026\t0036\tPAR\tFIS\tW\t2026-03-22\tTest place\tFIN\t\t\t\t\n" + (duplicate ? first : ""));
            }
        }
        return output.ToArray();
    }

    [Fact]
    public async Task OnePublicCalendarDownloadJoinsEventsAndSupportedRacesWithAllAvailableFields()
    {
        var calls = 0;
        using var handler = new Handler(request =>
        {
            calls++; Assert.Equal("https://api.fis-ski.com/data-feeds/calendar", request.RequestUri!.AbsoluteUri);
            Assert.Null(request.Headers.Authorization); Assert.Equal("synthetic-key", Assert.Single(request.Headers.GetValues("X-Api-Key")));
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(Archive()) };
        });
        using var http = new HttpClient(handler);
        var item = Assert.Single(await new FisRaceInformationClient(http).GetCalendarAsync(2026, "synthetic-key"));
        Assert.Equal(1, calls); Assert.Equal(s_series.Name, item.Name); Assert.Equal(s_series.Organizer, item.Organizer);
        Assert.Equal(s_series.Location, item.Location); Assert.Equal(s_series.Nation, item.Nation);
        Assert.Equal(s_series.StartDate, item.StartDate); Assert.Equal(s_series.EndDate, item.EndDate);
        Assert.Equal(2, item.Competitions.Count); Assert.Equal("0034", item.Competitions[0].FisCode);
        Assert.Equal("TESTLAST", item.Competitions[0].Calendar!.TechnicalDelegate!.LastName);
        Assert.Equal("1047", item.Competitions[0].Calendar!.TechnicalDelegate!.Number);
        Assert.Equal("123/10/26", item.Competitions[0].HomologationNumber);
        Assert.Equal(Discipline.Downhill, item.Competitions[1].Discipline); Assert.Equal(1, item.Competitions[1].RunCount);
        Assert.Equal("MAS", item.Competitions[1].Calendar!.Category);
        Assert.Empty(FisRaceInformationClient.ReadCalendarEvents(Archive(), 2027, "synthetic"));
        Assert.Throws<DomainValidationException>(() => FisRaceInformationClient.ReadCalendarEvents(Archive(true), 2026, "synthetic"));
    }

    [Fact]
    public async Task CancelledRaceAllowsEventButChangedIdentityStillRejectsIt()
    {
        var item = Assert.Single(FisRaceInformationClient.ReadCalendarEvents(Archive(), 2026, "synthetic"));
        var changed = false;
        using var handler = new Handler(request => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(
            request.RequestUri!.AbsolutePath.EndsWith("/34", StringComparison.Ordinal)
                ? new FisCompetitionInformation(123, 34, 2026, new(2026, 3, changed ? 20 : 21), "SL", "FIS", "FIN", true, "Test place", "W", 456)
                : new FisCompetitionInformation(124, 35, 2026, new(2026, 3, 22), "DH", "MAS", "FIN", true, "Test place", "M", 456))) });
        using var http = new HttpClient(handler);
        await new FisRaceInformationClient(http).ValidateCalendarEventAsync(item, "synthetic-key");
        changed = true;
        await Assert.ThrowsAsync<DomainValidationException>(() => new FisRaceInformationClient(http).ValidateCalendarEventAsync(item, "synthetic-key"));
    }

    [Fact]
    public async Task NewFileContainsTheWholeSelectedEventAndInvalidSelectionCreatesNoFile()
    {
        var folder = Path.Combine(Path.GetTempPath(), "openskitime-series-calendar-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        try
        {
            var file = Path.Combine(folder, "calendar.ost");
            await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore());
            var item = Assert.Single(FisRaceInformationClient.ReadCalendarEvents(Archive(), 2026, "synthetic"));
            var created = await workspace.CreateAsync(file, s_series, item.Competitions);
            Assert.Equal(2, created.Competitions.Count);
            await workspace.CloseAsync(); var reopened = await workspace.OpenAsync(file);
            Assert.Equal(s_series, reopened.Values); Assert.Equal(item.Competitions.Select(x => x.FisCode), reopened.Competitions.Select(x => x.Values.FisCode));
            var invalid = Path.Combine(folder, "invalid.ost");
            await Assert.ThrowsAsync<DomainValidationException>(() => workspace.CreateAsync(invalid, s_series, [Race("0034"), Race("0034")]));
            Assert.False(File.Exists(invalid)); Assert.Equal(file, workspace.FilePath);
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public async Task ReimportUpdatesBySeasonAndCodexPreservesLocalFieldsAndRollsBackConflicts()
    {
        var folder = Path.Combine(Path.GetTempPath(), "openskitime-calendar-update-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        try
        {
            await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore());
            var original = Race("0034") with { ShortLabel = "Local label", CourseName = "Local course", StartAltitudeMeters = 500, HomologationNumber = "123/10/26" };
            var created = await workspace.CreateAsync(Path.Combine(folder, "test.ost"), s_series, [original]);
            var id = created.Competitions[0].Id;
            var incoming = Race("0034") with { Name = "Updated name", Calendar = Race("0034").Calendar! with { TechnicalDelegate = new("TESTLAST", "Testfirst", "FIN", "") } };
            var updated = await workspace.ApplyCalendarAsync(s_series with { Name = "Updated series" }, [incoming, Race("0035")], created.Revision);
            Assert.Equal(2, updated.Competitions.Count); var race = updated.Competitions.Single(x => x.Id == id).Values;
            Assert.Equal("Updated name", race.Name); Assert.Equal("Local label", race.ShortLabel); Assert.Equal("Local course", race.CourseName);
            Assert.Equal(500, race.StartAltitudeMeters); Assert.Equal("123/10/26", race.HomologationNumber); Assert.Equal("1047", race.Calendar!.TechnicalDelegate!.Number);
            updated = await workspace.ApplyCalendarAsync(updated.Values, [incoming, Race("0035")], updated.Revision);
            Assert.Equal(2, updated.Competitions.Count); Assert.Equal(id, updated.Competitions.Single(x => x.Values.FisCode == "0034").Id);
            await Assert.ThrowsAsync<DomainValidationException>(() => workspace.ApplyCalendarAsync(s_series,
                [incoming with { Name = "Must not commit" }, Race("0036") with { ShortLabel = "Local label" }], updated.Revision));
            var unchanged = await workspace.ReadAsync(); Assert.Equal(updated.Revision, unchanged.Revision); Assert.Equal(updated.Values, unchanged.Values);
            Assert.Equal(updated.Competitions, unchanged.Competitions);
            await Assert.ThrowsAsync<SeriesConflictException>(() => workspace.ApplyCalendarAsync(s_series, [incoming], created.Revision));
            var entrant = await workspace.SaveDeskRowAsync(null, new("TESTATHLETE", "Testfirst", 2000, "100001", "FIN", "Synthetic", Gender.Female),
                id, true, null, updated.Revision);
            var plan = FisStartOrder.FirstRun(id, race, Gender.Female, [new(entrant.Value.Id, entrant.Value.Values, 10)],
                new("SYNTHETIC", new(2026, 3, 1), new(2026, 3, 31)), new(), "synthetic-seed");
            var lists = await workspace.SaveStartListAsync(new(plan, entrant.Revision, "Test operator", "Test draw",
                new DateTimeOffset(2026, 3, 20, 12, 0, 0, TimeSpan.Zero)));
            var before = await workspace.ReadAsync();
            await Assert.ThrowsAsync<DomainValidationException>(() => workspace.ApplyCalendarAsync(s_series,
                [Race("0036"), incoming with { Date = incoming.Date.AddDays(1) }], before.Revision));
            Assert.Equal(before.Competitions, (await workspace.ReadAsync()).Competitions);
            Assert.Equal(before.Revision, (await workspace.ReadAsync()).Revision);
            Assert.Equal(JsonSerializer.Serialize(lists.Revisions), JsonSerializer.Serialize((await workspace.ReadStartListsAsync(id)).Revisions));
        }
        finally { Directory.Delete(folder, true); }
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(response(request)); }
}
