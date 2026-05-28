using OpenSkiTime.Domain.Common;

namespace OpenSkiTime.Domain.CategoryRules;

/// <summary>
/// A classification rule that assigns a category label to
/// competitors whose <c>YearOfBirth</c> falls within
/// [<see cref="BirthYearMin"/>, <see cref="BirthYearMax"/>] and whose
/// <c>Gender</c> matches (null = applies to all genders).
/// </summary>
public class CategoryRule
{
    /// <summary>EF Core constructor. Do not call from production code.</summary>
    private CategoryRule() { }

    private CategoryRule(
        Guid id,
        Guid eventSeriesId,
        string label,
        int birthYearMin,
        int birthYearMax,
        Gender? gender,
        int displayOrder)
    {
        Id = id;
        EventSeriesId = eventSeriesId;
        Label = label;
        BirthYearMin = birthYearMin;
        BirthYearMax = birthYearMax;
        Gender = gender;
        DisplayOrder = displayOrder;
    }

    public Guid Id { get; private set; }

    public Guid EventSeriesId { get; private set; }

    /// <summary>Human-readable category label, e.g. "U10 Boys", "Senior Women".</summary>
    public string Label { get; private set; } = null!;

    /// <summary>Inclusive minimum year of birth (e.g., 2015).</summary>
    public int BirthYearMin { get; private set; }

    /// <summary>Inclusive maximum year of birth (e.g., 2019).</summary>
    public int BirthYearMax { get; private set; }

    /// <summary>
    /// Restricts the rule to one gender. Null means the rule applies to all genders.
    /// </summary>
    public Gender? Gender { get; private set; }

    /// <summary>Determines the rendering order in the competitor grid.</summary>
    public int DisplayOrder { get; private set; }

    public static CategoryRule Create(
        Guid id,
        Guid eventSeriesId,
        string label,
        int birthYearMin,
        int birthYearMax,
        Gender? gender = null,
        int displayOrder = 0)
    {
        if (eventSeriesId == Guid.Empty)
        {
            throw new ArgumentException("EventSeriesId is required.", nameof(eventSeriesId));
        }

        ValidateLabel(label);
        ValidateBirthYearRange(birthYearMin, birthYearMax);

        return new CategoryRule(
            id == Guid.Empty ? Guid.NewGuid() : id,
            eventSeriesId,
            label.Trim(),
            birthYearMin,
            birthYearMax,
            gender,
            displayOrder);
    }

    public void Update(string label, int birthYearMin, int birthYearMax, Gender? gender, int displayOrder)
    {
        ValidateLabel(label);
        ValidateBirthYearRange(birthYearMin, birthYearMax);

        Label = label.Trim();
        BirthYearMin = birthYearMin;
        BirthYearMax = birthYearMax;
        Gender = gender;
        DisplayOrder = displayOrder;
    }

    /// <summary>
    /// Returns true when a competitor with the given year of birth and gender
    /// matches this rule. A null Gender on the rule matches any competitor gender.
    /// </summary>
    public bool Matches(int yearOfBirth, Gender? competitorGender)
    {
        if (yearOfBirth < BirthYearMin || yearOfBirth > BirthYearMax)
        {
            return false;
        }

        return Gender is null || Gender == competitorGender;
    }

    private static void ValidateLabel(string label)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            throw new ArgumentException("Category label is required.", nameof(label));
        }
    }

    private static void ValidateBirthYearRange(int min, int max)
    {
        if (min < 1900 || max < 1900)
        {
            throw new ArgumentOutOfRangeException(nameof(min), "Birth year must be ≥ 1900.");
        }

        if (max < min)
        {
            throw new ArgumentException(
                "BirthYearMax must be ≥ BirthYearMin.",
                nameof(max));
        }
    }
}
