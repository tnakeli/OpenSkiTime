using OpenSkiTime.Domain;
using OpenSkiTime.Timing;

namespace OpenSkiTime.Application;

/// <summary>
/// A start or finish time of day for one competitor from the Edit result dialog. <see cref="TimeOfDay"/> is the typed
/// value for a hand or device-record time; an EET carries its complete calculation instead. <see cref="Replacing"/>
/// lists the impulses assigned to the competitor at that position when the operator saw them (none when the time was
/// missing); the entry is refused when they changed, so a late System A impulse is never replaced unseen.
/// </summary>
/// For an EET, <see cref="ReferenceKeys"/> are the System A times that were eligible references when the table was
/// prepared; the EET is refused when that set changed, because the nearest ten could then be others.
public sealed record CompetitorTimeEntry(int Bib, int Channel, ManualTimeSource Source, string? TimeOfDay = null,
    string? SourceReference = null, EetCalculation? Eet = null, IReadOnlyList<string>? Replacing = null,
    IReadOnlyList<string>? ReferenceKeys = null)
{
    /// <summary>The impulses an entry for <paramref name="bib"/> at <paramref name="channel"/> replaces (unassigns).</summary>
    public static IReadOnlyList<string> AssignedKeys(TimingSnapshot snapshot, int bib, int channel)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return snapshot.Observations.Where(x => x.Bib == bib && !x.Ignored && x.DuplicateOf is null
                && x.Observation.Kind == ObservationKind.Impulse && x.Observation.Channel == channel)
            .Select(x => x.Observation.Key).Order(StringComparer.Ordinal).ToArray();
    }

    /// <summary>
    /// The System A times of a preparation that can be EET references, in start order with the competitor's own place
    /// marked, for <see cref="ReferenceKeys"/>: a change of eligibility or of the order changes the nearest ten.
    /// </summary>
    public static IReadOnlyList<string> EligibleKeys(EetPreparationResult preparation)
    {
        ArgumentNullException.ThrowIfNull(preparation);
        var keys = preparation.StartOrder.Select((x, i) => (x, i)).Where(x => x.x.A is not null).ToList();
        var before = keys.Count(x => x.i < preparation.TargetIndex);
        var result = keys.Select(x => x.x.A!.Key).ToList();
        result.Insert(before, "target:" + preparation.Bib.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return result;
    }
}

/// <summary>
/// A competitor considered as an EET reference at one timing position, in start order. <see cref="A"/> is the usable
/// System A observation (null with <see cref="Unavailable"/> saying why); <see cref="Backup"/> is the backup
/// observation paired with it, uniquely, when backup timing was captured.
/// </summary>
public sealed record EetReference(int Bib, TimingObservation? A, string? Unavailable, TimingObservation? Backup, string BackupState);

/// <summary>
/// Everything the EET dialog needs for one missing time: the run's start order around the competitor, the captured
/// backup paired with System A, and the unpaired backup readings that may be the competitor's own (the proposal is set
/// only when exactly one fits between the neighbouring System A times and no other competitor could have made it).
/// <see cref="StartOrder"/> is the order the competitors actually started in.
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
        var results = snapshot.Results.ToDictionary(x => x.Bib);
        var order = snapshot.StartOrder.Where(results.ContainsKey).ToList();
        if (!order.Contains(bib)) { order.Add(bib); }
        // "Started before" is the order competitors actually started in: a competitor may start out of queue order.
        // Those with a start time take the queue places of the started competitors in start-time order; the others
        // (not started, or the start is the missing time) keep their queue place.
        var started = order.Where(x => At(results[x], 0)?.DeviceTicks is not null).ToArray();
        var byStart = new Queue<int>(started.OrderBy(x => At(results[x], 0)!.DeviceTicks!.Value).ThenBy(x => Array.IndexOf(started, x)));
        for (var i = 0; i < order.Count; i++) { if (started.Contains(order[i])) { order[i] = byStart.Dequeue(); } }
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
                // A classification or net-time correction can hide a review: the A time must be the only one assigned.
                : CompetitorTimeEntry.AssignedKeys(snapshot, other, channel) is not [var only] || only != a.Key ? "Timing needs review"
                // Only original device impulses: an entered time (even a System A device-record time) is not used.
                : a.ManualEntry ? "Entered time" : a.Manual ? "Device keyboard time"
                : a.DeviceTicks is null || a.Precision < 3 ? "Below millisecond precision"
                : clock is not null && a.ClockId != clock ? "Other device clock"
                : null;
            return (Bib: other, A: unavailable is null ? a : null, Unavailable: unavailable);
        }).ToArray();

        // Backup readings are matched against every original System A passage at this position (forerunners, competitors
        // in review, unassigned impulses), so a reading near an ineligible passage is never taken as a reference's.
        bool Original(TimingObservation x) => x is { Kind: ObservationKind.Impulse, Manual: false, ManualEntry: false, DeviceTicks: not null }
            && x.Channel == channel;
        var targets = snapshot.Observations.Where(x => !x.Ignored && x.DuplicateOf is null && Original(x.Observation))
            .Select(x => new EvidenceTarget(x.Observation.Key, x.Bib ?? 0, channel, x.Observation.DeviceTicks!.Value)).ToArray();
        var evidence = backup.Where(x => x.Kind == ObservationKind.Impulse && x.DeviceTicks is not null && x.Channel == channel)
            .Select(x => new EvidenceTimestamp(x.Key, x.DeviceTicks!.Value, x.Precision, x.Channel, x.Message)).ToArray();
        var matches = targets.Length == 0 || evidence.Length == 0 ? []
            : TimingEvidenceMatching.Match(targets, evidence, pairingToleranceTicks, allowAdjacentDay: true);
        var byKey = backup.Where(x => x.Key is not null).GroupBy(x => x.Key).ToDictionary(x => x.Key, x => x.First(), StringComparer.Ordinal);
        var byTarget = matches.ToDictionary(x => x.Target.Key, StringComparer.Ordinal);
        var matched = references.Where(x => x.A is not null && byTarget.GetValueOrDefault(x.A.Key)?.Evidence is not null)
            .ToDictionary(x => x.Bib, x => byKey[byTarget[x.A!.Key].Evidence!.Key]);
        var rows = references.Select(x =>
        {
            var match = x.A is null ? null : byTarget.GetValueOrDefault(x.A.Key);
            var state = x.A is null ? "" : evidence.Length == 0 ? "No backup captured"
                : match?.Evidence is not null ? "Paired" : match?.State ?? "Not found";
            return new EetReference(x.Bib, x.A, x.Unavailable, matched.GetValueOrDefault(x.Bib), state);
        }).ToArray();

        // Backup readings no reference claimed, between the System A times of the nearest neighbours in start order.
        // A repeated reading (the same time under another journal key) is one reading: a copy of a paired one is paired too.
        var used = matches.Where(x => x.Evidence is not null).Select(x => byKey[x.Evidence!.Key].DeviceTicks!.Value).ToHashSet();
        var unpaired = backup.Where(x => x.Kind == ObservationKind.Impulse && x.DeviceTicks is not null && x.Channel == channel
                && !used.Contains(x.DeviceTicks.Value))
            .GroupBy(x => x.DeviceTicks!.Value).Select(x => x.First()).OrderBy(x => x.DeviceTicks % TimeSpan.TicksPerDay).ToArray();
        var head = Math.Min(index, rows.Length);
        var beforeIndex = head > 0 ? Array.FindLastIndex(rows, head - 1, head, x => x.A is not null) : -1;
        var afterIndex = index < rows.Length ? Array.FindIndex(rows, index, x => x.A is not null) : -1;
        var before = beforeIndex >= 0 ? rows[beforeIndex].A : null;
        var after = afterIndex >= 0 ? rows[afterIndex].A : null;
        // The reading in that window is the competitor's own only when nobody else in the run, forerunners included,
        // could have made it (a racer may overtake another): one with an original System A passage whose backup is not
        // paired, within the pairing tolerance; one without such a passage unless they did not start or, at the finish,
        // did not finish, never started or started after the reading. Otherwise the operator chooses from the list.
        var paired = matches.Where(x => x.Evidence is not null).Select(x => x.Target.Key).ToHashSet(StringComparer.Ordinal);
        bool CouldHaveMade(TimingResult other, TimingObservation reading)
        {
            var timeOfDay = reading.DeviceTicks!.Value % TimeSpan.TicksPerDay;
            // An unresolved System A passage near the reading (an unassigned impulse, or one matched ambiguously) may own it.
            if (targets.Any(t => !paired.Contains(t.Key) && Math.Abs(EetCalculator.TimeOfDayDifference(timeOfDay, t.Ticks)) <= pairingToleranceTicks))
            { return true; }
            if (At(other, channel) is { } a && Original(a))
            { return !paired.Contains(a.Key) && Math.Abs(EetCalculator.TimeOfDayDifference(timeOfDay, a.DeviceTicks!.Value)) <= pairingToleranceTicks; }
            if (other.Status == TimingStatus.DNS) { return false; }
            if (channel == 0) { return true; }
            if (other.Status == TimingStatus.DNF) { return false; }
            return At(other, 0) is { DeviceTicks: { } start }
                ? EetCalculator.TimeOfDayDifference(timeOfDay, start) > 0
                : other.Status != TimingStatus.Ready;
        }
        var others = snapshot.Results.Where(x => x.Bib != bib).Concat(snapshot.Forerunners).ToArray();
        var window = unpaired.Where(x =>
            (before is null || EetCalculator.TimeOfDayDifference(x.DeviceTicks!.Value % TimeSpan.TicksPerDay, before.DeviceTicks!.Value) > -pairingToleranceTicks)
            && (after is null || EetCalculator.TimeOfDayDifference(x.DeviceTicks!.Value % TimeSpan.TicksPerDay, after.DeviceTicks!.Value) < pairingToleranceTicks))
            .ToArray();
        return new(bib, channel, clock, rows, index, window.Length > 0 ? window : unpaired, window is [var only] && !others.Any(x => CouldHaveMade(x, only)) ? only : null);
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
