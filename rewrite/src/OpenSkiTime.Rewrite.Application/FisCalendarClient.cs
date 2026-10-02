using System.Globalization;
using System.IO.Compression;
using OpenSkiTime.Rewrite.Domain;

namespace OpenSkiTime.Rewrite.Application;

public sealed record FisCalendarEvent(int Id, int Season, string Name, string Location, string Nation,
    string Organizer, DateOnly StartDate, DateOnly EndDate, IReadOnlyList<CompetitionValues> Competitions, bool HasEventName = true)
{
    public int CompetitionCount => Competitions.Count;
    public string Display => $"{StartDate:dd.MM.yyyy}–{EndDate:dd.MM.yyyy} · {Location} · {Nation} · {Name} · {Competitions.Count} races";
}

public sealed partial class FisRaceInformationClient
{
    public async Task ValidateCalendarEventAsync(FisCalendarEvent selected, string apiKey, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(selected);
        if (selected.Competitions.Count is < 1 or > 100) { throw new DomainValidationException("Select an event with 1–100 competitions."); }
        if (string.IsNullOrWhiteSpace(apiKey) || apiKey.Any(char.IsWhiteSpace) || apiKey.Any(char.IsControl))
        { throw new DomainValidationException("Save a valid public FIS API key in Settings first."); }
        foreach (var race in selected.Competitions)
        {
            if (race.FisCode is not { Length: 4 } || !race.FisCode.All(char.IsAsciiDigit) || race.Calendar is null)
            { throw new DomainValidationException("Calendar race has an invalid codex or missing metadata."); }
            var codex = int.Parse(race.FisCode!, CultureInfo.InvariantCulture);
            var summary = await GetAsync($"https://api.fis-ski.com/competitions/find-by-codex/AL/{codex}?season={selected.Season}", apiKey, ct);
            if (summary.Id <= 0 || summary.IsCancelled || string.IsNullOrWhiteSpace(summary.EventCode)
                || summary.EventId != selected.Id || summary.SeasonCode != selected.Season || summary.Codex != codex
                || summary.Date != race.Date || CalendarDiscipline(summary.EventCode.ToUpperInvariant()) != race.Discipline
                || !string.Equals(summary.CategoryCode, race.Calendar!.Category, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(summary.PlaceNationCode, race.Calendar.Nation, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(summary.GenderCode, race.Calendar.Gender, StringComparison.OrdinalIgnoreCase))
            { throw new DomainValidationException("A selected race is cancelled or differs from the current FIS calendar. Reload the calendar and review it. No fields were changed."); }
        }
    }

    public async Task<IReadOnlyList<FisCalendarEvent>> GetCalendarAsync(int season, string apiKey, CancellationToken ct = default)
    {
        if (season is < 1900 or > 2999) { throw new DomainValidationException("Enter a valid FIS season year."); }
        if (string.IsNullOrWhiteSpace(apiKey) || apiKey.Any(char.IsWhiteSpace) || apiKey.Any(char.IsControl))
        { throw new DomainValidationException("Save a valid public FIS API key in Settings first."); }
        const string source = "https://api.fis-ski.com/data-feeds/calendar";
        using var request = new HttpRequestMessage(HttpMethod.Get, source);
        request.Headers.Add("X-Api-Key", apiKey);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode) { throw new DomainValidationException($"FIS calendar download failed (HTTP {(int)response.StatusCode})."); }
        await using var input = await response.Content.ReadAsStreamAsync(ct);
        using var output = new MemoryStream();
        var buffer = new byte[81920]; int count;
        while ((count = await input.ReadAsync(buffer, ct)) > 0)
        {
            if (output.Length + count > 20_000_000) { throw new DomainValidationException("FIS calendar archive is unexpectedly large."); }
            output.Write(buffer, 0, count);
        }
        return ReadCalendarEvents(output.ToArray(), season, source);
    }

    public static IReadOnlyList<FisCalendarEvent> ReadCalendarEvents(byte[] archive, int season, string source)
    {
        try
        {
            using var input = new MemoryStream(archive, false);
            using var zip = new ZipArchive(input, ZipArchiveMode.Read);
            var events = ReadRows(zip, "A_event.csv").Where(x => Int(x, "Seasoncode") == season && Value(x, "Sectorcode") == "AL").ToArray();
            var races = ReadRows(zip, "A_raceal.csv").Where(x => Int(x, "Seasoncode") == season).ToArray();
            if (events.GroupBy(x => Int(x, "Eventid")).Any(x => x.Key <= 0 || x.Count() != 1)
                || races.GroupBy(x => Int(x, "Racecodex")).Any(x => x.Count() != 1))
            { throw new DomainValidationException("FIS calendar contains ambiguous event or codex identities."); }
            var result = new List<FisCalendarEvent>();
            var racesByEvent = races.ToLookup(x => Int(x, "Eventid"));
            foreach (var e in events)
            {
                var id = Int(e, "Eventid");
                var competitions = new List<CompetitionValues>();
                foreach (var r in racesByEvent[id])
                {
                    var code = Value(r, "Disciplinecode").ToUpperInvariant();
                    var discipline = CalendarDiscipline(code);
                    // Unsupported alpine disciplines are excluded; never relabel them as slalom.
                    if (discipline is null) { continue; }
                    var codex = Int(r, "Racecodex");
                    if (codex is < 1 or > 9999 || Int(r, "Raceid") <= 0
                        || !DateOnly.TryParseExact(Value(r, "Racedate")[..Math.Min(10, Value(r, "Racedate").Length)], "yyyy-MM-dd",
                            CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                    { throw new DomainValidationException("FIS calendar contains an invalid race identity or date."); }
                    var summary = new FisCompetitionInformation(Int(r, "Raceid"), codex, season, date, code,
                        Value(r, "Catcode"), Value(r, "Nationcode"), false, Value(r, "Place"), Value(r, "Gender"), id);
                    var location = string.IsNullOrWhiteSpace(summary.PlaceName) ? Value(e, "Place") : summary.PlaceName;
                    var calendar = new CompetitionCalendarData(season, location!, summary.PlaceNationCode ?? "",
                        summary.CategoryCode, summary.GenderCode ?? "", CalendarTechnicalDelegate(r), source).Validated();
                    var name = string.IsNullOrWhiteSpace(Value(e, "Eventname")) ? $"{location} {code}" : Value(e, "Eventname");
                    competitions.Add(new CompetitionValues(name!, $"{code} {calendar.Gender} {codex:0000}", date, discipline.Value,
                        RaceType.Fis, discipline is Discipline.Downhill or Discipline.SuperG ? 1 : 2, 0, codex.ToString("0000", CultureInfo.InvariantCulture),
                        HomologationNumber: Value(r, "Homol"), Calendar: calendar).Validated());
                }
                if (competitions.Count == 0) { continue; }
                var locationName = Value(e, "Place");
                var nameValue = Value(e, "Eventname");
                result.Add(new(id, season, string.IsNullOrWhiteSpace(nameValue) ? locationName : nameValue,
                    locationName, Value(e, "Nationcodeplace"), Value(e, "OrgaddressL1"),
                    CalendarDate(e, "Startdate", competitions.Min(x => x.Date)),
                    CalendarDate(e, "Enddate", competitions.Max(x => x.Date)), competitions.OrderBy(x => x.Date).ThenBy(x => x.ShortLabel).ToArray(),
                    !string.IsNullOrWhiteSpace(nameValue)));
            }
            return result.OrderBy(x => x.StartDate).ThenBy(x => x.Location).ToArray();
        }
        catch (Exception ex) when (ex is InvalidDataException or System.Text.DecoderFallbackException)
        { throw new DomainValidationException("FIS calendar archive is unreadable."); }
    }

    private static DateOnly CalendarDate(Dictionary<string, string> row, string key, DateOnly fallback)
        => DateOnly.TryParseExact(Value(row, key)[..Math.Min(10, Value(row, key).Length)], "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var date) ? date : fallback;
    private static CompetitionTechnicalDelegateInfo? CalendarTechnicalDelegate(Dictionary<string, string> race)
    {
        var original = Value(race, "Td1name"); var nation = Value(race, "Td1nation"); var number = Value(race, "Td1code");
        if (original.Length == 0 && number.Length == 0) { return null; }
        if (number.Length > 0 && !number.All(char.IsAsciiDigit)) { throw new DomainValidationException("FIS calendar TD number is invalid."); }
        var suffix = " (" + nation + ")";
        var name = original.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) ? original[..^suffix.Length] : original;
        var comma = name.Split(',', StringSplitOptions.TrimEntries);
        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        // FIS calendar TD display is surname first. Compound names remain available for review.
        return comma.Length == 2 ? new(comma[0], comma[1], nation, number)
            : words.Length == 2 ? new(words[0], words[1], nation, number) : new("", "", nation, number, name);
    }
    internal static Discipline? CalendarDiscipline(string code) => code switch
    { "SL" => Discipline.Slalom, "GS" => Discipline.GiantSlalom, "SG" => Discipline.SuperG, "DH" => Discipline.Downhill,
        "AC" => Discipline.AlpineCombined, _ => null };
}
