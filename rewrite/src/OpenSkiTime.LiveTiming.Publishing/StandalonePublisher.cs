using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace OpenSkiTime.LiveTiming.Publishing;

public sealed class StandalonePublisher : IDisposable
{
    private readonly HttpClient _http;
    private LiveSnapshot? _sent;
    private readonly Action<string> _log;
    public LiveSession? Session { get; private set; }
    public StandalonePublisher(string endpoint, LiveSession? resumeSession = null, Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        _log = log ?? (_ => { });
        var uri = new Uri(endpoint.TrimEnd('/') + "/");
        if (uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback))
        { throw new LiveValidationException("Cloud publishing requires HTTPS (HTTP permitted only on loopback)."); }
        _http = new(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = uri, Timeout = TimeSpan.FromSeconds(5) };
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
            using var response = await _http.PostAsync("api/sessions", null, ct);
            _log($"Session creation HTTP {(int)response.StatusCode}");
            response.EnsureSuccessStatusCode();
            Session = await response.Content.ReadFromJsonAsync<LiveSession>(LiveJson.Options, ct) ?? throw new IOException("Missing session credential.");
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Session.PublisherToken);
            refresh = true;
        }
        // Every reconnect and metadata/order change is repaired with the latest full state, never an unbounded replay queue.
        var changes = _sent is null ? [] : Changes(_sent, snapshot);
        var structureChanged = _sent is null || _sent.Competition != snapshot.Competition || _sent.CurrentRun != snapshot.CurrentRun
            || snapshot.Runs.Any(r => _sent.Runs.FirstOrDefault(p => p.Number == r.Number)?.Results.Any(p => !r.Results.Any(x => x.Bib == p.Bib)) == true)
            || !_sent.Competitors.SequenceEqual(snapshot.Competitors) || _sent.Runs.Length != snapshot.Runs.Length
            || snapshot.Runs.Any(r => !_sent.Runs.Any(p => p.Number == r.Number && p.StartOrder.SequenceEqual(r.StartOrder)));
        if (refresh || structureChanged || changes.Length == 0)
        {
            using var response = await _http.PutAsJsonAsync($"api/sessions/{Session.SessionId}/state", snapshot, LiveJson.Options, ct);
            _log($"Full snapshot publish HTTP {(int)response.StatusCode}, version {snapshot.Version}");
            response.EnsureSuccessStatusCode();
        }
        else
        {
            var wireVersion = _sent!.Version;
            foreach (var change in changes)
            {
                // Wire revision is contiguous even when authoritative snapshots were coalesced by IPC.
                snapshot = snapshot with { Version = ++wireVersion };
                var update = new LiveEvent(snapshot.Version, change.Run, EventKind(change.Result), change.Result, snapshot.UpdatedAt);
                using var response = await _http.PostAsJsonAsync($"api/sessions/{Session.SessionId}/events", update, LiveJson.Options, ct);
                _log($"Event publish HTTP {(int)response.StatusCode}, version {snapshot.Version}");
                if (response.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.NotFound)
                {
                    using var restore = await _http.PutAsJsonAsync($"api/sessions/{Session.SessionId}/state", snapshot, LiveJson.Options, ct);
                    _log($"Snapshot resync HTTP {(int)restore.StatusCode}");
                    restore.EnsureSuccessStatusCode(); break;
                }
                response.EnsureSuccessStatusCode();
            }
        }
        _sent = snapshot;
    }
    public async Task HealthAsync(CancellationToken ct)
    {
        using var response = await _http.GetAsync(Session is null ? "health" : $"api/sessions/{Session.SessionId}/state", ct);
        _log($"Server health HTTP {(int)response.StatusCode}");
        response.EnsureSuccessStatusCode();
    }
    public async Task PauseAsync(CancellationToken ct)
    {
        if (Session is null) { return; }
        using var response = await _http.PostAsync($"api/sessions/{Session.SessionId}/pause", null, ct);
        _log($"Session pause HTTP {(int)response.StatusCode}");
        response.EnsureSuccessStatusCode();
    }
    public async Task DeleteAsync(bool allData, CancellationToken ct)
    {
        if (Session is null) { return; }
        using var response = await _http.DeleteAsync($"api/sessions/{Session.SessionId}" + (allData ? "/data" : ""), ct);
        _log($"Session deletion HTTP {(int)response.StatusCode}");
        response.EnsureSuccessStatusCode();
        Session = null; _sent = null; _http.DefaultRequestHeaders.Authorization = null;
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
