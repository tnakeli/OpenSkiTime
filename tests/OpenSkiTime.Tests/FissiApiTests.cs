using System.Net;
using System.Text;
using OpenSkiTime.Application;
using OpenSkiTime.Domain;
using Xunit;
using static OpenSkiTime.Tests.FissiPages;

namespace OpenSkiTime.Tests;

public sealed class FissiApiTests
{
    private const string Key = "fissi_synthetic";
    private static readonly DateOnly s_date = new(2026, 11, 28);
    private static readonly IReadOnlySet<Guid> s_none = new HashSet<Guid>();

    private static CompetitionDetails Competition(string label, string? codex, string gender = "M",
        Discipline discipline = Discipline.Slalom, DateOnly? date = null)
        => new(Guid.NewGuid(), new CompetitionValues("Synthetic " + label, label, date ?? s_date, discipline, RaceType.Fis, 2, 0, codex,
            Calendar: new(2027, "Test place", "FIN", "FIS", gender)));

    private static FissiApiRace Read(string json) => FissiApiJson.ReadRace(Encoding.UTF8.GetBytes(json));

    // ---- Responses

    [Fact]
    public void ResponseGivesTheRaceAndItsAthletes()
    {
        var result = Read(ApiRace("0507", "2026-11-28", "SL", "Men", new Athlete("Testi ESIMERKKI", "100001"), new Athlete("Åke O'SYNTHETIC", "6530001", "2010", "SWE")));
        Assert.Equal(new FissiRace(0, "0507", "SL", "M"), result.Race);
        Assert.Equal((s_date, "0507"), (result.Page.Date, result.Page.Codex));
        Assert.Equal([new("100001", "Testi ESIMERKKI", 2008, "FIN", null), new("6530001", "Åke O'SYNTHETIC", 2010, "SWE", null)], result.Page.Athletes);
    }

    [Theory]
    [InlineData("M", "M")]
    [InlineData("women", "W")]
    [InlineData("L", "W")]
    [InlineData("Mixed", "")]
    public void GenderIsReadInItsCommonForms(string value, string expected)
        => Assert.Equal(expected, Read(ApiRace("0507", "2026-11-28", "Slalom", value)).Race.Gender);

    [Fact]
    public void NumbersAndMissingValuesAreAccepted()
    {
        var result = Read("""
            {"race":{"codex":507,"name":"Synthetic","date":"2026-11-28","location":"Test","discipline":"GS","category":"FIS","gender":null},"points_list":null,"count":2,
             "athletes":[{"fis_code":100001,"name":"Testi  ESIMERKKI","birth_year":2008,"nation":"fin","points":12.34},
                         {"fis_code":null,"name":"Nobody NOCODE","birth_year":null,"nation":"","points":null}]}
            """);
        Assert.Equal(new FissiRace(0, "0507", "GS", ""), result.Race);
        Assert.Equal([new("100001", "Testi ESIMERKKI", 2008, "FIN", null), new(null, "Nobody NOCODE", null, null, null)], result.Page.Athletes);
    }

    // A response that is not completely understood must never look like a shorter registration list.
    [Fact]
    public void IncompleteResponsesAreRefused()
    {
        var valid = ApiRace("0507", "2026-11-28", "SL", "Men", new Athlete("Testi ESIMERKKI", "100001"), new Athlete("Toinen ESIMERKKI", "100002"));
        Assert.Equal(2, Read(valid).Page.Athletes.Count);
        var broken = new Dictionary<string, string>
        {
            ["no json"] = "<html>Maintenance</html>",
            ["truncated"] = valid[..^3],
            ["array"] = "[]",
            ["no race"] = valid.Replace("\"race\"", "\"event\"", StringComparison.Ordinal),
            ["no count"] = valid.Replace("\"count\"", "\"total\"", StringComparison.Ordinal),
            ["count differs"] = valid.Replace("\"count\":2", "\"count\":3", StringComparison.Ordinal),
            ["no athletes"] = valid.Replace("\"athletes\"", "\"starters\"", StringComparison.Ordinal),
            ["renamed code"] = valid.Replace("\"fis_code\"", "\"fiscode\"", StringComparison.Ordinal),
            ["renamed name"] = valid.Replace("\"name\"", "\"full_name\"", StringComparison.Ordinal),
            ["renamed year"] = valid.Replace("\"birth_year\"", "\"year\"", StringComparison.Ordinal),
            ["renamed nation"] = valid.Replace("\"nation\"", "\"nat\"", StringComparison.Ordinal),
            ["no name"] = valid.Replace("Toinen ESIMERKKI", "", StringComparison.Ordinal),
            ["bad code"] = valid.Replace("100002", "1000 02!", StringComparison.Ordinal),
            ["bad nation"] = valid.Replace("FIN", "Finland", StringComparison.Ordinal),
            ["bad year"] = valid.Replace("2008", "08", StringComparison.Ordinal),
            ["bad date"] = valid.Replace("2026-11-28", "28.11.2026", StringComparison.Ordinal),
            ["bad codex"] = valid.Replace("0507", "SL-1", StringComparison.Ordinal),
            ["error"] = """{"error":{"code":"x","message":"y"}}""",
        };
        foreach (var (name, json) in broken)
        {
            Assert.NotEqual(valid, json);
            Assert.True(Record.Exception(() => Read(json)) is DomainValidationException, name);
        }
    }

    // ---- Finding the races

    [Fact]
    public async Task EveryCompetitionIsReadByCodexAndDateWithTheKeyInAHeaderOnly()
    {
        using var site = new ApiSite(Key);
        site.Races["0507", s_date] = ApiRace("0507", "2026-11-28", "SL", "Men", new Athlete("Testi ESIMERKKI", "100001"));
        site.Races["5489", new(2026, 11, 29)] = ApiRace("5489", "2026-11-29", "SL", "Women", new Athlete("Tiina ESIMERKKI", "100002"));
        site.Races["2075", s_date] = ApiRace("2075", "2026-11-28", "SL", "Men");
        site.Races["0600", s_date] = ApiRace("0600", "2026-11-28", "GS", "Men", new Athlete("Testi ESIMERKKI", "100001"));
        site.Races["0900", new(2025, 11, 28)] = ApiRace("0900", "2025-11-28", "SL", "Men", new Athlete("Testi ESIMERKKI", "100001"));
        using var http = new HttpClient(site);
        var locked = Competition("LOCKED", "0507");
        var competitions = new[]
        {
            Competition("SL M", "507"), Competition("MOVED", "5489", "W"), Competition("EMPTY", "2075"), Competition("NO CODEX", null), Competition("UNKNOWN", "9999"),
            Competition("GENDER", "5489"), Competition("DISCIPLINE", "0600"), Competition("OTHER SEASON", "0900"), locked,
        };
        var found = await new FissiApiClient(http, Key).GetAsync(competitions, new HashSet<Guid> { locked.Id });
        Assert.Equal([FissiRaceStatus.Ready, FissiRaceStatus.DateDiffers, FissiRaceStatus.NoneRegistered, FissiRaceStatus.NoCodex, FissiRaceStatus.NotFound,
            FissiRaceStatus.RaceDiffers, FissiRaceStatus.RaceDiffers, FissiRaceStatus.NotFound, FissiRaceStatus.StartListSaved], found.Select(x => x.Status));
        Assert.Equal(competitions.Select(x => x.Id), found.Select(x => x.CompetitionId));
        Assert.Equal("/public/api/race_athletes.php?codex=0507&date=2026-11-28", site.Requests[0]);
        Assert.Contains("/public/api/race_athletes.php?codex=5489", site.Requests);
        Assert.DoesNotContain(site.Requests, x => x.Contains(Key, StringComparison.Ordinal));
    }

    [Fact]
    public async Task OneRaceIsNotUsedForTwoCompetitions()
    {
        using var site = new ApiSite(Key);
        site.Races["0507", s_date] = ApiRace("0507", "2026-11-28", "SL", "Men", new Athlete("Testi ESIMERKKI", "100001"));
        using var http = new HttpClient(site);
        var found = await new FissiApiClient(http, Key).GetAsync([Competition("SL1", "0507"), Competition("SL2", "0507")], s_none);
        Assert.All(found, x => { Assert.Equal(FissiRaceStatus.SeveralMatches, x.Status); Assert.Null(x.Page); });
    }

    [Fact]
    public async Task FailuresStopTheWholeUpdateAndNeverShowTheKey()
    {
        using var site = new ApiSite(Key);
        using var http = new HttpClient(site);
        async Task<string> FailAsync(string key, HttpResponseMessage? response = null)
        {
            site.Answer = response;
            var error = await Assert.ThrowsAsync<DomainValidationException>(() => new FissiApiClient(http, key).GetAsync([Competition("SL", "0507")], s_none));
            Assert.DoesNotContain("fissi_", error.Message, StringComparison.Ordinal);
            return error.Message;
        }
        static HttpResponseMessage Error(HttpStatusCode status, string body = """{"error":{"code":"synthetic","message":"fissi_echo"}}""")
            => new(status) { Content = new StringContent(body) };
        Assert.Contains("Save the regs.fissi.fi API key", await FailAsync(""), StringComparison.Ordinal);
        Assert.Contains("cannot be sent", await FailAsync("fissi_a\r\nX-Other: 1"), StringComparison.Ordinal);
        Assert.Empty(site.Requests);
        Assert.Contains("rejected the API key", await FailAsync("fissi_wrong"), StringComparison.Ordinal);
        var limited = Error(HttpStatusCode.TooManyRequests); limited.Headers.RetryAfter = new(TimeSpan.FromSeconds(30));
        Assert.Contains("Try again in 30 s", await FailAsync(Key, limited), StringComparison.Ordinal);
        Assert.Contains("Try again later", await FailAsync(Key, Error(HttpStatusCode.TooManyRequests)), StringComparison.Ordinal);
        Assert.Contains("HTTP 400", await FailAsync(Key, Error(HttpStatusCode.BadRequest)), StringComparison.Ordinal);
        Assert.Contains("HTTP 302", await FailAsync(Key, Error(HttpStatusCode.Redirect)), StringComparison.Ordinal);
        // A missing endpoint is a failure, not an unknown race.
        Assert.Contains("HTTP 404", await FailAsync(Key, Error(HttpStatusCode.NotFound, "<html>404</html>")), StringComparison.Ordinal);
        Assert.Contains("HTTP 404", await FailAsync(Key, Error(HttpStatusCode.NotFound)), StringComparison.Ordinal);
        Assert.Contains("another codex", await FailAsync(Key, Error(HttpStatusCode.OK, ApiRace("0508", "2026-11-28", "SL", "Men"))), StringComparison.Ordinal);
        Assert.Contains("large", await FailAsync(Key, new(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[5_000_001]) }), StringComparison.Ordinal);
        Assert.Contains("cannot be read completely", await FailAsync(Key, Error(HttpStatusCode.OK, "{}")), StringComparison.Ordinal);
    }

    // The API has no club: the saved one stays, and the plan is otherwise the same as for the web pages.
    [Fact]
    public async Task TheSavedClubStaysWhenTheAthleteComesFromTheApi()
    {
        using var site = new ApiSite(Key);
        site.Races["0507", s_date] = ApiRace("0507", "2026-11-28", "SL", "Men", new Athlete("Testi ESIMERKKI", "100001"), new Athlete("Uusi TULOKAS", "100002"));
        using var http = new HttpClient(site);
        var race = Competition("SL", "0507");
        var series = new SeriesDetails(Guid.NewGuid(), new("Synthetic", "Test place", "Club", s_date, s_date, "FIN", "2026/27"), 1, [race]);
        var known = new CompetitorDetails(Guid.NewGuid(), new("ESIMERKKI", "Testi", 2008, "100001", "FIN", "Saved Club", Gender.Male));
        var desk = new CompetitorDeskDetails(series.Id, 1, [known], [new(known.Id, race.Id, true, null, null)], []);
        var plan = FissiRegistrationPlan.Build(series, desk, await new FissiApiClient(http, Key).GetAsync([race], s_none), s_none, s_none);
        var row = Assert.Single(plan.Rows);
        Assert.Equal(new CompetitorValues("TULOKAS", "Uusi", 2008, "100002", "FIN", null, Gender.Male), row.Values);
        Assert.Equal((2, 1, 1, 0), (plan.Races[0].Registered, plan.Races[0].New, plan.Races[0].Added, plan.Races[0].Removed));
    }
}
