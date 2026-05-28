namespace OpenSkiTime.Import;

/// <summary>
/// Canonical field identifiers used by the import pipeline.
/// The <see cref="HeaderMapper"/> maps raw column header strings onto these.
/// </summary>
public enum ImportField
{
    Unknown = 0,

    /// <summary>Upper-cased last / family name.</summary>
    LastName,

    /// <summary>First / given name (mixed case).</summary>
    FirstName,

    /// <summary>
    /// Single full-name column — will be split by <c>NameProjector</c>.
    /// Format expected: "LAST, First" or "First LAST".
    /// </summary>
    FullName,

    /// <summary>4-digit year of birth, e.g. 2005.</summary>
    YearOfBirth,

    /// <summary>FIS athlete code (up to 9 chars).</summary>
    FisCode,

    /// <summary>ISO 3-letter nation code.</summary>
    NationCode,

    /// <summary>Club / team name.</summary>
    ClubName,

    /// <summary>Bib / start number (positive integer).</summary>
    BibNumber,

    /// <summary>
    /// Competition participation flag. Column header matches a competition's
    /// <c>ShortLabel</c>. Value "1", "x", "X", "true", or non-empty = participating.
    /// </summary>
    ParticipationFlag,
}
