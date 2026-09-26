namespace OpenSkiTime.Import;

/// <summary>
/// Maps raw column header strings (from TSV paste) onto <see cref="ImportField"/>
/// values and, for participation columns, onto competition <c>ShortLabel</c>s.
/// Matching is case-insensitive and trims whitespace.
/// </summary>
public sealed class HeaderMapper
{
    /// <summary>Known aliases for each canonical ImportField.</summary>
    private static readonly Dictionary<string, ImportField> KnownHeaders =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["lastname"]    = ImportField.LastName,
            ["last name"]   = ImportField.LastName,
            ["last"]        = ImportField.LastName,
            ["surname"]     = ImportField.LastName,
            ["family name"] = ImportField.LastName,

            ["firstname"]   = ImportField.FirstName,
            ["first name"]  = ImportField.FirstName,
            ["first"]       = ImportField.FirstName,
            ["given name"]  = ImportField.FirstName,
            ["forename"]    = ImportField.FirstName,

            ["name"]        = ImportField.FullName,
            ["full name"]   = ImportField.FullName,
            ["fullname"]    = ImportField.FullName,
            ["athlete"]     = ImportField.FullName,

            ["yob"]         = ImportField.YearOfBirth,
            ["year"]        = ImportField.YearOfBirth,
            ["yearofbirth"] = ImportField.YearOfBirth,
            ["year of birth"] = ImportField.YearOfBirth,
            ["born"]        = ImportField.YearOfBirth,
            ["birthyear"]   = ImportField.YearOfBirth,

            ["fiscode"]     = ImportField.FisCode,
            ["fis code"]    = ImportField.FisCode,
            ["fis"]         = ImportField.FisCode,
            ["code"]        = ImportField.FisCode,

            ["nat"]         = ImportField.NationCode,
            ["nation"]      = ImportField.NationCode,
            ["nationcode"]  = ImportField.NationCode,
            ["nation code"] = ImportField.NationCode,
            ["country"]     = ImportField.NationCode,

            ["club"]        = ImportField.ClubName,
            ["clubname"]    = ImportField.ClubName,
            ["club name"]   = ImportField.ClubName,
            ["team"]        = ImportField.ClubName,

            ["gender"]      = ImportField.Gender,
            ["sex"]         = ImportField.Gender,
            ["sukupuoli"]   = ImportField.Gender,

            ["bib"]         = ImportField.BibNumber,
            ["bib number"]  = ImportField.BibNumber,
            ["bibnumber"]   = ImportField.BibNumber,
            ["startnumber"] = ImportField.BibNumber,
            ["start number"] = ImportField.BibNumber,
            ["nr"]          = ImportField.BibNumber,
            ["no"]          = ImportField.BibNumber,
        };

    private readonly HashSet<string> _competitionShortLabels;

    /// <param name="competitionShortLabels">
    /// The set of competition short labels for this Event Series (e.g. "3.1 SL").
    /// Any column whose header matches one of these is treated as a
    /// <see cref="ImportField.ParticipationFlag"/> for that competition.
    /// </param>
    public HeaderMapper(IEnumerable<string> competitionShortLabels)
    {
        ArgumentNullException.ThrowIfNull(competitionShortLabels);
        _competitionShortLabels = new HashSet<string>(
            competitionShortLabels, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Maps a single raw header string to an <see cref="ImportField"/>.
    /// Returns <see cref="ImportField.Unknown"/> for unrecognised headers
    /// that are not competition short labels.
    /// </summary>
    public ImportField Map(string rawHeader)
    {
        var key = rawHeader?.Trim() ?? string.Empty;

        if (KnownHeaders.TryGetValue(key, out var field))
        {
            return field;
        }

        if (_competitionShortLabels.Contains(key))
        {
            return ImportField.ParticipationFlag;
        }

        return ImportField.Unknown;
    }

    /// <summary>
    /// Maps a complete header row and returns the ordered list of
    /// <see cref="ColumnMapping"/> records (one per column).
    /// </summary>
    public IReadOnlyList<ColumnMapping> MapHeaders(string[] headers)
    {
        ArgumentNullException.ThrowIfNull(headers);

        var result = new List<ColumnMapping>(headers.Length);
        for (int i = 0; i < headers.Length; i++)
        {
            var raw = headers[i].Trim();
            var field = Map(raw);
            result.Add(new ColumnMapping(i, raw, field));
        }

        return result;
    }
}

/// <summary>Binding of a column index to its resolved <see cref="ImportField"/>.</summary>
public sealed record ColumnMapping(int Index, string RawHeader, ImportField Field);
