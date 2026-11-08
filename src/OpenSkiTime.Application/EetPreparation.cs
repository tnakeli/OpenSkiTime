using OpenSkiTime.Domain;
using OpenSkiTime.Timing;

namespace OpenSkiTime.Application;

/// <summary>
/// A start or finish time of day for one competitor from the Edit result dialog. <see cref="TimeOfDay"/> is the typed
/// value for a hand or device-record time; an EET carries its complete calculation instead.
/// </summary>
public sealed record CompetitorTimeEntry(int Bib, int Channel, ManualTimeSource Source, string? TimeOfDay = null,
    string? SourceReference = null, EetCalculation? Eet = null);

/// <summary>
/// A competitor considered as an EET reference at one timing position, in start order. <see cref="A"/> is the usable
/// System A observation (null with <see cref="Unavailable"/> saying why); <see cref="Backup"/> is the backup
/// observation paired with it, uniquely, when backup timing was captured.
/// </summary>
public sealed record EetReference(int Bib, TimingObservation? A, string? Unavailable, TimingObservation? Backup, string BackupState);

/// <summary>
/// Everything the EET dialog needs for one missing time: the run's start order around the competitor, the captured
/// backup paired with System A, and the unpaired backup readings that may be the competitor's own (the proposal is set
/// only when exactly one fits between the neighbouring System A times).
/// </summary>
public sealed record EetPreparationResult(int Bib, int Channel, string? ClockId, IReadOnlyList<EetReference> StartOrder,
    int TargetIndex, IReadOnlyList<TimingObservation> TargetCandidates, TimingObservation? ProposedTarget);

public static class EetPreparation
{
    /// <summary>
    /// Pairs System A times with captured backup times (System B or hand-timing devices) for the EET of
    /// <paramref name="bib"/>. Reads only the given snapshot and backup observations; it changes nothing.
    /// </summary>
    public static EetPreparationResult Prepare(TimingSnapshot snapshot, int bib, int channel,
        IReadOnlyList<TimingObservation> backup, long pairingToleranceTicks)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(backup);
        if (channel is not (0 or 1)) { throw new DomainValidationException("EET replaces a missing start or finish time. Intermediate times are unofficial."); }
        var target = snapshot.Results.SingleOrDefault(x => x.Bib == bib)
            ?? throw new DomainValidationException("Choose a competitor of this run.");
        var observations = snapshot.Observations.ToDictionary(x => x.Observation.Key, x => x.Observation, StringComparer.Ordinal);
        TimingObservation? At(TimingResult result, int position)
            => (position == 0 ? result.StartKey : result.FinishKey) is { } key ? observations.GetValueOrDefault(key) : null;
        var order = snapshot.StartOrder.Where(x => snapshot.Results.Any(r => r.Bib == x)).ToList();
        if (!order.Contains(bib)) { order.Add(bib); }
        var results = snapshot.Results.ToDictionary(x => x.Bib);
        var index = order.IndexOf(bib);
        // The device clock the replacement joins: the competitor's other time, else the nearest A time in start order.
        var clock = At(target, 1 - channel)?.ClockId
            ?? order.Select((x, i) => (Bib: x, Distance: Math.Abs(i - index) * 2 + (i > index ? 1 : 0)))
                .Where(x => x.Bib != bib).OrderBy(x => x.Distance)
                .Select(x => At(results[x.Bib], channel)).FirstOrDefault(x => x is { Manual: false, DeviceTicks: not null })?.ClockId;

        var references = order.Where(x => x != bib).Select(other =>
        {
            var result = results[other];
            var a = At(result, channel);
            var unavailable = a is null
                ? result.Status switch
                {
                    TimingStatus.DNS => "Did not start",
                    TimingStatus.DNF when channel == 1 => "Did not finish",
                    _ => channel == 0 ? "No start time" : "No finish time"
                }
                : result.Status == TimingStatus.Review ? "Timing needs review"
                // Only original device impulses: an entered time (even a System A device-record time) is not used.
                : a.ManualEntry ? "Entered time" : a.Manual ? "Device keyboard time"
                : a.DeviceTicks is null || a.Precision < 3 ? "Below millisecond precision"
                : clock is not null && a.ClockId != clock ? "Other device clock"
                : null;
            return (Bib: other, A: unavailable is null ? a : null, Unavailable: unavailable);
        }).ToArray();

        var targets = references.Where(x => x.A is not null)
            .Select(x => new EvidenceTarget(x.A!.Key, x.Bib, channel, x.A.DeviceTicks!.Value)).ToArray();
        var evidence = backup.Where(x => x.Kind == ObservationKind.Impulse && x.DeviceTicks is not null && x.Channel == channel)
            .Select(x => new EvidenceTimestamp(x.Key, x.DeviceTicks!.Value, x.Precision, x.Channel, x.Message)).ToArray();
        var matches = targets.Length == 0 || evidence.Length == 0 ? []
            : TimingEvidenceMatching.Match(targets, evidence, pairingToleranceTicks, allowAdjacentDay: true);
        var byKey = backup.Where(x => x.Key is not null).GroupBy(x => x.Key).ToDictionary(x => x.Key, x => x.First(), StringComparer.Ordinal);
        var matched = matches.Where(x => x.Evidence is not null).ToDictionary(x => x.Target.Bib, x => byKey[x.Evidence!.Key]);
        var rows = references.Select(x =>
        {
            var match = matches.FirstOrDefault(m => m.Target.Bib == x.Bib);
            var state = x.A is null ? "" : evidence.Length == 0 ? "No backup captured"
                : match?.Evidence is not null ? "Paired" : match?.State ?? "Not found";
            return new EetReference(x.Bib, x.A, x.Unavailable, matched.GetValueOrDefault(x.Bib), state);
        }).ToArray();

        // Backup readings no reference claimed, between the System A times of the nearest neighbours in start order.
        var used = matched.Values.Select(x => x.Key).ToHashSet(StringComparer.Ordinal);
        var unpaired = backup.Where(x => x.Kind == ObservationKind.Impulse && x.DeviceTicks is not null && x.Channel == channel
            && !used.Contains(x.Key)).OrderBy(x => x.DeviceTicks % TimeSpan.TicksPerDay).ToArray();
        var before = rows.Take(index).LastOrDefault(x => x.A is not null)?.A;
        var after = rows.Skip(index).FirstOrDefault(x => x.A is not null)?.A;
        var window = unpaired.Where(x =>
            (before is null || EetCalculator.TimeOfDayDifference(x.DeviceTicks!.Value % TimeSpan.TicksPerDay, before.DeviceTicks!.Value) > -pairingToleranceTicks)
            && (after is null || EetCalculator.TimeOfDayDifference(x.DeviceTicks!.Value % TimeSpan.TicksPerDay, after.DeviceTicks!.Value) < pairingToleranceTicks))
            .ToArray();
        return new(bib, channel, clock, rows, index, window.Length > 0 ? window : unpaired, window.Length == 1 ? window[0] : null);
    }

    /// <summary>The EET reference candidates in start order, using the backup values the operator confirmed or typed.</summary>
    public static IReadOnlyList<EetCandidate> Candidates(EetPreparationResult preparation,
        IReadOnlyDictionary<int, (long TimeOfDay, int Precision, string? Key)> backup, IReadOnlySet<int> excluded)
    {
        ArgumentNullException.ThrowIfNull(preparation);
        ArgumentNullException.ThrowIfNull(backup);
        ArgumentNullException.ThrowIfNull(excluded);
        var rows = preparation.StartOrder.Select(x =>
        {
            if (x.A is null) { return new EetCandidate(x.Bib, null, x.Unavailable); }
            if (excluded.Contains(x.Bib)) { return new EetCandidate(x.Bib, null, "Left out by operator"); }
            if (!backup.TryGetValue(x.Bib, out var value)) { return new EetCandidate(x.Bib, null, "No backup time"); }
            return new EetCandidate(x.Bib, new EetPair(x.Bib, x.A.Key, x.A.DeviceTicks!.Value, x.A.Precision, value.TimeOfDay,
                value.Precision, value.Key));
        }).ToList();
        rows.Insert(Math.Clamp(preparation.TargetIndex, 0, rows.Count), new EetCandidate(preparation.Bib, null, "Missing time"));
        return rows;
    }
}
