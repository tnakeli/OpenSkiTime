namespace OpenSkiTime.Domain.Competitors;

/// <summary>
/// A positive integer bib number. Zero is not a valid bib.
/// Two competitors within the same Event Series must not share the same bib.
/// That uniqueness invariant is enforced by EventSeries, not this value object.
/// </summary>
public readonly record struct BibNumber : IComparable<BibNumber>
{
    public int Value { get; }

    public BibNumber(int value)
    {
        if (value < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "Bib number must be a positive integer (≥ 1).");
        }

        Value = value;
    }

    public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public int CompareTo(BibNumber other) => Value.CompareTo(other.Value);

    public static bool operator <(BibNumber left, BibNumber right) => left.Value < right.Value;
    public static bool operator <=(BibNumber left, BibNumber right) => left.Value <= right.Value;
    public static bool operator >(BibNumber left, BibNumber right) => left.Value > right.Value;
    public static bool operator >=(BibNumber left, BibNumber right) => left.Value >= right.Value;

    public static implicit operator int(BibNumber bib) => bib.Value;
}
