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
    }

    [Fact]
    public void ClocksOnEitherSideOfMidnightAreCompared()
    {
        var a = new TimingReportStamp(TimeSpan.TicksPerDay - TimeSpan.TicksPerSecond / 100, 4);
        var b = new TimingReportStamp(TimeSpan.TicksPerDay + TimeSpan.TicksPerSecond / 100, 4);
        Assert.Null(TimingEvidenceCheck.Compare(a, b, false));
        Assert.Null(TimingEvidenceCheck.Compare(b, a, false));
    }
}
