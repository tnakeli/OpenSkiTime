using System.Globalization;

namespace OpenSkiTime.Domain.Common;

/// <summary>
/// A non-empty, trimmed, uppercase name. Encodes Constitution Principle VII
/// (Data Import Fidelity) and FR-023 at the type level: a value of this type
/// is, by construction, a valid uppercased last name.
/// </summary>
/// <remarks>
/// Equality and ordering are case-insensitive in both directions because the
/// stored value is already uppercase, but inputs may come from arbitrary
/// casing. Constructed values use <see cref="CultureInfo.InvariantCulture"/>
/// to keep behavior consistent across user locales (race offices may run on
/// machines configured for any culture).
/// </remarks>
public readonly record struct UpperCaseName : IComparable<UpperCaseName>
{
    public string Value { get; }

    public UpperCaseName(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            throw new ArgumentException(
                "Last name is required and must contain at least one non-whitespace character.",
                nameof(raw));
        }

        Value = raw.Trim().ToUpper(CultureInfo.InvariantCulture);
    }

    public override string ToString() => Value;

    public static implicit operator string(UpperCaseName name) => name.Value;

    public int CompareTo(UpperCaseName other)
        => string.Compare(Value, other.Value, StringComparison.Ordinal);

    public static bool operator <(UpperCaseName left, UpperCaseName right) => left.CompareTo(right) < 0;
    public static bool operator <=(UpperCaseName left, UpperCaseName right) => left.CompareTo(right) <= 0;
    public static bool operator >(UpperCaseName left, UpperCaseName right) => left.CompareTo(right) > 0;
    public static bool operator >=(UpperCaseName left, UpperCaseName right) => left.CompareTo(right) >= 0;
}
