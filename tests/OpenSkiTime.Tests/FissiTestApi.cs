using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

namespace OpenSkiTime.Tests;

// Synthetic responses of the regs.fissi.fi public API. The athletes do not exist.
internal static class FissiTestApi
{
    public sealed record Athlete(string Name, string Code, int Year = 2008, string Nation = "FIN");

    public static string Race(string codex, string date, string discipline, string gender, params Athlete[] athletes)
        => JsonSerializer.Serialize(new
        {
            race = new { codex, name = "Synthetic race", date, location = "Test place", discipline, category = "FIS", gender },
            points_list = new { name = "Synthetic list", number = 1, valid_from = "2026-11-19", valid_to = "2026-11-25" },
            count = athletes.Length,
            athletes = athletes.Select(x => new { fis_code = x.Code, name = x.Name, birth_year = x.Year, nation = x.Nation, points = (decimal?)null }),
        });

    // Answers like the API: the race of the date, or the newest race of the codex without a date.
    public sealed class Site(string key) : HttpMessageHandler
    {
        public RaceStore Races { get; } = new();
        public List<string> Requests { get; } = [];
        // Answers instead of the API, or delays its answer (null).
        public Func<Task<HttpResponseMessage?>>? Intercept { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            if (uri.Scheme != "https" || uri.Host != "regs.fissi.fi") { throw new InvalidOperationException("Unexpected host."); }
            Requests.Add(uri.PathAndQuery);
            if (Intercept is { } intercept && await intercept() is { } other) { return other; }
            if (!request.Headers.TryGetValues("X-API-Key", out var keys) || keys.Single() != key)
            { return Reply(HttpStatusCode.Unauthorized, """{"error":{"code":"invalid_api_key","message":"Synthetic"}}"""); }
            var query = uri.Query.TrimStart('?').Split('&').Select(x => x.Split('=')).ToDictionary(x => x[0], x => x[1]);
            var json = Races.Find(query["codex"], query.TryGetValue("date", out var date) ? DateOnly.ParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture) : null);
            return json is null ? Reply(HttpStatusCode.NotFound, """{"error":{"code":"race_not_found","message":"Synthetic"}}""") : Reply(HttpStatusCode.OK, json);
        }

        private static HttpResponseMessage Reply(HttpStatusCode status, string json)
            => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }

    public sealed class RaceStore
    {
        private readonly Dictionary<(string Codex, DateOnly Date), string> _races = [];
        public string this[string codex, DateOnly date] { set => _races[(codex, date)] = value; }
        public string? Find(string codex, DateOnly? date) => _races.Where(x => x.Key.Codex == codex && (date is null || x.Key.Date == date))
            .OrderByDescending(x => x.Key.Date).Select(x => x.Value).FirstOrDefault();
    }
}
