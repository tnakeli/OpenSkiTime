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
    private readonly HttpClient _http;
    private readonly RadioTarget _target;
    private readonly TimeProvider _time;
    public RadioServerSink(RadioTarget target, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        _time = time ?? TimeProvider.System;
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
    public async Task UploadAsync(RadioClip clip, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(clip);
        var lifetime = (int)Math.Clamp(Math.Ceiling((clip.ExpiresAt - _time.GetUtcNow()).TotalSeconds), 1, RadioPlayback.MaxClipLifetime.TotalSeconds);
        var upload = new RadioClipUpload(clip.ClipId, clip.BatchId, clip.Line, clip.LineCount, clip.Host, clip.Priority, clip.Interruptible,
            lifetime, clip.Phases, clip.Conditions, clip.Audio.ContentType, clip.Audio.DurationMs, Convert.ToBase64String(clip.Audio.Data));
        using var response = await Send(t => _http.PostAsJsonAsync($"api/sessions/{_target.SessionId}/radio/clips", upload, LiveJson.Options, t), ct);
    }
    public async Task HeartbeatAsync(RadioStatusUpdate status, CancellationToken ct)
    {
        using var response = await Send(t => _http.PutAsJsonAsync($"api/sessions/{_target.SessionId}/radio/status", status, LiveJson.Options, t), ct);
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
    public void Dispose() => _http.Dispose();
}
