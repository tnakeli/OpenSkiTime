using OpenSkiTime.Rewrite.Domain;

namespace OpenSkiTime.Rewrite.Timing;

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
                && (observation.Channel == startChannel || observation.Channel == finishChannel);
            var reviewedObservation = changedImpulse ? observation with { Kind = ObservationKind.DeviceCorrection,
                Message = "Previously received impulse changed on the device/server. Review the original assignment: " + observation.Message } : observation;
            reviewed.Add(new(reviewedObservation, bib, ignored, duplicate,
                ignored ? "Ignored" : duplicate is not null ? "Duplicate" : !usable ? "Review" : bib is null ? "Unassigned" : "Assigned"));
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
                var elapsed = finishes[0].DeviceTicks!.Value - starts[0].DeviceTicks!.Value;
                if (starts[0].ClockId != finishes[0].ClockId || elapsed < TimingTime.TicksPerHundredth || elapsed > TimeSpan.FromHours(2).Ticks
                    || starts[0].Precision < 3 || finishes[0].Precision < 3)
                { status = TimingStatus.Review; detail = "Check clock continuity and start/finish times. Use an audited time correction if necessary."; }
                else
                {
                    status = TimingStatus.Finished;
                    time = elapsed / TimingTime.TicksPerHundredth; // Alpine elapsed times are truncated, never rounded up.
                    if (starts[0].Manual || finishes[0].Manual) { detail = "Includes a device keyboard impulse; verify against backup timing."; }
                }
            }
            if (decisions.TryGetValue("t:" + id, out var correction) && correction.Hundredths is { } corrected)
            { time = corrected; status = TimingStatus.Finished; detail = "Corrected time · see history"; }
            if (decisions.TryGetValue("s:" + id, out var classification) && classification.Status is { } classified)
            { status = classified; time = null; detail = "Operator classification · see history"; }
            rows.Add(new(entry, status, time, null, starts.FirstOrDefault()?.Key, finishes.FirstOrDefault()?.Key, detail));
        }
        var ranked = rows.Where(x => x.Status == TimingStatus.Finished).OrderBy(x => x.Hundredths).ThenBy(x => x.Bib).ToArray();
        var ranks = ranked.Select((x, i) => (x.CompetitorId, Rank: Array.FindIndex(ranked, r => r.Hundredths == x.Hundredths) + 1))
            .ToDictionary(x => x.CompetitorId, x => x.Rank);
        return new(list.Id, audit.Count == 0 ? 0 : audit[^1].Id,
            rows.Select(x => x with { Rank = ranks.TryGetValue(x.CompetitorId, out var rank) ? rank : null }).ToArray(), reviewed, audit.ToArray());
    }

    public static string DecisionKey(TimingDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        return decision.Kind switch
        {
            DecisionKind.Assignment => "a:" + decision.ObservationKey,
            DecisionKind.Status => "s:" + decision.CompetitorId,
            DecisionKind.Time => "t:" + decision.CompetitorId,
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
                if (decision.Ignored || observation.Observation.Kind != ObservationKind.Impulse || observation.DuplicateOf is not null
                    || observation.Observation.Channel is not (0 or 1))
                { throw new DomainValidationException("Only an original timing impulse can be assigned. Other input can be reviewed and ignored."); }
                if (!snapshot.Results.Any(x => x.Bib == bib)) { throw new DomainValidationException("That bib is not on this run's start list."); }
            }
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
        var valid = decision.Kind switch
        {
            DecisionKind.Assignment => !string.IsNullOrWhiteSpace(decision.ObservationKey) && decision.ObservationKey.Split(':').Length >= 2
                && decision.CompetitorId is null && decision.Status is null && decision.Hundredths is null
                && !(decision.Ignored && decision.Bib is not null),
            DecisionKind.Status => decision.CompetitorId is not null && decision.ObservationKey is null && decision.Bib is null
                && !decision.Ignored && decision.Hundredths is null && decision.Status is null or TimingStatus.DNS or TimingStatus.DNF or TimingStatus.DSQ or TimingStatus.NPS,
            DecisionKind.Time => decision.CompetitorId is not null && decision.ObservationKey is null && decision.Bib is null
                && !decision.Ignored && decision.Status is null && decision.Hundredths is null or > 0 and <= 720000,
            _ => false
        };
        if (!valid) { throw new DomainValidationException("Invalid timing correction. Choose a single observation, classification or elapsed time."); }
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
