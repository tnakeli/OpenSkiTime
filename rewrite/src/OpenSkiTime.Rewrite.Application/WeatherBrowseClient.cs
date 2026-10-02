using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using OpenSkiTime.Rewrite.Domain;

namespace OpenSkiTime.Rewrite.Application;

public sealed record WeatherPlace(string Name, decimal Latitude, decimal Longitude, string? Country, string? Admin1, string Timezone)
{
    public string Display => $"{Name}, {Admin1}, {Country} ({Latitude}, {Longitude}) · {Timezone}";
}
public sealed record WeatherHour(DateTime LocalTime, string Timezone, decimal? Temperature, int? WeatherCode,
    string Source, DateTimeOffset RetrievedAt)
{
    public string Conditions => WeatherCode switch { 0 => "Clear", 1 => "Mainly clear", 2 => "Partly cloudy", 3 => "Overcast",
        45 or 48 => "Fog", >= 51 and <= 57 => "Drizzle", >= 61 and <= 67 => "Rain", >= 71 and <= 77 => "Snowfall",
        >= 80 and <= 82 => "Rain showers", 85 or 86 => "Snow showers", >= 95 and <= 99 => "Thunderstorm", _ => "" };
    public string Display => $"{LocalTime:yyyy-MM-dd HH:mm} {Timezone} · {Conditions} · {Temperature?.ToString("0.0", CultureInfo.InvariantCulture)} °C (forecast)";
}

public sealed class WeatherBrowseClient(HttpClient client)
{
    private sealed record Places(WeatherPlace[]? Results);
    private sealed record Forecast(string Timezone, Hours? Hourly);
    private sealed record Hours(string[] Time, [property: JsonPropertyName("temperature_2m")] decimal?[] Temperature,
        [property: JsonPropertyName("weather_code")] int?[] Code);

    public async Task<IReadOnlyList<WeatherPlace>> SearchAsync(string location, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(location)) { throw new DomainValidationException("Enter a location to browse weather."); }
        var url = "https://geocoding-api.open-meteo.com/v1/search?count=10&language=en&format=json&name=" + Uri.EscapeDataString(location.Trim());
        var result = await client.GetFromJsonAsync<Places>(url, ct);
        return result?.Results ?? [];
    }

    public async Task<IReadOnlyList<WeatherHour>> BrowseAsync(WeatherPlace place, DateOnly raceDate, DateTimeOffset retrievedAt,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(place);
        var url = string.Create(CultureInfo.InvariantCulture,
            $"https://api.open-meteo.com/v1/forecast?latitude={place.Latitude}&longitude={place.Longitude}&hourly=temperature_2m,weather_code&timezone=auto&forecast_days=16");
        var forecast = await client.GetFromJsonAsync<Forecast>(url, ct);
        if (forecast?.Hourly is not { } hourly || hourly.Time.Length != hourly.Temperature.Length || hourly.Time.Length != hourly.Code.Length)
        { throw new DomainValidationException("Weather service returned incomplete hourly data."); }
        var hours = new List<WeatherHour>();
        for (var i = 0; i < hourly.Time.Length; i++)
        {
            if (!DateTime.TryParseExact(hourly.Time[i], "yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
            { throw new DomainValidationException("Weather service returned an invalid local time."); }
            if (DateOnly.FromDateTime(time) == raceDate)
            { hours.Add(new(time, forecast.Timezone, hourly.Temperature[i], hourly.Code[i], url, retrievedAt)); }
        }
        if (hours.Count == 0) { throw new DomainValidationException("Race date is outside the available forecast. Enter weather manually."); }
        return hours;
    }
}
