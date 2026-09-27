using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using OpenSkiTime.Rewrite.Domain;

namespace OpenSkiTime.Rewrite.Application;

public sealed record ImportEntryPatch(Guid CompetitionId, bool? Participates, bool BibSpecified, int? ImportedBib);
public sealed record ImportReviewItem(int SourceRow, Guid? CompetitorId, CompetitorValues Values,
    IReadOnlyList<ImportEntryPatch> Entries, IReadOnlyList<string> ChangedFields,
    IReadOnlyList<string> Warnings, bool IsNew);
public sealed record ImportPreview(Guid SeriesId, long Revision, string SourceHash,
    IReadOnlyList<ImportReviewItem> Rows, IReadOnlyList<string> Warnings);
public sealed record ImportCommitRow(Guid? CompetitorId, CompetitorValues Values,
    IReadOnlyList<ImportEntryPatch> Entries);
public sealed record ImportCommit(Guid SeriesId, long ExpectedRevision, string SourceHash,
    IReadOnlyList<ImportCommitRow> Rows);
public sealed record ImportCommitResult(long Revision, int Created, int Updated, bool AlreadyApplied = false);

public static class TsvExchange
{
    public static string Hash(string text)
    {
        var canonical = Encode(Parse(text).Select(row => row.Select(cell => (string?)cell).ToArray()));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    public static IReadOnlyList<string[]> Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) { throw new DomainValidationException("Paste a table with a header row first."); }
        var rows = new List<string[]>();
        var fields = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        var afterQuote = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                else if (c == '"') { quoted = false; afterQuote = true; }
                else { cell.Append(c); }
                continue;
            }
            if (c == '"' && cell.Length == 0 && !afterQuote) { quoted = true; continue; }
            if (c is '\t' or '\n' or '\r')
            {
                fields.Add(cell.ToString());
                cell.Clear();
                afterQuote = false;
                if (c == '\t') { continue; }
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') { i++; }
                if (fields.Any(x => x.Length != 0)) { rows.Add([.. fields]); }
                fields.Clear();
                continue;
            }
            if (afterQuote) { throw new DomainValidationException("Unexpected text after a quoted TSV cell."); }
            if (c == '"') { throw new DomainValidationException("Quotes must surround the entire TSV cell."); }
            cell.Append(c);
        }
        if (quoted) { throw new DomainValidationException("The pasted table has an unclosed quoted cell."); }
        fields.Add(cell.ToString());
        if (fields.Any(x => x.Length != 0)) { rows.Add([.. fields]); }
        if (rows.Count < 2) { throw new DomainValidationException("Paste a header and at least one competitor row."); }
        return rows;
    }

    public static string Encode(IEnumerable<IReadOnlyList<string?>> rows)
        => string.Join("\r\n", rows.Select(row => string.Join('\t', row.Select(Quote))));

    public static string Export(SeriesDetails series, CompetitorDeskDetails desk, IEnumerable<Guid> competitorIds)
    {
        ArgumentNullException.ThrowIfNull(series);
        ArgumentNullException.ThrowIfNull(desk);
        ArgumentNullException.ThrowIfNull(competitorIds);
        if (series.Id != desk.SeriesId) { throw new SeriesConflictException(); }
        var ids = competitorIds.ToHashSet();
        var rows = new List<IReadOnlyList<string?>>();
        rows.Add(["Surname", "First name", "Year", "Gender", "Nation", "Club", "Fed ID",
            .. series.Competitions.SelectMany(x => new[] { x.Values.ShortLabel, $"Bib:{x.Values.ShortLabel}" })]);
        foreach (var competitor in desk.Competitors.Where(x => ids.Contains(x.Id)))
        {
            var values = competitor.Values;
            var cells = new List<string?>
            {
                values.Surname, values.FirstName, values.BirthYear?.ToString(CultureInfo.InvariantCulture),
                GenderLabels.Format(values.Gender), values.Nation, values.Club, values.FederationCode,
            };
            foreach (var competition in series.Competitions)
            {
                var entry = desk.Participations.FirstOrDefault(x => x.CompetitorId == competitor.Id
                    && x.CompetitionId == competition.Id);
                cells.Add(entry?.Participates == true ? "X" : string.Empty);
                cells.Add(entry?.ImportedBib?.ToString(CultureInfo.InvariantCulture));
            }
            rows.Add(cells);
        }
        return Encode(rows);
    }

    private static string Quote(string? value)
    {
        var text = value ?? string.Empty;
        return text.IndexOfAny(['\t', '\r', '\n', '"']) >= 0 ? '"' + text.Replace("\"", "\"\"", StringComparison.Ordinal) + '"' : text;
    }

    public static ImportPreview Preview(string source, SeriesDetails series, CompetitorDeskDetails desk,
        Guid? selectedCompetitionId = null)
    {
        ArgumentNullException.ThrowIfNull(series);
        ArgumentNullException.ThrowIfNull(desk);
        if (series.Id != desk.SeriesId || series.Revision != desk.Revision)
        {
            throw new SeriesConflictException();
        }
        var table = Parse(source);
        var columns = table[0].Select((header, index) => new Column(index, MapHeader(header, series.Competitions, selectedCompetitionId)))
            .ToArray();
        if (columns.All(x => x.Key is not "Surname" and not "FullName" and not "FederationCode"))
        {
            throw new DomainValidationException("Include a Surname, Name or Fed ID column to identify competitors.");
        }
        var repeated = columns.Where(x => x.Key is not null).GroupBy(x => x.Key!, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(x => x.Count() > 1);
        if (repeated is not null) { throw new DomainValidationException($"Column {repeated.Key} occurs more than once."); }
        var warnings = table[0].Where((_, i) => columns[i].Key is null)
            .Select(x => $"Unrecognized column '{x}' is ignored.").ToArray();
        var preview = new List<ImportReviewItem>();
        var sourceIdentity = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sourceNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var rowIndex = 1; rowIndex < table.Count; rowIndex++)
        {
            var cells = table[rowIndex];
            if (cells.Length > columns.Length && cells.Skip(columns.Length).Any(x => x.Length > 0))
            {
                throw new DomainValidationException($"Row {rowIndex + 1} has values beyond the header columns.");
            }
            var values = columns.Where(x => x.Key is not null)
                .ToDictionary(x => x.Key!, x => x.Index < cells.Length ? cells[x.Index].Trim() : string.Empty,
                    StringComparer.OrdinalIgnoreCase);
            var rowWarnings = new List<string>();
            if (Value(values, "Surname") is null && Value(values, "FullName") is { } fullName)
            {
                var split = SplitFullName(fullName);
                values["Surname"] = split.Surname;
                if (Value(values, "FirstName") is null) { values["FirstName"] = split.FirstName ?? string.Empty; }
                if (split.NeedsReview) { rowWarnings.Add("Check the split of the combined name before Commit."); }
            }
            var code = Value(values, "FederationCode");
            var surname = Value(values, "Surname");
            var firstName = Value(values, "FirstName");
            var birthYearText = Value(values, "BirthYear");
            var year = Number(birthYearText, $"row {rowIndex + 1} birth year");
            if (surname is not null && firstName is not null && year is not null
                && !sourceNames.Add($"{surname}|{firstName}|{year}"))
            {
                throw new DomainValidationException($"Row {rowIndex + 1} repeats a name and birth year in this paste.");
            }
            var byCode = code is null ? [] : desk.Competitors.Where(x =>
                string.Equals(x.Values.FederationCode, code, StringComparison.OrdinalIgnoreCase)).ToArray();
            var byName = surname is null || firstName is null || year is null ? [] : desk.Competitors.Where(x =>
                string.Equals(x.Values.Surname, surname, StringComparison.OrdinalIgnoreCase)
                && string.Equals(x.Values.FirstName, firstName, StringComparison.OrdinalIgnoreCase)
                && x.Values.BirthYear == year).ToArray();
            if (byCode.Length > 1 || byName.Length > 1 || (byCode.Length == 1 && byName.Length == 1 && byCode[0].Id != byName[0].Id))
            {
                throw new DomainValidationException($"Row {rowIndex + 1} matches multiple competitors; correct the identifier before previewing.");
            }
            var match = byCode.FirstOrDefault() ?? byName.FirstOrDefault();
            if (byCode.Length == 0 && byName.Length == 1 && code is not null
                && byName[0].Values.FederationCode is { } oldCode
                && !oldCode.Equals(code, StringComparison.OrdinalIgnoreCase))
            {
                rowWarnings.Add($"Federation code changes from {oldCode} to {code}; confirm this match.");
            }
            var identity = match is null ? $"new:{code ?? $"{surname}|{firstName}|{year}"}" : match.Id.ToString();
            if (!sourceIdentity.Add(identity)) { throw new DomainValidationException($"Row {rowIndex + 1} repeats a competitor in this paste."); }
            var before = match?.Values;
            var next = new CompetitorValues(
                Scalar(values, "Surname", before?.Surname) ?? string.Empty,
                Scalar(values, "FirstName", before?.FirstName) ?? string.Empty,
                Number(Scalar(values, "BirthYear", before?.BirthYear?.ToString(CultureInfo.InvariantCulture)), $"row {rowIndex + 1} birth year"),
                Scalar(values, "FederationCode", before?.FederationCode),
                Scalar(values, "Nation", before?.Nation),
                Scalar(values, "Club", before?.Club),
                GenderValue(Scalar(values, "Gender", before?.Gender?.ToString()), rowIndex + 1)).Validated(series.Values.EndDate.Year);
            var patches = new List<ImportEntryPatch>();
            foreach (var competition in series.Competitions)
            {
                var label = competition.Values.ShortLabel;
                var present = values.TryGetValue($"IN:{competition.Id}", out var participationText);
                var participates = present ? Participation(participationText!, rowIndex + 1, label) : null;
                var bibPresent = values.TryGetValue($"BIB:{competition.Id}", out var bibText) && !string.IsNullOrWhiteSpace(bibText);
                var bib = bibPresent ? Bib(bibText!, rowIndex + 1, label) : null;
                if (participates is not null || bibPresent)
                {
                    patches.Add(new ImportEntryPatch(competition.Id, participates, bibPresent, bib));
                }
            }
            var changed = new List<string>();
            if (before is null) { changed.Add("NEW"); }
            else
            {
                if (before.Surname != next.Surname) { changed.Add("Surname"); }
                if (before.FirstName != next.FirstName) { changed.Add("First name"); }
                if (before.BirthYear != next.BirthYear) { changed.Add("Year"); }
                if (before.Gender != next.Gender) { changed.Add("Gender"); }
                if (before.Nation != next.Nation) { changed.Add("Nation"); }
                if (before.Club != next.Club) { changed.Add("Club"); }
                if (before.FederationCode != next.FederationCode) { changed.Add("Fed ID"); }
            }
            foreach (var patch in patches)
            {
                var old = desk.Participations.FirstOrDefault(x => x.CompetitorId == match?.Id && x.CompetitionId == patch.CompetitionId);
                if (patch.Participates is { } state && state != (old?.Participates ?? false)) { changed.Add("Entry"); }
                if (patch.BibSpecified && patch.ImportedBib != old?.ImportedBib) { changed.Add("Bib ref"); }
            }
            if (before is null && code is null && (firstName is null || year is null))
            {
                rowWarnings.Add("New competitor has no stable matching identifier; review for duplicates.");
            }
            preview.Add(new ImportReviewItem(rowIndex + 1, match?.Id, next, patches, changed,
                rowWarnings, before is null));
        }
        return new ImportPreview(series.Id, series.Revision, Hash(source), preview, warnings);
    }

    private static string? MapHeader(string header, IReadOnlyList<CompetitionDetails> competitions, Guid? selectedCompetitionId)
    {
        var key = header.Trim();
        var normalized = key.Replace(" ", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
        var basic = normalized switch
        {
            "surname" or "lastname" or "familyname" or "last" => "Surname",
            "name" or "fullname" or "athlete" => "FullName",
            "firstname" or "givenname" or "forename" or "first" => "FirstName",
            "year" or "yob" or "birthyear" or "yearofbirth" or "born" => "BirthYear",
            "gender" or "sex" or "sukupuoli" => "Gender",
            "nation" or "nat" or "nationcode" or "country" => "Nation",
            "club" or "clubname" or "team" => "Club",
            "fedid" or "federationcode" or "fiscode" or "fis" or "code" => "FederationCode",
            _ => null,
        };
        if (basic is not null) { return basic; }
        if (normalized is "bib" or "bibnumber" or "startnumber" or "nr")
        {
            return selectedCompetitionId is null ? null : $"BIB:{selectedCompetitionId}";
        }
        foreach (var competition in competitions)
        {
            var label = competition.Values.ShortLabel;
            if (key.Equals(label, StringComparison.OrdinalIgnoreCase)
                || key.Equals($"IN:{label}", StringComparison.OrdinalIgnoreCase)) { return $"IN:{competition.Id}"; }
            if (key.Equals($"Bib:{label}", StringComparison.OrdinalIgnoreCase)
                || key.Equals($"{label}:Bib", StringComparison.OrdinalIgnoreCase)) { return $"BIB:{competition.Id}"; }
        }
        return null;
    }

    private static string? Value(Dictionary<string, string> values, string key)
        => values.TryGetValue(key, out var text) && !string.IsNullOrWhiteSpace(text) && text != "~" ? text : null;

    private static string? Scalar(Dictionary<string, string> values, string key, string? old)
        => !values.TryGetValue(key, out var value) || value.Length == 0 ? old : value == "~" ? null : value;

    private static int? Number(string? text, string label)
    {
        if (string.IsNullOrWhiteSpace(text) || text == "~") { return null; }
        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
        {
            throw new DomainValidationException($"{label} must be a whole number.");
        }
        return number;
    }

    private static int? Bib(string text, int row, string label)
    {
        if (text == "~") { return null; }
        var value = Number(text, $"row {row} bib for {label}");
        if (value is null or <= 0 or > 99999) { throw new DomainValidationException($"Row {row}: bib for {label} must be 1–99999 or ~ to clear."); }
        return value;
    }

    private static bool? Participation(string text, int row, string label) => text.Trim().ToLowerInvariant() switch
    {
        "" => null,
        "x" or "1" or "yes" or "y" or "true" or "kyllä" or "kylla" or "joo" or "k" => true,
        "0" or "no" or "n" or "false" or "ei" or "-" => false,
        _ => throw new DomainValidationException($"Row {row}: participation in {label} must be X, 0 or blank."),
    };

    private static Gender? GenderValue(string? text, int row) => text?.Trim().ToLowerInvariant() switch
    {
        null or "" or "~" => null,
        "f" or "female" or "woman" or "women" => Gender.Female,
        "m" or "male" or "man" or "men" => Gender.Male,
        "o" or "other" => Gender.Other,
        _ => throw new DomainValidationException($"Row {row}: gender must be Women, Men or Other."),
    };

    private static (string Surname, string? FirstName, bool NeedsReview) SplitFullName(string fullName)
    {
        var comma = fullName.IndexOf(',', StringComparison.Ordinal);
        if (comma > 0)
        {
            return (fullName[..comma].Trim(), fullName[(comma + 1)..].Trim(), false);
        }
        var parts = fullName.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 1) { return (parts[0], null, true); }
        var upperPrefix = 0;
        while (upperPrefix < parts.Length && IsUpper(parts[upperPrefix])) { upperPrefix++; }
        if (upperPrefix is > 0 && upperPrefix < parts.Length)
        {
            return (string.Join(' ', parts[..upperPrefix]), string.Join(' ', parts[upperPrefix..]), false);
        }
        var upperSuffix = parts.Length;
        while (upperSuffix > 0 && IsUpper(parts[upperSuffix - 1])) { upperSuffix--; }
        if (upperSuffix is > 0 && upperSuffix < parts.Length)
        {
            return (string.Join(' ', parts[upperSuffix..]), string.Join(' ', parts[..upperSuffix]), false);
        }
        return (parts[^1], string.Join(' ', parts[..^1]), true);
    }

    private static bool IsUpper(string value) => value.Any(char.IsLetter)
        && value.Where(char.IsLetter).All(c => char.ToUpperInvariant(c) == c);

    private sealed record Column(int Index, string? Key);
}
