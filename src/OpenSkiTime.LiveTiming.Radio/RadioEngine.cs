using System.Text.Json;
using OpenSkiTime.LiveTiming.Radio.Providers;

namespace OpenSkiTime.LiveTiming.Radio;

/// <summary>
/// The radio pipeline: snapshot diff → events → priority batcher → dialogue (language model or templates) → validation →
/// optional curation → Finnish rendering → speech → clip upload, plus a producer for idle moments. Everything runs in the
/// radio worker process, outside timing capture, with explicit time, timeouts, cancellation and a hard budget stop.
/// </summary>
public sealed class RadioEngine
{
    public static readonly TimeSpan DialogueTimeout = TimeSpan.FromSeconds(20);
    public static readonly TimeSpan CurationTimeout = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan ProducerTimeout = TimeSpan.FromSeconds(12);
    public static readonly TimeSpan SpeechTimeout = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan UploadTimeout = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan ProducerAfterRace = TimeSpan.FromMinutes(30);
    private const int DialogueTokens = 900;

    private readonly RadioConfiguration _config;
    private readonly IRadioLlm _llm;
    private readonly IRadioTts _tts;
    private readonly TimeProvider _time;
    private readonly Action<string> _log;
    private readonly LlmPrice _llmPrice;
    private readonly TtsPrice _ttsPrice;
    private readonly IReadOnlyDictionary<string, string> _lexicon;
    private readonly EventBatcher _batcher = new();
    private readonly TopicMemory _topics = new(TimeSpan.FromHours(2));
    private readonly StyleMemory _styles = new();
    private readonly EventDiary _diary = new();
    private LiveSnapshot? _latest;
    private LiveSnapshot? _processed;
    private RacePhase? _phase;
    private DateTimeOffset? _afterRaceSince;
    private DateTimeOffset _speechEnds;
    private DateTimeOffset _nextProducer;
    private IReadOnlyList<IRadioClipSink> _sinks = [];
    private int _seed;
    private int _producerTurn;
    // Racers already featured while on course, per run, so each run of each racer is introduced on course once.
    private readonly HashSet<(int Run, int Bib)> _featured = [];
    /// <summary>Silence after which a racer on course is talked about during a run.</summary>
    public static readonly TimeSpan OnCourseAfter = TimeSpan.FromSeconds(8);
    private int _remainingLines;
    private RadioHealth _health;

    public RadioEngine(RadioConfiguration configuration, IRadioLlm llm, IRadioTts tts, RadioBudget budget, TimeProvider time, Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(configuration); ArgumentNullException.ThrowIfNull(llm); ArgumentNullException.ThrowIfNull(tts);
        ArgumentNullException.ThrowIfNull(budget); ArgumentNullException.ThrowIfNull(time);
        _config = configuration; _llm = llm; _tts = tts; Budget = budget; _time = time; _log = log ?? (_ => { });
        _llmPrice = RadioPrices.ForLlm(llm.Provider, llm.Model) ?? throw new RadioProviderException("The language model has no price entry.");
        _ttsPrice = RadioPrices.ForTts(tts.Provider);
        _lexicon = FinnishVerbalizer.Lexicon(configuration.Lexicon);
        _nextProducer = time.GetUtcNow() + TimeSpan.FromSeconds(30);
        _health = new(RadioState.Running, BudgetUsd: budget.Limit, SpentUsd: budget.Spent);
    }
    public RadioBudget Budget { get; }
    public RadioHealth Health => Volatile.Read(ref _health);
    /// <summary>Latest snapshot from the panel. One reference write; detection happens on the engine loop.</summary>
    public void Offer(LiveSnapshot snapshot) => Volatile.Write(ref _latest, snapshot);
    public void SetSinks(IReadOnlyList<IRadioClipSink> sinks) => Volatile.Write(ref _sinks, sinks);
    public bool Exhausted => Budget.Mode(DialogueEstimate, SpeechEstimate) == RadioMode.Exhausted;

    public async Task RunAsync(CancellationToken ct)
    {
        var heartbeat = HeartbeatLoopAsync(ct);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await StepAsync(ct);
                await Task.Delay(TimeSpan.FromMilliseconds(250), _time, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally { await heartbeat; }
    }
    private async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await HeartbeatAsync(ct);
                await Task.Delay(HeartbeatInterval, _time, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }
    /// <summary>Tells every target whether the play control may be shown. Sent independently of generation.</summary>
    public async Task HeartbeatAsync(CancellationToken ct)
    {
        var status = new RadioStatusUpdate(true, Exhausted);
        foreach (var sink in Volatile.Read(ref _sinks))
        {
            try { await WithTimeout(UploadTimeout, t => sink.HeartbeatAsync(status, t), ct); }
            catch (Exception ex) when (Recoverable(ex, ct)) { Report(error: Describe(ex)); }
        }
    }

    /// <summary>One pass: detect, batch, and generate at most one due batch or one producer segment.</summary>
    public async Task StepAsync(CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        if (Detect(now) is not { } state) { return; }
        var mode = Budget.Mode(DialogueEstimate, SpeechEstimate);
        if (mode == RadioMode.Exhausted)
        { Report(RadioState.BudgetExhausted, mode: mode); return; }
        // Nothing is generated, and nothing is paid for, while no server accepts the clips.
        var sinks = Volatile.Read(ref _sinks);
        if (sinks.Count == 0 || sinks.All(x => x.Failure is not null))
        { Report(RadioState.Running, error: sinks.FirstOrDefault(x => x.Failure is not null)?.Failure, mode: mode); return; }
        Report(RadioState.Running, mode: mode);
        if (_batcher.TakeDue(now) is { } batch) { await GenerateAsync(batch, mode, ct); return; }
        // A run lasts 45-80 s: a short silence is enough to talk about a racer on course who has not been featured yet.
        if (_batcher.Count == 0 && _phase == RacePhase.RunInProgress && now >= _speechEnds + OnCourseAfter && OnCourse(state, now) is { } racer)
        {
            _featured.Add((state.CurrentRun, racer));
            var feature = new ProducerDecision(ProducerSources.OnCourse, racer,
                "Kerro radalla olevasta laskijasta: seura, maa kansainvälisessä kentässä ja aiempi tulos, jos sellainen on. Ei arvailua.");
            await GenerateAsync([ProducerGate.Event(feature, state, now)], mode, ct);
            return;
        }
        if (_batcher.Count > 0 || now < _nextProducer || now < _speechEnds + Budget.ProducerInterval) { return; }
        var phase = _phase!.Value;
        if (_afterRaceSince is { } since && now - since > ProducerAfterRace) { return; }
        _nextProducer = now + Budget.ProducerInterval;
        var decision = mode == RadioMode.Full ? await ProduceAsync(state, phase, ct) : null;
        decision ??= ProducerGate.Deterministic(state, phase, _producerTurn++);
        if (decision is not null) { await GenerateAsync([ProducerGate.Event(decision, state, now)], mode, ct); }
    }

    // The racer on course the longest who has not been featured in this run, once the start announcement had its time.
    private int? OnCourse(LiveSnapshot state, DateTimeOffset now)
    {
        var run = state.Runs.First(x => x.Number == state.CurrentRun);
        return run.Results.Where(x => x.Status == LiveStatus.OnCourse && x.StartedAt is { } started && now - started >= OnCourseAfter
                && !_featured.Contains((run.Number, x.Bib)))
            .OrderBy(x => x.StartedAt).Select(x => (int?)x.Bib).FirstOrDefault();
    }

    private LiveSnapshot? Detect(DateTimeOffset now)
    {
        var state = Volatile.Read(ref _latest);
        if (state is null) { return null; }
        if (!ReferenceEquals(state, _processed))
        {
            foreach (var value in RadioEventDetector.Detect(_processed, state, now)) { _batcher.Add(value, now); }
            _processed = state;
        }
        var phase = RacePhases.Derive(state, now);
        if (phase == RacePhase.Interrupted && _phase == RacePhase.RunInProgress)
        { _batcher.Add(new(RadioEventKind.Interrupted, 3, state.CurrentRun, [], now, TimeSpan.FromSeconds(120), [RacePhase.Interrupted], [], 7), now); }
        if (phase == RacePhase.AfterRace && _phase != RacePhase.AfterRace) { _afterRaceSince = now; }
        else if (phase != RacePhase.AfterRace) { _afterRaceSince = null; }
        _phase = phase;
        _batcher.Prune(state, now);
        return state;
    }

    private async Task GenerateAsync(IReadOnlyList<RadioEvent> batch, RadioMode mode, CancellationToken ct)
    {
        var state = Volatile.Read(ref _latest)!;
        var now = _time.GetUtcNow();
        var bibs = batch.SelectMany(x => x.Bibs).Concat(_diary.Entries.TakeLast(3).SelectMany(x => x.Bibs));
        var facts = FactSheet.Build(state, bibs);
        var rules = new LineRules(state, RadioPersonas.Hosts.Select(x => x.Name), _config.EventContext);
        IReadOnlyList<ScriptLine>? lines = null; string? topic = null;
        if (mode is RadioMode.Full or RadioMode.Lean)
        {
            (lines, topic) = await DialogueAsync(batch, facts, rules, ct);
            if (lines is not null && mode == RadioMode.Full && _config.Curation) { lines = await CurateAsync(lines, facts, rules, ct); }
        }
        if (lines is null)
        {
            lines = FinnishTemplates.Lines(batch, facts, _seed++).Where(x => RadioSlots.Problem(x.Text, facts, rules) is null).ToArray();
            topic = string.Join(", ", batch.Select(x => x.Kind.ToString()));
        }
        if (lines.Count == 0) { return; }
        _topics.Add(topic ?? "", now);
        var batchId = Guid.NewGuid();
        var run = state.CurrentRun;
        // Conditions are never truncated: lines whose facts would not fit the limit are left unsaid instead.
        var conditions = new List<RadioCondition> { new(RadioConditionKind.CurrentRun, run) };
        foreach (var condition in batch.SelectMany(x => x.Conditions)) { if (!conditions.Contains(condition)) { conditions.Add(condition); } }
        var kept = new List<ScriptLine>();
        foreach (var line in lines)
        {
            var added = RadioSlots.Conditions(line.Text, facts).Where(x => !conditions.Contains(x)).Distinct().ToArray();
            if (conditions.Count + added.Length > RadioPlayback.MaxConditions) { break; }
            conditions.AddRange(added); kept.Add(line);
        }
        if (kept.Count == 0) { _log("Radio batch states too many facts for one clip"); return; }
        lines = kept.Count == lines.Count ? lines : [.. kept[..^1], kept[^1] with { Interruptible = true }];
        // Phases: only events valid now were batched (see Prune), so the intersection contains the current phase.
        var phaseSets = batch.Where(x => x.Phases.Length > 0).Select(x => x.Phases).ToArray();
        RacePhase[] phases = phaseSets.Length == 0 ? [] : phaseSets.Aggregate((a, b) => [.. a.Intersect(b)]);
        if (phaseSets.Length > 0 && phases.Length == 0) { _log("Radio batch has no common phase"); return; }
        var expires = batch.Min(x => x.At + x.Lifetime);
        var priority = batch.Min(x => x.Priority);
        var spoken = 0;
        _remainingLines = lines.Count;
        try
        {
            for (var i = 0; i < lines.Count; i++)
            {
                var line = lines[i];
                // Checked before paying for speech as well as before upload.
                if (!RadioPlayback.Playable(Volatile.Read(ref _latest)!, _time.GetUtcNow(), expires, phases, conditions)) { _log("Radio batch dropped as stale before speech"); break; }
                var text = RadioSlots.Render(line.Text, facts, _lexicon);
                var estimate = RadioPrices.Cost(_ttsPrice, text.Length, RadioPrices.EstimatedDurationMs(text.Length));
                if (!Budget.CanAfford(estimate)) { Report(RadioState.BudgetExhausted, error: "Radio budget reached; generation stopped."); break; }
                var host = RadioPersonas.Find(line.Host) ?? RadioPersonas.Host(i);
                var voice = RadioPersonas.Voice(host, _tts.Provider, Array.IndexOf(RadioPersonas.Hosts, host) == 0 ? _config.VoiceA : _config.VoiceB);
                TtsAudio audio;
                // Reserved before the call, like model calls: a failed or cancelled request may still be billed.
                Budget.Charge(estimate);
                try { audio = await WithTimeout(SpeechTimeout, t => _tts.SynthesizeAsync(text, voice, t), ct); }
                catch (Exception ex) when (Recoverable(ex, ct)) { Report(error: Describe(ex)); break; }
                Budget.Settle(estimate, RadioPrices.Cost(_ttsPrice, text.Length, audio.DurationMs));
                now = _time.GetUtcNow();
                var latest = Volatile.Read(ref _latest)!;
                // The race moved on while this line was generated: drop it and the rest of the batch.
                if (!RadioPlayback.Playable(latest, now, expires, phases, conditions)) { _log("Radio batch dropped as stale before upload"); break; }
                var clip = new RadioClip(Guid.NewGuid(), batchId, i, lines.Count, host.Name, priority, line.Interruptible || i == lines.Count - 1,
                    expires, phases, [.. conditions], audio, text);
                await UploadAsync(clip, ct);
                spoken++; _remainingLines = lines.Count - i - 1;
                _speechEnds = (now > _speechEnds ? now : _speechEnds) + TimeSpan.FromMilliseconds(audio.DurationMs);
                Report(lastClip: (now, host.Name));
                Detect(now);
                // Urgent news waits for a natural break, not for the end of a long low-priority conversation.
                if (priority >= 3 && clip.Interruptible && i < lines.Count - 1 && _batcher.Highest <= 2)
                { _log("Radio batch cut at an interruptible line for urgent news"); break; }
            }
        }
        finally { _remainingLines = 0; Report(); }
        if (spoken > 0) { foreach (var value in batch) { _diary.Add(value); } }
    }

    private async Task<(IReadOnlyList<ScriptLine>?, string?)> DialogueAsync(IReadOnlyList<RadioEvent> batch, FactSheet facts, LineRules rules, CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        var maxLines = Math.Min(RadioPrompts.MaxLines, 2 + batch.Count * 2);
        var request = new LlmRequest(RadioPrompts.DialogueSystem(maxLines),
            RadioPrompts.DialogueUser(batch, facts, _phase ?? RacePhase.RunInProgress, _config.EventContext, _topics.Recent(now), _diary.Entries), DialogueTokens, 0.8);
        if (await CompleteAsync(request, DialogueTimeout, ct) is not { } reply) { return (null, null); }
        var (lines, topic, problems) = ReadScript(reply.Text, facts, rules);
        // One repair round: the model gets its own script back with each broken line and why, and returns it fixed.
        if (problems is { Count: > 0 } && Budget.CanAfford(DialogueEstimate))
        {
            _log($"Radio script repaired: {problems[0].Problem}");
            var repair = request with { User = RadioPrompts.RepairUser(request.User, reply.Text, problems) };
            if (await CompleteAsync(repair, DialogueTimeout, ct) is not { } fixedReply) { return (null, null); }
            (lines, topic, problems) = ReadScript(fixedReply.Text, facts, rules);
        }
        if (problems is { Count: > 0 })
        {
            // The valid lines before the first invalid one still make a coherent, shorter conversation.
            if (lines is not null && problems.Min(x => x.Line) is var first and > 0)
            {
                _log($"Radio script shortened: {problems[0].Problem}");
                lines = [.. lines[..first]];
                lines[^1] = lines[^1] with { Interruptible = true };
                return (lines, topic is { } kept && !kept.Any(char.IsDigit) ? kept : null);
            }
            Report(error: $"Language model script rejected: {problems[0].Problem}. Templates used.");
            _log($"Radio script rejected: {problems[0].Problem}");
            return (null, null);
        }
        if (lines is null) { return (null, null); }
        return (lines, topic is { } summary && !summary.Any(char.IsDigit) ? summary : null);
    }

    /// <summary>The script's lines and topic, or the problems of the lines that may not be spoken (null lines: unusable JSON).</summary>
    private (ScriptLine[]? Lines, string? Topic, List<RadioPrompts.LineProblem>? Problems) ReadScript(string text, FactSheet facts, LineRules rules)
    {
        try
        {
            using var json = JsonDocument.Parse(text);
            var lines = json.RootElement.GetProperty("lines").EnumerateArray().Select(x => new ScriptLine(
                RadioPersonas.Find(x.GetProperty("host").GetString() ?? "")?.Name ?? "", RadioSlots.Normalize(x.GetProperty("text").GetString() ?? ""),
                x.TryGetProperty("interruptible", out var cut) && cut.ValueKind == JsonValueKind.True)).ToArray();
            if (lines.Length is 0 or > RadioPrompts.MaxLines || lines.Any(x => x.Host.Length == 0))
            { Report(error: "Language model script rejected: no lines or unknown host. Templates used."); return (null, null, null); }
            var problems = lines.Select((x, i) => (Check: RadioSlots.Check(x.Text, facts, rules), Line: i))
                .Where(x => x.Check.Problem is not null).Select(x => new RadioPrompts.LineProblem(x.Line, x.Check.Problem!, x.Check.Word)).ToList();
            var topic = json.RootElement.TryGetProperty("topic", out var t) ? t.GetString() : null;
            return (lines, topic, problems.Count > 0 ? problems : null);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        { Report(error: "Language model returned invalid JSON. Templates used."); return (null, null, null); }
    }

    /// <summary>Style-only second pass. Any change to line count, hosts or slots, or an invalid line, keeps the original.</summary>
    private async Task<IReadOnlyList<ScriptLine>> CurateAsync(IReadOnlyList<ScriptLine> original, FactSheet facts, LineRules rules, CancellationToken ct)
    {
        var request = new LlmRequest(RadioPrompts.CurationSystem(_config.CurationNotes, _styles.Recent), RadioPrompts.CurationUser(original), DialogueTokens, 0.4);
        if (await CompleteAsync(request, CurationTimeout, ct) is not { } reply) { return original; }
        try
        {
            using var json = JsonDocument.Parse(reply.Text);
            var lines = json.RootElement.GetProperty("lines").EnumerateArray().ToArray();
            if (lines.Length != original.Count) { _log("Radio curation discarded: line count changed"); return original; }
            var curated = new List<ScriptLine>();
            for (var i = 0; i < lines.Length; i++)
            {
                var text = RadioSlots.Normalize(lines[i].GetProperty("text").GetString() ?? "");
                var host = lines[i].TryGetProperty("host", out var h) ? h.GetString() : original[i].Host;
                if (!string.Equals(host, original[i].Host, StringComparison.OrdinalIgnoreCase)
                    || !RadioSlots.Parse(text).Select(x => x.Text).SequenceEqual(RadioSlots.Parse(original[i].Text).Select(x => x.Text))
                    || RadioSlots.Problem(text, facts, rules) is not null)
                { _log("Radio curation discarded: host, slots or validation changed"); return original; }
                curated.Add(original[i] with { Text = text });
            }
            if (json.RootElement.TryGetProperty("patterns", out var patterns) && patterns.ValueKind == JsonValueKind.Array)
            { _styles.AddRange(patterns.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString()! : "").Take(4)); }
            return curated;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        { _log("Radio curation discarded: invalid JSON"); return original; }
    }

    private async Task<ProducerDecision?> ProduceAsync(LiveSnapshot state, RacePhase phase, CancellationToken ct)
    {
        var hasContext = !string.IsNullOrWhiteSpace(_config.EventContext);
        var allowed = ProducerSources.Phases.Where(x => x.Value.Contains(phase) && (x.Key != ProducerSources.EventContext || hasContext)).Select(x => x.Key).ToArray();
        var failed = new List<string>();
        // One retry, like mikradio: the second attempt is told which source failed the gate.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var request = new LlmRequest(RadioPrompts.ProducerSystem(), RadioPrompts.ProducerUser(state, phase, allowed, _topics.Recent(_time.GetUtcNow()), failed, hasContext), 200, 0.7);
            if (await CompleteAsync(request, ProducerTimeout, ct) is not { } reply) { return null; }
            try
            {
                using var json = JsonDocument.Parse(reply.Text);
                var source = json.RootElement.GetProperty("source").GetString() ?? "";
                int? bib = json.RootElement.TryGetProperty("bib", out var b) && b.ValueKind == JsonValueKind.Number ? b.GetInt32() : null;
                var instruction = json.RootElement.TryGetProperty("instruction", out var i) ? i.GetString() ?? "" : "";
                var decision = new ProducerDecision(source, bib, instruction);
                if (ProducerGate.Problem(decision, state, phase, hasContext) is not { } problem) { return decision; }
                _log($"Radio producer choice rejected: {problem}");
                failed.Add(source);
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
            { failed.Add("invalid"); }
        }
        return null;
    }

    private async Task<LlmReply?> CompleteAsync(LlmRequest request, TimeSpan timeout, CancellationToken ct)
    {
        var estimate = RadioPrices.Cost(_llmPrice, (request.System.Length + request.User.Length) / 3, request.MaxOutputTokens);
        if (!Budget.CanAfford(estimate + SpeechEstimate)) { return null; }
        // Reserved before the call: a failed, timed-out or cancelled request may still be billed, so it keeps the estimate.
        Budget.Charge(estimate);
        try
        {
            var reply = await WithTimeout(timeout, t => _llm.CompleteAsync(request, t), ct);
            Budget.Settle(estimate, RadioPrices.Cost(_llmPrice, reply.InputTokens, reply.OutputTokens));
            Report();
            return reply;
        }
        catch (Exception ex) when (Recoverable(ex, ct))
        {
            Report(error: Describe(ex) + " Templates used.");
            return null;
        }
    }

    private async Task UploadAsync(RadioClip clip, CancellationToken ct)
    {
        foreach (var sink in Volatile.Read(ref _sinks))
        {
            if (sink.Failure is not null) { continue; }
            try { await WithTimeout(UploadTimeout, t => sink.UploadAsync(clip, t), ct); }
            catch (Exception ex) when (Recoverable(ex, ct)) { Report(error: Describe(ex)); }
        }
    }

    private async Task<T> WithTimeout<T>(TimeSpan timeout, Func<CancellationToken, Task<T>> call, CancellationToken ct)
    {
        using var limit = new CancellationTokenSource(timeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, limit.Token);
        try { return await call(linked.Token).WaitAsync(linked.Token); }
        catch (OperationCanceledException) when (limit.IsCancellationRequested && !ct.IsCancellationRequested)
        { throw new TimeoutException(); }
    }
    private Task<bool> WithTimeout(TimeSpan timeout, Func<CancellationToken, Task> call, CancellationToken ct)
        => WithTimeout(timeout, async t => { await call(t); return true; }, ct);
    // ObjectDisposedException covers a sink removed (and disposed) by a target change while a call was under way.
    private static bool Recoverable(Exception ex, CancellationToken ct) => !ct.IsCancellationRequested
        && ex is TimeoutException or RadioProviderException or HttpRequestException or IOException or JsonException or LiveValidationException
            or ObjectDisposedException or InvalidOperationException or OperationCanceledException;
    private static string Describe(Exception ex) => ex switch
    {
        TimeoutException => "A radio provider did not answer in time.",
        RadioProviderException or LiveValidationException => ex.Message,
        _ => "A radio provider call failed."
    };

    private decimal DialogueEstimate => RadioPrices.Cost(_llmPrice, 6000, DialogueTokens);
    private decimal SpeechEstimate => RadioPrices.Cost(_ttsPrice, 200, RadioPrices.EstimatedDurationMs(200));

    private void Report(RadioState? state = null, string? error = null, RadioMode? mode = null, (DateTimeOffset At, string Host)? lastClip = null)
    {
        var current = Health;
        // Errors are OpenSkiTime's own messages (no keys, scripts or names); a repeated error is logged once.
        if (error is not null && error != current.Error) { _log($"Radio error: {error}"); }
        Volatile.Write(ref _health, current with
        {
            State = state ?? current.State, Error = error ?? current.Error, Mode = mode?.ToString() ?? current.Mode,
            LastClipAt = lastClip?.At ?? current.LastClipAt, LastClipHost = lastClip?.Host ?? current.LastClipHost,
            SpentUsd = Budget.Spent, BudgetUsd = Budget.Limit, Phase = _phase, QueueDepth = _batcher.Count + _remainingLines
        });
    }
}
