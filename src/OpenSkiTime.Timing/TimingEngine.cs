using OpenSkiTime.Domain;

namespace OpenSkiTime.Timing;

public static class TimingEngine
{
    public static TimingSnapshot Replay(StartListRevision list, IReadOnlyList<TimingObservation> observations,
        IReadOnlyList<TimingAudit> audit, int startChannel, int finishChannel)
    {
        ArgumentNullException.ThrowIfNull(list);
        ArgumentNullException.ThrowIfNull(observations);
        ArgumentNullException.ThrowIfNull(audit);
        var decisions = new Dictionary<string, TimingDecision>(StringComparer.Ordinal);
        foreach (var change in audit.OrderBy(x => x.Id)) { decisions[DecisionKey(change.After)] = change.After; }
        var seen = new Dictionary<string, string>(StringComparer.Ordinal);
        var physical = new HashSet<string>(StringComparer.Ordinal);
        var reviewed = new List<ObservationReview>();
        foreach (var observation in observations)
        {
            if (observation.Kind == ObservationKind.Information) { continue; }
            decisions.TryGetValue("a:" + observation.Key, out var decision);
            var duplicate = seen.GetValueOrDefault(observation.Fingerprint);
            if (duplicate is null) { seen[observation.Fingerprint] = observation.Key; }
            var changedImpulse = observation.Kind == ObservationKind.Impulse && duplicate is null
                && !physical.Add($"{observation.Source}:{observation.DeviceTicks}:{observation.Channel}");
            var bib = decision?.Bib;
            var ignored = decision?.Ignored == true;
            var usable = !changedImpulse && observation.Kind == ObservationKind.Impulse && observation.DeviceTicks is not null
                && (observation.Channel == startChannel || observation.Channel == finishChannel
                    || observation.Channel >= 2 && observation.Channel < 2 + list.Plan.Competition.IntermediateCount);
            var reviewedObservation = changedImpulse ? observation with { Kind = ObservationKind.DeviceCorrection,
                Message = "Previously received impulse changed on the device/server. Review the original assignment: " + observation.Message } : observation;
            var unusedIntermediate = !changedImpulse && duplicate is null && observation.Kind == ObservationKind.Impulse
                && observation.Channel is >= 2 and <= 21
                && observation.Channel >= 2 + list.Plan.Competition.IntermediateCount;
            reviewed.Add(new(reviewedObservation, bib, ignored || unusedIntermediate, duplicate,
                ignored || unusedIntermediate ? "Ignored" : duplicate is not null ? "Duplicate" : !usable ? "Review" : bib is null ? "Unassigned" : "Assigned"));
        }
        var rows = new List<TimingResult>();
        var assignedByBib = reviewed.Where(x => x.Bib is not null && x.State == "Assigned")
            .GroupBy(x => x.Bib!.Value).ToDictionary(x => x.Key, x => x.Select(r => r.Observation).ToArray());
        foreach (var entry in list.Plan.Entries)
        {
            var id = entry.Entrant.CompetitorId;
            var assigned = assignedByBib.GetValueOrDefault(entry.Bib) ?? [];
            var starts = assigned.Where(x => x.Channel == startChannel).ToArray();
            var finishes = assigned.Where(x => x.Channel == finishChannel).ToArray();
            var status = TimingStatus.Ready;
            long? time = null;
            var detail = string.Empty;
            if (starts.Length > 1 || finishes.Length > 1) { status = TimingStatus.Review; detail = "Multiple impulses assigned. Unassign or ignore the extra observation."; }
            else if (starts.Length == 0 && finishes.Length > 0) { status = TimingStatus.Review; detail = "Finish without start."; }
            else if (starts.Length == 1 && finishes.Length == 0) { status = TimingStatus.OnCourse; }
            else if (starts.Length == 1 && finishes.Length == 1)
            {
                var elapsed = Elapsed(starts[0], finishes[0], finish: true);
                if (elapsed.Problem == ElapsedProblem.DifferentClock)
                { status = TimingStatus.Review; detail = "Start and finish belong to different device clock sessions. Correct the time using verified timing."; }
                else if (elapsed.Problem == ElapsedProblem.NotAfterStart)
                { status = TimingStatus.Review; detail = "Finish is not at least one hundredth after start. Check the assigned competitor and device clock."; }
                else if (elapsed.Problem == ElapsedProblem.OverTwoHours)
                { status = TimingStatus.Review; detail = "Elapsed time exceeds two hours. Check the device date or correct the time."; }
                else if (elapsed.Problem == ElapsedProblem.LowPrecision)
                { status = TimingStatus.Review; detail = "Device output precision is too low. Gate inputs need at least milliseconds; device keyboard inputs need at least hundredths."; }
                else
                {
                    status = TimingStatus.Finished;
                    time = elapsed.Hundredths;
                    if (starts[0].Manual || finishes[0].Manual) { detail = "Includes a device keyboard impulse; verify against backup timing."; }
                }
            }
            if (decisions.TryGetValue("t:" + id, out var correction) && correction.Hundredths is { } corrected)
            { time = corrected; status = TimingStatus.Finished; detail = "Corrected time · see history"; }
            if (decisions.TryGetValue("s:" + id, out var classification) && classification.Status is { } classified)
            { status = classified; time = null; detail = "Operator classification · see history"; }
            var splits = Enumerable.Range(1, list.Plan.Competition.IntermediateCount).Select(number =>
            {
                var pulses = assigned.Where(x => x.Channel == number + 1).ToArray();
                var pulse = pulses.FirstOrDefault();
                if (pulse is null) { return new TimingSplit(number, null, null, "Awaiting intermediate"); }
                var elapsed = starts.Length == 1 ? Elapsed(starts[0], pulse, finish: false) : default;
                var valid = pulses.Length == 1 && starts.Length == 1 && elapsed.Problem == ElapsedProblem.None
                    && (finishes.Length == 0 || pulse.DeviceTicks < finishes[0].DeviceTicks);
                return new TimingSplit(number, pulse.Key, valid ? elapsed.Hundredths : null,
                    valid ? "" : "Review intermediate impulses / clock continuity");
            }).ToArray();
            rows.Add(new(entry, status, time, null, starts.FirstOrDefault()?.Key, finishes.FirstOrDefault()?.Key, detail)
            { Splits = splits, Disqualification = status == TimingStatus.DSQ ? classification?.Disqualification : null });
        }
        var ranked = rows.Where(x => x.Status == TimingStatus.Finished).OrderBy(x => x.Hundredths).ThenBy(x => x.Bib).ToArray();
        var ranks = ranked.Select((x, i) => (x.CompetitorId, Rank: Array.FindIndex(ranked, r => r.Hundredths == x.Hundredths) + 1))
            .ToDictionary(x => x.CompetitorId, x => x.Rank);
        var badSplits = rows.SelectMany(x => x.Splits).Where(x => x.ObservationKey is not null && x.Hundredths is null)
            .Select(x => x.ObservationKey!).ToHashSet(StringComparer.Ordinal);
        var startOrder = decisions.TryGetValue("q", out var queue) && queue.StartOrder is { } savedOrder
            ? ParseStartOrder(savedOrder) : list.Plan.Entries.OrderBy(x => x.Position).Select(x => x.Bib).ToArray();
        return new(list.Id, audit.Count == 0 ? 0 : audit[^1].Id,
            rows.Select(x => x with { Rank = ranks.TryGetValue(x.CompetitorId, out var rank) ? rank : null }).ToArray(),
            reviewed.Select(x => badSplits.Contains(x.Observation.Key) ? x with { State = "Review" } : x).ToArray(), audit.ToArray())
        { StartOrder = startOrder };
    }

    // Timy keyboard impulses carry the manual marker and may contain only hundredths.
    // They use the same exact scale/calculation; this does not claim automatic gate timing or verified EET.
    private static bool HasUsablePrecision(TimingObservation observation) => observation.Precision >= (observation.Manual ? 2 : 3);

    // The one elapsed-time rule shared by results, splits and drag previews: same clock session,
    // usable source precision, at most two hours, then subtraction on the full tick scale before
    // truncation to hundredths. A finish must be at least one hundredth after start; an
    // intermediate only has to be later than start.
    public static TimingElapsed Elapsed(TimingObservation start, TimingObservation end, bool finish)
    {
        ArgumentNullException.ThrowIfNull(start);
        ArgumentNullException.ThrowIfNull(end);
        if (start.DeviceTicks is not { } startTicks || end.DeviceTicks is not { } endTicks)
        { throw new ArgumentException("Elapsed time needs two device timestamps.", start.DeviceTicks is null ? nameof(start) : nameof(end)); }
        var elapsed = endTicks - startTicks;
        var problem = start.ClockId != end.ClockId ? ElapsedProblem.DifferentClock
            : elapsed < (finish ? TimingTime.TicksPerHundredth : 1) ? ElapsedProblem.NotAfterStart
            : elapsed > TimeSpan.FromHours(2).Ticks ? ElapsedProblem.OverTwoHours
            : !HasUsablePrecision(start) || !HasUsablePrecision(end) ? ElapsedProblem.LowPrecision
            : ElapsedProblem.None;
        // Alpine elapsed times are truncated, never rounded up.
        return new(problem == ElapsedProblem.None ? elapsed / TimingTime.TicksPerHundredth : null, problem);
    }

    // Side-effect free "what if" for assigning one recorded impulse to a competitor. It reads the
    // given snapshot only and applies the same rules Replay would after the existing drop action
    // (which displaces the competitor's other impulses on the same channel).
    public static AssignmentPreview PreviewAssignment(TimingSnapshot snapshot, int bib, string observationKey,
        int startChannel, int finishChannel)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(observationKey);
        var target = snapshot.Observations.FirstOrDefault(x => x.Observation.Key == observationKey)?.Observation;
        if (target is not { Kind: ObservationKind.Impulse, DeviceTicks: { } targetTicks, Channel: { } channel })
        { return new(bib, null, null, "Not a timing impulse"); }
        if (channel == startChannel) { return new(bib, channel, null, "Start timestamp"); }
        var assigned = snapshot.Observations.Where(x => x.Bib == bib && x.State == "Assigned"
            && x.Observation.Key != observationKey).Select(x => x.Observation).ToArray();
        var starts = assigned.Where(x => x.Channel == startChannel).ToArray();
        if (starts.Length == 0) { return new(bib, channel, null, "No start time"); }
        if (starts.Length > 1) { return new(bib, channel, null, "No start time: multiple start impulses"); }
        var finish = channel == finishChannel;
        var elapsed = Elapsed(starts[0], target, finish);
        var problem = elapsed.Problem switch
        {
            ElapsedProblem.DifferentClock => "Invalid: different device clock",
            ElapsedProblem.NotAfterStart => targetTicks < starts[0].DeviceTicks ? "Invalid: time is before start" : "Invalid: time is not after start",
            ElapsedProblem.OverTwoHours => "Invalid: over two hours after start",
            ElapsedProblem.LowPrecision => "Invalid: device precision too low",
            _ => null
        };
        var finishes = assigned.Where(x => x.Channel == finishChannel).ToArray();
        if (problem is null && !finish && finishes.Length > 0 && targetTicks >= finishes[0].DeviceTicks)
        { problem = "Invalid: time is after finish"; }
        return new(bib, channel, problem is null ? elapsed.Hundredths : null, problem);
    }

    public static string DecisionKey(TimingDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        return decision.Kind switch
        {
            DecisionKind.Assignment => "a:" + decision.ObservationKey,
            DecisionKind.Status => "s:" + decision.CompetitorId,
            DecisionKind.Time => "t:" + decision.CompetitorId,
            DecisionKind.StartOrder => "q",
            _ => throw new DomainValidationException("Unknown timing correction.")
        };
    }

    public static TimingDecision CurrentDecision(TimingDecision target, IReadOnlyList<TimingAudit> audit)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(audit);
        var key = DecisionKey(target);
        return audit.LastOrDefault(x => DecisionKey(x.After) == key)?.After
            ?? new(target.Kind, target.ObservationKey, target.CompetitorId);
    }

    public static void ValidateDecision(TimingDecision decision, TimingSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentNullException.ThrowIfNull(snapshot);
        ValidateDecisionShape(decision);
        if (decision.Kind == DecisionKind.Assignment)
        {
            var observation = snapshot.Observations.SingleOrDefault(x => x.Observation.Key == decision.ObservationKey)
                ?? throw new DomainValidationException("Select a received observation.");
            if (decision.Bib is { } bib)
            {
                var channel = observation.Observation.Channel;
                var intermediates = snapshot.Results.Count == 0 ? 0 : snapshot.Results[0].Splits.Count;
                var knownChannel = channel is 0 or 1 || channel >= 2 && channel < 2 + intermediates;
                if (decision.Ignored || observation.Observation.Kind != ObservationKind.Impulse || observation.DuplicateOf is not null
                    || !knownChannel)
                { throw new DomainValidationException("Only an original timing impulse can be assigned. Other input can be reviewed and ignored."); }
                if (!snapshot.Results.Any(x => x.Bib == bib)) { throw new DomainValidationException("That bib is not on this run's start list."); }
            }
        }
        else if (decision.Kind == DecisionKind.StartOrder)
        {
            if (decision.StartOrder is { } order && !ParseStartOrder(order).Order()
                .SequenceEqual(snapshot.Results.Select(x => x.Bib).Order()))
            { throw new DomainValidationException("The start order must include every starter exactly once."); }
        }
        else
        {
            if (!snapshot.Results.Any(x => x.CompetitorId == decision.CompetitorId)) { throw new DomainValidationException("Select a starter in this run."); }
            if (decision.Status is not null && decision.Status is not (TimingStatus.DNS or TimingStatus.DNF or TimingStatus.DSQ or TimingStatus.NPS))
            { throw new DomainValidationException("Choose DNS, DNF, DSQ or NPS; clear the classification to use the recorded times."); }
            if (decision.Hundredths is <= 0 or > 720000) { throw new DomainValidationException("A corrected time must be greater than zero and no longer than two hours."); }
        }
    }

    public static void ValidateDecisionShape(TimingDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        if (decision.Disqualification is { } dsq)
        {
            if (decision.Kind != DecisionKind.Status || decision.Status != TimingStatus.DSQ)
            { throw new DomainValidationException("Gate, disqualification reason and judge belong to a DSQ classification only."); }
            dsq.Validate();
        }
        var valid = decision.Kind switch
        {
            DecisionKind.Assignment => !string.IsNullOrWhiteSpace(decision.ObservationKey) && decision.ObservationKey.Split(':').Length >= 2
                && decision.CompetitorId is null && decision.Status is null && decision.Hundredths is null
                && decision.StartOrder is null && !(decision.Ignored && decision.Bib is not null),
            DecisionKind.Status => decision.CompetitorId is not null && decision.ObservationKey is null && decision.Bib is null
                && !decision.Ignored && decision.Hundredths is null && decision.StartOrder is null
                && decision.Status is null or TimingStatus.DNS or TimingStatus.DNF or TimingStatus.DSQ or TimingStatus.NPS,
            DecisionKind.Time => decision.CompetitorId is not null && decision.ObservationKey is null && decision.Bib is null
                && !decision.Ignored && decision.Status is null && decision.StartOrder is null && decision.Hundredths is null or > 0 and <= 720000,
            DecisionKind.StartOrder => decision.ObservationKey is null && decision.CompetitorId is null && decision.Bib is null
                && !decision.Ignored && decision.Status is null && decision.Hundredths is null
                && (decision.StartOrder is null || ParseStartOrder(decision.StartOrder).Length > 0),
            _ => false
        };
        if (!valid) { throw new DomainValidationException("Invalid timing correction. Choose a single observation, classification or elapsed time."); }
    }

    public static int[] ParseStartOrder(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) { throw new DomainValidationException("Start order is empty."); }
        var parts = value.Split(',');
        if (parts.Any(x => !int.TryParse(x, out var bib) || bib <= 0))
        { throw new DomainValidationException("Start order contains an invalid bib."); }
        return parts.Select(int.Parse).ToArray();
    }

    public static IReadOnlyList<(TimingResult Result, long? Total, int? Rank)> Combined(TimingSnapshot current, TimingSnapshot? previous)
    {
        ArgumentNullException.ThrowIfNull(current);
        var totals = current.Results.Select(row =>
        {
            var earlier = previous?.Results.SingleOrDefault(x => x.CompetitorId == row.CompetitorId);
            long? total = row.Status == TimingStatus.Finished && (previous is null || earlier?.Status == TimingStatus.Finished)
                ? row.Hundredths + (earlier?.Hundredths ?? 0) : null;
            return (Result: row, Total: total);
        }).ToArray();
        return totals.Select(x => (x.Result, x.Total, x.Total is { } value
            ? (int?)(totals.Count(y => y.Total is { } t && t < value) + 1) : null)).ToArray();
    }
}
