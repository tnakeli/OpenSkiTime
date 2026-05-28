namespace OpenSkiTime.Domain.Competitors;

/// <summary>
/// A non-empty club / team name. Stored as-trimmed; not forced to upper-case
/// because club names carry proper casing (e.g., "Lahti Ski Club").
/// </summary>
public readonly record struct ClubName
{
    public string Value { get; }

    public ClubName(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            throw new ArgumentException("Club name must not be empty.", nameof(raw));
        }

        Value = raw.Trim();
    }

    public override string ToString() => Value;

    public static implicit operator string(ClubName club) => club.Value;
}
