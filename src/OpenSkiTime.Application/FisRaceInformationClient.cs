using System.Globalization;
using System.Net;

using System.Text.Json;
using OpenSkiTime.Domain;

namespace OpenSkiTime.Application;

public sealed record FisCompetitionInformation(int Id, int Codex, int SeasonCode, DateOnly Date,
    string EventCode, string CategoryCode, string? PlaceNationCode, bool IsCancelled,
    string? PlaceName = null, string? GenderCode = null, int EventId = 0);
public sealed record RaceInformationSuggestion(int Run, string Field, string Value)
{
    public string Display => (Run == 0 ? "Race" : $"Run {Run}") + $" · {Field}: {Value}";
}
public sealed record FisInformationPreview(int CompetitionId, string Source,
    IReadOnlyList<RaceInformationSuggestion> Suggestions, string Note);

public sealed partial class FisRaceInformationClient(HttpClient client)
{
    private static readonly JsonSerializerOptions s_json = new() { PropertyNameCaseInsensitive = true };

    public async Task<FisInformationPreview> BrowseAsync(CompetitionValues race, string nation, string apiKey,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(race);
        if (race.FisCode is not { Length: 4 } || !race.FisCode.All(char.IsAsciiDigit))
        { throw new DomainValidationException("Enter a four-digit FIS codex in Competitions first."); }
        if (string.IsNullOrWhiteSpace(apiKey)) { throw new DomainValidationException("Save the FIS API key in Settings first."); }
        if (apiKey.Any(char.IsWhiteSpace))
        { throw new DomainValidationException("FIS credentials cannot contain whitespace. Check Settings."); }
        var season = race.Date.Month >= 6 ? race.Date.Year + 1 : race.Date.Year;
        var codex = int.Parse(race.FisCode, CultureInfo.InvariantCulture);
        var lookup = $"https://api.fis-ski.com/competitions/find-by-codex/AL/{codex}?season={season}";
        var summary = await GetAsync(lookup, apiKey, ct);
        ValidateIdentity(summary, race, season, codex, nation);
        if (string.IsNullOrWhiteSpace(summary.CategoryCode)) { throw new DomainValidationException("FIS returned no calendar category. Enter it manually."); }
        return new(summary.Id, lookup, [new(0, "Category", summary.CategoryCode.ToUpperInvariant())],
            "Calendar category retrieved from the public API. Jury and run report fields are entered locally; the public competition endpoint does not provide them." + CancellationNote(summary));
    }
    private static string CancellationNote(FisCompetitionInformation summary) => summary.IsCancelled
        ? " FIS marks this competition as cancelled; local registration, start lists and timing remain available." : "";
    private async Task<FisCompetitionInformation> GetAsync(string url, string credential, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Add("X-Api-Key", credential);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        { throw new DomainValidationException("FIS rejected the API key. Check Settings or enter race information manually."); }
        if (!response.IsSuccessStatusCode) { throw new DomainValidationException($"FIS lookup failed (HTTP {(int)response.StatusCode}). Retry or enter information manually."); }
        if (response.Content.Headers.ContentLength is > 5_000_000) { throw new DomainValidationException("FIS response is unexpectedly large."); }
        await using var input = await response.Content.ReadAsStreamAsync(ct);
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await input.ReadAsync(buffer, ct)) > 0)
        {
            if (output.Length + read > 5_000_000) { throw new DomainValidationException("FIS response is unexpectedly large."); }
            await output.WriteAsync(buffer.AsMemory(0, read), ct);
        }
        try { return JsonSerializer.Deserialize<FisCompetitionInformation>(output.ToArray(), s_json)
            ?? throw new DomainValidationException("FIS returned no competition."); }
        catch (JsonException) { throw new DomainValidationException("FIS returned an unsupported competition response. Enter information manually."); }
    }

    private static void ValidateIdentity(FisCompetitionInformation value, CompetitionValues race, int season, int codex, string nation)
    {
        var discipline = race.Discipline switch { Discipline.Slalom => "SL", Discipline.GiantSlalom => "GS",
            Discipline.Downhill => "DH", Discipline.SuperG => "SG", _ => "" };
        if (value.Id <= 0 || value.Codex != codex || value.SeasonCode != season || value.Date != race.Date
            || !string.Equals(value.EventCode, discipline, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(value.PlaceNationCode, nation, StringComparison.OrdinalIgnoreCase))
        { throw new DomainValidationException("FIS codex, season, date, discipline or nation differs from this race. Review Competitions before importing."); }
    }

}
