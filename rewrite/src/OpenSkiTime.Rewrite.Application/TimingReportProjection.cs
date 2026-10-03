using System.Security.Cryptography;
using System.Text;
using OpenSkiTime.Rewrite.Timing;

namespace OpenSkiTime.Rewrite.Application;

public static class TimingReportProjection
{
    public static string Fingerprint(IEnumerable<TimingReplayData> sources) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(string.Join("|", sources.OrderBy(x => x.List.Plan.RunNumber)
            .ThenBy(x => x.List.Id).Select(x => ResultSourceFingerprint.Create(x))))));

    public static TimingReportRun FromTiming(TimingReplayData source, TimingSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(source); ArgumentNullException.ThrowIfNull(snapshot);
        var observations = snapshot.Observations.ToDictionary(x => x.Observation.Key, x => x.Observation);
        TimingObservation? Find(string? key) => key is not null ? observations.GetValueOrDefault(key) : null;
        TimingReportStamp? Stamp(string? key) => Find(key) is { DeviceTicks: { } ticks } value
            ? new(ticks, value.Precision, value.Key) : null;
        TimingReportBib Bib(TimingResult result) => new()
        {
            Bib = result.Bib, AStart = Stamp(result.StartKey), AFinish = Stamp(result.FinishKey),
            NetHundredths = result.Hundredths
        };
        var timeDecisions = snapshot.Audit.OrderBy(x => x.Id).Where(x => x.After.Kind == DecisionKind.Time)
            .GroupBy(x => x.After.CompetitorId).ToDictionary(x => x.Key!.Value, x => x.Last().After);
        // Classification removes official result time, not evidence of a physically completed run.
        // Sample the measured pair or active audited correction without changing official results.
        TimingResult? Completed(TimingResult result)
        {
            var start = Find(result.StartKey); var finish = Find(result.FinishKey);
            if (start?.DeviceTicks is not { } s || finish?.DeviceTicks is not { } f || start.ClockId != finish.ClockId
                || f - s < TimingTime.TicksPerHundredth || f - s > 2 * TimeSpan.TicksPerHour
                || start.Precision < (start.Manual ? 2 : 3) || finish.Precision < (finish.Manual ? 2 : 3)) { return null; }
            var corrected = timeDecisions.GetValueOrDefault(result.CompetitorId)?.Hundredths;
            return result with { Hundredths = corrected ?? result.Hundredths ?? (f - s) / TimingTime.TicksPerHundredth };
        }
        // First/last mean actual finish chronology including classified finishers, never bib order.
        var complete = snapshot.Results.Select(Completed).Where(x => x is not null).Select(x => x!)
            .OrderBy(x => Find(x.FinishKey)?.DeviceTicks).ThenBy(x => x.Entry.Position).ToArray();
        bool IsSystemA(TimingResult result) => Find(result.StartKey) is { Manual: false }
            && Find(result.FinishKey) is { Manual: false }
            && (!timeDecisions.TryGetValue(result.CompetitorId, out var decision) || decision.Hundredths is null);
        var best = complete.Where(x => x.Status == TimingStatus.Finished && IsSystemA(x)).OrderBy(x => x.Hundredths).ThenBy(x => x.Entry.Position).FirstOrDefault();
        var timed = snapshot.Results.Where(x => x.Hundredths is not null).Concat(complete).DistinctBy(x => x.CompetitorId);
        var missed = timed.Where(x => !IsSystemA(x))
            .Select(x => new TimingReportMissed(x.Bib, "", "")).ToArray();
        return new()
        {
            Run = source.List.Plan.RunNumber,
            First = complete.Length == 0 ? new() : Bib(complete[0]),
            Last = complete.Length == 0 ? new() : Bib(complete[^1]),
            BestBib = best?.Bib, BestHundredths = best?.Hundredths,
            AllResultsA = missed.Length == 0, MissedA = missed
        };
    }
}
