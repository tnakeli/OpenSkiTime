using System.Globalization;
using System.Text.Json;
using OpenSkiTime.Domain;

namespace OpenSkiTime.Application;

public sealed record FisTimingDevice(int Id, string Name, string HomologationCode, int? ValidUntilSeason,
    int CompanyId, string CompanyName, int CategoryId, string CategoryName, int? Precision, string? Comment, bool Valid)
{
    public string Display => $"{CompanyName} {Name} · {HomologationCode}"
        + (ValidUntilSeason is { } season ? $" · through {season}" : "") + (Valid ? "" : " · expired");
}

/// <summary>Optional equipment catalogue. Stored report snapshots never depend on this online lookup.</summary>
public sealed class FisTimingDeviceClient(HttpClient client)
{
    private static readonly JsonSerializerOptions s_json = new() { PropertyNameCaseInsensitive = true };

    public async Task<IReadOnlyList<FisTimingDevice>> GetAsync(string apiKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey) || apiKey.Any(char.IsWhiteSpace) || apiKey.Any(char.IsControl))
        { throw new DomainValidationException("Save a valid FIS API key in Settings first."); }
        // Include expired equipment so historical reports can use the race season instead of today's validity.
        using var request = new HttpRequestMessage(HttpMethod.Get,
            "https://api.fis-ski.com/homologation/timing-devices?includeExpired=true");
        request.Headers.Add("X-Api-Key", apiKey);
        request.Headers.Accept.ParseAdd("application/json");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
        { throw new DomainValidationException($"FIS equipment lookup failed (HTTP {((int)response.StatusCode).ToString(CultureInfo.InvariantCulture)}). Saved equipment and cached homologations were preserved."); }
        await using var input = await response.Content.ReadAsStreamAsync(ct);
        using var bytes = new MemoryStream(); var buffer = new byte[8192]; int count;
        while ((count = await input.ReadAsync(buffer, ct)) > 0)
        {
            if (bytes.Length + count > 5_000_000) { throw new DomainValidationException("FIS equipment catalogue exceeds 5 MB."); }
            bytes.Write(buffer, 0, count);
        }
        FisTimingDevice[] devices;
        try { devices = JsonSerializer.Deserialize<FisTimingDevice[]>(bytes.ToArray(), s_json) ?? throw new JsonException(); }
        catch (JsonException) { throw new DomainValidationException("FIS returned an unreadable equipment catalogue. The previous cache was preserved."); }
        Validate(devices);
        return devices.OrderBy(x => x.CompanyName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.HomologationCode, StringComparer.Ordinal).ToArray();
    }

    public static void Validate(IReadOnlyList<FisTimingDevice> devices)
    {
        ArgumentNullException.ThrowIfNull(devices);
        if (devices.Count > 5000 || devices.Any(x => x is null || x.Id <= 0
            || !Text(x.Name, 200) || !Text(x.CompanyName, 200) || !Text(x.HomologationCode, 80)
            || x.CategoryId is not (1 or 10 or 15 or 20 or 30)
            || x.ValidUntilSeason is < 1900 or > 2200 || x.Precision is <= 0)
            || devices.Select(x => x.Id).Distinct().Count() != devices.Count)
        { throw new DomainValidationException("FIS equipment catalogue contains invalid or duplicate devices. The previous cache was preserved."); }
    }

    private static bool Text(string? value, int max) => !string.IsNullOrWhiteSpace(value)
        && value.Length <= max && !value.Any(char.IsControl);
}
