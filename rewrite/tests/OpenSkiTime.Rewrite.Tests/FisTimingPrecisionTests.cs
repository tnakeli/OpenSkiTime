using OpenSkiTime.Rewrite.Devices;
using OpenSkiTime.Rewrite.Domain;
using OpenSkiTime.Rewrite.Timing;
using Xunit;

namespace OpenSkiTime.Rewrite.Tests;

// Timing Booklet 2.67: ICR 611.2.1 / 611.3.5 and section 14;
// Data Booklet 1.15: sections 7.3 and 8.1. See docs/fis-timing-review.md.
public sealed class FisTimingPrecisionTests
{
    [Theory]
    [InlineData("10:48:31.8", 1, 8000000L, "10:48:31.8000000")]
    [InlineData("10:48:31.86", 2, 8600000L, "10:48:31.8600000")]
    [InlineData("10:48:31.978", 3, 9780000L, "10:48:31.9780000")]
    [InlineData("10:48:31.9781", 4, 9781000L, "10:48:31.9781000")]
    [InlineData("10:48:31.97812", 5, 9781200L, "10:48:31.9781200")]
    [InlineData("10:48:31.978123", 6, 9781230L, "10:48:31.9781230")]
    [InlineData("10:48:31.9781234", 7, 9781234L, "10:48:31.9781234")]
    public void AllSourcesUseTheSameIntegralScaleWithoutClaimingExtraSourcePrecision(
        string input, int precision, long fractionTicks, string display)
    {
        Assert.True(TimingTime.TryTimeOfDay(input, out var ticks, out var actualPrecision));
        Assert.Equal((10 * 3600L + 48 * 60 + 31) * TimeSpan.TicksPerSecond + fractionTicks, ticks);
        Assert.Equal(precision, actualPrecision);
        Assert.Equal(display, TimingTime.FormatTimeOfDay(ticks));
    }

    [Theory]
    [InlineData("12:00:00.0000001", "12:01:00.0000000", 5999L)]
    [InlineData("12:00:00.9999999", "12:01:01.0099998", 6000L)]
    [InlineData("12:00:00.1234", "12:01:00.1234", 6000L)]
    [InlineData("12:00:00.123", "12:01:00.1229999", 5999L)]
    public void NoTimestampDigitsAreDroppedBeforeSubtractionAndNetTimeIsNeverRounded(
        string start, string finish, long expected)
    {
        var snapshot = Calculate(TimingRulesTests.List(1), start, finish);
        var result = Assert.Single(snapshot.Results);
        Assert.Equal(TimingStatus.Finished, result.Status);
        Assert.Equal(expected, result.Hundredths);
    }

    [Fact]
    public void TwoRunTotalSumsTruncatedRunTimesRatherThanTruncatingTheirRawSum()
    {
        var list = TimingRulesTests.List(1);
        var first = Calculate(list, "12:00:00.0000000", "12:01:00.0099999");
        var second = Calculate(list, "13:00:00.0000000", "13:01:01.0099999");
        Assert.Equal(6000, first.Results[0].Hundredths);
        Assert.Equal(6100, second.Results[0].Hundredths);
        Assert.Equal(12100, Assert.Single(TimingEngine.Combined(second, first)).Total);
    }

    [Fact]
    public void ZeroPaddingHandClockInputDoesNotTurnItIntoAnElectronicMainTime()
    {
        var snapshot = Calculate(TimingRulesTests.List(1), "12:00:00.86", "12:01:00.97");
        Assert.All(snapshot.Observations, x => Assert.Equal(2, x.Observation.Precision));
        Assert.Equal(TimingStatus.Review, snapshot.Results[0].Status);
        Assert.Null(snapshot.Results[0].Hundredths);
        Assert.False(snapshot.Complete); // A verified EET is needed, not merely a different numeric scale.
    }

    private static TimingSnapshot Calculate(StartListRevision list, string start, string finish)
    {
        var session = TimingRulesTests.Session(list);
        var observations = new AlgeAsciiDecoder(session, "Synthetic", "1").Feed(TimingRulesTests.Packet(session, 1,
            $" *0001 C0 {start}\r *0001 C1 {finish}\r"));
        var audit = observations.Select((o, i) => new TimingAudit(i + 1, list.Id, TimingRulesTests.At,
            "Test operator", "Synthetic assignment", new(DecisionKind.Assignment, o.Key),
            new(DecisionKind.Assignment, o.Key, Bib: 1))).ToArray();
        return TimingEngine.Replay(list, observations, audit, 0, 1);
    }
}
