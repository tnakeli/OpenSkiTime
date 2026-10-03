using System.Globalization;
using System.IO.Compression;
using System.Text;
using OpenSkiTime.Domain;

namespace OpenSkiTime.Application;

public sealed record FisCompetitionData(FisCompetitionInformation Competition, string? EventName,
    CompetitionTechnicalDelegateInfo? TechnicalDelegate, string Source, string Note);

public sealed partial class FisRaceInformationClient
{
    public async Task<FisCompetitionData> GetCompetitionAsync(int season, string codex, string apiKey,
        DateOnly today, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(codex);
        if (season is < 1900 or > 2999 || codex.Length != 4 || !codex.All(char.IsAsciiDigit))
        { throw new DomainValidationException("Enter the FIS season year and four-digit codex first."); }
        if (string.IsNullOrWhiteSpace(apiKey) || apiKey.Any(char.IsWhiteSpace) || apiKey.Any(char.IsControl))
        { throw new DomainValidationException("Save a valid public FIS API key in Settings first."); }
        var code = int.Parse(codex, CultureInfo.InvariantCulture);
        var lookup = $"https://api.fis-ski.com/competitions/find-by-codex/AL/{code}?season={season}";
        var summary = await GetAsync(lookup, apiKey, ct);
        if (summary.Id <= 0 || summary.Codex != code || summary.SeasonCode != season || summary.Date == default
            || string.IsNullOrWhiteSpace(summary.CategoryCode) || string.IsNullOrWhiteSpace(summary.EventCode))
        { throw new DomainValidationException("FIS returned invalid competition data or a different season/codex. No fields were changed."); }
        var exportDate = summary.Date < today ? summary.Date : today;
        var feed = $"https://api.fis-ski.com/data-feeds/calendar?date={exportDate:yyyy-MM-dd}";
        using var request = new HttpRequestMessage(HttpMethod.Get, feed);
        request.Headers.Add("X-Api-Key", apiKey);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        { return new(summary, null, null, lookup, "Competition loaded. Calendar archive unavailable; event name and TD fields were preserved." + CancellationNote(summary)); }
        if (!response.IsSuccessStatusCode)
        { throw new DomainValidationException($"FIS calendar download failed (HTTP {(int)response.StatusCode}). No fields were changed."); }
        using var output = new MemoryStream();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var buffer = new byte[81920]; int count;
        while ((count = await stream.ReadAsync(buffer, ct)) > 0)
        {
            if (output.Length + count > 20_000_000) { throw new DomainValidationException("FIS calendar archive is unexpectedly large."); }
            output.Write(buffer, 0, count);
        }
        return ReadCalendar(output.ToArray(), summary, lookup + " | " + feed);
    }

    public static FisCompetitionData ReadCalendar(byte[] archive, FisCompetitionInformation summary, string source)
    {
        ArgumentNullException.ThrowIfNull(archive); ArgumentNullException.ThrowIfNull(summary);
        try
        {
            using var input = new MemoryStream(archive, writable: false);
            using var zip = new ZipArchive(input, ZipArchiveMode.Read);
            var races = ReadRows(zip, "A_raceal.csv");
            var matches = races.Where(x => Int(x, "Seasoncode") == summary.SeasonCode && Int(x, "Racecodex") == summary.Codex).ToArray();
            if (matches.Length == 0) { return new(summary, null, null, source, "Competition loaded. This calendar archive has no matching race; event name and TD fields were preserved." + CancellationNote(summary)); }
            if (matches.Length != 1) { throw new DomainValidationException("FIS calendar contains ambiguous season/codex matches. No fields were changed."); }
            var race = matches[0];
            var date = Value(race, "Racedate");
            if (Int(race, "Raceid") != summary.Id || Int(race, "Eventid") != summary.EventId
                || !date.StartsWith(summary.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), StringComparison.Ordinal)
                || !string.Equals(Value(race, "Disciplinecode"), summary.EventCode, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(Value(race, "Catcode"), summary.CategoryCode, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(Value(race, "Gender"), summary.GenderCode, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(Value(race, "Nationcode"), summary.PlaceNationCode, StringComparison.OrdinalIgnoreCase))
            { throw new DomainValidationException("FIS calendar export differs from the codex lookup. No fields were changed."); }
            var events = ReadRows(zip, "A_event.csv").Where(x => Int(x, "Eventid") == summary.EventId
                && Int(x, "Seasoncode") == summary.SeasonCode && Value(x, "Sectorcode") == "AL").ToArray();
            if (events.Length > 1) { throw new DomainValidationException("FIS calendar event match is ambiguous. No fields were changed."); }
            var eventName = events.Length == 1 ? Value(events[0], "Eventname") : null;
            var td = CalendarTechnicalDelegate(race);
            var note = td is null ? "Competition loaded. This calendar export has no TD details; existing TD fields were preserved."
                : td.FirstName.Length == 0 || td.LastName.Length == 0 ? "Competition loaded. Review the TD name and enter surname and first name separately."
                : "Competition and TD data loaded.";
            if (td is not null && td.Number.Length == 0) { note += " This calendar export has no TD number; review the number manually."; }
            if (string.IsNullOrWhiteSpace(eventName)) { note += " This calendar export has no event name; the current name was preserved when available."; }
            return new(summary, string.IsNullOrWhiteSpace(eventName) ? null : eventName, td, source, note + CancellationNote(summary));
        }
        catch (Exception ex) when (ex is InvalidDataException or DecoderFallbackException)
        { throw new DomainValidationException("FIS calendar archive is unreadable. No fields were changed."); }
    }

    private static List<Dictionary<string, string>> ReadRows(ZipArchive zip, string name)
    {
        var entries = zip.Entries.Where(x => x.FullName.Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (entries.Length != 1 || entries[0].Length > 20_000_000) { throw new DomainValidationException("FIS calendar archive has missing, duplicate or oversized tables."); }
        using var reader = new StreamReader(entries[0].Open(), new UTF8Encoding(false, true), true);
        var header = reader.ReadLine()?.Trim('\uFEFF').Split('\t') ?? throw new DomainValidationException("FIS calendar table has no header.");
        var rows = new List<Dictionary<string, string>>(); string? line; var length = 0;
        while ((line = reader.ReadLine()) is not null)
        {
            length += line.Length;
            if (length > 20_000_000) { throw new DomainValidationException("FIS calendar table is unexpectedly large."); }
            var values = line.Split('\t');
            if (values.Length != header.Length) { throw new DomainValidationException("FIS calendar table has an invalid row."); }
            var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < header.Length; i++) { row.Add(header[i], values[i].Trim('\0', '"', ' ').Replace("\"\"", "\"", StringComparison.Ordinal)); }
            rows.Add(row);
        }
        return rows;
    }
    private static string Value(Dictionary<string, string> row, string key) => row.GetValueOrDefault(key) ?? "";
    private static int Int(Dictionary<string, string> row, string key) => int.TryParse(Value(row, key), NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : 0;
}
