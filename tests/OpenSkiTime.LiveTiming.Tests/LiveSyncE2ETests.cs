using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using OpenSkiTime.LiveTiming.Harness;
using Xunit;

namespace OpenSkiTime.LiveTiming.Tests;

// Viewer synchronization against the real server process: revision order per connection under concurrent publishes and
// subscriptions, deltas and full states, beacons, resync, session statuses across deletion and restart, and older pages.
[Collection("Live timing processes")]
public sealed class LiveSyncE2ETests
{
    private static readonly Dictionary<string, string> s_fastBeacon = new() { ["LiveTiming__SyncBeaconSeconds"] = "1" };

    [Fact]
    public async Task EveryViewerConvergesInRevisionOrderUnderConcurrentPublishesAndSubscriptions()
    {
        await using var server = new ServerProcess(environment: s_fastBeacon); await server.Start();
        using var http = new HttpClient { BaseAddress = new(server.Endpoint), Timeout = TimeSpan.FromSeconds(10) };
        var session = await Create(server, http);
        var states = SyntheticRace.Simulate(SyntheticRace.Create(40)).ToArray();
        (await http.PutAsJsonAsync($"api/sessions/{session.SessionId}/state", SyntheticRace.Create(40), LiveJson.Options)).EnsureSuccessStatusCode();
        var viewers = new List<HubViewer>();
        try
        {
            for (var i = 0; i < 3; i++) { viewers.Add(await HubViewer.ConnectAsync(server.Endpoint)); await viewers[^1].WatchAsync(session.SessionId, sync: true); }
            var legacy = await HubViewer.ConnectAsync(server.Endpoint); viewers.Add(legacy); await legacy.WatchAsync(session.SessionId, sync: false);
            // Six publishers replace the state at once while more viewers subscribe: a broadcast must never overtake an
            // earlier one, and no subscription reply may come after a newer broadcast.
            var joining = Task.Run(async () =>
            {
                var joined = new List<HubViewer>();
                for (var i = 0; i < 8; i++) { await Task.Delay(Random.Shared.Next(5, 40)); var v = await HubViewer.ConnectAsync(server.Endpoint); joined.Add(v); await v.WatchAsync(session.SessionId, sync: true); }
                return joined;
            });
            await Task.WhenAll(Enumerable.Range(0, 6).Select(worker => Task.Run(async () =>
            {
                for (var i = worker; i < states.Length; i += 6)
                { using var response = await http.PutAsJsonAsync($"api/sessions/{session.SessionId}/state", states[i], LiveJson.Options); response.EnsureSuccessStatusCode(); }
            })));
            viewers.AddRange(await joining);
            // Then the normal flow: contiguous events, a full replacement that re-ranks every finisher, and a pause.
            var state = await Read(http, session);
            foreach (var bib in new[] { 3, 5 })
            {
                var next = SyntheticRace.Update(state, bib, LiveStatus.DSQ) with { Version = state.Version + 1 };
                using var response = await http.PostAsJsonAsync($"api/sessions/{session.SessionId}/events",
                    new LiveEvent(next.Version, 1, LiveEventKind.DSQ, next.Runs[0].Results.Single(x => x.Bib == bib), next.UpdatedAt), LiveJson.Options);
                response.EnsureSuccessStatusCode();
                state = await Read(http, session);
            }
            var leader = SyntheticRace.Update(SyntheticRace.Update(state, 34, LiveStatus.OnCourse), 34, LiveStatus.Finished, 4000);
            (await http.PutAsJsonAsync($"api/sessions/{session.SessionId}/state", leader, LiveJson.Options)).EnsureSuccessStatusCode();
            (await http.PostAsync($"api/sessions/{session.SessionId}/pause", null)).EnsureSuccessStatusCode();
            var done = DateTime.UtcNow;
            var authoritative = JsonSerializer.Serialize(await Read(http, session), LiveJson.Options);
            // Every synchronizing viewer gets beacons after the last change; the legacy page gets the paused state.
            // The second beacon received after the last change was certainly sent after it.
            foreach (var viewer in viewers.Where(x => x != legacy))
            { await viewer.WaitAsync(x => x.Target == "Beacon" && x.At > done && viewer.Messages.Count(m => m.Target == "Beacon" && m.At > done) >= 2); }
            await legacy.WaitAsync(x => x.Target == "State" && x.Argument.GetProperty("paused").GetBoolean());
            var deltas = 0;
            foreach (var viewer in viewers.Where(x => x != legacy))
            {
                var (final, received) = Converge(viewer.Messages);
                Assert.Equal(authoritative, JsonSerializer.Serialize(final, LiveJson.Options));
                deltas += received;
            }
            Assert.True(deltas > 0, "no delta was sent");
            // An older page gets full states only, the last one equal to the published state.
            Assert.All(legacy.Messages, x => Assert.True(x.Target is not ("Sync" or "Delta" or "Beacon"), x.Target));
            var last = legacy.Messages.Last(x => x.Target == "State").Argument.Deserialize<LiveSnapshot>(LiveJson.Options)!;
            Assert.Equal(authoritative, JsonSerializer.Serialize(last, LiveJson.Options));
        }
        finally { foreach (var viewer in viewers) { await viewer.DisposeAsync(); } }
    }

    [Fact]
    public async Task SubscriptionsTellLiveAwaitingAndDeletedAcrossDeletionRestartAndRestore()
    {
        await using var server = new ServerProcess(environment: s_fastBeacon); await server.Start();
        using var http = new HttpClient { BaseAddress = new(server.Endpoint), Timeout = TimeSpan.FromSeconds(10) };
        var published = await Create(server, http); var unpublished = await Create(server, http);
        var state = SyntheticRace.Create(6);
        Authorize(http, published);
        (await http.PutAsJsonAsync($"api/sessions/{published.SessionId}/state", state, LiveJson.Options)).EnsureSuccessStatusCode();
        await using (var unknown = await HubViewer.ConnectAsync(server.Endpoint))
        {
            // An unknown session and a created but unpublished one are awaiting, also in their beacons.
            await unknown.WatchAsync(Guid.NewGuid(), sync: true);
            Assert.Equal("Awaiting", (await unknown.WaitAsync(x => x.Target == "Sync")).Argument.GetProperty("status").GetString());
            var beacon = (await unknown.WaitAsync(x => x.Target == "Beacon")).Argument;
            Assert.Equal(("Awaiting", JsonValueKind.Null, 0), (beacon.GetProperty("status").GetString(), beacon.GetProperty("generation").ValueKind, beacon.GetProperty("revision").GetInt32()));
            var mark = unknown.Messages.Count;
            await unknown.WatchAsync(unpublished.SessionId, sync: true);
            Assert.Equal("Awaiting", (await unknown.WaitAsync(x => x.Index >= mark && x.Target == "Sync")).Argument.GetProperty("status").GetString());
            // A viewer that declares a sync version the server does not know is refused, so it can subscribe as an older page.
            await unknown.SendAsync("WatchSync", published.SessionId.ToString(), 99);
            Assert.Contains("Unsupported sync version", (await unknown.WaitAsync(x => x.Target == "Completion")).Argument.GetString(), StringComparison.Ordinal);
        }
        var first = await HubViewer.ConnectAsync(server.Endpoint);
        var legacy = await HubViewer.ConnectAsync(server.Endpoint);
        try
        {
            await first.WatchAsync(published.SessionId, sync: true); await legacy.WatchAsync(published.SessionId, sync: false);
            var live = (await first.WaitAsync(x => x.Target == "Sync")).Argument.Deserialize<LiveSyncState>(LiveJson.Options)!;
            Assert.Equal((LiveSessionStatus.Live, 1L), (live.Status, live.Revision));
            Assert.Equal(JsonSerializer.Serialize(state, LiveJson.Options), JsonSerializer.Serialize(live.State, LiveJson.Options));
            Assert.NotNull(await legacy.WaitAsync(x => x.Target == "State" && x.Argument.ValueKind == JsonValueKind.Object));
            // A resync replies with the complete state; a second request within two seconds is ignored.
            var mark = first.Messages.Count;
            await first.SendAsync("Resync"); await first.SendAsync("Resync");
            var reply = (await first.WaitAsync(x => x.Index >= mark && x.Target == "Sync")).Argument.Deserialize<LiveSyncState>(LiveJson.Options)!;
            Assert.Equal((live.Generation, 1L), (reply.Generation, reply.Revision));
            await Task.Delay(1500);
            Assert.Single(first.Messages, x => x.Index >= mark && x.Target == "Sync");
            await Task.Delay(1000);
            await first.SendAsync("Resync");
            await first.WaitAsync(x => x.Target == "Sync" && first.Messages.Count(m => m.Index >= mark && m.Target == "Sync") == 2);
            // Deletion reaches both kinds of pages, and a page that subscribes later learns it too.
            (await http.DeleteAsync($"api/sessions/{published.SessionId}")).EnsureSuccessStatusCode();
            Assert.Equal("Deleted", (await first.WaitAsync(x => x.Target == "Sync" && x.Argument.GetProperty("status").GetString() == "Deleted")).Argument.GetProperty("status").GetString());
            await legacy.WaitAsync(x => x.Target == "State" && x.Argument.ValueKind == JsonValueKind.Null);
            await using (var late = await HubViewer.ConnectAsync(server.Endpoint))
            {
                await late.WatchAsync(published.SessionId, sync: true);
                Assert.Equal("Deleted", (await late.WaitAsync(x => x.Target == "Sync")).Argument.GetProperty("status").GetString());
                Assert.Equal("Deleted", (await late.WaitAsync(x => x.Target == "Beacon")).Argument.GetProperty("status").GetString());
            }
        }
        finally { await first.DisposeAsync(); await legacy.DisposeAsync(); }
        // A restarted server holds nothing in RAM: it cannot tell a deleted session from one not yet restored.
        Authorize(http, unpublished);
        (await http.PutAsJsonAsync($"api/sessions/{unpublished.SessionId}/state", state, LiveJson.Options)).EnsureSuccessStatusCode();
        await using (var before = await HubViewer.ConnectAsync(server.Endpoint))
        {
            await before.WatchAsync(unpublished.SessionId, sync: true);
            var generation = (await before.WaitAsync(x => x.Target == "Sync")).Argument.GetProperty("generation").GetString();
            await server.Kill(); await server.Start();
            await using var after = await HubViewer.ConnectAsync(server.Endpoint);
            await after.WatchAsync(unpublished.SessionId, sync: true);
            Assert.Equal("Awaiting", (await after.WaitAsync(x => x.Target == "Sync")).Argument.GetProperty("status").GetString());
            await using var deleted = await HubViewer.ConnectAsync(server.Endpoint);
            await deleted.WatchAsync(published.SessionId, sync: true);
            Assert.Equal("Awaiting", (await deleted.WaitAsync(x => x.Target == "Sync")).Argument.GetProperty("status").GetString());
            // The publisher restores the state: the waiting viewer gets it as a broadcast of a new generation.
            (await http.PutAsJsonAsync($"api/sessions/{unpublished.SessionId}/state", state, LiveJson.Options)).EnsureSuccessStatusCode();
            var restored = (await after.WaitAsync(x => x.Target == "Sync" && x.Argument.GetProperty("status").GetString() == "Live")).Argument;
            Assert.Equal(1, restored.GetProperty("revision").GetInt64());
            Assert.NotEqual(generation, restored.GetProperty("generation").GetString());
            var beacon = (await after.WaitAsync(x => x.Target == "Beacon" && x.Argument.GetProperty("status").GetString() == "Live")).Argument;
            Assert.Equal(restored.GetProperty("generation").GetString(), beacon.GetProperty("generation").GetString());
        }
    }

    // Applies a connection's messages with the browser's rules and checks the order the server promises: the first state
    // message is the subscription reply, every delta follows the revision before it, every full state is the next revision
    // of its generation (or a new generation), and a beacon always names the revision received last.
    private static (LiveSnapshot State, int Deltas) Converge(IReadOnlyList<HubMessage> messages)
    {
        LiveSnapshot? state = null; string? generation = null; long revision = 0; var deltas = 0;
        foreach (var message in messages)
        {
            switch (message.Target)
            {
                case "Sync":
                    var full = message.Argument.Deserialize<LiveSyncState>(LiveJson.Options)!;
                    Assert.Equal(LiveSessionStatus.Live, full.Status);
                    if (state is not null && full.Generation == generation) { Assert.Equal(revision + 1, full.Revision); }
                    (state, generation, revision) = (full.State, full.Generation, full.Revision);
                    break;
                case "Delta":
                    var delta = message.Argument.Deserialize<LiveDelta>(LiveJson.Options)!;
                    Assert.NotNull(state);
                    Assert.Equal((generation, revision, revision + 1), (delta.Generation, delta.BaseRevision, delta.Revision));
                    state = LiveSync.Apply(state, delta); Assert.NotNull(state);
                    revision = delta.Revision; deltas++;
                    break;
                case "Beacon":
                    Assert.NotNull(state);
                    var beacon = message.Argument.Deserialize<LiveSyncBeacon>(LiveJson.Options)!;
                    Assert.Equal((LiveSessionStatus.Live, generation, revision), (beacon.Status, beacon.Generation, beacon.Revision));
                    break;
            }
        }
        return (state!, deltas);
    }
    private static async Task<LiveSession> Create(ServerProcess server, HttpClient http)
    {
        using var request = server.CreateSession(); using var response = await http.SendAsync(request); response.EnsureSuccessStatusCode();
        var session = (await response.Content.ReadFromJsonAsync<LiveSession>(LiveJson.Options))!;
        Authorize(http, session);
        return session;
    }
    private static void Authorize(HttpClient http, LiveSession session) => http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.PublisherToken);
    private static async Task<LiveSnapshot> Read(HttpClient http, LiveSession session) => (await http.GetFromJsonAsync<LiveSnapshot>($"api/sessions/{session.SessionId}/state", LiveJson.Options))!;
}

/// <summary>One received hub message: the target and its first argument (a completion's error text as "Completion").</summary>
internal sealed record HubMessage(int Index, string Target, JsonElement Argument, DateTime At);

/// <summary>A viewer connection speaking the SignalR JSON protocol like wwwroot/live.js, recording every message it receives.</summary>
internal sealed class HubViewer : IAsyncDisposable
{
    private readonly ClientWebSocket _socket = new();
    private readonly SemaphoreSlim _sending = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly List<HubMessage> _messages = [];
    private readonly Lock _gate = new();
    private Task _receiving = Task.CompletedTask, _pinging = Task.CompletedTask;

    public IReadOnlyList<HubMessage> Messages { get { lock (_gate) { return [.. _messages]; } } }

    public static async Task<HubViewer> ConnectAsync(string endpoint)
    {
        var viewer = new HubViewer();
        using (var http = new HttpClient { BaseAddress = new(endpoint) })
        using (var response = await http.PostAsync("live/negotiate?negotiateVersion=1", null))
        {
            response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var uri = new UriBuilder(new Uri(new Uri(endpoint), "live")) { Scheme = "ws", Query = "id=" + json.RootElement.GetProperty("connectionToken").GetString() };
            await viewer._socket.ConnectAsync(uri.Uri, CancellationToken.None);
        }
        await viewer.SendRawAsync("{\"protocol\":\"json\",\"version\":1}");
        viewer._receiving = viewer.ReceiveAsync();
        // The server closes a connection that sends nothing for 30 s; the page pings every 15 s.
        viewer._pinging = Task.Run(async () =>
        {
            try { while (true) { await Task.Delay(10000, viewer._stop.Token); await viewer.SendRawAsync("{\"type\":6}"); } }
            catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or ObjectDisposedException) { }
        });
        return viewer;
    }
    public Task WatchAsync(Guid id, bool sync) => sync ? SendAsync("WatchSync", id.ToString(), LiveSync.Version) : SendAsync("Watch", id.ToString());
    public Task SendAsync(string target, params object[] arguments)
        => SendRawAsync(JsonSerializer.Serialize(new { type = 1, target, arguments, invocationId = "1" }));
    private async Task SendRawAsync(string json)
    {
        await _sending.WaitAsync();
        try { await _socket.SendAsync(Encoding.UTF8.GetBytes(json + "\u001e"), WebSocketMessageType.Text, true, CancellationToken.None); }
        finally { _sending.Release(); }
    }
    /// <summary>Waits for the first message (in arrival order) that matches.</summary>
    public async Task<HubMessage> WaitAsync(Func<HubMessage, bool> match, int seconds = 15)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        while (true)
        {
            if (Messages.FirstOrDefault(match) is { } found) { return found; }
            if (_receiving.IsCompleted) { throw new IOException("Connection closed before the expected message."); }
            await Task.Delay(20, timeout.Token);
        }
    }
    private async Task ReceiveAsync()
    {
        var buffer = new byte[64 * 1024]; var pending = new StringBuilder(); var handshaken = false;
        try
        {
            while (true)
            {
                var read = await _socket.ReceiveAsync(buffer, _stop.Token);
                if (read.MessageType == WebSocketMessageType.Close) { return; }
                pending.Append(Encoding.UTF8.GetString(buffer, 0, read.Count));
                var text = pending.ToString(); var end = text.LastIndexOf('\u001e');
                if (end < 0) { continue; }
                pending.Clear().Append(text[(end + 1)..]);
                foreach (var record in text[..end].Split('\u001e', StringSplitOptions.RemoveEmptyEntries))
                {
                    using var json = JsonDocument.Parse(record);
                    var root = json.RootElement;
                    if (!handshaken) { handshaken = true; continue; }
                    var type = root.TryGetProperty("type", out var t) ? t.GetInt32() : 0;
                    var message = type switch
                    {
                        1 => (root.GetProperty("target").GetString()!, root.GetProperty("arguments")[0].Clone()),
                        3 when root.TryGetProperty("error", out var error) => ("Completion", error.Clone()),
                        _ => ((string, JsonElement)?)null,
                    };
                    if (message is { } m) { lock (_gate) { _messages.Add(new(_messages.Count, m.Item1, m.Item2, DateTime.UtcNow)); } }
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException) { }
    }
    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        try { if (_socket.State == WebSocketState.Open) { await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None); } }
        catch (WebSocketException) { }
        await Task.WhenAll(_receiving, _pinging);
        _socket.Dispose(); _sending.Dispose(); _stop.Dispose();
    }
}
