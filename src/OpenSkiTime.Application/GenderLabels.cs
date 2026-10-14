using OpenSkiTime.Domain;

namespace OpenSkiTime.Application;

public static class GenderLabels
{
    public static string Format(Gender? gender) => gender switch
    {
        Gender.Female => "Women",
        Gender.Male => "Men",
        Gender.Other => "Other",
        _ => string.Empty,
    };
}

/// <summary>
/// Display names of a competition's FIS calendar gender code. The code itself (M, W, A) is what the series file and FIS
/// messages store; the application shows the same Men / Women words as for competitors, and Mixed for A.
/// </summary>
public static class CompetitionGenderLabels
{
    public static IReadOnlyList<string> Choices { get; } = ["", "Women", "Men", "Mixed"];

    public static string Format(string? code) => code?.Trim().ToUpperInvariant() switch
    {
        null or "" => string.Empty,
        "M" => "Men",
        "W" or "L" => "Women",
        "A" => "Mixed",
        _ => code.Trim(),
    };

    public static string ToCode(string? label) => label?.Trim().ToUpperInvariant() switch
    {
        null or "" => string.Empty,
        "MEN" or "M" => "M",
        "WOMEN" or "W" or "L" => "W",
        "MIXED" or "A" => "A",
        _ => label.Trim(),
    };
}
