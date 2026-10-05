using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace OpenSkiTime.LiveTiming.Publishing;

public sealed class StandalonePublisher : IDisposable
{
    private readonly HttpClient _http;
    private LiveSnapshot? _sent;
    private readonly Action<string> _log;
    public LiveSession? Session { get; private set; }
    private readonly string? _publisherKey;
    private readonly TimeSpan _requestTimeout;
    private readonly TimeSpan _wakeTimeout;
    private bool _answered;
    /// <summary>Request timeout for a server that has answered and for the managed loopback server.</summary>
    public static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(5);
    /// <summary>Cloud request timeout once the server answers: allows ingress latency and a full snapshot on a mobile uplink.</summary>
    public static readonly TimeSpan CloudRequestTimeout = TimeSpan.FromSeconds(15);
    /// <summary>Cloud timeout until the server answers: a scale-to-zero cold start (image pull, container start) takes up to minutes.</summary>
    public static readonly TimeSpan CloudWakeTimeout = TimeSpan.FromMinutes(2);
    /// <summary>
    /// True while the next request waits for a server that has not answered yet (first contact or after a failure) and
    /// may get the longer wake timeout. Status displays use it to report a waking server instead of an error.
    /// </summary>
    public bool Waking => !_answered && _wakeTimeout > _requestTimeout;
    public StandalonePublisher(string endpoint, LiveSession? resumeSession = null, Action<string>? log = null, string? publisherKey = null,
        TimeSpan? requestTimeout = null, TimeSpan? wakeTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        _log = log ?? (_ => { });
        _publisherKey = publisherKey;
        _requestTimeout = requestTimeout ?? DefaultRequestTimeout;
        _wakeTimeout = wakeTimeout is { } wake && wake > _requestTimeout ? wake : _requestTimeout;
        var uri = new Uri(endpoint.TrimEnd('/') + "/");
        if (uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback))
        { throw new LiveValidationException("Cloud publishing requires HTTPS (HTTP permitted only on loopback)."); }
        _http = new(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = uri, Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.Add(LiveProtocol.Header, LiveProtocol.Version.ToString(CultureInfo.InvariantCulture));
        if (resumeSession is not null && resumeSession.ExpiresAt > DateTimeOffset.UtcNow)
        {
            Session = resumeSession;
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", resumeSession.PublisherToken);
        }
    }
    public async Task PublishAsync(LiveSnapshot snapshot, bool refresh, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        snapshot.Validate();
        if (Session?.ExpiresAt <= DateTimeOffset.UtcNow)
        { Session = null; _sent = null; _http.DefaultRequestHeaders.Authorization = null; }
        if (Session is null)
        {
            // The publisher key authorizes only session creation; later requests use the session's own token.
            using var creation = new HttpRequestMessage(HttpMethod.Post, "api/sessions");
            if (!string.IsNullOrEmpty(_publisherKey)) { creation.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _publisherKey); }
            using var response = await SendAsync(token => _http.SendAsync(creation, token), ct);
            _log($"Session creation HTTP {(int)response.StatusCode}");
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                throw new LiveValidationException(string.IsNullOrEmpty(_publisherKey)
                    ? "This live timing server requires a publisher key. Save the key in Settings → Live timing."
                    : "The live timing server rejected the publisher key. Check the key in Settings → Live timing.");
            }
            Ensure(response);
            LiveSession? created;
            // A captive portal or proxy can answer 200 with a page instead of a session: retry like any network failure.
            try { created = await response.Content.ReadFromJsonAsync<LiveSession>(LiveJson.Options, ct); }
            catch (JsonException) { throw new IOException("Invalid session response."); }
            if (created is null || created.SessionId == Guid.Empty || string.IsNullOrWhiteSpace(created.PublisherToken)
                || created.PublisherToken.Any(char.IsControl))
            { throw new IOException("Missing session credential."); }
            Session = created;
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Session.PublisherToken);
            refresh = true;
        }
        // Every reconnect and metadata/order change is repaired with the latest full state, never an unbounded replay queue.
        var changes = _sent is null ? [] : Changes(_sent, snapshot);
        var structureChanged = _sent is null || _sent.Competition != snapshot.Competition || _sent.CurrentRun != snapshot.CurrentRun
            || snapshot.Runs.Any(r => _sent.Runs.FirstOrDefault(p => p.Number == r.Number)?.Results.Any(p => !r.Results.Any(x => x.Bib == p.Bib)) == true)
            || !_sent.Competitors.SequenceEqual(snapshot.Competitors) || _sent.Runs.Length != snapshot.Runs.Length
            || snapshot.Runs.Any(r => !_sent.Runs.Any(p => p.Number == r.Number && p.StartOrder.SequenceEqual(r.StartOrder)));
        // One changed row is an event. Several (a new leader moves every rank and gap) replace the state at once: viewers
        // never see a half-applied ranking, and one request replaces a request and a broadcast per row.
        if (refresh || structureChanged || changes.Length != 1)
        {
            using var response = await SendAsync(token => _http.PutAsJsonAsync($"api/sessions/{Session.SessionId}/state", snapshot, LiveJson.Options, token), ct);
            _log($"Full snapshot publish HTTP {(int)response.StatusCode}, version {snapshot.Version}");
            Ensure(response);
        }
        else
        {
            var wireVersion = _sent!.Version;
            foreach (var change in changes)
            {
                // Wire revision is contiguous even when authoritative snapshots were coalesced by IPC.
                snapshot = snapshot with { Version = ++wireVersion };
                var update = new LiveEvent(snapshot.Version, change.Run, EventKind(change.Result), change.Result, snapshot.UpdatedAt);
                using var response = await SendAsync(token => _http.PostAsJsonAsync($"api/sessions/{Session.SessionId}/events", update, LiveJson.Options, token), ct);
                _log($"Event publish HTTP {(int)response.StatusCode}, version {snapshot.Version}");
                if (response.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.NotFound)
                {
                    using var restore = await SendAsync(token => _http.PutAsJsonAsync($"api/sessions/{Session.SessionId}/state", snapshot, LiveJson.Options, token), ct);
                    _log($"Snapshot resync HTTP {(int)restore.StatusCode}");
                    Ensure(restore); break;
                }
                Ensure(response);
            }
        }
        _sent = snapshot;
    }
    /// <summary>Returns false when the server no longer holds this session's state and needs the full snapshot again.</summary>
    public async Task<bool> HealthAsync(CancellationToken ct)
    {
        var path = Session is null ? "health" : $"api/sessions/{Session.SessionId}/state";
        using var response = await SendAsync(token => _http.GetAsync(path, token), ct);
        _log($"Server health HTTP {(int)response.StatusCode}");
        // RAM state is lost on every restart, new revision or scale-to-zero while the stateless session token stays valid.
        if (Session is not null && response.StatusCode == HttpStatusCode.NotFound) { return false; }
        Ensure(response);
        return true;
    }
    public async Task PauseAsync(CancellationToken ct)
    {
        if (Session is null) { return; }
        var path = $"api/sessions/{Session.SessionId}/pause";
        using var response = await SendAsync(token => _http.PostAsync(path, null, token), ct);
        _log($"Session pause HTTP {(int)response.StatusCode}");
        Ensure(response);
    }
    public async Task DeleteAsync(bool allData, CancellationToken ct)
    {
        if (Session is null) { return; }
        var path = $"api/sessions/{Session.SessionId}" + (allData ? "/data" : "");
        using var response = await SendAsync(token => _http.DeleteAsync(path, token), ct);
        _log($"Session deletion HTTP {(int)response.StatusCode}");
        Ensure(response);
        Session = null; _sent = null; _http.DefaultRequestHeaders.Authorization = null;
    }
    // Every request gets the short timeout once the server has answered, and the wake timeout before that. A gateway
    // status (the ingress answering while no replica runs yet) or a failure returns to waking: after an outage the server
    // may have scaled to zero again. Running out of time is a network failure the worker retries, not cancellation.
    private async Task<HttpResponseMessage> SendAsync(Func<CancellationToken, Task<HttpResponseMessage>> send, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_answered ? _requestTimeout : _wakeTimeout);
        try
        {
            var response = await send(timeout.Token);
            _answered = response.StatusCode is not (HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout);
            return response;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { _answered = false; throw new IOException("The live timing server did not answer in time."); }
        catch (HttpRequestException) { _answered = false; throw; }
    }
    private static void Ensure(HttpResponseMessage response)
    {
        // 426 is permanent until either side is updated; the message is OpenSkiTime's own, never server text.
        if (response.StatusCode == HttpStatusCode.UpgradeRequired)
        { throw new LiveValidationException("The live timing server uses an incompatible protocol version. Update OpenSkiTime or choose a compatible server."); }
        response.EnsureSuccessStatusCode();
    }
    private static (int Run, LiveResult Result)[] Changes(LiveSnapshot before, LiveSnapshot after) =>
        after.Runs.SelectMany(run => run.Results.Where(r =>
        {
            var old = before.Runs.FirstOrDefault(x => x.Number == run.Number)?.Results.FirstOrDefault(x => x.Bib == r.Bib);
            return old is null || !Equivalent(old, r);
        }).Select(r => (run.Number, r))).ToArray();
    public static bool Equivalent(LiveResult a, LiveResult b)
    {
        ArgumentNullException.ThrowIfNull(a); ArgumentNullException.ThrowIfNull(b);
        return a with { Intermediates = null } == b with { Intermediates = null } && a.Splits.SequenceEqual(b.Splits);
    }
    private static LiveEventKind EventKind(LiveResult r) => r.Status switch
    {
        LiveStatus.DNS => LiveEventKind.DNS, LiveStatus.DNF => LiveEventKind.DNF, LiveStatus.DSQ => LiveEventKind.DSQ,
        LiveStatus.Finished => LiveEventKind.CompetitorFinished,
        LiveStatus.OnCourse when r.Splits.Length > 0 => LiveEventKind.IntermediateTime,
        LiveStatus.OnCourse => LiveEventKind.CompetitorStarted, _ => LiveEventKind.ResultUpdated
    };
    public void Dispose() => _http.Dispose();
}
