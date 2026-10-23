namespace OpenSkiTime.LiveTiming.Radio;

public enum RadioEventKind { Finish, NewLeader, Podium, DNF, DSQ, DNS, Correction, TopStart, RunStarted, RunComplete, Interrupted, Producer }

/// <summary>
/// Something worth saying. Priority 1 is breaking, 5 filler. Conditions are facts the event depends on beyond the slots
/// its lines render (a start announcement is stale once the racer is no longer on course).
/// </summary>
public sealed record RadioEvent(RadioEventKind Kind, int Priority, int Run, int[] Bibs, DateTimeOffset At, TimeSpan Lifetime,
    RacePhase[] Phases, RadioCondition[] Conditions, int Relevance)
{
    /// <summary>Producer instruction for the hosts (never spoken) and the chosen producer source.</summary>
    public string? Instruction { get; init; }
    public string? Source { get; init; }
}

/// <summary>Deterministic event detection from consecutive snapshots of the current run (adapted from mikradio's analyzers).</summary>
public static class RadioEventDetector
{
    private static readonly RacePhase[] Live = [RacePhase.RunInProgress, RacePhase.Interrupted];

    public static IReadOnlyList<RadioEvent> Detect(LiveSnapshot? previous, LiveSnapshot current, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(current);
        // The first snapshot is history, not news: nothing is announced until something changes.
        if (previous is null) { return []; }
        var events = new List<RadioEvent>();
        var run = current.Runs.First(x => x.Number == current.CurrentRun);
        var before = previous.Runs.FirstOrDefault(x => x.Number == run.Number);
        var started = (LiveRun r) => r.Results.Any(x => x.Status is LiveStatus.OnCourse or LiveStatus.Finished or LiveStatus.DNF or LiveStatus.DSQ);
        RadioCondition Status(int bib, LiveStatus status) => new(RadioConditionKind.Status, run.Number, bib, status);
        RadioEvent Make(RadioEventKind kind, int priority, int[] bibs, int seconds, int relevance, RacePhase[]? phases = null, params RadioCondition[] conditions)
            => new(kind, priority, run.Number, bibs, now, TimeSpan.FromSeconds(seconds), phases ?? [], conditions, relevance);

        if (before is null || !started(before))
        {
            if (started(run))
            {
                // A racer is named only while on course ("Radalla on nyt ..."); a start already finished is not news.
                var first = run.Results.Where(x => x.Status == LiveStatus.OnCourse && x.StartedAt is not null).OrderBy(x => x.StartedAt).FirstOrDefault();
                events.Add(first is null ? Make(RadioEventKind.RunStarted, 3, [], 90, 6, Live)
                    : Make(RadioEventKind.RunStarted, 3, [first.Bib], 90, 6, Live, Status(first.Bib, first.Status)));
            }
            if (before is null) { return events; }
        }
        var totals = run.Number > 1 && run.Results.Any(x => x.TotalRank is not null);
        var finishers = run.Results.Count(x => x.Status == LiveStatus.Finished);
        foreach (var result in run.Results)
        {
            var old = before.Results.FirstOrDefault(x => x.Bib == result.Bib);
            var was = old?.Status ?? LiveStatus.Ready;
            if (result.Status == was)
            {
                if (result.Status == LiveStatus.Finished && old!.Hundredths != result.Hundredths)
                { events.Add(Make(RadioEventKind.Correction, 3, [result.Bib], 90, 5, conditions: Status(result.Bib, LiveStatus.Finished))); }
                // A correction in an earlier run changes only the combined time of this run's row.
                else if (result.Status == LiveStatus.Finished && result.TotalHundredths is not null && old!.TotalHundredths != result.TotalHundredths)
                { events.Add(Make(RadioEventKind.Correction, 3, [result.Bib], 90, 5, conditions: Status(result.Bib, LiveStatus.Finished)) with { Source = "total" }); }
                continue;
            }
            switch (result.Status)
            {
                case LiveStatus.Finished:
                    var place = totals ? result.TotalRank : result.Rank;
                    // Equal to the leader's time is a shared lead, not a new leader.
                    var shared = run.Results.Any(x => x.Bib != result.Bib && (totals ? x.TotalRank : x.Rank) == 1);
                    var kind = place == 1 && finishers > 1 && !shared ? RadioEventKind.NewLeader : place <= 3 ? RadioEventKind.Podium : RadioEventKind.Finish;
                    // A finish announcement states the place, so it is stale once anyone moves the racer down.
                    var placed = new RadioCondition(totals ? RadioConditionKind.TotalRank : RadioConditionKind.Rank, run.Number, result.Bib, Value: place);
                    events.Add(Make(kind, kind == RadioEventKind.Finish ? 3 : 2, [result.Bib], kind == RadioEventKind.Finish ? 75 : 120,
                        kind == RadioEventKind.NewLeader ? 8 : kind == RadioEventKind.Podium ? 5 : 2, conditions: [Status(result.Bib, LiveStatus.Finished), placed]));
                    break;
                case LiveStatus.DNF or LiveStatus.DSQ:
                    events.Add(Make(result.Status == LiveStatus.DNF ? RadioEventKind.DNF : RadioEventKind.DSQ, 3, [result.Bib], 90, 4,
                        conditions: Status(result.Bib, result.Status)));
                    break;
                case LiveStatus.DNS when was == LiveStatus.Ready:
                    events.Add(Make(RadioEventKind.DNS, 4, [result.Bib], 60, 1, conditions: Status(result.Bib, LiveStatus.DNS)));
                    break;
                case LiveStatus.OnCourse when was == LiveStatus.Ready && run.Number > 1 && TopAfterEarlierRuns(current, run.Number, result.Bib):
                    // In a later run the leaders start last: their start is news, every other start is routine.
                    events.Add(Make(RadioEventKind.TopStart, 2, [result.Bib], 60, 5, Live, Status(result.Bib, LiveStatus.OnCourse)));
                    break;
            }
        }
        if (!RacePhases.Complete(before) && RacePhases.Complete(run))
        {
            var podium = run.Results.Where(x => (totals ? x.TotalRank : x.Rank) is <= 3).OrderBy(x => totals ? x.TotalRank : x.Rank).Select(x => x.Bib).ToArray();
            events.Add(Make(RadioEventKind.RunComplete, 2, podium, 300, 9, [RacePhase.BetweenRuns, RacePhase.AfterRace]));
        }
        return events;
    }
    private static bool TopAfterEarlierRuns(LiveSnapshot state, int run, int bib)
    {
        var previous = state.Runs.FirstOrDefault(x => x.Number == run - 1)?.Results.FirstOrDefault(x => x.Bib == bib);
        return (previous?.TotalRank ?? previous?.Rank) is <= 3;
    }
}

/// <summary>
/// Priority batcher (after mikradio's event batcher). The highest priority in the batch sets the flush delay; a higher
/// priority arriving later shortens it from its own arrival; a full batch flushes at once. Time is supplied explicitly.
/// </summary>
public sealed class EventBatcher
{
    // Alpine finishes come every 40-60 s, so delays are shorter than in endurance racing.
    public static readonly IReadOnlyDictionary<int, TimeSpan> Delays = new Dictionary<int, TimeSpan>
    {
        [1] = TimeSpan.FromSeconds(2), [2] = TimeSpan.FromSeconds(5), [3] = TimeSpan.FromSeconds(12),
        [4] = TimeSpan.FromSeconds(25), [5] = TimeSpan.FromSeconds(40)
    };
    public const int MaxBatch = 6;
    private readonly List<RadioEvent> _pending = [];
    private DateTimeOffset _deadline;
    private int _highest = 6;
    public int Count => _pending.Count;
    /// <summary>The most urgent pending priority, or 6 when nothing is pending.</summary>
    public int Highest => _highest;
    public DateTimeOffset? Deadline => _pending.Count == 0 ? null : _deadline;

    public void Add(RadioEvent value, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(value);
        _pending.Add(value);
        var delay = Delays[Math.Clamp(value.Priority, 1, 5)];
        if (_pending.Count == 1) { _deadline = now + delay; }
        else if (value.Priority < _highest && now + delay < _deadline) { _deadline = now + delay; }
        _highest = Math.Min(_highest, value.Priority);
    }
    public IReadOnlyList<RadioEvent>? TakeDue(DateTimeOffset now)
    {
        if (_pending.Count == 0 || (now < _deadline && _pending.Count < MaxBatch)) { return null; }
        var batch = _pending.OrderBy(x => x.Priority).ThenBy(x => x.At).Take(MaxBatch).ToArray();
        _pending.RemoveAll(batch.Contains);
        _highest = _pending.Count == 0 ? 6 : _pending.Min(x => x.Priority);
        if (_pending.Count > 0) { _deadline = now + Delays[_highest]; }
        return batch;
    }
    /// <summary>Drops events that are already stale, so a slow generation never narrates outdated news.</summary>
    public void Prune(LiveSnapshot state, DateTimeOffset now)
    {
        var phase = RacePhases.Derive(state, now);
        _pending.RemoveAll(x => x.Run != state.CurrentRun || now >= x.At + x.Lifetime || (x.Phases.Length > 0 && !x.Phases.Contains(phase))
            || x.Conditions.Any(c => !RadioPlayback.Holds(state, c)));
        if (_pending.Count == 0) { _highest = 6; }
    }
}
