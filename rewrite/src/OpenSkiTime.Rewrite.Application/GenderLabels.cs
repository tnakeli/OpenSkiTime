using OpenSkiTime.Rewrite.Domain;

namespace OpenSkiTime.Rewrite.Application;

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
