using OpenSkiTime.Rewrite.Application;
using OpenSkiTime.Rewrite.Timing;
using Xunit;

namespace OpenSkiTime.Rewrite.Tests;

public sealed class AuxiliaryClockComparisonTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void SameClockBasisNeedsNoOffsetAndPreservesOriginal(bool targetUtc, bool sourceUtc)
    {
        var original = Observation(sourceUtc);
        var result = AuxiliaryClockComparison.Normalize(targetUtc, [new(AuxiliaryTimingRole.B, true, original)]);
        Assert.Empty(result.Warnings); Assert.Same(original, Assert.Single(result.Observations));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void MixedClockBasisWithoutExplicitOffsetCannotAutoMatch(bool targetUtc, bool sourceUtc)
    {
        var result = AuxiliaryClockComparison.Normalize(targetUtc, [new(AuxiliaryTimingRole.B, true, Observation(sourceUtc))]);
        Assert.Empty(result.Observations); Assert.Single(result.Warnings);
    }

    [Theory]
    [InlineData(false, true, 180, 180)]
    [InlineData(true, false, 180, -180)]
    [InlineData(false, true, -300, -300)]
    [InlineData(true, false, -300, 300)]
    public void ExplicitOffsetNormalizesDerivedValueAcrossMidnightWithoutRewritingOriginal(bool targetUtc, bool sourceUtc, int offset, int difference)
    {
        var original = Observation(sourceUtc);
        var result = AuxiliaryClockComparison.Normalize(targetUtc,
            [new(AuxiliaryTimingRole.B, true, original) { ComparisonUtcOffsetMinutes = offset }]);
        var normalized = Assert.Single(result.Observations);
        Assert.Empty(result.Warnings);
        Assert.Equal(original.DeviceTicks + difference * TimeSpan.TicksPerMinute, normalized.DeviceTicks);
        Assert.Equal(original.Key, normalized.Key); Assert.Equal(original.Precision, normalized.Precision);
        Assert.Equal(new DateTime(2026, 9, 27, 23, 30, 0).Ticks + 1234567, original.DeviceTicks);
    }

    private static TimingObservation Observation(bool utc) => new("source-key", Guid.NewGuid(), 1, "Synthetic",
        "fingerprint", ObservationKind.Impulse, 1, new DateTime(2026, 9, 27, 23, 30, 0).Ticks + 1234567,
        7, 12, false, utc ? "UTC" : "serial-clock", "Synthetic timestamp");
}
