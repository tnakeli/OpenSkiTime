using System.Net;
using System.Text;
using System.Text.Json;
using OpenSkiTime.Rewrite.Application;
using OpenSkiTime.Rewrite.Domain;
using Xunit;

namespace OpenSkiTime.Rewrite.Tests;

public sealed class CompetitionToolsTests
{
    [Theory]
    [InlineData(2026, 5, 31, 2026, "2025/26")]
    [InlineData(2026, 6, 1, 2027, "2026/27")]
    [InlineData(2026, 10, 2, 2027, "2026/27")]
    public void SeasonUsesExplicitDateAndJuneBoundary(int year, int month, int day, int season, string label)
    {
        var date = new DateOnly(year, month, day);
        Assert.Equal(season, FisSeason.FromDate(date)); Assert.Equal(label, FisSeason.SeriesLabel(date));
    }

    [Theory]
    [InlineData("2025/26", 2026)]
    [InlineData("2025–26", 2026)]
    [InlineData("2025-2026", 2026)]
    [InlineData("2026", 2026)]
    [InlineData("1999/00", 2000)]
    [InlineData("Invalid", 2027)]
    public void CompetitionSeasonPrefersSeriesSeason(string series, int expected)
        => Assert.Equal(expected, FisSeason.FromSeries(series, new(2026, 10, 2)));

    private static FisCourseHomologation Course(int id, string gender = "A", DateOnly? to = null) => new(id, "123/10/26", "Test slope",
        "Test place", "FIN", "AL", "SL", gender, 500, 300, 200, 640, new(2025, 1, 1), to ?? new(2030, 1, 1));

    [Fact]
    public async Task HomologationsUsePublicFiltersPaginationAndRaceDateIncludingAllGenderCourses()
    {
        var calls = new List<string>();
        using var handler = new Handler(request =>
        {
            calls.Add(request.RequestUri!.ToString()); Assert.Null(request.Headers.Authorization);
            var page = calls.Count;
            var courses = page == 1 ? new[] { Course(1), Course(2, "M"), Course(3, to: new(2026, 1, 1)) }
                : new[] { Course(4, "W"), Course(5) with { Place = "Wrong place" } };
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { data = courses,
                meta = new { current_page = page, last_page = 2 }, links = new { next = "https://untrusted.example/page" } }), Encoding.UTF8, "application/json") };
        });
        using var http = new HttpClient(handler);
        var found = await new FisCourseClient(http).FindAsync("Test place", "FIN", Discipline.Slalom, "W", new(2026, 3, 21));
        Assert.Equal([1, 4], found.Select(x => x.HomologationId));
        Assert.Equal(2, calls.Count); Assert.All(calls, x => Assert.StartsWith("https://api.fis-ski.com/homologation/course/AL?place=Test place&eventCode=SL&nationCode=FIN&page=", x, StringComparison.Ordinal));
        Assert.Equal(640, found[0].CourseLength);
    }

    [Fact]
    public async Task InvalidHomologationResponseRejectsWholeFetch()
    {
        using var handler = new Handler(_ => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new {
            data = new[] { Course(1), Course(2) with { StartAltitude = -1 } } }), Encoding.UTF8, "application/json") });
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<DomainValidationException>(() => new FisCourseClient(http).FindAsync("Test place", "FIN", Discipline.Slalom, "W", new(2026, 3, 21)));
    }

    [Fact]
    public void ChangedCompetitionDefaultsPreserveSecondRunDifferences()
    {
        var race = new CompetitionValues("Test", "Test", new(2026, 3, 21), Discipline.Slalom, RaceType.Fis, 2, 0, "0034",
            CourseName: "Test slope", StartAltitudeMeters: 500, FinishAltitudeMeters: 300, VerticalDropMeters: 200,
            HomologationNumber: "123/10/26", CourseLengthMeters: 640);
        var info = RaceInformation.Empty(race);
        info = info with { Runs = [info.Runs[0], info.Runs[1] with { StartAltitude = 480, Drop = 180 }] };
        var changed = race with { CourseName = "New slope", StartAltitudeMeters = 510, FinishAltitudeMeters = 310,
            HomologationNumber = "234/10/26", CourseLengthMeters = 650 };
        var effective = info.WithCompetitionCourse(changed);
        Assert.Equal(510, effective.Runs[0].StartAltitude); Assert.Equal(480, effective.Runs[1].StartAltitude);
        Assert.Equal(180, effective.Runs[1].Drop); Assert.All(effective.Runs, x => Assert.Equal(310, x.FinishAltitude));
        Assert.All(effective.Runs, x => Assert.Equal("234/10/26", x.Homologation));
        Assert.All(effective.Runs, x => Assert.Equal(650, x.Length)); Assert.Equal("Test slope", info.Runs[0].Course);
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(response(request));
    }
}
