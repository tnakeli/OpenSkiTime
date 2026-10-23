using System.Collections.Concurrent;
using OpenSkiTime.LiveTiming.Radio;
using OpenSkiTime.LiveTiming.Radio.Providers;

namespace OpenSkiTime.LiveTiming.Tests;

/// <summary>Injected clock: time moves only on Advance, which also fires due timers (timeouts, delays).</summary>
internal sealed class ManualTime(DateTimeOffset start) : TimeProvider
{
    private readonly Lock _gate = new();
    private readonly List<ManualTimer> _timers = [];
    private DateTimeOffset _now = start;
    public override DateTimeOffset GetUtcNow() { lock (_gate) { return _now; } }
    public void Advance(TimeSpan by)
    {
        ManualTimer[] due;
        lock (_gate)
        {
            _now += by;
            due = _timers.Where(x => x.Due <= _now).ToArray();
            foreach (var timer in due) { _timers.Remove(timer); }
        }
        foreach (var timer in due) { timer.Fire(); }
    }
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }
    private void Schedule(ManualTimer timer, TimeSpan due)
    {
        lock (_gate)
        {
            _timers.Remove(timer);
            if (due == Timeout.InfiniteTimeSpan) { return; }
            timer.Due = _now + due;
            _timers.Add(timer);
        }
        if (due == TimeSpan.Zero) { Advance(TimeSpan.Zero); }
    }
    private void Cancel(ManualTimer timer) { lock (_gate) { _timers.Remove(timer); } }
    private sealed class ManualTimer(ManualTime owner, TimerCallback callback, object? state) : ITimer
    {
        public DateTimeOffset Due { get; set; }
        public bool Change(TimeSpan dueTime, TimeSpan period) { owner.Schedule(this, dueTime); return true; }
        public void Fire() => callback(state);
        public void Dispose() => owner.Cancel(this);
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}

/// <summary>Synthetic race with fictional names. Any match with a real person is accidental.</summary>
internal static class RadioRace
{
    public static readonly DateTimeOffset Start = new(2026, 10, 2, 9, 0, 0, TimeSpan.Zero);
    public static LiveSnapshot Create(string discipline = "SL") => new(1,
        new("Synthetic Cup", "Testitunturi", discipline, new(2026, 10, 2), false, "", "L", "U16", 2, "Harjoitusrinne"),
        [
            new(1, "LUMIVAARA", "Aada", "FIN", "Rinneseura", ""), new(2, "HANKIRANTA", "Veeti", "FIN", "Rinneseura", ""),
            new(3, "PORTTILA", "Saga", "SWE", "Fjällklubben", ""), new(4, "KURVINEN", "Ilona", "FIN", "Tunturiklubi", ""),
            new(5, "VIISTOLA", "Onni", "FIN", "Tunturiklubi", "")
        ], 1, [new(1, Start, [1, 2, 3, 4, 5], [.. Enumerable.Range(1, 5).Select(b => new LiveResult(b, LiveStatus.Ready, null, null, null, Start))])], Start);

    /// <summary>Sets one racer's result in the current run and recomputes ranks and gaps like timing does.</summary>
    public static LiveSnapshot Set(LiveSnapshot state, int bib, LiveStatus status, long? hundredths = null, DateTimeOffset? at = null)
    {
        var when = at ?? state.UpdatedAt.AddSeconds(30);
        var run = state.Runs.First(x => x.Number == state.CurrentRun);
        var results = run.Results.Select(r => r.Bib == bib ? r with { Status = status, Hundredths = hundredths, At = when, StartedAt = r.StartedAt ?? when } : r).ToArray();
        var best = results.Where(x => x.Status == LiveStatus.Finished).Select(x => x.Hundredths).Min();
        results = results.Select(r => r.Status == LiveStatus.Finished
            ? r with { Rank = results.Count(x => x.Status == LiveStatus.Finished && x.Hundredths < r.Hundredths) + 1, Difference = r.Hundredths - best }
            : r with { Rank = null, Difference = null }).ToArray();
        return state with { Version = state.Version + 1, UpdatedAt = when, Runs = state.Runs.Select(x => x.Number == run.Number ? x with { Results = results } : x).ToArray() };
    }
}

internal sealed class FakeLlm(Func<LlmRequest, CancellationToken, Task<string>> reply, string provider = "openai", string model = "gpt-4.1-mini") : IRadioLlm
{
    public ConcurrentQueue<LlmRequest> Requests { get; } = new();
    public string Provider => provider;
    public string Model => model;
    public async Task<LlmReply> CompleteAsync(LlmRequest request, CancellationToken ct)
    {
        Requests.Enqueue(request);
        var text = await reply(request, ct);
        return new(text, 1000, 200);
    }
    public static bool IsProducer(LlmRequest r) => r.System.StartsWith("You are the producer", StringComparison.Ordinal);
    public static bool IsCuration(LlmRequest r) => r.System.StartsWith("You review", StringComparison.Ordinal);
}

internal sealed class FakeTts(Func<string, CancellationToken, Task>? hook = null, string provider = "azure") : IRadioTts
{
    public ConcurrentQueue<string> Texts { get; } = new();
    public string Provider => provider;
    public async Task<TtsAudio> SynthesizeAsync(string text, RadioVoice voice, CancellationToken ct)
    {
        Texts.Enqueue(text);
        if (hook is not null) { await hook(text, ct); }
        return new(SimulationTts.Tone(600, 440), "audio/wav", 600);
    }
}

internal sealed class FakeSink : IRadioClipSink
{
    public ConcurrentQueue<RadioClip> Clips { get; } = new();
    public ConcurrentQueue<RadioStatusUpdate> Heartbeats { get; } = new();
    public Func<RadioClip, Task>? OnUpload { get; init; }
    public PublisherKind Channel => PublisherKind.Cloud;
    public string? Failure { get; set; }
    public async Task UploadAsync(RadioClip clip, CancellationToken ct)
    {
        if (OnUpload is not null) { await OnUpload(clip); }
        Clips.Enqueue(clip);
    }
    public Task HeartbeatAsync(RadioStatusUpdate status, CancellationToken ct) { Heartbeats.Enqueue(status); return Task.CompletedTask; }
}
