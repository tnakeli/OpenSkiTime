namespace OpenSkiTime.LiveTiming.Radio;

public static class ProducerSources
{
    public const string Standings = "standings";
    public const string NextStarter = "next_starter";
    public const string OnCourse = "on_course";
    public const string RunSummary = "run_summary";
    public const string EventContext = "event_context";
    public const string General = "general";
    public static readonly IReadOnlyDictionary<string, RacePhase[]> Phases = new Dictionary<string, RacePhase[]>(StringComparer.Ordinal)
    {
        [Standings] = [RacePhase.RunInProgress, RacePhase.BetweenRuns, RacePhase.Interrupted, RacePhase.AfterRace],
        [NextStarter] = [RacePhase.BeforeStart, RacePhase.RunInProgress, RacePhase.BetweenRuns, RacePhase.Interrupted],
        [OnCourse] = [RacePhase.RunInProgress],
        [RunSummary] = [RacePhase.RunInProgress, RacePhase.BetweenRuns, RacePhase.Interrupted, RacePhase.AfterRace],
        [EventContext] = [RacePhase.BeforeStart, RacePhase.BetweenRuns, RacePhase.Interrupted, RacePhase.AfterRace],
        [General] = [RacePhase.BeforeStart, RacePhase.RunInProgress, RacePhase.BetweenRuns, RacePhase.Interrupted, RacePhase.AfterRace]
    };
}

public sealed record ProducerDecision(string Source, int? Bib, string Instruction);

/// <summary>
/// Hard code gate for the producer agent's choice (after mikradio's phase gate): the source must be on the allowlist for
/// the current phase, a named racer must be a participant in the right state, and the source must have data.
/// The prompt states the same rules, but a model may ignore soft constraints.
/// </summary>
public static class ProducerGate
{
    public static string? Problem(ProducerDecision decision, LiveSnapshot state, RacePhase phase, bool hasContext)
    {
        ArgumentNullException.ThrowIfNull(decision); ArgumentNullException.ThrowIfNull(state);
        if (!ProducerSources.Phases.TryGetValue(decision.Source, out var phases)) { return "unknown source"; }
        if (!phases.Contains(phase)) { return "source not allowed in this phase"; }
        if (decision.Instruction.Length > 300 || decision.Instruction.Any(char.IsControl)) { return "invalid instruction"; }
        var run = state.Runs.First(x => x.Number == state.CurrentRun);
        LiveStatus Status(int bib) => run.Results.FirstOrDefault(x => x.Bib == bib)?.Status ?? LiveStatus.Ready;
        return decision.Source switch
        {
            ProducerSources.NextStarter => decision.Bib is { } next && run.StartOrder.FirstOrDefault(x => Status(x) == LiveStatus.Ready) == next && next != 0
                ? null : "racer is not the next to start",
            ProducerSources.OnCourse => decision.Bib is { } racer && run.StartOrder.Contains(racer) && Status(racer) == LiveStatus.OnCourse ? null : "racer is not on course",
            ProducerSources.Standings => Leaders(state).Length > 0 ? null : "no standings yet",
            ProducerSources.RunSummary => run.Results.Any(x => x.Status != LiveStatus.Ready) ? null : "run has not started",
            ProducerSources.EventContext => hasContext ? null : "no operator context",
            _ => null
        };
    }
    /// <summary>Builds the event for an accepted decision with the racers it concerns.</summary>
    public static RadioEvent Event(ProducerDecision decision, LiveSnapshot state, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(decision); ArgumentNullException.ThrowIfNull(state);
        var run = state.CurrentRun;
        int[] bibs = decision.Source == ProducerSources.Standings ? Leaders(state) : decision.Bib is { } bib ? [bib] : [];
        var current = state.Runs.First(x => x.Number == run);
        RadioCondition[] conditions = decision.Source switch
        {
            ProducerSources.NextStarter => [new(RadioConditionKind.NextStarter, run, bibs[0])],
            ProducerSources.OnCourse => [new(RadioConditionKind.Status, run, bibs[0], LiveStatus.OnCourse)],
            // Standings of the previous run hold only until the first finish of this run changes them.
            ProducerSources.Standings when !current.Results.Any(x => x.Rank is not null || x.TotalRank is not null)
                => [new(RadioConditionKind.StatusCount, run, Status: LiveStatus.Finished, Value: 0)],
            _ => []
        };
        return new(RadioEventKind.Producer, 5, run, bibs, now, TimeSpan.FromSeconds(120), ProducerSources.Phases[decision.Source], conditions, 0)
        { Instruction = decision.Instruction, Source = decision.Source };
    }
    /// <summary>Choice without a language model: rotate through the sources that the phase and data allow.</summary>
    public static ProducerDecision? Deterministic(LiveSnapshot state, RacePhase phase, int turn)
    {
        ArgumentNullException.ThrowIfNull(state);
        var run = state.Runs.First(x => x.Number == state.CurrentRun);
        var next = run.StartOrder.FirstOrDefault(bib => (run.Results.FirstOrDefault(x => x.Bib == bib)?.Status ?? LiveStatus.Ready) == LiveStatus.Ready);
        var onCourse = run.Results.Where(x => x.Status == LiveStatus.OnCourse).OrderBy(x => x.StartedAt).Select(x => x.Bib).FirstOrDefault();
        var candidates = new[]
        {
            new ProducerDecision(ProducerSources.Standings, null, ""), new ProducerDecision(ProducerSources.NextStarter, next == 0 ? null : next, ""),
            new ProducerDecision(ProducerSources.RunSummary, null, ""), new ProducerDecision(ProducerSources.OnCourse, onCourse == 0 ? null : onCourse, "")
        }.Where(x => Problem(x, state, phase, false) is null).ToArray();
        return candidates.Length == 0 ? null : candidates[Math.Abs(turn) % candidates.Length];
    }
    internal static int[] Leaders(LiveSnapshot state)
    {
        var run = state.Runs.First(x => x.Number == state.CurrentRun);
        var totals = run.Number > 1 && run.Results.Any(x => x.TotalRank is not null);
        var leaders = run.Results.Where(x => (totals ? x.TotalRank : x.Rank) is <= 3).OrderBy(x => totals ? x.TotalRank : x.Rank).Select(x => x.Bib).ToArray();
        // Between runs the new run has no times yet; the standings are those of the run before.
        if (leaders.Length == 0 && state.Runs.FirstOrDefault(x => x.Number == run.Number - 1) is { } previous)
        { leaders = previous.Results.Where(x => (x.TotalRank ?? x.Rank) is <= 3).OrderBy(x => x.TotalRank ?? x.Rank).Select(x => x.Bib).ToArray(); }
        return leaders;
    }
}

/// <summary>Recently discussed topics with time-to-live, so hosts do not repeat themselves. RAM only.</summary>
public sealed class TopicMemory(TimeSpan lifetime, int capacity = 50)
{
    private readonly LinkedList<(DateTimeOffset At, string Text)> _entries = new();
    public void Add(string summary, DateTimeOffset now)
    {
        var text = string.Join(' ', (summary ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(10));
        if (text.Length == 0 || text.Length > 120) { return; }
        _entries.AddLast((now, text));
        while (_entries.Count > capacity) { _entries.RemoveFirst(); }
    }
    public IReadOnlyList<string> Recent(DateTimeOffset now)
    {
        while (_entries.First is { } first && now - first.Value.At >= lifetime) { _entries.RemoveFirst(); }
        return _entries.Select(x => x.Text).ToArray();
    }
}

/// <summary>Recently used jokes, analogies and phrasings, fed by curation. First in, first out. RAM only.</summary>
public sealed class StyleMemory(int capacity = 30)
{
    private readonly Queue<string> _patterns = new();
    public void AddRange(IEnumerable<string> patterns)
    {
        foreach (var pattern in patterns ?? [])
        {
            var text = pattern?.Trim() ?? "";
            if (text.Length is 0 or > 100) { continue; }
            _patterns.Enqueue(text);
            while (_patterns.Count > capacity) { _patterns.Dequeue(); }
        }
    }
    public IReadOnlyList<string> Recent => _patterns.ToArray();
}

public sealed record DiaryEntry(DateTimeOffset At, RadioEventKind Kind, int Run, int[] Bibs, int Relevance);

/// <summary>
/// The day's key moments. When full, the least relevant entry is evicted, but only for a more relevant one. Relevance is
/// assigned deterministically by event kind instead of an extra model call. RAM only.
/// </summary>
public sealed class EventDiary(int capacity = 40)
{
    private readonly List<DiaryEntry> _entries = [];
    public IReadOnlyList<DiaryEntry> Entries => _entries;
    public void Add(RadioEvent value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Relevance <= 1) { return; }
        var entry = new DiaryEntry(value.At, value.Kind, value.Run, value.Bibs, value.Relevance);
        if (_entries.Count >= capacity)
        {
            var least = _entries.MinBy(x => x.Relevance)!;
            if (entry.Relevance <= least.Relevance) { return; }
            _entries.Remove(least);
        }
        _entries.Add(entry);
        _entries.Sort((a, b) => a.At.CompareTo(b.At));
    }
}
