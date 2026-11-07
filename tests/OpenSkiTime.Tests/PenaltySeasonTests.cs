using OpenSkiTime.Domain;
using Xunit;

namespace OpenSkiTime.Tests;

public sealed class PenaltySeasonTests
{
    private static FisPenaltyListRules Rules(int season)
        => new(season, [new("FIS", 0, 0m, 999.99m)], [new("SL", Gender.Male, 730, 165m, 0m, [0m, 0m, 0m, 0m, 0m])]);

    [Theory]
    [InlineData(2026, true)]
    [InlineData(2027, true)]
    [InlineData(2025, false)]
    [InlineData(2028, false)]
    public void OnlyReviewedSeasonsAreSupported(int season, bool supported)
        => Assert.Equal(supported, Rules(season).IsSupportedSeason);

    [Theory]
    [InlineData(2026, "2025/26")]
    [InlineData(2027, "2026/27")]
    [InlineData(2100, "2099/00")]
    public void SeasonLabelUsesTheYearTheSeasonEnds(int season, string label)
        => Assert.Equal(label, FisPenaltyListRules.SeasonLabel(season));

    [Fact]
    public void UnsupportedSeasonMessageNamesTheSeasonAndTheSupportedOnes()
    {
        var message = Rules(2025).UnsupportedSeasonMessage;
        Assert.Contains("2024/25", message, StringComparison.Ordinal);
        Assert.Contains("2025/26", message, StringComparison.Ordinal);
        Assert.Contains("2026/27", message, StringComparison.Ordinal);
    }

    [Fact]
    public void PreviousSeasonResolvesTheSameProfileAsTheReviewedSeason()
        => Assert.Equal(Rules(2027).Resolve("FIS", Discipline.Slalom, Gender.Male), Rules(2026).Resolve("FIS", Discipline.Slalom, Gender.Male));
}
