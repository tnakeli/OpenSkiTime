using OpenSkiTime.Application.Abstractions;

namespace OpenSkiTime.Import;

/// <summary>
/// Matches a parsed import row to an existing <see cref="CompetitorRef"/> in
/// the snapshot. Match strategy (in priority order):
/// <list type="number">
///   <item>FIS code (if both sides have a non-empty FIS code).</item>
///   <item>LastName (upper) + FirstName (case-insensitive) + YearOfBirth.</item>
/// </list>
/// </summary>
public sealed class CompetitorMatcher
{
    private readonly IReadOnlyList<CompetitorRef> _existing;

    public CompetitorMatcher(IReadOnlyList<CompetitorRef> existing)
    {
        _existing = existing ?? throw new ArgumentNullException(nameof(existing));
    }

    /// <summary>
    /// Attempts to find a matching existing competitor for the given row data.
    /// Returns null when no match is found (the competitor is new).
    /// </summary>
    public CompetitorRef? Match(
        string lastNameUpper,
        string firstName,
        int yearOfBirth,
        string? fisCode)
    {
        // Priority 1: FIS code.
        if (!string.IsNullOrWhiteSpace(fisCode))
        {
            var byFis = _existing.FirstOrDefault(c =>
                !string.IsNullOrWhiteSpace(c.Code) &&
                string.Equals(c.Code, fisCode.Trim(), StringComparison.OrdinalIgnoreCase));
            if (byFis is not null)
            {
                return byFis;
            }
        }

        // Priority 2: Name + YOB.
        return _existing.FirstOrDefault(c =>
            string.Equals(c.LastNameUpper, lastNameUpper, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(c.FirstName, firstName, StringComparison.OrdinalIgnoreCase) &&
            c.YearOfBirth == yearOfBirth);
    }
}
