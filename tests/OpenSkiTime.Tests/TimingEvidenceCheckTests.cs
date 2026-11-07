using OpenSkiTime.Application;
using Xunit;

namespace OpenSkiTime.Tests;

public sealed class TimingEvidenceCheckTests
{
    private static TimingReportStamp At(double seconds) => new((long)(seconds * TimeSpan.TicksPerSecond) + TimeSpan.FromHours(12).Ticks, 4);

    [Theory]
    [InlineData(0.0, false, null)]
    [InlineData(0.014, false, null)]
    [InlineData(0.10, false, null)]
    [InlineData(0.11, false, EvidenceSeverity.Warning)]
    [InlineData(1.0, false, EvidenceSeverity.Warning)]
    [InlineData(1.01, false, EvidenceSeverity.Error)]
    [InlineData(-3.2, false, EvidenceSeverity.Error)]
    [InlineData(0.5, true, null)]
    [InlineData(0.6, true, EvidenceSeverity.Warning)]
    [InlineData(2.0, true, EvidenceSeverity.Warning)]
    [InlineData(2.1, true, EvidenceSeverity.Error)]
    public void ATimeIsComparedWithTheLimitsOfItsClock(double offset, bool hand, EvidenceSeverity? expected)
        => Assert.Equal(expected, TimingEvidenceCheck.Compare(At(0), At(offset), hand)?.Severity);

    [Fact]
    public void TheMessageSaysHowFarAndWhichWay()
    {
        var late = TimingEvidenceCheck.Compare(At(0), At(3.2), false)!;
        Assert.Contains("3.200 s later than A", late.Message, StringComparison.Ordinal);
        var early = TimingEvidenceCheck.Compare(At(10), At(9.5), false)!;
        Assert.Contains("0.500 s earlier than A", early.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AMissingTimeIsNotJudgedHere()
    {
        Assert.Null(TimingEvidenceCheck.Compare(null, At(0), false));
        Assert.Null(TimingEvidenceCheck.Compare(At(0), null, false));
        Assert.Null(TimingEvidenceCheck.CompareNet(5000, null, At(50), false));
        Assert.Null(TimingEvidenceCheck.CompareNet(null, At(0), At(50), false));
    }

    [Fact]
    public void ClocksOnEitherSideOfMidnightAreCompared()
    {
        var a = new TimingReportStamp(TimeSpan.TicksPerDay - TimeSpan.TicksPerSecond / 100, 4);
        var b = new TimingReportStamp(TimeSpan.TicksPerDay + TimeSpan.TicksPerSecond / 100, 4);
        Assert.Null(TimingEvidenceCheck.Compare(a, b, false));
        Assert.Null(TimingEvidenceCheck.Compare(b, a, false));
    }

    [Fact]
    public void ElapsedTimeIsComparedWithTheNetTimeOfA()
    {
        Assert.Null(TimingEvidenceCheck.CompareNet(5023, At(0), At(50.23), false));
        Assert.Null(TimingEvidenceCheck.CompareNet(5023, At(0), At(50.30), false));
        Assert.Equal(EvidenceSeverity.Warning, TimingEvidenceCheck.CompareNet(5023, At(0), At(50.5), false)?.Severity);
        // Both B times a second late agree with each other but give the same net: only the cell check can see that.
        Assert.Null(TimingEvidenceCheck.CompareNet(5023, At(1), At(51.23), false));
        Assert.Equal(EvidenceSeverity.Error, TimingEvidenceCheck.CompareNet(5023, At(0), At(52.0), false)?.Severity);
        Assert.Contains("net time", TimingEvidenceCheck.CompareNet(5023, At(0), At(52.0), false)!.Message, StringComparison.Ordinal);
    }
}
