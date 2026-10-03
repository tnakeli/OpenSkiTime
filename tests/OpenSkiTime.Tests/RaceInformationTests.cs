using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using OpenSkiTime.Application;
using OpenSkiTime.Domain;
using OpenSkiTime.Persistence;
using Xunit;

namespace OpenSkiTime.Tests;

public sealed class RaceInformationTests
{
    private static readonly DateOnly s_date = new(2026, 10, 2);
    private static readonly CompetitionValues s_competition = new("Synthetic slalom", "SL", s_date,
        Discipline.Slalom, RaceType.Fis, 2, 0, "0034", CourseName: "Test slope", StartAltitudeMeters: 500,
        FinishAltitudeMeters: 300, VerticalDropMeters: 200, HomologationNumber: "123/10/26");

    [Fact]
    public async Task InformationTransfersAndInvalidOrConflictingWritesLeaveHistoryIntact()
    {
        var folder = Path.Combine(Path.GetTempPath(), "openskitime-information-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore());
            var series = await workspace.CreateAsync(Path.Combine(folder, "test.ost"), new("Test", "Test", "Test", s_date, s_date, "FIN", "2026/27"));
            series = await workspace.SaveCompetitionAsync(null, s_competition, series.Revision);
            var id = series.Competitions[0].Id;
            var empty = RaceInformation.Empty(s_competition);
            var values = empty with { Runs = [empty.Runs[0] with { Length = 640,
                Forerunners = [new("A", new("Test", "RACER", "FIN")), new("B", new("Other", "RACER", "SWE"))],
                Weather = new("Clear", "Hard", 0.0m, -1.2m, "Synthetic forecast source") }, empty.Runs[1]] };
            var at = new DateTimeOffset(2026, 10, 2, 8, 0, 0, TimeSpan.Zero);
            var revision = await workspace.SaveRaceInformationAsync(id, values, series.Revision, at);
            await Assert.ThrowsAsync<SeriesConflictException>(() => workspace.SaveRaceInformationAsync(id, values, series.Revision, at));
            await Assert.ThrowsAsync<DomainValidationException>(() => workspace.SaveRaceInformationAsync(id,
                values with { Runs = [values.Runs[0] with { Gates = 40, TurningGates = 50 }, values.Runs[1]] }, revision, at));
            Assert.Equal(revision, (await workspace.ReadAsync()).Revision);
            await workspace.SaveRaceInformationAsync(id, values with { Category = "FIS" }, revision, at.AddMinutes(1));
            var transfer = Path.Combine(folder, "transfer.ost");
            await workspace.BackupAsync(transfer);
            await workspace.CloseAsync(); await workspace.OpenAsync(transfer);
            var saved = (await workspace.ReadRaceInformationAsync(id))!;
            Assert.Equal(2, saved.Revision); Assert.Equal("FIS", saved.Values.Category);
            Assert.Equal(-1.2m, saved.Values.Runs[0].Weather!.FinishTemperature);
            Assert.Equal(2, saved.Values.Runs[0].Forerunners!.Count);
            Assert.Equal("Synthetic forecast source", saved.Values.Runs[0].Weather!.Source);
            await using var connection = new SqliteConnection($"Data Source={transfer};Pooling=False");
            await connection.OpenAsync();
            await using var command = connection.CreateCommand(); command.CommandText = "SELECT COUNT(*) FROM RaceInformation";
            Assert.Equal(2L, await command.ExecuteScalarAsync());
            command.CommandText = "UPDATE RaceInformation SET ValuesJson='{}'";
            await Assert.ThrowsAsync<SqliteException>(() => command.ExecuteNonQueryAsync());
            command.CommandText = "DELETE FROM RaceInformation";
            await Assert.ThrowsAsync<SqliteException>(() => command.ExecuteNonQueryAsync());
        }
        finally { Directory.Delete(folder, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FisPublicLookupUsesExplicitSeasonAndOnlyApiKey(bool cancelled)
    {
        var requests = new List<string>();
        var detail = new FisCompetitionInformation(123, 34, 2027, s_date, "SL", "NC", "FIN", cancelled);
        using var handler = new ResponseHandler(request =>
        {
            requests.Add(request.RequestUri!.ToString());
            Assert.Equal("synthetic-key", Assert.Single(request.Headers.GetValues("X-Api-Key")));
            Assert.Null(request.Headers.Authorization);
            return JsonSerializer.Serialize(detail);
        });
        using var http = new HttpClient(handler);
        var preview = await new FisRaceInformationClient(http).BrowseAsync(s_competition, "FIN", "synthetic-key");
        Assert.EndsWith("/AL/34?season=2027", Assert.Single(requests));
        Assert.Equal("NC", Assert.Single(preview.Suggestions).Value);
        Assert.DoesNotContain("OAuth", preview.Note);
        Assert.Equal(cancelled, preview.Note.Contains("cancelled", StringComparison.Ordinal));
    }
    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task FisHttpFailuresRemainRecoverable(HttpStatusCode status)
    {
        using var handler = new ResponseHandler(_ => "{}", status); using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<DomainValidationException>(() => new FisRaceInformationClient(http).BrowseAsync(s_competition, "FIN", "synthetic"));
    }

    [Fact]
    public async Task FisMismatchStopsPublicImportAndLookupNeedsOnlyOneRequest()
    {
        var count = 0;
        var summary = new FisCompetitionInformation(123, 34, 2027, s_date, "SL", "FIS", "FIN", false);
        using var handler = new ResponseHandler(_ => { count++; return JsonSerializer.Serialize(summary); }); using var http = new HttpClient(handler);
        var client = new FisRaceInformationClient(http);
        Assert.Single((await client.BrowseAsync(s_competition, "FIN", "synthetic")).Suggestions); Assert.Equal(1, count);
        summary = summary with { SeasonCode = 2026 };
        await Assert.ThrowsAsync<DomainValidationException>(() => client.BrowseAsync(s_competition, "FIN", "synthetic"));
        Assert.Equal(2, count);
    }

    [Fact]
    public async Task WeatherKeepsLocalRaceDayNullValuesAndForecastProvenance()
    {
        using var handler = new ResponseHandler(request =>
        {
            Assert.Contains("timezone=auto", request.RequestUri!.Query, StringComparison.Ordinal);
            return """{"timezone":"Europe/Helsinki","hourly":{"time":["2026-10-01T23:00","2026-10-02T10:00","2026-10-02T13:00"],"temperature_2m":[1,0.0,null],"weather_code":[3,0,null]}}""";
        });
        using var http = new HttpClient(handler);
        var at = new DateTimeOffset(2026, 10, 2, 7, 0, 0, TimeSpan.Zero);
        var hours = await new WeatherBrowseClient(http).BrowseAsync(new("Test", 66m, 28m, "Finland", "Test", "Europe/Helsinki"), s_date, at);
        Assert.Equal(2, hours.Count); Assert.Equal("Clear", hours[0].Conditions); Assert.Null(hours[1].Temperature);
        Assert.Equal(at, hours[0].RetrievedAt); Assert.Equal("Europe/Helsinki", hours[0].Timezone);
        await Assert.ThrowsAsync<DomainValidationException>(() => new WeatherBrowseClient(http).BrowseAsync(new("Test", 66m, 28m, "Finland", "Test", "Europe/Helsinki"), s_date.AddDays(50), at));
    }

    private sealed class ResponseHandler(Func<HttpRequestMessage, string> response, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(response(request), Encoding.UTF8, "application/json") });
    }
}
