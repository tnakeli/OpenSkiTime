using System.Globalization;
using System.Net;
using System.Text.Json;
using OpenSkiTime.Domain;

namespace OpenSkiTime.Application;

/// <summary>One race of the regs.fissi.fi public API with its registered athletes.</summary>
public sealed record FissiApiRace(FissiRace Race, FissiRacePage Page);

/// <summary>
/// Reads the JSON of the public API. A response that is not completely understood is refused: a missing athlete would be
/// proposed for removal from the race.
/// </summary>
public static class FissiApiJson
{
    private const int MaximumAthletes = 1000;
    private const string Unreadable = "The regs.fissi.fi API returned a response that cannot be read completely. Nothing was changed.";

    public static FissiApiRace ReadRace(ReadOnlyMemory<byte> json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var race = Member(root, "race", JsonValueKind.Object);
            var athletes = Member(root, "athletes", JsonValueKind.Array);
            var codex = Text(race, "codex");
            if (codex is not { Length: <= 4 } || !codex.All(char.IsAsciiDigit)
                || !DateOnly.TryParseExact(Text(race, "date"), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                || athletes.GetArrayLength() > MaximumAthletes
                // The stated count protects against a shortened list.
                || !Member(root, "count", JsonValueKind.Number).TryGetInt32(out var count) || count != athletes.GetArrayLength())
            { throw new DomainValidationException(Unreadable); }
            codex = codex.PadLeft(4, '0');
            return new(new FissiRace(0, codex, Discipline(Text(race, "discipline")), Gender(Text(race, "gender"))),
                new FissiRacePage(codex, date, athletes.EnumerateArray().Select(ReadAthlete).ToArray()));
        }
        catch (JsonException) { throw new DomainValidationException(Unreadable); }
    }

    /// <summary>The error code of an error response, or null when it is not one of the API.</summary>
    public static string? ReadErrorCode(ReadOnlyMemory<byte> json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement is { ValueKind: JsonValueKind.Object } root && root.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.Object && error.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String
                ? code.GetString() : null;
        }
        catch (JsonException) { return null; }
    }

    private static FissiAthlete ReadAthlete(JsonElement athlete)
    {
        if (athlete.ValueKind != JsonValueKind.Object) { throw new DomainValidationException(Unreadable); }
        var name = Text(athlete, "name");
        var code = Text(athlete, "fis_code");
        var yearText = Text(athlete, "birth_year");
        var nation = Text(athlete, "nation")?.ToUpperInvariant();
        int? year = null;
        if (yearText is not null)
        {
            if (yearText.Length != 4 || !int.TryParse(yearText, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
            { throw new DomainValidationException(Unreadable); }
            year = value;
        }
        if (name is not { Length: <= 200 } || !name.Any(char.IsLetter) || (code is not null && (code.Length > 20 || !code.All(char.IsAsciiLetterOrDigit)))
            || (nation is not null && (nation.Length != 3 || !nation.All(char.IsAsciiLetterUpper))))
        { throw new DomainValidationException(Unreadable); }
        // The API has no club; the saved one stays.
        return new FissiAthlete(code, name, year, nation, null);
    }

    private static JsonElement Member(JsonElement parent, string name, JsonValueKind kind)
        => parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var value) && value.ValueKind == kind
            ? value : throw new DomainValidationException(Unreadable);

    // Every documented field must be there; null and blank mean that the value is not known.
    private static string? Text(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value)) { throw new DomainValidationException(Unreadable); }
        var text = value.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => throw new DomainValidationException(Unreadable),
        };
        text = text is null ? null : string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return string.IsNullOrEmpty(text) ? null : text;
    }

    private static string Gender(string? value) => value?.ToUpperInvariant() switch
    {
        "M" or "MEN" or "MALE" or "MIEHET" => "M",
        "W" or "L" or "F" or "WOMEN" or "LADIES" or "FEMALE" or "NAISET" => "W",
        _ => string.Empty,
    };

    private static string Discipline(string? value) => value?.ToUpperInvariant() switch
    {
        "SL" or "SLALOM" => "SL",
        "GS" or "GIANT SLALOM" => "GS",
        "SG" or "SUPER G" or "SUPER-G" => "SG",
        "DH" or "DOWNHILL" => "DH",
        "AC" or "ALPINE COMBINED" => "AC",
        _ => string.Empty,
    };
}

/// <summary>
/// Reads the registrations of a series' competitions from the public API of regs.fissi.fi. The client must not follow
/// redirects: the API key is sent in a header.
/// </summary>
public sealed class FissiApiClient(HttpClient client, string apiKey)
{
    private const string Endpoint = "https://regs.fissi.fi/public/api/race_athletes.php";
    private const int MaximumBytes = 5_000_000;
    private static readonly TimeSpan s_requestTimeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Reads the race of every competition by codex and date. Competitions in <paramref name="locked"/> are not looked up.
    /// Fails as a whole when a race cannot be read.
    /// </summary>
    public async Task<IReadOnlyList<FissiCompetition>> GetAsync(IReadOnlyList<CompetitionDetails> competitions,
        IReadOnlySet<Guid> locked, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(competitions); ArgumentNullException.ThrowIfNull(locked);
        if (string.IsNullOrWhiteSpace(apiKey)) { throw new DomainValidationException("Save the regs.fissi.fi API key in Settings → API keys first."); }
        if (!apiKey.All(x => x is > ' ' and <= '~'))
        { throw new DomainValidationException("The regs.fissi.fi API key has characters that cannot be sent. Check Settings → API keys."); }
        var found = new List<FissiCompetition>();
        foreach (var competition in competitions)
        {
            var codex = FissiRegistrationClient.Codex(competition.Values.FisCode);
            found.Add(codex is null ? new(competition.Id, competition.Values.FisCode, FissiRaceStatus.NoCodex)
                : locked.Contains(competition.Id) ? new(competition.Id, codex, FissiRaceStatus.StartListSaved)
                : await FindAsync(competition, codex, ct));
        }
        // One registration list cannot belong to two competitions.
        var shared = found.Where(x => x.Page is not null).GroupBy(x => (x.Page!.Codex, x.Page.Date)).Where(x => x.Count() > 1)
            .SelectMany(x => x).Select(x => x.CompetitionId).ToHashSet();
        return found.Select(x => shared.Contains(x.CompetitionId) ? x with { Status = FissiRaceStatus.SeveralMatches, Page = null } : x).ToArray();
    }

    private async Task<FissiCompetition> FindAsync(CompetitionDetails competition, string codex, CancellationToken ct)
    {
        var values = competition.Values;
        // Without a date the API gives the newest race of the codex: a race moved to another day is found too.
        var found = await GetRaceAsync(codex, values.Date, ct) ?? await GetRaceAsync(codex, null, ct);
        // FIS uses the same codexes again every season.
        if (found is null || FisSeason.FromDate(found.Page.Date) != FisSeason.FromDate(values.Date))
        { return new(competition.Id, codex, FissiRaceStatus.NotFound); }
        if (!FissiRegistrationClient.SameRace(values, found.Race)) { return new(competition.Id, codex, FissiRaceStatus.RaceDiffers); }
        return new(competition.Id, codex, found.Page.Athletes.Count == 0 ? FissiRaceStatus.NoneRegistered
            : found.Page.Date == values.Date ? FissiRaceStatus.Ready : FissiRaceStatus.DateDiffers, found.Page, found.Race.Gender);
    }

    // Null: the API knows no such race.
    private async Task<FissiApiRace?> GetRaceAsync(string codex, DateOnly? date, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(s_requestTimeout);
        var query = date is { } day ? $"?codex={codex}&date={day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}" : $"?codex={codex}";
        using var request = new HttpRequestMessage(HttpMethod.Get, Endpoint + query);
        request.Headers.Accept.ParseAdd("application/json");
        if (!request.Headers.TryAddWithoutValidation("X-API-Key", apiKey))
        { throw new DomainValidationException("The regs.fissi.fi API key cannot be sent. Check Settings → API keys."); }
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        switch (response.StatusCode)
        {
            case HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden:
                throw new DomainValidationException("regs.fissi.fi rejected the API key. Check Settings → API keys. Nothing was changed.");
            case HttpStatusCode.TooManyRequests:
                var wait = response.Headers.RetryAfter?.Delta is { } delta
                    ? $" Try again in {Math.Max(1, (long)Math.Ceiling(delta.TotalSeconds)).ToString(CultureInfo.InvariantCulture)} s." : " Try again later.";
                throw new DomainValidationException("The regs.fissi.fi request limit is reached. Nothing was changed." + wait);
            case HttpStatusCode.OK or HttpStatusCode.NotFound: break;
            default: throw new DomainValidationException($"regs.fissi.fi lookup failed (HTTP {(int)response.StatusCode}). Nothing was changed.");
        }
        await using var input = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var output = new MemoryStream(); var buffer = new byte[81920]; int count;
        while ((count = await input.ReadAsync(buffer, timeout.Token)) > 0)
        {
            if (output.Length + count > MaximumBytes) { throw new DomainValidationException("The regs.fissi.fi response is unexpectedly large. Nothing was changed."); }
            await output.WriteAsync(buffer.AsMemory(0, count), timeout.Token);
        }
        var json = output.GetBuffer().AsMemory(0, (int)output.Length);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            // Only the API's own answer means an unknown race; a missing page is a failure.
            return FissiApiJson.ReadErrorCode(json) == "race_not_found" ? null
                : throw new DomainValidationException("The regs.fissi.fi API was not found (HTTP 404). Nothing was changed.");
        }
        var race = FissiApiJson.ReadRace(json);
        return race.Page.Codex == codex ? race
            : throw new DomainValidationException($"regs.fissi.fi returned another codex than {codex}. Nothing was changed.");
    }
}
