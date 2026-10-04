namespace OpenSkiTime.Domain;

public enum Gender { Female, Male, Other }

public sealed record CompetitorValues(
    string Surname, string FirstName, int? BirthYear,
    string? FederationCode, string? Nation, string? Club, Gender? Gender)
{
    public CompetitorValues Validated(int eventYear)
    {
        var surname = Surname?.Trim() ?? string.Empty;
        var code = Optional(FederationCode, 20);
        if (surname.Length == 0 && code is null)
        {
            throw new DomainValidationException("Enter a Code or surname to identify the competitor.");
        }
        if (surname.Length > 100) { throw new DomainValidationException("Surname must be at most 100 characters."); }
        surname = surname.ToUpperInvariant();
        var firstName = Optional(FirstName, 100) ?? string.Empty;
        if (BirthYear is { } year && (year < 1850 || year > eventYear))
        {
            throw new DomainValidationException($"Birth year must be between 1850 and {eventYear}.");
        }

        if (Gender is { } gender && !Enum.IsDefined(gender))
        {
            throw new DomainValidationException("Select a valid gender.");
        }

        var nation = Optional(Nation, 3)?.ToUpperInvariant();
        if (nation is not null && (nation.Length != 3 || !nation.All(c => c is >= 'A' and <= 'Z')))
        {
            throw new DomainValidationException("Nation must be a three-letter code.");
        }

        return this with
        {
            Surname = surname, FirstName = firstName, Nation = nation,
            FederationCode = code, Club = Optional(Club, 160),
        };
    }

    public string Readiness(bool entered)
    {
        if (!entered) { return "Not entered"; }
        var missing = new List<string>();
        if (FirstName.Length == 0) { missing.Add("first name"); }
        if (BirthYear is null) { missing.Add("birth year"); }
        if (Gender is null) { missing.Add("gender"); }
        return missing.Count == 0 ? "Ready" : "Review: " + string.Join(", ", missing);
    }

    private static string? Optional(string? value, int maxLength)
    {
        var trimmed = value?.Trim();
        if (trimmed?.Length > maxLength)
        {
            throw new DomainValidationException($"Value must be at most {maxLength} characters.");
        }
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }
}

public sealed record CategoryRuleValues(
    string Label, int BirthYearMin, int BirthYearMax, Gender? Gender, int DisplayOrder)
{
    public CategoryRuleValues Validated(int eventYear)
    {
        var label = SeriesValues.Required(Label, nameof(Label), 100);
        if (BirthYearMin < 1850 || BirthYearMax > eventYear || BirthYearMin > BirthYearMax)
        {
            throw new DomainValidationException("Category birth years must form a valid inclusive range.");
        }
        if (Gender is { } gender && !Enum.IsDefined(gender))
        {
            throw new DomainValidationException("Select a valid category gender.");
        }
        if (DisplayOrder < 0)
        {
            throw new DomainValidationException("Category order cannot be negative.");
        }
        return this with { Label = label };
    }
}

public sealed record CompetitorDetails(Guid Id, CompetitorValues Values);
public sealed record ParticipationDetails(Guid CompetitorId, Guid CompetitionId, bool Participates,
    int? ImportedBib, int? StartOrder);
public sealed record CategoryRuleDetails(Guid Id, CategoryRuleValues Values);
public sealed record CompetitorDeskDetails(
    Guid SeriesId, long Revision, IReadOnlyList<CompetitorDetails> Competitors,
    IReadOnlyList<ParticipationDetails> Participations, IReadOnlyList<CategoryRuleDetails> Categories);
public sealed record DeskMutationResult<T>(long Revision, T Value);

public static class CategoryResolver
{
    public const string Unclassified = "Unclassified";
    public static string Resolve(CompetitorValues competitor, IEnumerable<CategoryRuleDetails> rules)
    {
        ArgumentNullException.ThrowIfNull(competitor);
        ArgumentNullException.ThrowIfNull(rules);
        if (competitor.BirthYear is not { } year) { return Unclassified; }
        var matches = rules.Where(rule => year >= rule.Values.BirthYearMin
            && year <= rule.Values.BirthYearMax
            && (rule.Values.Gender is null || rule.Values.Gender == competitor.Gender))
            .Take(2).ToArray();
        return matches.Length switch
        {
            0 => Unclassified,
            1 => matches[0].Values.Label,
            _ => "Ambiguous",
        };
    }
}
