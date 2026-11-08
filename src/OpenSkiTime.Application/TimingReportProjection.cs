using System.Security.Cryptography;
using System.Text;
using OpenSkiTime.Timing;

namespace OpenSkiTime.Application;

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
        var best = snapshot.Results.Where(x => x.Status == TimingStatus.Finished && x.Hundredths is > 0)
            .OrderBy(x => x.Hundredths).ThenBy(x => x.Entry.Position).FirstOrDefault();
        var timed = snapshot.Results.Where(x => x.Hundredths is not null).Concat(complete).DistinctBy(x => x.CompetitorId);
        // An EET replacement already says why A was missing and where the time came from; other replacements are
        // explained by the operator in the report. Photo finish counts as the B system here (the report offers only
        // System B or Manual); the EET history keeps the exact source.
        EetCalculation? Eet(string? key) => Find(key) is { EntrySource: ManualTimeSource.Eet } entry
            ? TimingEngine.CurrentDecision(new(DecisionKind.ManualTime, entry.Key), snapshot.Audit).Timestamp?.Eet : null;
        TimingReportMissed Missed(TimingResult result)
        {
            // Only when every replaced time of the result is an EET; a mix with another replacement needs the operator.
            var start = Find(result.StartKey); var finish = Find(result.FinishKey);
            var replaced = new[] { start, finish }.Where(x => x is { Manual: true }).ToArray();
            var calculations = replaced.Select(x => Eet(x!.Key)).ToArray();
            if (calculations.Length == 0 || calculations.Any(x => x is null)
                || timeDecisions.TryGetValue(result.CompetitorId, out var corrected) && corrected.Hundredths is not null)
            { return new(result.Bib, "", ""); }
            var sources = calculations.Select(x => x!.Source == EetBackupSource.HandTime ? "Manual" : "System B").Distinct().ToArray();
            var reasons = calculations.Select(x => x!.ReplacementReason).Where(x => x.Length > 0).Distinct().ToArray();
            return new(result.Bib, reasons.Length == 1 ? reasons[0] : "", sources.Length == 1 ? sources[0] : "");
        }
        var missed = timed.Where(x => !IsSystemA(x)).Select(Missed).ToArray();
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
