namespace OpenSkiTime.Import;

/// <summary>
/// Determines whether a raw cell value should be interpreted as "is participating"
/// per constitution Principle VII and FR-032.
/// Accepted truthy values (case-insensitive): "yes", "kyllä", "x".
/// Everything else (including empty/null) is treated as non-participating.
/// </summary>
public static class ParticipationValueMatcher
{
    private static readonly HashSet<string> TruthyValues =
        new(StringComparer.OrdinalIgnoreCase) { "yes", "kyllä", "x" };

    public static bool IsParticipating(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return TruthyValues.Contains(value.Trim());
    }
}
