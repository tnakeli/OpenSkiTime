using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenSkiTime.Rewrite.Domain;

namespace OpenSkiTime.Rewrite.Application;

public sealed record FisCourseHomologation(int HomologationId, string HomologationCode, string CourseName,
    string Place, string OrganisationNationCode, string DisciplineCode, string EventCode, string GenderCode,
    int? StartAltitude, int? FinishAltitude, int? VerticalDrop, int? CourseLength, DateOnly? ValidFrom, DateOnly? ValidTo)
{
    public string Display => $"{CourseName} · {HomologationCode} · {GenderCode} · {StartAltitude}–{FinishAltitude} m · valid {ValidFrom:yyyy-MM-dd}–{ValidTo:yyyy-MM-dd}";
}

public sealed class FisCourseClient(HttpClient client)
{
    private static readonly JsonSerializerOptions s_json = new() { PropertyNameCaseInsensitive = true };
    private sealed record Page(FisCourseHomologation[] Data, Pagination? Meta);
    private sealed record Pagination([property: JsonPropertyName("current_page")] int CurrentPage,
        [property: JsonPropertyName("last_page")] int LastPage);

    public async Task<IReadOnlyList<FisCourseHomologation>> FindAsync(string location, string nation,
        Discipline discipline, string gender, DateOnly raceDate, string? apiKey = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(location); ArgumentNullException.ThrowIfNull(nation); ArgumentNullException.ThrowIfNull(gender);
        if (string.IsNullOrWhiteSpace(location) || location.Length > 160 || location.Any(char.IsControl))
        { throw new DomainValidationException("Enter the competition location before browsing homologations."); }
        if (nation.Length != 3 || !nation.All(char.IsAsciiLetter) || gender is not ("" or "W" or "M" or "A"))
        { throw new DomainValidationException("Check the competition nation and gender before browsing homologations."); }
        var eventCode = discipline switch { Discipline.Slalom => "SL", Discipline.GiantSlalom => "GS",
            Discipline.SuperG => "SG", Discipline.Downhill => "DH", _ => throw new DomainValidationException("Choose SL, GS, SG or DH for course homologations.") };
        var query = $"https://api.fis-ski.com/homologation/course/AL?place={Uri.EscapeDataString(location.Trim())}&eventCode={eventCode}&nationCode={nation.ToUpperInvariant()}";
        var results = new List<FisCourseHomologation>();
        for (var pageNumber = 1; pageNumber <= 20; pageNumber++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, query + "&page=" + pageNumber.ToString(CultureInfo.InvariantCulture));
            request.Headers.Accept.ParseAdd("application/json");
            if (!string.IsNullOrWhiteSpace(apiKey)) { request.Headers.Add("X-Api-Key", apiKey); }
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode) { throw new DomainValidationException($"FIS homologation lookup failed (HTTP {(int)response.StatusCode}). Existing course fields were preserved."); }
            await using var input = await response.Content.ReadAsStreamAsync(ct);
            using var output = new MemoryStream(); var buffer = new byte[81920]; int count;
            while ((count = await input.ReadAsync(buffer, ct)) > 0)
            {
                if (output.Length + count > 5_000_000) { throw new DomainValidationException("FIS homologation response is unexpectedly large."); }
                await output.WriteAsync(buffer.AsMemory(0, count), ct);
            }
            Page data;
            try { data = JsonSerializer.Deserialize<Page>(output.ToArray(), s_json) ?? throw new JsonException(); }
            catch (JsonException) { throw new DomainValidationException("FIS returned an unreadable homologation response. Existing fields were preserved."); }
            if (data.Data is null || data.Meta is { } meta && (meta.CurrentPage != pageNumber || meta.LastPage < pageNumber || meta.LastPage > 20))
            { throw new DomainValidationException("FIS homologation pagination is invalid or too large."); }
            foreach (var course in data.Data)
            {
                if (!string.Equals(course.Place, location.Trim(), StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(course.OrganisationNationCode, nation, StringComparison.OrdinalIgnoreCase)
                    || course.DisciplineCode != "AL" || course.EventCode != eventCode
                    || (gender.Length > 0 && gender != "A" && course.GenderCode != "A" && course.GenderCode != gender)
                    || (course.ValidFrom is { } from && raceDate < from) || (course.ValidTo is { } to && raceDate > to)) { continue; }
                if (course.HomologationId <= 0 || string.IsNullOrWhiteSpace(course.CourseName) || course.CourseName.Length > 160
                    || string.IsNullOrWhiteSpace(course.HomologationCode) || course.HomologationCode.Length > 50
                    || new[] { course.StartAltitude, course.FinishAltitude, course.VerticalDrop, course.CourseLength }.Any(x => x is < 0 or > 9000))
                { throw new DomainValidationException("FIS returned invalid course dimensions or identifiers. Existing fields were preserved."); }
                results.Add(course);
                if (results.Count > 2000) { throw new DomainValidationException("Too many FIS homologations; narrow the location."); }
            }
            if (data.Meta is null || pageNumber == data.Meta.LastPage)
            { return results.DistinctBy(x => x.HomologationId).OrderBy(x => x.CourseName, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.HomologationCode, StringComparer.Ordinal).ToArray(); }
        }
        throw new DomainValidationException("FIS homologation pagination exceeded its limit.");
    }
}
