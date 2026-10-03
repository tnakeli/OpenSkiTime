using System.Globalization;
using System.IO.Compression;
using System.Text;
using OpenSkiTime.Domain;

namespace OpenSkiTime.Application;

public sealed record FisAthlete(string Code, string Surname, string FirstName, int? BirthYear,
    Gender? Gender, string? Nation, string? Club, string? Status,
    IReadOnlyDictionary<string, decimal>? Points = null);

public sealed class FisPointsList(
    string listCode, string name, DateOnly publishedOn, DateOnly validFrom, DateOnly validTo,
    IReadOnlyDictionary<string, FisAthlete> athletes, FisPenaltyListRules? penaltyRules = null)
{
    public string ListCode { get; } = listCode;
    public string Name { get; } = name;
    public DateOnly PublishedOn { get; } = publishedOn;
    public DateOnly ValidFrom { get; } = validFrom;
    public DateOnly ValidTo { get; } = validTo;
    public IReadOnlyDictionary<string, FisAthlete> Athletes { get; } = athletes;
    public FisPenaltyListRules? PenaltyRules { get; } = penaltyRules;
    public string DisplayName => $"{ListCode}: {Name} ({PublishedOn:dd-MM-yyyy})";

    public IEnumerable<FisAthlete> Search(string query) => Athletes.Values
        .Where(x => query.Length > 0 && (x.Code.Contains(query, StringComparison.OrdinalIgnoreCase)
            || x.Surname.Contains(query, StringComparison.OrdinalIgnoreCase)
            || x.FirstName.Contains(query, StringComparison.OrdinalIgnoreCase)
            || (x.Nation?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)
            || (x.Club?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)))
        .Take(40);
}

public static class FisPointsListReader
{
    public static FisPointsList Read(byte[] archive)
    {
        ArgumentNullException.ThrowIfNull(archive);
        if (archive.Length == 0 || archive.Length > 40_000_000)
        {
            throw new DomainValidationException("FIS download is empty or unexpectedly large.");
        }
        try
        {
            using var zip = new ZipArchive(new MemoryStream(archive), ZipArchiveMode.Read);
            var header = RequiredEntry(zip, "hdr.csv");
            var competitor = RequiredEntry(zip, "com.csv");
            var points = RequiredEntry(zip, "pts.csv");
            if (header.Name.Length != 13 || !header.Name.StartsWith("AL", StringComparison.OrdinalIgnoreCase))
            {
                throw new DomainValidationException("FIS points-list filename is invalid.");
            }
            var prefix = header.Name[..^7];
            if (!competitor.Name.Equals(prefix + "com.csv", StringComparison.OrdinalIgnoreCase)
                || !points.Name.Equals(prefix + "pts.csv", StringComparison.OrdinalIgnoreCase))
            {
                throw new DomainValidationException("FIS ZIP contains files from different points lists.");
            }
            var metadata = ReadRows(header, 2_000).ToArray();
            if (metadata.Length != 2 || !metadata[0].SequenceEqual(
                ["Listid", "Seasoncode", "Listnumber", "Listname", "Calculationdate", "Startracedate",
                 "Endracedate", "Validfrom", "Validto", "Lastupdate"]))
            {
                throw new DomainValidationException("FIS points-list header has an unsupported format.");
            }
            var h = metadata[1];
            if (h.Length < 9 || !int.TryParse(h[2], out var number)
                || !int.TryParse(h[1], out var seasonCode))
            {
                throw new DomainValidationException("FIS points-list version is invalid.");
            }
            var code = prefix[2..];
            if (code.Length != 4 || !code.All(char.IsDigit)
                || code != $"{number:00}{seasonCode % 100:00}")
            {
                throw new DomainValidationException("FIS points-list filename is invalid.");
            }
            var athletes = new Dictionary<string, FisAthlete>(StringComparer.OrdinalIgnoreCase);
            var competitorIds = new Dictionary<string, string>(StringComparer.Ordinal);
            using var rows = ReadRows(competitor, 20_000_000).GetEnumerator();
            if (!rows.MoveNext() || !rows.Current.SequenceEqual(
                ["Competitorid", "Sectorcode", "Fiscode", "Lastname", "Firstname", "Gender", "Birthdate",
                 "Nationcode", "Nationalcode", "Skiclub", "Association", "Status"]))
            {
                throw new DomainValidationException("FIS competitor file has an unsupported format.");
            }
            while (rows.MoveNext())
            {
                var r = rows.Current;
                if (r.Length != 12) { throw new DomainValidationException("FIS competitor row is malformed."); }
                var athleteCode = r[2].Trim();
                if (athleteCode.Length == 0) { continue; }
                var birthYear = DateOnly.TryParseExact(r[6], "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var birthdate) ? birthdate.Year : (int?)null;
                Gender? gender = r[5].Trim().ToUpperInvariant() switch
                {
                    "M" => Gender.Male, "F" or "W" => Gender.Female, _ => null
                };
                var athlete = new FisAthlete(athleteCode, r[3].Trim(), r[4].Trim(), birthYear,
                    gender, EmptyToNull(r[7]), EmptyToNull(r[9]), EmptyToNull(r[11]));
                if (!athletes.TryAdd(athleteCode, athlete))
                {
                    throw new DomainValidationException($"FIS list has duplicate Code {athleteCode}.");
                }
                if (!competitorIds.TryAdd(r[0], athleteCode))
                {
                    throw new DomainValidationException("FIS list has duplicate competitor IDs.");
                }
            }
            if (athletes.Count == 0) { throw new DomainValidationException("FIS list contains no competitors."); }
            var pointValues = new Dictionary<string, Dictionary<string, decimal>>(StringComparer.OrdinalIgnoreCase);
            using var pointRows = ReadRows(points, 30_000_000).GetEnumerator();
            if (!pointRows.MoveNext() || !pointRows.Current.SequenceEqual(
                ["Recid", "Listid", "Competitorid", "Disciplinecode", "Fispoints", "Position", "Penalty", "Lastupdate"]))
            {
                throw new DomainValidationException("FIS points file has an unsupported format.");
            }
            while (pointRows.MoveNext())
            {
                var r = pointRows.Current;
                if (r.Length != 8 || r[1] != h[0])
                {
                    throw new DomainValidationException("FIS points row is malformed or belongs to another list.");
                }
                if (!competitorIds.TryGetValue(r[2], out var athleteCode))
                {
                    throw new DomainValidationException("FIS points row has an unknown competitor.");
                }
                if (r[4].Length == 0) { continue; }
                if (!decimal.TryParse(r[4], NumberStyles.Number, CultureInfo.InvariantCulture, out var value))
                {
                    throw new DomainValidationException("FIS points row has invalid points.");
                }
                if (!pointValues.TryGetValue(athleteCode, out var disciplines))
                {
                    disciplines = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
                    pointValues.Add(athleteCode, disciplines);
                }
                if (!disciplines.TryAdd(r[3], value))
                {
                    throw new DomainValidationException("FIS points file has duplicate discipline values.");
                }
            }
            foreach (var (athleteCode, disciplines) in pointValues)
            {
                athletes[athleteCode] = athletes[athleteCode] with { Points = disciplines };
            }
            var published = ParseDate(h[4]);
            var validFrom = ParseDate(h[7]);
            var validTo = ParseDate(h[8]);
            if (string.IsNullOrWhiteSpace(h[3]) || validFrom > validTo)
            {
                throw new DomainValidationException("FIS points-list metadata is invalid.");
            }
            return new FisPointsList(code, h[3], published, validFrom, validTo, athletes,
                ReadPenaltyRules(zip, prefix, h[0], seasonCode));
        }
        catch (InvalidDataException ex)
        {
            throw new DomainValidationException($"FIS ZIP could not be read: {ex.Message}");
        }
        catch (DecoderFallbackException)
        {
            throw new DomainValidationException("FIS ZIP contains text with an unsupported encoding.");
        }
    }

    private static FisPenaltyListRules? ReadPenaltyRules(ZipArchive zip, string prefix, string listId, int season)
    {
        string[] names = [prefix + "cat.csv", prefix + "dis.csv", "Fiscategory.txt"];
        var entries = names.Select(name => zip.Entries.Where(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray()).ToArray();
        if (entries.All(x => x.Length == 0)) { return null; }
        if (entries.Any(x => x.Length != 1))
        { throw new DomainValidationException("FIS penalty rule tables are incomplete or duplicated."); }
        string[][] Rows(int index, string[] expected)
        {
            var rows = ReadRows(entries[index][0], 2_000_000).ToArray();
            if (rows.Length == 0 || !rows[0].SequenceEqual(expected) || rows.Skip(1).Any(x => x.Length != expected.Length))
            { throw new DomainValidationException("FIS penalty rule table has an unsupported format."); }
            return rows.Skip(1).ToArray();
        }
        decimal Number(string value) => decimal.TryParse(value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture, out var result) && Math.Abs(result) <= 9999 && decimal.Round(result, 2) == result
            ? result : throw new DomainValidationException("FIS penalty rule value is invalid.");
        int Integer(string value) => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var result)
            ? result : throw new DomainValidationException("FIS penalty rule integer is invalid.");
        var categoryRows = Rows(0, ["Recid", "Listid", "Seasoncode", "Catcode", "Minfispoints", "Maxfispoints", "Lastupdate"]);
        var disciplineRows = Rows(1, ["Recid", "Listid", "Seasoncode", "Disciplinecode", "Gender", "Zvalue", "Fvalue", "Maxpoints", "Adder0", "Adder1", "Adder2", "Adder3", "Adder4"]);
        if (categoryRows.Concat(disciplineRows).Any(x => x[1] != listId || Integer(x[2]) != season))
        { throw new DomainValidationException("FIS penalty rules belong to another list or season."); }
        var levels = Rows(2, ["Recid", "Sectorcode", "Catcode", "Description", "Displayorder", "Inuse", "Published", "Racelevel", "Calautoload", "Lastupdate"])
            .Where(x => x[1] == "AL").ToArray();
        if (levels.GroupBy(x => x[2]).Any(x => x.Count() != 1)
            || categoryRows.GroupBy(x => x[3]).Any(x => x.Count() != 1)
            || disciplineRows.GroupBy(x => (x[3], x[4])).Any(x => x.Count() != 1))
        { throw new DomainValidationException("FIS penalty rule tables contain duplicate values."); }
        var categories = categoryRows.Select(x =>
        {
            var match = levels.SingleOrDefault(y => y[2] == x[3]);
            return new FisCategoryPenalty(x[3], match is null ? -1 : Integer(match[7]), Number(x[4]), Number(x[5]));
        }).ToArray();
        var disciplines = disciplineRows.Select(x => new FisDisciplinePenalty(x[3], x[4] switch
        { "M" => Gender.Male, "W" or "F" => Gender.Female, _ => throw new DomainValidationException("FIS rule gender is invalid.") },
            Integer(x[6]), Number(x[7]), Number(x[5]), x.Skip(8).Select(Number).ToArray())).ToArray();
        if (categories.Any(x => x.Minimum < 0 || x.Maximum < x.Minimum || x.Maximum > 999.99m || x.RaceLevel > 4)
            || disciplines.Any(x => x.FValue <= 0 || x.MaximumPoints <= 0 || x.Adders.Any(a => a < 0 || a > 999.99m)))
        { throw new DomainValidationException("FIS penalty rule limits are invalid."); }
        return new(season, categories, disciplines);
    }

    private static ZipArchiveEntry RequiredEntry(ZipArchive zip, string suffix)
    {
        var matches = zip.Entries.Where(x => x.Name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            .Take(2).ToArray();
        return matches.Length == 1 ? matches[0]
            : throw new DomainValidationException($"FIS ZIP must contain exactly one {suffix} file.");
    }

    private static IEnumerable<string[]> ReadRows(ZipArchiveEntry entry, long maxUncompressedBytes)
    {
        if (entry.Length > maxUncompressedBytes)
        {
            throw new DomainValidationException($"FIS {entry.Name} is unexpectedly large.");
        }
        using var stream = entry.Open();
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            yield return line.Split('\t').Select(x => x.Trim('"').Replace("\"\"", "\"")).ToArray();
        }
    }

    private static DateOnly ParseDate(string text) => DateOnly.TryParseExact(text, "yyyy-MM-dd",
        CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
        ? date : throw new DomainValidationException("FIS list date is invalid.");

    private static string? EmptyToNull(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
