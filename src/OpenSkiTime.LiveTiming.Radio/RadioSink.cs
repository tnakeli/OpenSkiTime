using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using OpenSkiTime.LiveTiming.Radio.Providers;

namespace OpenSkiTime.LiveTiming.Radio;

/// <summary>A synthesized line. The transcript stays in this process (status and listening tests); it is never uploaded.</summary>
public sealed record RadioClip(Guid ClipId, Guid BatchId, int Line, int LineCount, string Host, int Priority, bool Interruptible,
    DateTimeOffset ExpiresAt, RacePhase[] Phases, RadioCondition[] Conditions, TtsAudio Audio, string Transcript);

public interface IRadioClipSink
{
    PublisherKind Channel { get; }
    /// <summary>Set when the session no longer accepts this worker (deleted, expired or rejected credential).</summary>
    string? Failure { get; }
    Task UploadAsync(RadioClip clip, CancellationToken ct);
    Task HeartbeatAsync(RadioStatusUpdate status, CancellationToken ct);
}

/// <summary>
/// Uploads clips to one Live Timing Server session with that session's publisher token. Same transport rules as the
/// publisher: HTTPS, or HTTP only on loopback; no redirects; the live protocol header on every request.
/// </summary>
public sealed class RadioServerSink : IRadioClipSink, IDisposable
{
    /// <summary>Pause after a failed music upload before the next heartbeat tries again.</summary>
    public static readonly TimeSpan MusicRetry = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan MusicTrackTimeout = TimeSpan.FromMinutes(2);
    private readonly HttpClient _http;
    private readonly RadioTarget _target;
    private readonly TimeProvider _time;
    private readonly Action<string> _log;
    private readonly CancellationTokenSource _stop = new();
    private RadioMusicLibrary _music = RadioMusicLibrary.Empty;
    private Task _musicSync = Task.CompletedTask;
    // UTC ticks; written by the background upload, read by the heartbeat.
    private long _musicRetryTicks;
    public RadioServerSink(RadioTarget target, TimeProvider? time = null, Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        _time = time ?? TimeProvider.System;
        _log = log ?? (_ => { });
        var uri = new Uri(target.Endpoint.TrimEnd('/') + "/");
        if (uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback))
        { throw new LiveValidationException("Radio clips require HTTPS (HTTP only on loopback)."); }
        _target = target;
        _http = new(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = uri, Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.Add(LiveProtocol.Header, LiveProtocol.Version.ToString(CultureInfo.InvariantCulture));
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", target.Token);
    }
    public PublisherKind Channel => _target.Channel;
    public string? Failure { get; private set; }
    private volatile bool _closed;
    // Status updates go out one at a time, so an enabled heartbeat already under way cannot land after the final disable.
    private readonly SemaphoreSlim _status = new(1, 1);
    /// <summary>No more uploads or enabled heartbeats; only the final disabled status may still be sent.</summary>
    public void Close() { _closed = true; _stop.Cancel(); }
    /// <summary>The music this target should hold; the next heartbeat uploads it when the server reports another playlist.</summary>
    public void SetMusic(RadioMusicLibrary music)
    {
        ArgumentNullException.ThrowIfNull(music);
        Volatile.Write(ref _music, music); Interlocked.Exchange(ref _musicRetryTicks, 0);
    }
    /// <summary>The music upload under way, for tests and an orderly shutdown.</summary>
    public Task MusicSync => _musicSync;
    public async Task UploadAsync(RadioClip clip, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(clip);
        if (_closed) { throw new RadioProviderException($"{_target.Channel} radio target was removed."); }
        var lifetime = (int)Math.Clamp(Math.Ceiling((clip.ExpiresAt - _time.GetUtcNow()).TotalSeconds), 1, RadioPlayback.MaxClipLifetime.TotalSeconds);
        var upload = new RadioClipUpload(clip.ClipId, clip.BatchId, clip.Line, clip.LineCount, clip.Host, clip.Priority, clip.Interruptible,
            lifetime, clip.Phases, clip.Conditions, clip.Audio.ContentType, clip.Audio.DurationMs, Convert.ToBase64String(clip.Audio.Data));
        using var response = await Send(t => _http.PostAsJsonAsync($"api/sessions/{_target.SessionId}/radio/clips", upload, LiveJson.Options, t), ct);
    }
    public async Task HeartbeatAsync(RadioStatusUpdate status, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(status);
        await _status.WaitAsync(ct);
        try
        {
            if (_closed && status.Enabled) { throw new RadioProviderException($"{_target.Channel} radio target was removed."); }
            using var response = await Send(t => _http.PutAsJsonAsync($"api/sessions/{_target.SessionId}/radio/status", status, LiveJson.Options, t), ct);
            if (status.Enabled && !_closed && await Reply(response, ct) is { } reply) { StartMusic(reply); }
        }
        finally { _status.Release(); }
    }
    private static async Task<RadioStatusReply?> Reply(HttpResponseMessage response, CancellationToken ct)
    {
        try { return await response.Content.ReadFromJsonAsync<RadioStatusReply>(LiveJson.Options, ct); }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or NotSupportedException) { return null; }
    }
    // Music goes up in the background, one track at a time, so clips and heartbeats never wait for it.
    private void StartMusic(RadioStatusReply reply)
    {
        var music = Volatile.Read(ref _music);
        if ((reply.Playlist == music.Id && reply.Missing.Length == 0) || !_musicSync.IsCompleted || _time.GetUtcNow().UtcTicks < Interlocked.Read(ref _musicRetryTicks)) { return; }
        _musicSync = Task.Run(() => UploadMusicAsync(music, _stop.Token));
    }
    private async Task UploadMusicAsync(RadioMusicLibrary music, CancellationToken ct)
    {
        try
        {
            string[] missing;
            // Bounded like every request: an unanswered playlist must not block later retries.
            using (var limit = new CancellationTokenSource(MusicTrackTimeout, _time))
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, limit.Token))
            {
                using var playlist = await Send(t => _http.PutAsJsonAsync($"api/sessions/{_target.SessionId}/radio/music", music.Playlist, LiveJson.Options, t), linked.Token);
                missing = (await Reply(playlist, linked.Token))?.Missing ?? [];
            }
            foreach (var track in music.Tracks.Where(x => missing.Contains(x.Id)))
            {
                using var limit = new CancellationTokenSource(MusicTrackTimeout, _time);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, limit.Token);
                using var content = new ByteArrayContent(track.Data);
                content.Headers.ContentType = new MediaTypeHeaderValue(RadioMusic.ContentType);
                using var uploaded = await Send(t => _http.PutAsync($"api/sessions/{_target.SessionId}/radio/music/{track.Id}", content, t), linked.Token);
            }
            _log(string.Create(CultureInfo.InvariantCulture, $"Radio music on {_target.Channel}: {music.Tracks.Count} tracks, {missing.Length} uploaded"));
        }
        catch (Exception ex) when (ex is RadioProviderException or OperationCanceledException or HttpRequestException or IOException)
        {
            if (ct.IsCancellationRequested) { return; }
            Interlocked.Exchange(ref _musicRetryTicks, (_time.GetUtcNow() + MusicRetry).UtcTicks);
            _log($"Radio music upload to {_target.Channel} failed: {(ex is RadioProviderException ? ex.Message : "no answer in time")}");
        }
    }
    private async Task<HttpResponseMessage> Send(Func<CancellationToken, Task<HttpResponseMessage>> send, CancellationToken ct)
    {
        HttpResponseMessage response;
        try { response = await send(ct); }
        catch (HttpRequestException) { throw new RadioProviderException($"{_target.Channel} live timing server could not be reached for radio."); }
        if (response.IsSuccessStatusCode) { Failure = null; return response; }
        var status = response.StatusCode;
        response.Dispose();
        // A session the publisher has not restored yet answers 404/409: retried on the next clip or heartbeat.
        if (status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.UpgradeRequired)
        { Failure = $"{_target.Channel} live timing server rejected the radio (HTTP {(int)status})."; }
        throw new RadioProviderException($"{_target.Channel} live timing server answered HTTP {(int)status} to radio.");
    }
    public void Dispose() { _stop.Cancel(); _http.Dispose(); _status.Dispose(); _stop.Dispose(); }
}
