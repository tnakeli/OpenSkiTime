using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace OpenSkiTime.LiveTiming.Server;

public sealed class SessionStore
{
    private sealed record Claims(Guid SessionId, long Expires);
    private sealed record Entry(DateTimeOffset Expires, LiveSnapshot? State, RadioRoom? Radio = null);
    // Race radio of one session: worker health and a RAM-only ring of clips. Removed with the session (delete, Delete All
    // Data, expiry); nothing is written to disk. Mutated only under the store lock.
    private sealed class RadioRoom
    {
        public bool Enabled;
        public bool BudgetExhausted;
        public DateTimeOffset? Heartbeat;
        public long Sequence;
        public readonly List<StoredClip> Clips = [];
    }
    private sealed record StoredClip(RadioClipInfo Info, RacePhase[] Phases, RadioCondition[] Conditions, string ContentType, byte[] Audio);
    private readonly Dictionary<Guid, Entry> _sessions = [];
    private readonly Dictionary<Guid, DateTimeOffset> _deleted = [];
    private readonly Lock _gate = new();
    private readonly byte[] _key;
    private readonly TimeProvider _time;
    private readonly int _capacity;
    private readonly TimeSpan _lifetime;
    private readonly long _radioBytes;
    public SessionStore(IConfiguration config, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(config); ArgumentNullException.ThrowIfNull(time);
        _time = time;
        var key = config["LiveTiming:SigningKey"] ?? throw new InvalidOperationException("LiveTiming signing key is required (base64, at least 32 bytes).");
        _key = Convert.FromBase64String(key);
        if (_key.Length < 32) { throw new InvalidOperationException("LiveTiming signing key needs at least 32 bytes."); }
        _capacity = Math.Clamp(config.GetValue("LiveTiming:MaxSessions", 100), 1, 10000);
        _lifetime = TimeSpan.FromDays(Math.Clamp(config.GetValue("LiveTiming:TokenDays", 14), 1, 14));
        _radioBytes = Math.Clamp(config.GetValue("LiveTiming:RadioMaxMegabytes", 64), 1, 1024) * 1024L * 1024L;
    }
    public LiveSession Create(string publicBase)
    {
        ArgumentNullException.ThrowIfNull(publicBase);
        lock (_gate)
        {
            Prune();
            if (_sessions.Count + _deleted.Count >= _capacity) { throw new LiveCapacityException(); }
            var id = Guid.NewGuid(); var expires = _time.GetUtcNow().Add(_lifetime);
            _sessions.Add(id, new(expires, null));
            var payload = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new Claims(id, expires.ToUnixTimeSeconds())));
            var signature = Convert.ToBase64String(HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(payload)));
            return new(id, payload + "." + signature, expires, publicBase.TrimEnd('/') + "/r/" + id);
        }
    }
    public DateTimeOffset? Authorize(Guid id, string token)
    {
        ArgumentNullException.ThrowIfNull(token);
        try
        {
            var parts = token.Split('.');
            if (parts.Length != 2 || token.Length > 2048) { return null; }
            var signature = Convert.FromBase64String(parts[1]);
            if (!CryptographicOperations.FixedTimeEquals(signature, HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(parts[0])))) { return null; }
            var claims = JsonSerializer.Deserialize<Claims>(Convert.FromBase64String(parts[0]));
            if (claims is null || claims.SessionId != id || claims.Expires <= _time.GetUtcNow().ToUnixTimeSeconds()) { return null; }
            lock (_gate) { return _deleted.ContainsKey(id) ? null : DateTimeOffset.FromUnixTimeSeconds(claims.Expires); }
        }
        catch (Exception ex) when (ex is FormatException or JsonException or ArgumentOutOfRangeException) { return null; }
    }
    public LiveSnapshot? Read(Guid id)
    {
        lock (_gate) { Prune(); return _sessions.GetValueOrDefault(id)?.State; }
    }
    public IReadOnlyList<LiveSessionSummary> List()
    {
        lock (_gate)
        {
            Prune();
            return _sessions.Where(x => x.Value.State is not null).Select(x => (Id: x.Key, State: x.Value.State!))
                .OrderByDescending(x => x.State.Competition.Date).ThenByDescending(x => x.State.UpdatedAt).ThenBy(x => x.Id)
                .Select(x => new LiveSessionSummary(x.Id, x.State.Competition.Name, x.State.Competition.Place, x.State.Competition.Discipline,
                    x.State.Competition.Date, x.State.Competition.Gender, x.State.Competition.Category, x.State.Competition.IsFis,
                    x.State.Competition.Codex, x.State.UpdatedAt, x.State.Paused)).ToArray();
        }
    }
    public void Replace(Guid id, DateTimeOffset expires, LiveSnapshot state)
    {
        ArgumentNullException.ThrowIfNull(state);
        state.Validate(); // Complete validation precedes mutation.
        lock (_gate)
        {
            Prune();
            if (_deleted.ContainsKey(id)) { throw new LiveValidationException("Session was deleted."); }
            if (!_sessions.ContainsKey(id) && _sessions.Count + _deleted.Count >= _capacity) { throw new LiveCapacityException(); }
            // A full replacement keeps the session's radio, so a publisher resync never silences it.
            _sessions[id] = _sessions.TryGetValue(id, out var existing) ? existing with { Expires = expires, State = state } : new(expires, state);
        }
    }
    public LiveSnapshot Apply(Guid id, LiveEvent update)
    {
        ArgumentNullException.ThrowIfNull(update);
        lock (_gate)
        {
            Prune();
            var entry = _sessions.GetValueOrDefault(id);
            var state = entry?.State ?? throw new LiveConflictException();
            if (!Enum.IsDefined(update.Kind) || update.Result is null) { throw new LiveValidationException("Invalid event."); }
            LiveSnapshot.ValidateResult(update.Result, state.Competition.IntermediateCount);
            var matches = update.Kind switch
            {
                LiveEventKind.DNS => update.Result.Status == LiveStatus.DNS,
                LiveEventKind.DNF => update.Result.Status == LiveStatus.DNF,
                LiveEventKind.DSQ => update.Result.Status == LiveStatus.DSQ,
                LiveEventKind.CompetitorStarted => update.Result.Status == LiveStatus.OnCourse,
                LiveEventKind.IntermediateTime => update.Result.Status == LiveStatus.OnCourse && update.Result.Splits.Length > 0,
                LiveEventKind.CompetitorFinished => update.Result.Status == LiveStatus.Finished,
                _ => true
            };
            if (!matches || update.At == default) { throw new LiveValidationException("Event kind and result do not match."); }
            if (update.Version != state.Version + 1) { throw new LiveConflictException(); }
            var run = state.Runs.FirstOrDefault(x => x.Number == update.Run);
            if (run is null || !run.StartOrder.Contains(update.Result.Bib)) { throw new LiveValidationException("Event racer is absent from start list."); }
            var results = run.Results.Where(x => x.Bib != update.Result.Bib).Append(update.Result).OrderBy(x => x.Bib).ToArray();
            var next = state with { Version = update.Version, UpdatedAt = update.At,
                Runs = state.Runs.Select(x => x.Number == run.Number ? x with { Results = results } : x).ToArray() };
            _sessions[id] = entry! with { State = next };
            return next;
        }
    }
    public LiveSnapshot? Pause(Guid id)
    {
        lock (_gate)
        {
            var entry = _sessions.GetValueOrDefault(id);
            if (entry?.State is not { } state) { return null; }
            state = state with { Paused = true };
            _sessions[id] = entry with { State = state }; return state;
        }
    }
    public void Delete(Guid id, DateTimeOffset expires)
    {
        lock (_gate)
        {
            Prune();
            if (!_sessions.ContainsKey(id) && _deleted.Count + _sessions.Count >= _capacity) { throw new LiveCapacityException(); }
            _sessions.Remove(id); _deleted[id] = expires;
        }
    }
    /// <summary>Worker heartbeat with the server's receive time. Disabling removes every clip at once.</summary>
    public void RadioStatus(Guid id, RadioStatusUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);
        lock (_gate)
        {
            Prune();
            var room = Room(id);
            room.Enabled = update.Enabled; room.BudgetExhausted = update.BudgetExhausted; room.Heartbeat = _time.GetUtcNow();
            if (!update.Enabled) { room.Clips.Clear(); }
        }
    }
    /// <summary>
    /// Validates the complete clip before mutation, then appends it to the session ring. The oldest clips are evicted per
    /// session (count and bytes) and across sessions (server-wide bytes). Repeated uploads of one clip are ignored.
    /// </summary>
    public void AddClip(Guid id, RadioClipUpload clip)
    {
        ArgumentNullException.ThrowIfNull(clip);
        var now = _time.GetUtcNow();
        RadioPlayback.Validate(clip, out var audio);
        lock (_gate)
        {
            Prune();
            var room = Room(id);
            if (room.Clips.Any(x => x.Info.ClipId == clip.ClipId)) { return; }
            // An upload proves the worker is alive; the budget flag stays as last reported.
            room.Enabled = true; room.Heartbeat = now;
            room.Clips.Add(new(new(clip.ClipId, clip.BatchId, ++room.Sequence, clip.Line, clip.LineCount, clip.Host, clip.Priority, clip.Interruptible,
                now.AddSeconds(clip.LifetimeSeconds), clip.DurationMs), clip.Phases, clip.Conditions, clip.ContentType, audio));
            room.Clips.RemoveAll(x => x.Info.ExpiresAt <= now);
            while (room.Clips.Count > RadioPlayback.MaxClipsPerSession || room.Clips.Sum(x => (long)x.Audio.Length) > RadioPlayback.MaxSessionBytes)
            { room.Clips.RemoveAt(0); }
            while (_sessions.Values.Sum(x => x.Radio?.Clips.Sum(c => (long)c.Audio.Length) ?? 0) > _radioBytes)
            {
                var oldest = _sessions.Values.Where(x => x.Radio?.Clips.Count > 0).Select(x => x.Radio!).MinBy(x => x.Clips[0].Info.ExpiresAt)!;
                oldest.Clips.RemoveAt(0);
            }
        }
    }
    /// <summary>Whether the play control shows, and the clips still valid for the latest state, phase and time.</summary>
    public RadioAnnouncement Announce(Guid id)
    {
        lock (_gate)
        {
            Prune();
            var entry = _sessions.GetValueOrDefault(id);
            if (entry?.Radio is not { } room || entry.State is not { } state) { return new(false, []); }
            var now = _time.GetUtcNow();
            room.Clips.RemoveAll(x => x.Info.ExpiresAt <= now);
            var available = RadioPlayback.Available(room.Enabled, room.BudgetExhausted, room.Heartbeat, state, now);
            return new(available, available ? room.Clips.Where(x => RadioPlayback.Playable(state, now, x.Info.ExpiresAt, x.Phases, x.Conditions)).Select(x => x.Info).ToArray() : []);
        }
    }
    public (byte[] Audio, string ContentType)? ClipAudio(Guid id, Guid clipId)
    {
        lock (_gate)
        {
            Prune();
            var clip = _sessions.GetValueOrDefault(id)?.Radio?.Clips.FirstOrDefault(x => x.Info.ClipId == clipId);
            return clip is null || clip.Info.ExpiresAt <= _time.GetUtcNow() ? null : (clip.Audio, clip.ContentType);
        }
    }
    public IReadOnlyList<Guid> RadioSessions()
    {
        lock (_gate) { Prune(); return _sessions.Where(x => x.Value.Radio is not null).Select(x => x.Key).ToArray(); }
    }
    // Radio needs published state; a session the publisher has not restored after a restart answers 409 until it has.
    private RadioRoom Room(Guid id)
    {
        if (_sessions.GetValueOrDefault(id) is not { State: not null } entry) { throw new LiveConflictException(); }
        if (entry.Radio is { } room) { return room; }
        room = new RadioRoom();
        _sessions[id] = entry with { Radio = room };
        return room;
    }
    private void Prune()
    {
        var now = _time.GetUtcNow();
        foreach (var id in _sessions.Where(x => x.Value.Expires <= now).Select(x => x.Key).ToArray()) { _sessions.Remove(id); }
        foreach (var id in _deleted.Where(x => x.Value <= now).Select(x => x.Key).ToArray()) { _deleted.Remove(id); }
    }
}
public sealed class LiveCapacityException : Exception;
public sealed class LiveConflictException : Exception;
