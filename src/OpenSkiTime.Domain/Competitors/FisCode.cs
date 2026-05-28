namespace OpenSkiTime.Domain.Competitors;

/// <summary>
/// FIS competitor code: 1–9 uppercase alphanumeric characters, e.g. "6530613".
/// This is optional per the spec (FR-019); use <c>null</c> when absent.
/// </summary>
public readonly record struct FisCode
{
    public string Value { get; }

    public FisCode(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            throw new ArgumentException("FIS code must not be empty.", nameof(raw));
        }

        var trimmed = raw.Trim().ToUpperInvariant();

        if (trimmed.Length > 9)
        {
            throw new ArgumentException(
                "FIS code must not exceed 9 characters.", nameof(raw));
        }

        Value = trimmed;
    }

    public override string ToString() => Value;

    public static implicit operator string(FisCode code) => code.Value;
}
