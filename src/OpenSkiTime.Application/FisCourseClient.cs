using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenSkiTime.Domain;

namespace OpenSkiTime.Application;

public sealed record FisCourseHomologation(int HomologationId, string HomologationCode, string CourseName,
    string Place, string OrganisationNationCode, string DisciplineCode, string EventCode, string GenderCode,
    int? StartAltitude, int? FinishAltitude, int? VerticalDrop, int? CourseLength, DateOnly? ValidFrom, DateOnly? ValidTo)
{
    public string Display => $"{Place} · {CourseName} · {HomologationCode} · {GenderLabel} · {StartAltitude}–{FinishAltitude} m · valid {ValidFrom:yyyy-MM-dd}–{ValidTo:yyyy-MM-dd}";
    private string GenderLabel => GenderCode switch { "M" => "Men", "W" or "L" => "Women", "A" => "Men & Women", _ => GenderCode };
}

public sealed class FisCourseClient(HttpClient client)
{
    private static readonly JsonSerializerOptions s_json = new() { PropertyNameCaseInsensitive = true };
    private sealed record Page(FisCourseHomologation[] Data, Pagination? Meta);
    private sealed record Pagination([property: JsonPropertyName("current_page")] int CurrentPage,
        [property: JsonPropertyName("last_page")] int LastPage);

    /// <summary>
    /// Every course homologated in the nation for the discipline that is valid on the race date and fits the gender,
    /// sorted by place, course and code. The location does not filter: a moved race, a wrong calendar location or a race
    /// without a FIS calendar entry can still choose any course. <see cref="BestMatch"/> suggests one.
    /// </summary>
    public async Task<IReadOnlyList<FisCourseHomologation>> FindAsync(string nation,
        Discipline discipline, string gender, DateOnly raceDate, string? apiKey = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(nation); ArgumentNullException.ThrowIfNull(gender);
        if (nation.Length != 3 || !nation.All(char.IsAsciiLetter) || gender is not ("" or "W" or "M" or "A"))
        { throw new DomainValidationException("Check the competition nation and gender before browsing homologations."); }
        var eventCode = discipline switch { Discipline.Slalom => "SL", Discipline.GiantSlalom => "GS",
            Discipline.SuperG => "SG", Discipline.Downhill => "DH", _ => throw new DomainValidationException("Choose SL, GS, SG or DH for course homologations.") };
        var results = new List<FisCourseHomologation>();
        await FetchAsync(nation, eventCode, gender, raceDate, apiKey, results, ct);
        return results.DistinctBy(x => x.HomologationId)
            .OrderBy(x => x.Place, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.CourseName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.HomologationCode, StringComparer.Ordinal).ToArray();
    }

    /// <summary>
    /// The course to preselect: the one with the competition's current homologation code, otherwise the first course whose
    /// place matches the location (whole location first, then its parts and words). Null when nothing matches.
    /// </summary>
    public static FisCourseHomologation? BestMatch(IReadOnlyList<FisCourseHomologation> courses, string? location, string? homologationCode)
    {
        ArgumentNullException.ThrowIfNull(courses);
        if (!string.IsNullOrWhiteSpace(homologationCode)
            && courses.FirstOrDefault(x => string.Equals(x.HomologationCode, homologationCode.Trim(), StringComparison.OrdinalIgnoreCase)) is { } coded)
        { return coded; }
        if (string.IsNullOrWhiteSpace(location)) { return null; }
        return PlaceTerms(location).Select(term => courses.FirstOrDefault(x => string.Equals(x.Place, term, StringComparison.OrdinalIgnoreCase)))
            .FirstOrDefault(x => x is not null);
    }

    /// <summary>The whole location; for a combined location also its parts (split at | / , ;) and their words.</summary>
    public static IReadOnlyList<string> PlaceTerms(string location)
    {
        ArgumentNullException.ThrowIfNull(location);
        var full = location.Trim();
        var parts = full.Split(['|', '/', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        // A single place name is matched as entered; only a combined calendar location such as "Passo Stelvio | Bormio"
        // is broken into its parts and their words, because FIS lists those courses under "Stelvio" and "Bormio".
        if (parts.Length <= 1) { return [full]; }
        var words = parts.Where(x => x.Contains(' ', StringComparison.Ordinal))
            .SelectMany(x => x.Split([' ', '-'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).Where(x => x.Length >= 3);
        return new[] { full }.Concat(parts).Concat(words).Distinct(StringComparer.OrdinalIgnoreCase).Take(8).ToArray();
    }

    private async Task FetchAsync(string nation, string eventCode, string gender, DateOnly raceDate,
        string? apiKey, List<FisCourseHomologation> results, CancellationToken ct)
    {
        var query = $"https://api.fis-ski.com/homologation/course/AL?eventCode={eventCode}&nationCode={nation.ToUpperInvariant()}";
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
                if (!string.Equals(course.OrganisationNationCode, nation, StringComparison.OrdinalIgnoreCase)
                    || course.DisciplineCode != "AL" || course.EventCode != eventCode
                    || (gender.Length > 0 && gender != "A" && course.GenderCode != "A" && course.GenderCode != gender)
                    || (course.ValidFrom is { } from && raceDate < from) || (course.ValidTo is { } to && raceDate > to)) { continue; }
                if (course.HomologationId <= 0 || string.IsNullOrWhiteSpace(course.CourseName) || course.CourseName.Length > 160
                    || string.IsNullOrWhiteSpace(course.HomologationCode) || course.HomologationCode.Length > 50
                    || course.Place is null || course.Place.Length > 160
                    || new[] { course.StartAltitude, course.FinishAltitude, course.VerticalDrop, course.CourseLength }.Any(x => x is < 0 or > 9000))
                { throw new DomainValidationException("FIS returned invalid course dimensions or identifiers. Existing fields were preserved."); }
                results.Add(course);
                if (results.Count > 2000) { throw new DomainValidationException("FIS returned more than 2000 homologations for this nation and discipline."); }
            }
            if (data.Meta is null || pageNumber == data.Meta.LastPage) { return; }
        }
        throw new DomainValidationException("FIS homologation pagination exceeded its limit.");
    }
}
