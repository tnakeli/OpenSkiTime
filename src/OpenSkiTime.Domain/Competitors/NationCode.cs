namespace OpenSkiTime.Domain.Competitors;

/// <summary>
/// ISO 3-letter nation code stored in upper-case (e.g., FIN, ITA).
/// Reuses the same invariant as EventSeries.Nation (3 chars, non-empty).
/// </summary>
public readonly record struct NationCode
{
    public string Value { get; }

    public NationCode(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw.Trim().Length != 3)
        {
            throw new ArgumentException(
                "Nation code must be a 3-letter ISO code (e.g., FIN, ITA).", nameof(raw));
        }

        Value = raw.Trim().ToUpperInvariant();
    }

    public override string ToString() => Value;

    public static implicit operator string(NationCode code) => code.Value;
}
