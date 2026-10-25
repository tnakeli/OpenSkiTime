using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenSkiTime.LiveTiming.LoadTest;

/// <summary>
/// One received hub message. Kind is the SignalR target (State, Sync, Delta, Beacon, Radio, Viewers) or Other. Version is
/// the publisher's version of a message that carries race data (State, a Sync with state, a Delta), otherwise -1. First
/// marks the reply to a subscription or resync, which is not a broadcast of an update.
/// </summary>
public readonly record struct MessageSample(int Viewer, string Kind, long Version, long ReceivedTicks, long UpdatedTicks, int Bytes, bool First);

/// <summary>One connection of a viewer, from negotiation to its drop. A class, not a record: it changes while it is a
/// member of a set, so it must keep reference equality.</summary>
public sealed class ConnectionAttempt(int viewer, long startedTicks)
{
    public int Viewer { get; } = viewer;
    public long StartedTicks { get; } = startedTicks;
    public long? WatchingTicks { get; set; }
    public long? FirstStateTicks { get; set; }
    public long? EndedTicks { get; set; }
    public string? Failure { get; set; }
}

/// <summary>
/// A simulated browser on a race page. It follows wwwroot/live.js and live-sync.js: it loads the page assets once,
/// negotiates, opens the WebSocket, sends the JSON handshake and WatchSync(session, 1) (Watch(session) with legacy: true,
/// as pages opened before viewer synchronization do), pings every 15 s, drops a connection after 35 s without any server
/// message and reconnects after a jittered exponential backoff of 0.5–15 s. It applies deltas with the browser's rules:
/// only to the revision they name in the same generation, duplicates ignored, a gap or unknown generation asks for the
/// complete state (Resync) after 0.5–5 s and drops deltas until it arrives, a disagreeing beacon does the same. Final is
/// the state it shows when the run ends.
/// </summary>
public sealed class Viewer(int index, Uri origin, Guid session, HttpClient http, LoadRecorder recorder, bool loadPage, bool legacy)
{
    private static readonly string[] s_assets = ["/live.css", "/live-order.js", "/live-columns.js", "/live-nations.js", "/live-summary.js",
        "/live-filter.js", "/live-favorites.js", "/live-radio.js", "/live-sync.js", "/live.js"];
    private static readonly TimeSpan s_serverTimeout = TimeSpan.FromSeconds(35), s_pingInterval = TimeSpan.FromSeconds(15), s_resyncTimeout = TimeSpan.FromSeconds(30);
    // Shown state: phase is subscribing, live, awaiting or deleted, as in live-sync.js. A legacy page keeps the raw state.
    private string _phase = "subscribing";
    private string? _generation;
    private long _revision;
    private JsonObject? _state;
    private byte[]? _legacyState;
    // UTC ticks of the pending resync request, 0 when none; read by the delayed request task.
    private long _resyncSince;

    /// <summary>The state this viewer shows (null when none), as JSON.</summary>
    public JsonNode? Final => legacy ? (_legacyState is null ? null : JsonNode.Parse(_legacyState)) : _state;
    /// <summary>The page assets a viewer loads: the race page's stylesheet and scripts.</summary>
    public static IReadOnlyList<string> Assets => s_assets;

    public async Task RunAsync(CancellationToken stop)
    {
        if (loadPage) { await LoadPageAsync(stop); }
        var retries = 0;
        while (!stop.IsCancellationRequested)
        {
            var attempt = recorder.Begin(index);
            var watching = await ConnectAsync(attempt, stop);
            recorder.End(attempt);
            if (stop.IsCancellationRequested) { return; }
            if (watching) { retries = 0; }
            var delay = Math.Min(15000, 1000 * Math.Pow(2, retries++)) * (0.5 + Random.Shared.NextDouble() / 2);
            try { await Task.Delay(TimeSpan.FromMilliseconds(delay), stop); }
            catch (OperationCanceledException) { return; }
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exceptions", Justification = "Every failure of one simulated viewer is recorded; the run goes on.")]
    private async Task LoadPageAsync(CancellationToken stop)
    {
        var started = DateTime.UtcNow.Ticks;
        try
        {
            foreach (var path in s_assets.Prepend("/r/" + session))
            {
                using var response = await http.GetAsync(new Uri(origin, path), stop);
                await response.Content.CopyToAsync(Stream.Null, stop);
                if (!response.IsSuccessStatusCode) { recorder.PageFailed($"{path} {(int)response.StatusCode}"); return; }
            }
            recorder.PageLoaded(DateTime.UtcNow.Ticks - started);
        }
        catch (Exception ex) when (!stop.IsCancellationRequested) { recorder.PageFailed(ex.GetType().Name); }
        catch (OperationCanceledException) { }
    }

    // True when the connection reached Watch, so that the next retry starts the backoff from its beginning.
    [SuppressMessage("Design", "CA1031:Do not catch general exceptions", Justification = "Every failure of one simulated viewer is recorded; the run goes on.")]
    private async Task<bool> ConnectAsync(ConnectionAttempt attempt, CancellationToken stop)
    {
        using var socket = new ClientWebSocket();
        using var sending = new SemaphoreSlim(1, 1);
        using var closed = CancellationTokenSource.CreateLinkedTokenSource(stop);
        var heard = DateTime.UtcNow;
        // A new connection shows the previous state until its subscription reply; nothing else applies before it.
        _phase = "subscribing"; _generation = null; _revision = 0; Volatile.Write(ref _resyncSince, 0);
        try
        {
            string token;
            using (var negotiate = await http.PostAsync(new Uri(origin, "/live/negotiate?negotiateVersion=1"), null, stop))
            {
                if (!negotiate.IsSuccessStatusCode) { attempt.Failure = "negotiate " + (int)negotiate.StatusCode; return false; }
                using var info = JsonDocument.Parse(await negotiate.Content.ReadAsStreamAsync(stop));
                token = info.RootElement.GetProperty("connectionToken").GetString()!;
            }
            var url = new UriBuilder(new Uri(origin, "/live")) { Scheme = origin.Scheme == "https" ? "wss" : "ws", Query = "id=" + Uri.EscapeDataString(token) };
            await socket.ConnectAsync(url.Uri, http, stop);
            await SendAsync(socket, sending, "{\"protocol\":\"json\",\"version\":1}", stop);
            // The browser's keep-alive loop: a silent server ends the connection, otherwise a ping every 15 s.
            var pinged = DateTime.UtcNow;
            _ = Task.Run(async () =>
            {
                using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
                try
                {
                    while (await timer.WaitForNextTickAsync(closed.Token))
                    {
                        if (DateTime.UtcNow - heard > s_serverTimeout) { attempt.Failure ??= "server silent 35 s"; socket.Abort(); return; }
                        if (attempt.WatchingTicks is not null && DateTime.UtcNow - pinged >= s_pingInterval)
                        { pinged = DateTime.UtcNow; await SendAsync(socket, sending, "{\"type\":6}", closed.Token); }
                    }
                }
                catch (Exception) { /* The connection is closing; the receive loop records why. */ }
            }, CancellationToken.None);

            var buffer = new byte[16 * 1024];
            var pending = new ArrayBufferWriter<byte>(64 * 1024);
            var handshaken = false;
            while (true)
            {
                var read = await socket.ReceiveAsync(buffer, stop);
                if (read.MessageType == WebSocketMessageType.Close) { attempt.Failure ??= "closed by server " + socket.CloseStatus; return handshaken; }
                heard = DateTime.UtcNow;
                var received = heard.Ticks;
                pending.Write(buffer.AsSpan(0, read.Count));
                while (pending.WrittenSpan.IndexOf((byte)0x1e) is var end and >= 0)
                {
                    var record = pending.WrittenSpan[..end].ToArray();
                    var rest = pending.WrittenSpan[(end + 1)..].ToArray();
                    pending.Clear(); pending.Write(rest);
                    if (record.Length == 0) { continue; }
                    if (!handshaken)
                    {
                        using var reply = JsonDocument.Parse(record);
                        if (reply.RootElement.TryGetProperty("error", out var error)) { attempt.Failure = "handshake " + error.GetString(); return false; }
                        handshaken = true;
                        await SendAsync(socket, sending, legacy
                            ? $"{{\"type\":1,\"target\":\"Watch\",\"arguments\":[\"{session}\"],\"invocationId\":\"1\"}}"
                            : $"{{\"type\":1,\"target\":\"WatchSync\",\"arguments\":[\"{session}\",1],\"invocationId\":\"1\"}}", stop);
                        attempt.WatchingTicks = DateTime.UtcNow.Ticks;
                        continue;
                    }
                    var (resync, close) = Receive(attempt, record, received);
                    if (close) { return true; }
                    if (resync != 0) { _ = ResyncAsync(socket, sending, resync, closed.Token); }
                }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { return true; }
        catch (Exception ex) { attempt.Failure ??= Describe(ex); return attempt.WatchingTicks is not null; }
        finally { await closed.CancelAsync(); }
    }

    // Records one message and applies it. Returns the ticks of a resync request to send (0 for none) and whether the server
    // closed the connection.
    private (long Resync, bool Close) Receive(ConnectionAttempt attempt, byte[] record, long received)
    {
        using var message = JsonDocument.Parse(record);
        var root = message.RootElement;
        var type = root.TryGetProperty("type", out var t) ? t.GetInt32() : 0;
        if (type == 7) { attempt.Failure ??= "close message " + (root.TryGetProperty("error", out var e) ? e.GetString() : ""); return (0, true); }
        var target = type == 1 ? root.GetProperty("target").GetString()! : "Other";
        var argument = type == 1 ? root.GetProperty("arguments")[0] : default;
        var kind = target is "State" or "Sync" or "Delta" or "Beacon" or "Radio" or "Viewers" ? target : "Other";
        var version = -1L; var updated = 0L; var first = false; var resync = 0L;
        switch (kind)
        {
            case "State":
                first = attempt.FirstStateTicks is null;
                if (argument.ValueKind == JsonValueKind.Object)
                {
                    (version, updated) = (argument.GetProperty("version").GetInt64(), argument.GetProperty("updatedAt").GetDateTimeOffset().UtcTicks);
                    _legacyState = Encoding.UTF8.GetBytes(argument.GetRawText()); _phase = "live";
                }
                else if (_phase == "subscribing" && _legacyState is not null) { _phase = "awaiting"; }
                else { _legacyState = null; _phase = "deleted"; }
                break;
            case "Sync":
                // A subscription or resync reply is not an update broadcast; a full state broadcast is.
                first = attempt.FirstStateTicks is null || Volatile.Read(ref _resyncSince) != 0;
                var status = argument.GetProperty("status").GetString();
                if (status == "Live")
                {
                    var state = argument.GetProperty("state");
                    (version, updated) = (state.GetProperty("version").GetInt64(), state.GetProperty("updatedAt").GetDateTimeOffset().UtcTicks);
                    (_phase, _generation, _revision, _state) = ("live", argument.GetProperty("generation").GetString(), argument.GetProperty("revision").GetInt64(), JsonNode.Parse(state.GetRawText())!.AsObject());
                }
                else if (status == "Deleted") { (_phase, _generation, _revision, _state) = ("deleted", null, 0, null); }
                else { (_phase, _generation, _revision) = ("awaiting", null, 0); }
                Volatile.Write(ref _resyncSince, 0);
                break;
            case "Delta":
                (version, updated) = (argument.GetProperty("version").GetInt64(), argument.GetProperty("updatedAt").GetDateTimeOffset().UtcTicks);
                if (_phase == "subscribing" || Volatile.Read(ref _resyncSince) != 0) { break; }
                var revision = argument.GetProperty("revision").GetInt64();
                if (_phase != "live" || argument.GetProperty("generation").GetString() != _generation) { resync = Gap(); break; }
                if (revision <= _revision) { break; }
                if (argument.GetProperty("baseRevision").GetInt64() != _revision || !Merge(_state!, JsonNode.Parse(argument.GetRawText())!.AsObject())) { resync = Gap(); break; }
                _revision = revision;
                break;
            case "Beacon":
                if (_phase == "subscribing") { break; }
                if (Volatile.Read(ref _resyncSince) is var since and not 0)
                { if (DateTime.UtcNow.Ticks - since >= s_resyncTimeout.Ticks) { resync = Gap(); } break; }
                var said = argument.GetProperty("status").GetString();
                var agrees = said == "Live" ? _phase == "live" && argument.GetProperty("generation").GetString() == _generation && argument.GetProperty("revision").GetInt64() == _revision
                    : said == "Deleted" ? _phase == "deleted" : _phase == "awaiting";
                if (!agrees) { resync = Gap(); }
                break;
        }
        if (kind is "State" or "Sync" && attempt.FirstStateTicks is null) { attempt.FirstStateTicks = received; }
        recorder.Message(new(index, kind, version, received, updated, record.Length, first));
        return (resync, false);
    }

    private long Gap() { recorder.Gap(); var now = DateTime.UtcNow.Ticks; Volatile.Write(ref _resyncSince, now); return now; }

    // Asks for the complete state after 0.5–5 s, unless it arrived meanwhile or the connection closed.
    [SuppressMessage("Design", "CA1031:Do not catch general exceptions", Justification = "A failed request on a closing connection is retried by the next beacon.")]
    private async Task ResyncAsync(ClientWebSocket socket, SemaphoreSlim sending, long requested, CancellationToken closed)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(500 + Random.Shared.NextDouble() * 4500), closed);
            if (Volatile.Read(ref _resyncSince) != requested) { return; }
            recorder.Resync();
            await SendAsync(socket, sending, "{\"type\":1,\"target\":\"Resync\",\"arguments\":[]}", closed);
        }
        catch (Exception) { /* The connection is closing; a new one subscribes again. */ }
    }

    /// <summary>
    /// Applies a delta to the state as a whole, like liveSyncMerge: listed rows replace the row with their bib or are new
    /// (appended unless an order is given), listed forerunners replace the run's. False, with the state unchanged, when the
    /// delta does not fit.
    /// </summary>
    public static bool Merge(JsonObject state, JsonObject delta)
    {
        ArgumentNullException.ThrowIfNull(state); ArgumentNullException.ThrowIfNull(delta);
        var runs = state["runs"]!.AsArray().Select(x => x!.AsObject()).ToArray();
        var plans = new List<(JsonObject Run, JsonArray Changed, JsonNode[] Results, JsonNode? Forerunners)>();
        foreach (var change in delta["runs"]!.AsArray().Select(x => x!.AsObject()))
        {
            var number = change["number"]!.GetValue<int>();
            if (runs.FirstOrDefault(x => x["number"]!.GetValue<int>() == number) is not { } run) { return false; }
            var rows = new Dictionary<int, JsonNode>(); var order = new List<int>();
            foreach (var row in run["results"]!.AsArray()) { var bib = row!["bib"]!.GetValue<int>(); rows[bib] = row; order.Add(bib); }
            var changed = change["results"]!.AsArray();
            foreach (var row in changed) { var bib = row!["bib"]!.GetValue<int>(); if (!rows.ContainsKey(bib)) { order.Add(bib); } rows[bib] = row; }
            if (change["order"] is JsonArray given) { order = given.Select(x => x!.GetValue<int>()).ToList(); }
            if (order.Count != rows.Count || order.Distinct().Count() != order.Count || order.Any(x => !rows.ContainsKey(x))) { return false; }
            plans.Add((run, changed, order.Select(x => rows[x]).ToArray(), change["forerunners"]));
        }
        foreach (var (run, changed, results, forerunners) in plans)
        {
            // A node has one parent: detach the rows from both arrays before building the new one.
            run["results"]!.AsArray().Clear(); changed.Clear();
            run["results"] = new JsonArray(results);
            if (forerunners is not null) { forerunners.Parent!.AsObject().Remove("forerunners"); run["forerunners"] = forerunners; }
        }
        state["version"] = delta["version"]!.DeepClone();
        state["updatedAt"] = delta["updatedAt"]!.DeepClone();
        state["paused"] = delta["paused"]!.DeepClone();
        return true;
    }

    private static async Task SendAsync(ClientWebSocket socket, SemaphoreSlim sending, string json, CancellationToken stop)
    {
        await sending.WaitAsync(stop);
        try { await socket.SendAsync(Encoding.UTF8.GetBytes(json + "\u001e"), WebSocketMessageType.Text, true, stop); }
        finally { sending.Release(); }
    }

    private static string Describe(Exception ex) => ex switch
    {
        WebSocketException { InnerException: HttpRequestException inner } => "websocket " + (inner.StatusCode is { } code ? (int)code : inner.HttpRequestError.ToString()),
        WebSocketException socket => "websocket " + socket.WebSocketErrorCode,
        HttpRequestException http => "http " + (http.StatusCode is { } code ? ((int)code).ToString(System.Globalization.CultureInfo.InvariantCulture) : http.HttpRequestError.ToString()),
        _ => ex.GetType().Name,
    };
}
