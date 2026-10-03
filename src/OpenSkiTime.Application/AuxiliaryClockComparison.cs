using OpenSkiTime.Timing;

namespace OpenSkiTime.Application;

public sealed record AuxiliaryClockComparisonResult(IReadOnlyList<TimingObservation> Observations, IReadOnlyList<string> Warnings);

public static class AuxiliaryClockComparison
{
    public static bool UsesUtc(TimingObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        return observation.ClockId == "UTC";
    }

    // Normalize only a derived comparison/report value. Original raw packets and decoded observations remain unchanged.
    // Offset means local device wall clock minus UTC, in whole minutes, explicitly recorded with auxiliary capture.
    public static AuxiliaryClockComparisonResult Normalize(bool targetUsesUtc, IReadOnlyList<AuxiliaryTimingObservation> evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        var result = new List<TimingObservation>();
        var warnings = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in evidence)
        {
            var observation = item.Observation;
            if (observation.Kind != ObservationKind.Impulse || observation.DeviceTicks is not { } ticks) { continue; }
            var sourceUsesUtc = UsesUtc(observation);
            if (sourceUsesUtc == targetUsesUtc) { result.Add(observation); continue; }
            if (item.ComparisonUtcOffsetMinutes is not { } offset || offset is < -840 or > 840)
            {
                warnings.Add("UTC/local clock comparison needs an explicit local UTC offset. Set it in auxiliary settings and reconnect, or review timestamps manually.");
                continue;
            }
            var normalized = ticks + (sourceUsesUtc ? 1L : -1L) * offset * TimeSpan.TicksPerMinute;
            if (normalized < DateTime.MinValue.Ticks || normalized > DateTime.MaxValue.Ticks)
            { warnings.Add("The auxiliary UTC offset puts a timestamp outside the supported date range."); continue; }
            result.Add(observation with { DeviceTicks = normalized });
        }
        return new(result.ToArray(), warnings.ToArray());
    }
}
