using OpenSkiTime.Application;
using Xunit;

namespace OpenSkiTime.Tests;

public sealed class CompetitionEditorChoicesTests
{
    [Theory]
    [InlineData("520", "380", 140)]
    [InlineData(" 1200 ", "1200", 0)]
    [InlineData("438", "258", 180)]
    public void VerticalDropIsStartMinusFinish(string start, string finish, int expected)
        => Assert.Equal(expected, CourseProfile.VerticalDrop(start, finish));

    [Theory]
    [InlineData("", "380")]
    [InlineData("520", "")]
    [InlineData("52", null)]
    [InlineData("5x0", "380")]
    [InlineData("-520", "380")]
    [InlineData("520.5", "380")]
    [InlineData("380", "520")]
    public void VerticalDropIsUnknownForIncompleteOrInvertedAltitudes(string? start, string? finish)
        => Assert.Null(CourseProfile.VerticalDrop(start, finish));

    [Fact]
    public void SeasonChoicesSurroundTheSeasonOfTheCompetitionDate()
    {
        // December 2026 belongs to the 2027 season (2026/27); FIS seasons turn in June.
        Assert.Equal(["2026", "2027", "2028"], FisReferenceData.SeasonChoices(new DateOnly(2026, 12, 12), "2027"));
        Assert.Equal(["2026", "2027", "2028"], FisReferenceData.SeasonChoices(new DateOnly(2027, 3, 1), ""));
        Assert.Equal(["2027", "2028", "2029"], FisReferenceData.SeasonChoices(new DateOnly(2027, 6, 1), null));
    }

    [Fact]
    public void SeasonChoicesKeepTheCurrentSeasonEvenOutsideTheRange()
    {
        Assert.Equal(["2020", "2026", "2027", "2028"], FisReferenceData.SeasonChoices(new DateOnly(2026, 12, 12), "2020"));
        Assert.Equal(["2031"], FisReferenceData.SeasonChoices(null, "2031"));
        Assert.Empty(FisReferenceData.SeasonChoices(null, "27"));
    }

    [Fact]
    public void SeriesSeasonChoicesAreLabelsAroundTheStartDate()
    {
        Assert.Equal(["2025/26", "2026/27", "2027/28"], FisReferenceData.SeriesSeasonChoices(new DateOnly(2026, 12, 12)));
        Assert.Equal(["2025/26", "2026/27", "2027/28"], FisReferenceData.SeriesSeasonChoices(new DateOnly(2027, 3, 1)));
        Assert.Empty(FisReferenceData.SeriesSeasonChoices(null));
    }

    [Theory]
    [InlineData("fin", "FIN", true)]
    [InlineData("F", "FIN", true)]
    [InlineData("land", "FIN", true)]
    [InlineData("switz", "SUI", true)]
    [InlineData("", "SUI", true)]
    [InlineData("xyz", "FIN", false)]
    [InlineData("swe", "SUI", false)]
    public void NationSearchMatchesCodePrefixOrName(string search, string code, bool expected)
        => Assert.Equal(expected, FisReferenceData.Matches(FisReferenceData.Nations.Single(x => x.Code == code), search));

    [Fact]
    public void ReferenceListsHaveUniqueThreeLetterCodes()
    {
        Assert.All(FisReferenceData.Nations, x => Assert.Matches("^[A-Z]{3}$", x.Code));
        Assert.Equal(FisReferenceData.Nations.Count, FisReferenceData.Nations.Select(x => x.Code).Distinct().Count());
        Assert.Equal(FisReferenceData.CalendarCategories.Count, FisReferenceData.CalendarCategories.Distinct().Count());
        Assert.Contains("NJR", FisReferenceData.CalendarCategories);
    }
}
