using System.Globalization;

namespace OpenSkiTime.Import;

/// <summary>
/// Resolves (LastName, FirstName) from a <see cref="RawImportRow"/>.
/// Handles two layouts per spec US3:
/// <list type="bullet">
///   <item><b>Two-column preferred</b> — separate LastName + FirstName columns.</item>
///   <item>
///     <b>Single FullName column</b> — split heuristic:
///     <list type="bullet">
///       <item><c>"SMITH, John"</c> → (SMITH, John)  — comma separator</item>
///       <item><c>"John SMITH"</c> → last token is all-upper → (SMITH, John)</item>
///       <item><c>"JOHN SMITH"</c> → all uppercase tokens → last token as last name</item>
///     </list>
///   </item>
/// </list>
/// </summary>
public static class NameProjector
{
    /// <summary>
    /// Projects (lastNameRaw, firstName) from the row.
    /// Returns null for both when neither last name nor full name is present.
    /// </summary>
    public static (string? LastName, string? FirstName) Project(RawImportRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        // Preferred: explicit separate columns.
        bool hasLast = row.TryGet(ImportField.LastName, out var last);
        bool hasFirst = row.TryGet(ImportField.FirstName, out var first);

        if (hasLast)
        {
            return (last, hasFirst ? first : null);
        }

        // Fall back to FullName column.
        if (!row.TryGet(ImportField.FullName, out var full))
        {
            return (null, null);
        }

        return SplitFullName(full);
    }

    /// <summary>
    /// Splits a single full-name string into (LastName, FirstName).
    /// Exposed internal for unit testing.
    /// </summary>
    internal static (string LastName, string? FirstName) SplitFullName(string full)
    {
        full = full.Trim();

        // --- "SMITH, John" or "SMITH,John" ---
        int comma = full.IndexOf(',', StringComparison.Ordinal);
        if (comma > 0)
        {
            var ln = full[..comma].Trim();
            var fn = full[(comma + 1)..].Trim();
            return (ln, string.IsNullOrEmpty(fn) ? null : fn);
        }

        // --- Split on whitespace ---
        var tokens = full.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 1)
        {
            return (tokens[0], null);
        }

        // If the last token is entirely uppercase letters (possibly with diacritics
        // uppercased via InvariantCulture), treat it as the last name.
        var lastToken = tokens[^1];
        if (IsUpperCase(lastToken))
        {
            var fn = string.Join(' ', tokens[..^1]);
            return (lastToken, fn);
        }

        // If the first token is entirely uppercase, treat it as the last name.
        var firstToken = tokens[0];
        if (IsUpperCase(firstToken))
        {
            var fn = string.Join(' ', tokens[1..]);
            return (firstToken, fn);
        }

        // No casing hint — last token is the last name (FIS export convention).
        return (tokens[^1], string.Join(' ', tokens[..^1]));
    }

    private static bool IsUpperCase(string s)
    {
        foreach (var c in s)
        {
            if (!char.IsLetter(c))
            {
                continue;
            }

            if (char.ToUpper(c, CultureInfo.InvariantCulture) != c)
            {
                return false;
            }
        }

        return s.Any(char.IsLetter);
    }
}
