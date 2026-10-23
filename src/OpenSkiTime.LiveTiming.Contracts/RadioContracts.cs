namespace OpenSkiTime.LiveTiming;

/// <summary>Alpine race phase, derived from the live snapshot. Gates producer sources and clip applicability.</summary>
public enum RacePhase { BeforeStart, RunInProgress, BetweenRuns, Interrupted, AfterRace }

public enum RadioConditionKind { Status, Rank, TotalRank, Time, Total, CurrentRun, StatusCount, NextStarter, Starters }

/// <summary>
/// One fact a radio clip states. A clip is stale once the latest snapshot no longer satisfies every condition of its
/// batch, for example when a stated leader has been beaten or a racer is no longer on course.
/// </summary>
public sealed record RadioCondition(RadioConditionKind Kind, int Run, int Bib = 0, LiveStatus? Status = null, long? Value = null);

/// <summary>
/// Authenticated clip upload from the radio worker. Audio is base64 MP3 or WAV; transcripts are never sent. The lifetime is
/// relative, so the operator computer's clock and the server's clock need not agree.
/// </summary>
public sealed record RadioClipUpload(Guid ClipId, Guid BatchId, int Line, int LineCount, string Host, int Priority,
    bool Interruptible, int LifetimeSeconds, RacePhase[] Phases, RadioCondition[] Conditions, string ContentType,
    int DurationMs, string Audio);

/// <summary>Worker heartbeat. The server uses its own receive time, never the worker clock, for health.</summary>
public sealed record RadioStatusUpdate(bool Enabled, bool BudgetExhausted);

/// <summary>Public metadata of a clip that is playable now. Audio is fetched from the clip URL. Times are server times.</summary>
public sealed record RadioClipInfo(Guid ClipId, Guid BatchId, long Sequence, int Line, int LineCount, string Host, int Priority,
    bool Interruptible, DateTimeOffset ReceivedAt, DateTimeOffset ExpiresAt, int DurationMs);

/// <summary>
/// SignalR "Radio" message: whether the play control is shown and which clips are still valid. The server time lets a
/// viewer judge clip age with its own clock, so joining skips clips that were already old when the page loaded.
/// </summary>
public sealed record RadioAnnouncement(bool Available, RadioClipInfo[] Clips, DateTimeOffset ServerTime = default);

public enum RadioState { Stopped, Starting, Running, BudgetExhausted, Error }

/// <summary>Operator configuration of the optional race radio for one competition. Contains no secrets.</summary>
public sealed record RadioConfiguration
{
    public bool LocalEnabled { get; init; }
    public bool CloudEnabled { get; init; }
    public decimal BudgetUsd { get; init; } = 5m;
    public string LlmProvider { get; init; } = RadioProviders.OpenAi;
    public string LlmModel { get; init; } = "gpt-4.1-mini";
    public bool Curation { get; init; } = true;
    public string TtsProvider { get; init; } = RadioProviders.Azure;
    public string TtsRegion { get; init; } = "westeurope";
    public string VoiceA { get; init; } = "";
    public string VoiceB { get; init; } = "";
    public string EventContext { get; init; } = "";
    public string CurationNotes { get; init; } = "";
    /// <summary>Lines of "written name = spoken form" applied only when a racer name is rendered for speech.</summary>
    public string Lexicon { get; init; } = "";
    public bool Enabled(PublisherKind channel) => channel == PublisherKind.Local ? LocalEnabled : channel == PublisherKind.Cloud && CloudEnabled;
    public void Validate()
    {
        if (BudgetUsd is < 0m or > 1000m) { throw new LiveValidationException("Radio budget must be between 0 and 1000 USD."); }
        if (!RadioProviders.Llm.Contains(LlmProvider) || !RadioProviders.Tts.Contains(TtsProvider))
        { throw new LiveValidationException("Choose a supported radio provider."); }
        foreach (var (text, max) in new[] { (LlmModel, 64), (TtsRegion, 32), (VoiceA, 64), (VoiceB, 64) })
        {
            if (text is null || text.Length > max || text.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '_')))
            { throw new LiveValidationException("Radio model, region and voice names may contain only letters, digits, '-', '.' and '_'."); }
        }
        foreach (var (text, max) in new[] { (EventContext, 2000), (CurationNotes, 1000), (Lexicon, 4000) })
        {
            if (text is null || text.Length > max || text.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t')))
            { throw new LiveValidationException("Radio context, notes and lexicon are too long or contain control characters."); }
        }
    }
}

public static class RadioProviders
{
    public const string OpenAi = "openai";
    public const string Gemini = "gemini";
    public const string Azure = "azure";
    public const string Google = "google";
    public const string Simulation = "simulation";
    public static readonly string[] Llm = [OpenAi, Gemini, Simulation];
    public static readonly string[] Tts = [Azure, Google, OpenAi, Simulation];
}

public sealed record RadioTarget(PublisherKind Channel, string Endpoint, Guid SessionId, string Token);

// Private current-user IPC only. Keys and session tokens never appear in process arguments or logs.
public sealed record RadioWorkerOptions(RadioConfiguration Configuration, decimal SpentUsd, string? LlmKey = null, string? TtsKey = null);
public sealed record RadioWorkerInput(string Command, RadioWorkerOptions? Options = null, LiveSnapshot? Snapshot = null, RadioTarget[]? Targets = null);
public sealed record RadioHealth(RadioState State, int QueueDepth = 0, DateTimeOffset? LastClipAt = null, string? LastClipHost = null,
    string? Error = null, decimal SpentUsd = 0m, decimal BudgetUsd = 0m, string Mode = "", RacePhase? Phase = null);
public sealed record RadioWorkerOutput(RadioHealth Health);

public static class RacePhases
{
    /// <summary>A run in progress with nobody on course and no new result for this long is interrupted.</summary>
    public static readonly TimeSpan InterruptionAfter = TimeSpan.FromMinutes(4);

    /// <summary>
    /// Derives the phase from the current run only. Speed disciplines (DH, SG) have one run, every other discipline two;
    /// the snapshot does not carry a planned run count. Inactivity is measured from the snapshot's update time, which the
    /// publisher stamps with its own clock, never from device timestamps that may be set to another time of day.
    /// </summary>
    public static RacePhase Derive(LiveSnapshot state, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(state);
        var run = state.Runs.First(x => x.Number == state.CurrentRun);
        var started = run.Results.Any(x => x.Status is LiveStatus.OnCourse or LiveStatus.Finished or LiveStatus.DNF or LiveStatus.DSQ or LiveStatus.NPS);
        if (!started) { return run.Number == 1 ? RacePhase.BeforeStart : RacePhase.BetweenRuns; }
        if (Complete(run)) { return run.Number >= PlannedRuns(state.Competition) ? RacePhase.AfterRace : RacePhase.BetweenRuns; }
        if (run.Results.Any(x => x.Status == LiveStatus.OnCourse)) { return RacePhase.RunInProgress; }
        return now - state.UpdatedAt >= InterruptionAfter ? RacePhase.Interrupted : RacePhase.RunInProgress;
    }
    public static bool Complete(LiveRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        return run.StartOrder.Length > 0 && run.StartOrder.All(bib => run.Results.FirstOrDefault(x => x.Bib == bib) is { } r
            && r.Status is not (LiveStatus.Ready or LiveStatus.OnCourse or LiveStatus.Review));
    }
    public static int PlannedRuns(LiveCompetition competition)
    {
        ArgumentNullException.ThrowIfNull(competition);
        return competition.Discipline is "DH" or "SG" ? 1 : 2;
    }
}

/// <summary>Server and worker share these rules, so a viewer never receives a clip the worker would have dropped.</summary>
public static class RadioPlayback
{
    public static readonly TimeSpan HeartbeatTimeout = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan MaxClipLifetime = TimeSpan.FromMinutes(10);
    public const int MaxClipBytes = 1024 * 1024;
    public const int MaxClipsPerSession = 32;
    public const int MaxSessionBytes = 8 * 1024 * 1024;
    public const int MaxConditions = 32;
    public static readonly string[] ContentTypes = ["audio/mpeg", "audio/wav"];

    public static bool Holds(LiveSnapshot state, RadioCondition condition)
    {
        ArgumentNullException.ThrowIfNull(state); ArgumentNullException.ThrowIfNull(condition);
        if (condition.Kind == RadioConditionKind.CurrentRun) { return state.CurrentRun == condition.Run; }
        var run = state.Runs.FirstOrDefault(x => x.Number == condition.Run);
        if (condition.Kind == RadioConditionKind.Starters) { return run is not null && run.StartOrder.Length == condition.Value; }
        // The racer is still the first one waiting in the current start order (a reordered queue makes it false).
        if (condition.Kind == RadioConditionKind.NextStarter)
        { return run is not null && run.StartOrder.FirstOrDefault(bib => (run.Results.FirstOrDefault(x => x.Bib == bib)?.Status ?? LiveStatus.Ready) == LiveStatus.Ready) == condition.Bib; }
        // The number of racers of the run with a status, counted like the fact sheet counts them (no result is Ready).
        if (condition.Kind == RadioConditionKind.StatusCount)
        { return run is not null && run.StartOrder.Count(bib => (run.Results.FirstOrDefault(x => x.Bib == bib)?.Status ?? LiveStatus.Ready) == condition.Status) == condition.Value; }
        if (run is null || !run.StartOrder.Contains(condition.Bib)) { return false; }
        var result = run.Results.FirstOrDefault(x => x.Bib == condition.Bib);
        return condition.Kind switch
        {
            RadioConditionKind.Status => (result?.Status ?? LiveStatus.Ready) == condition.Status,
            RadioConditionKind.Rank => result?.Rank == condition.Value,
            RadioConditionKind.TotalRank => result?.TotalRank == condition.Value,
            RadioConditionKind.Time => result?.Hundredths == condition.Value,
            RadioConditionKind.Total => result?.TotalHundredths == condition.Value,
            _ => false
        };
    }
    /// <summary>A clip plays only before expiry, in an applicable phase and while every stated fact still holds.</summary>
    public static bool Playable(LiveSnapshot state, DateTimeOffset now, DateTimeOffset expiresAt, IReadOnlyCollection<RacePhase> phases,
        IEnumerable<RadioCondition> conditions)
    {
        ArgumentNullException.ThrowIfNull(phases); ArgumentNullException.ThrowIfNull(conditions);
        return now < expiresAt && (phases.Count == 0 || phases.Contains(RacePhases.Derive(state, now)))
            && conditions.All(x => Holds(state, x));
    }
    public static bool Available(bool enabled, bool budgetExhausted, DateTimeOffset? heartbeat, LiveSnapshot? state, DateTimeOffset now)
        => enabled && !budgetExhausted && heartbeat is { } beat && now - beat <= HeartbeatTimeout && state is { Paused: false };

    public static void Validate(RadioClipUpload clip, out byte[] audio)
    {
        ArgumentNullException.ThrowIfNull(clip);
        if (clip.ClipId == Guid.Empty || clip.BatchId == Guid.Empty || clip.LineCount is < 1 or > 16 || clip.Line < 0 || clip.Line >= clip.LineCount
            || clip.Priority is < 1 or > 5 || clip.Host is not { Length: >= 1 and <= 32 } || !clip.Host.All(char.IsLetter)
            || clip.LifetimeSeconds < 1 || clip.LifetimeSeconds > MaxClipLifetime.TotalSeconds || clip.DurationMs is < 0 or > 120_000
            || clip.Phases is null || clip.Phases.Length > 5 || clip.Phases.Any(x => !Enum.IsDefined(x))
            || clip.Conditions is null || clip.Conditions.Length > MaxConditions
            || clip.Conditions.Any(x => x is null || !Enum.IsDefined(x.Kind) || x.Run is < 1 or > 9 || x.Bib is < 0 or > 99999
                || (x.Status is { } status && !Enum.IsDefined(status)))
            || !ContentTypes.Contains(clip.ContentType) || clip.Audio is null || clip.Audio.Length > (MaxClipBytes + 2) / 3 * 4)
        { throw new LiveValidationException("Invalid radio clip."); }
        try { audio = Convert.FromBase64String(clip.Audio); }
        catch (FormatException) { throw new LiveValidationException("Invalid radio clip."); }
        if (audio.Length is 0 or > MaxClipBytes || !Sniff(audio, clip.ContentType)) { throw new LiveValidationException("Invalid radio clip."); }
    }
    // The served bytes must match the declared type, so a clip URL can never deliver another kind of content.
    private static bool Sniff(byte[] audio, string contentType) => contentType == "audio/wav"
        ? audio.Length > 44 && audio.AsSpan(0, 4).SequenceEqual("RIFF"u8) && audio.AsSpan(8, 4).SequenceEqual("WAVE"u8)
        : audio.Length > 4 && (audio.AsSpan(0, 3).SequenceEqual("ID3"u8) || (audio[0] == 0xFF && (audio[1] & 0xE0) == 0xE0));
}
