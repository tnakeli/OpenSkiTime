using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

namespace OpenSkiTime.Tests;

// Synthetic pages with the markup of regs.fissi.fi and responses of its public API. The athletes do not exist.
internal static class FissiPages
{
    public sealed record Race(int Id, string Codex, string Discipline = "SL", string Gender = "Men");
    public sealed record Athlete(string Name, string Code = "—", string Year = "2008", string Nation = "FIN", string Club = "Synthetic Club");

    public static string Races(params Race[] races)
    {
        var page = new StringBuilder("""
            <!DOCTYPE html><html lang="en"><head><style>a:hover { color: #2563eb; }</style></head><body>
            <div class="v3-segments" role="tablist"><a href="/public/index.php?lang=en&amp;showPast=0" class="v3-segment active">Upcoming <span class="count">1</span></a></div>
            """);
        foreach (var race in races)
        {
            page.Append(CultureInfo.InvariantCulture, $"""
                <a class="v3-race-row" href="registered_athletes.php?race_id={race.Id}&amp;lang=en&amp;back=%2Fpublic%2Findex.php%3Flang%3Den">
                <div class="v3-stripe sl"></div><div class="v3-race-time"><span class="v3-race-day">28<span>.Nov</span></span></div>
                <div class="v3-race-body"><div class="v3-race-line1">
                <span class="v3-disc sl">{race.Discipline}</span>
                <span class="v3-race-name">Synthetic race</span>
                <span class="v3-race-codex">#{race.Codex}</span>
                </div><div class="v3-race-line2">
                <span class="v3-race-codex v3-race-codex--line2">#{race.Codex}</span>
                {race.Gender}                 · FIS                            </div></div>
                <div class="v3-race-right"><span class="v3-race-count">37</span><span class="v3-race-bar "><span style="width: 62%"></span></span></div>
                </a>
                """);
        }
        return page.Append("</body></html>").ToString();
    }

    public static string RacePage(string codex, string date, params Athlete[] athletes) => RacePage(codex, date, athletes.Length, athletes);

    public static string RacePage(string codex, string date, int count, IReadOnlyList<Athlete> athletes)
    {
        var page = new StringBuilder();
        page.Append(CultureInfo.InvariantCulture, $"""
            <!DOCTYPE html><html lang="en"><head><style>a:hover {OpenBrace} color: #2563eb; {CloseBrace}</style></head><body>
            <div class="v3-event-overview"><div class="head"><div><div class="title"><span class="v3-disc sl">SL</span></div>
            <div class="meta">
            <span class="v3-mono v3-tiny v3-upper">{date}</span>          ·<span><i class="fa-solid fa-location-dot"></i> Test place</span>          ·<span class="v3-mono">#{codex}</span>          ·<span class="v3-mono">FIS</span>
            """);
        page.Append(CultureInfo.InvariantCulture, $"""·<span><i class="fa-solid fa-users"></i> {count}</span>""");
        page.Append("</div></div></div></div>");
        if (athletes.Count == 0) { return page.Append("<p>No athletes registered yet.</p><style>#reg-ath-table a {}</style></body></html>").ToString(); }
        page.Append("""
            <table class="v3-athletes-table" id="reg-ath-table"><thead><tr><th style="width: 32px;">#</th><th data-sort="str" data-col="1">Athlete</th>
            <th data-sort="num" data-col="2" class="col-fis">FIS Code</th><th data-sort="num" data-col="3" class="col-year">Year</th>
            <th data-sort="str" data-col="4" class="col-nat">Nat.</th><th data-sort="str" data-col="5" class="col-club">Club</th>
            <th data-sort="num" data-col="6" data-sort-default class="col-pts" style="text-align:right;">Points</th>          </tr></thead><tbody>
            """);
        for (var i = 0; i < athletes.Count; i++)
        {
            var athlete = athletes[i];
            page.Append(CultureInfo.InvariantCulture, $"""
                <tr>
                <td class="mono row-num" style="color:var(--muted);">{i + 1}</td>
                <td class="name"><div class="name-wrap"><a class="fis-profile-link" href="https://example.invalid/?sectorcode=AL&amp;fiscode=1" title="FIS">{WebUtility.HtmlEncode(athlete.Name)}</a></div></td>
                <td class="mono col-fis">{athlete.Code}</td>
                <td class="mono col-year">{athlete.Year}</td>
                <td class="col-nat" title="{athlete.Nation}"><img src="fi.svg" alt="FI" loading="lazy"></td>
                <td class="col-club">{WebUtility.HtmlEncode(athlete.Club)}</td>
                <td class="mono col-pts" style="text-align:right;" data-val="999">999.00</td>          </tr>
                """);
        }
        return page.Append("</tbody></table></body></html>").ToString();
    }

    private const string OpenBrace = "{";
    private const string CloseBrace = "}";

    public static string ApiRace(string codex, string date, string discipline, string gender, params Athlete[] athletes)
        => JsonSerializer.Serialize(new
        {
            race = new { codex, name = "Synthetic race", date, location = "Test place", discipline, category = "FIS", gender },
            points_list = new { name = "Synthetic list", number = 1, valid_from = "2026-11-19", valid_to = "2026-11-25" },
            count = athletes.Length,
            athletes = athletes.Select(x => new { fis_code = x.Code, name = x.Name, birth_year = int.Parse(x.Year, CultureInfo.InvariantCulture), nation = x.Nation, points = (decimal?)null }),
        });

    // Answers like the public API: the race of the date, or the newest race of the codex without a date.
    public sealed class ApiSite(string key) : HttpMessageHandler
    {
        public RaceStore Races { get; } = new();
        public List<string> Requests { get; } = [];
        public HttpResponseMessage? Answer { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            if (uri.Scheme != "https" || uri.Host != "regs.fissi.fi") { throw new InvalidOperationException("Unexpected host."); }
            Requests.Add(uri.PathAndQuery);
            if (Answer is { } answer) { return Task.FromResult(answer); }
            if (!request.Headers.TryGetValues("X-API-Key", out var keys) || keys.Single() != key)
            { return Reply(HttpStatusCode.Unauthorized, """{"error":{"code":"invalid_api_key","message":"Synthetic"}}"""); }
            var query = uri.Query.TrimStart('?').Split('&').Select(x => x.Split('=')).ToDictionary(x => x[0], x => x[1]);
            var json = Races.Find(query["codex"], query.TryGetValue("date", out var date) ? DateOnly.ParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture) : null);
            return json is null ? Reply(HttpStatusCode.NotFound, """{"error":{"code":"race_not_found","message":"Synthetic"}}""") : Reply(HttpStatusCode.OK, json);
        }

        private static Task<HttpResponseMessage> Reply(HttpStatusCode status, string json)
            => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") });

        public sealed class RaceStore
        {
            private readonly Dictionary<(string Codex, DateOnly Date), string> _races = [];
            public string this[string codex, DateOnly date] { set => _races[(codex, date)] = value; }
            public string? Find(string codex, DateOnly? date) => _races.Where(x => x.Key.Codex == codex && (date is null || x.Key.Date == date))
                .OrderByDescending(x => x.Key.Date).Select(x => x.Value).FirstOrDefault();
        }
    }

    // Answers like the site: race lists by showPast, registration pages by race_id.
    public sealed class Site : HttpMessageHandler
    {
        public string Upcoming { get; set; } = Races();
        public string Past { get; set; } = Races();
        public Dictionary<int, string> Pages { get; } = [];
        public List<string> Requests { get; } = [];
        public Func<HttpRequestMessage, Task<HttpResponseMessage?>>? Intercept { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            Requests.Add(uri.PathAndQuery);
            if (Intercept is { } intercept && await intercept(request) is { } other) { return other; }
            if (uri.Scheme != "https" || uri.Host != "regs.fissi.fi") { throw new InvalidOperationException("Unexpected host."); }
            var html = uri.AbsolutePath.EndsWith("/index.php", StringComparison.Ordinal)
                ? uri.Query.Contains("showPast=1", StringComparison.Ordinal) ? Past : Upcoming
                : Pages.GetValueOrDefault(int.Parse(uri.Query.Split('=', '&')[1], CultureInfo.InvariantCulture), "Race not found");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(html, Encoding.UTF8, "text/html"), RequestMessage = request };
        }
    }
}
