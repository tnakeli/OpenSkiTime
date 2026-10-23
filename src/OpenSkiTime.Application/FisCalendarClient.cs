using System.Globalization;
using System.IO.Compression;
using OpenSkiTime.Domain;

namespace OpenSkiTime.Application;

public sealed record FisCalendarEvent(int Id, int Season, string Name, string Location, string Nation,
    string Organizer, DateOnly StartDate, DateOnly EndDate, IReadOnlyList<CompetitionValues> Competitions, bool HasEventName = true)
{
    public int CompetitionCount => Competitions.Count;
    /// <summary>Races FIS has not given a codex yet; the operator enters it in Competitions.</summary>
    public int MissingCodexCount => Competitions.Count(x => x.FisCode is null);
    public string Display => $"{StartDate:dd.MM.yyyy}–{EndDate:dd.MM.yyyy} · {Location} · {Nation} · {Name} · {Competitions.Count} races";
}

/// <summary>The alpine events of one season and the count of calendar races that could not form a competition.</summary>
public sealed record FisCalendar(IReadOnlyList<FisCalendarEvent> Events, int SkippedRaces);

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
            if (race.Calendar is null) { throw new DomainValidationException("Calendar race has missing metadata."); }
            // A race without a codex has nothing to look up; the operator enters the codex later.
            if (race.FisCode is null) { continue; }
            if (race.FisCode is not { Length: 4 } || !race.FisCode.All(char.IsAsciiDigit))
            { throw new DomainValidationException("Calendar race has an invalid codex."); }
            var codex = int.Parse(race.FisCode!, CultureInfo.InvariantCulture);
            var summary = await GetAsync($"https://api.fis-ski.com/competitions/find-by-codex/AL/{codex}?season={selected.Season}", apiKey, ct);
            if (summary.Id <= 0 || string.IsNullOrWhiteSpace(summary.EventCode)
                || summary.EventId != selected.Id || summary.SeasonCode != selected.Season || summary.Codex != codex
                || summary.Date != race.Date || CalendarDiscipline(summary.EventCode.ToUpperInvariant()) != race.Discipline
                || !string.Equals(summary.CategoryCode, race.Calendar!.Category, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(summary.PlaceNationCode, race.Calendar.Nation, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(summary.GenderCode, race.Calendar.Gender, StringComparison.OrdinalIgnoreCase))
            { throw new DomainValidationException("A selected race differs from the current FIS calendar. Reload the calendar and review it. No fields were changed."); }
        }
    }

    public async Task<FisCalendar> GetCalendarAsync(int season, string apiKey, CancellationToken ct = default)
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

    // One unusable row never rejects the calendar: races without a usable codex are imported without one for the
    // operator to enter, and only races that cannot form a competition (no valid date, unreadable fields) are skipped.
    public static FisCalendar ReadCalendarEvents(byte[] archive, int season, string source)
    {
        try
        {
            using var input = new MemoryStream(archive, false);
            using var zip = new ZipArchive(input, ZipArchiveMode.Read);
            // A repeated event row is the same event; its first row supplies the event fields.
            var events = ReadRows(zip, "A_event.csv", out _).Where(x => Int(x, "Seasoncode") == season && Value(x, "Sectorcode") == "AL"
                && Int(x, "Eventid") > 0).GroupBy(x => Int(x, "Eventid")).Select(x => x.First()).ToArray();
            // A repeated row of one FIS race ID is the same race.
            var races = ReadRows(zip, "A_raceal.csv", out var malformed).Where(x => Int(x, "Seasoncode") == season)
                .Select((row, index) => (row, index)).GroupBy(x => Int(x.row, "Raceid") is > 0 and var id ? id : -1L - x.index)
                .Select(x => x.First().row).ToArray();
            // A codex shared by several races identifies none of them.
            var shared = races.Select(CalendarCodex).OfType<int>().GroupBy(x => x).Where(x => x.Count() > 1).Select(x => x.Key).ToHashSet();
            var eventIds = events.Select(x => Int(x, "Eventid")).ToHashSet();
            var skipped = malformed + races.Count(x => !eventIds.Contains(Int(x, "Eventid"))
                && CalendarDiscipline(Value(x, "Disciplinecode").ToUpperInvariant()) is not null);
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
                    var codex = CalendarCodex(r) is { } value && !shared.Contains(value) ? value : (int?)null;
                    if (CalendarRace(e, r, season, code, discipline.Value, codex, source) is { } race) { competitions.Add(race); }
                    else { skipped++; }
                }
                if (competitions.Count == 0) { continue; }
                // Number each discipline/gender chronologically, with codex as a stable same-day tie-breaker.
                competitions = competitions.OrderBy(x => x.Date).ThenBy(x => x.FisCode, StringComparer.Ordinal).ToList();
                var raceNumbers = new Dictionary<(Discipline, string), int>();
                for (var i = 0; i < competitions.Count; i++)
                {
                    var race = competitions[i];
                    var gender = race.Calendar!.Gender;
                    var key = (race.Discipline, gender);
                    var number = raceNumbers.GetValueOrDefault(key) + 1;
                    raceNumbers[key] = number;
                    var code = race.Discipline switch
                    {
                        Discipline.Slalom => "SL", Discipline.GiantSlalom => "GS", Discipline.SuperG => "SG",
                        Discipline.Downhill => "DH", Discipline.AlpineCombined => "AC",
                        _ => throw new DomainValidationException("Unsupported calendar discipline.")
                    };
                    competitions[i] = (race with
                    {
                        ShortLabel = FormattableString.Invariant($"{code}{number} {gender} {race.Date.Day}.{race.Date.Month}")
                    }).Validated();
                }
                var locationName = Value(e, "Place");
                var nameValue = Value(e, "Eventname");
                result.Add(new(id, season, string.IsNullOrWhiteSpace(nameValue) ? locationName : nameValue,
                    locationName, Value(e, "Nationcodeplace"), Value(e, "OrgaddressL1"),
                    CalendarDate(e, "Startdate", competitions.Min(x => x.Date)),
                    CalendarDate(e, "Enddate", competitions.Max(x => x.Date)), competitions.OrderBy(x => x.Date).ThenBy(x => x.ShortLabel).ToArray(),
                    !string.IsNullOrWhiteSpace(nameValue)));
            }
            return new(result.OrderBy(x => x.StartDate).ThenBy(x => x.Location).ToArray(), skipped);
        }
        catch (Exception ex) when (ex is InvalidDataException or System.Text.DecoderFallbackException)
        { throw new DomainValidationException("FIS calendar archive is unreadable."); }
    }

    private static int? CalendarCodex(Dictionary<string, string> race) => Value(race, "Racecodex") is { Length: > 0 and <= 4 } text
        && int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var codex) && codex > 0 ? codex : null;

    private static CompetitionValues? CalendarRace(Dictionary<string, string> e, Dictionary<string, string> r, int season,
        string code, Discipline discipline, int? codex, string source)
    {
        var dateText = Value(r, "Racedate");
        if (!DateOnly.TryParseExact(dateText[..Math.Min(10, dateText.Length)], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        { return null; }
        var raceId = Int(r, "Raceid");
        // Without a codex or a FIS race ID a later import could not find the race again and would duplicate it.
        if (codex is null && raceId <= 0) { return null; }
        var location = string.IsNullOrWhiteSpace(Value(r, "Place")) ? Value(e, "Place") : Value(r, "Place");
        var eventName = string.IsNullOrWhiteSpace(Value(e, "Eventname")) ? location : Value(e, "Eventname");
        var gender = Value(r, "Gender");
        var genderName = gender.ToUpperInvariant() switch { "W" => "Women", "M" => "Men", "A" => "Mixed", _ => gender };
        var fisCode = codex?.ToString("0000", CultureInfo.InvariantCulture);
        // Unreadable TD details are left for manual entry rather than dropping the race.
        foreach (var td in new[] { CalendarTechnicalDelegate(r), null })
        {
            try
            {
                var calendar = new CompetitionCalendarData(season, location, Value(r, "Nationcode"), Value(r, "Catcode"), gender, td, source,
                    raceId > 0 ? raceId : null).Validated();
                return new CompetitionValues(fisCode is null ? $"{eventName} - {code} {genderName}" : $"{eventName} - {code} {genderName} {fisCode}",
                    $"{code} {calendar.Gender} {fisCode}", date, discipline, RaceType.Fis, discipline is Discipline.Downhill or Discipline.SuperG ? 1 : 2, 0,
                    fisCode, HomologationNumber: Value(r, "Homol"), Calendar: calendar).Validated();
            }
            catch (DomainValidationException) when (td is not null) { }
            catch (DomainValidationException) { return null; }
        }
        return null;
    }

    private static DateOnly CalendarDate(Dictionary<string, string> row, string key, DateOnly fallback)
        => DateOnly.TryParseExact(Value(row, key)[..Math.Min(10, Value(row, key).Length)], "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var date) ? date : fallback;
    private static CompetitionTechnicalDelegateInfo? CalendarTechnicalDelegate(Dictionary<string, string> race)
    {
        var original = Value(race, "Td1name"); var nation = Value(race, "Td1nation"); var number = Value(race, "Td1code");
        if (original.Length == 0 && number.Length == 0) { return null; }
        // A malformed TD number is left for manual entry.
        if (!number.All(char.IsAsciiDigit)) { number = ""; }
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
