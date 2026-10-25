using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace OpenSkiTime.LiveTiming.LoadTest;

/// <summary>One received State message: Version is -1 for an empty (deleted or not yet restored) state.</summary>
public readonly record struct StateSample(int Viewer, long Version, long ReceivedTicks, long UpdatedTicks, int Bytes, bool First);

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
/// A simulated browser on a race page. It follows wwwroot/live.js: it loads the page assets once, negotiates, opens the
/// WebSocket, sends the JSON handshake and Watch(session), pings every 15 s, drops a connection after 35 s without any
/// server message and reconnects after a jittered exponential backoff of 0.5–15 s.
/// </summary>
public sealed class Viewer(int index, Uri origin, Guid session, HttpClient http, LoadRecorder recorder, bool loadPage)
{
    private static readonly string[] s_assets = ["/live.css", "/live-order.js", "/live-columns.js", "/live-nations.js", "/live-summary.js", "/live-radio.js", "/live.js"];
    private static readonly TimeSpan s_serverTimeout = TimeSpan.FromSeconds(35), s_pingInterval = TimeSpan.FromSeconds(15);

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
                    using var message = JsonDocument.Parse(record);
                    var root = message.RootElement;
                    if (!handshaken)
                    {
                        if (root.TryGetProperty("error", out var error)) { attempt.Failure = "handshake " + error.GetString(); return false; }
                        handshaken = true;
                        await SendAsync(socket, sending, $"{{\"type\":1,\"target\":\"Watch\",\"arguments\":[\"{session}\"],\"invocationId\":\"1\"}}", stop);
                        attempt.WatchingTicks = DateTime.UtcNow.Ticks;
                        continue;
                    }
                    var type = root.TryGetProperty("type", out var t) ? t.GetInt32() : 0;
                    if (type == 7) { attempt.Failure ??= "close message " + (root.TryGetProperty("error", out var e) ? e.GetString() : ""); return true; }
                    if (type != 1 || root.GetProperty("target").GetString() != "State") { continue; }
                    var state = root.GetProperty("arguments")[0];
                    var first = attempt.FirstStateTicks is null;
                    if (first) { attempt.FirstStateTicks = received; }
                    recorder.State(state.ValueKind == JsonValueKind.Null
                        ? new(index, -1, received, 0, record.Length, first)
                        : new(index, state.GetProperty("version").GetInt64(), received, state.GetProperty("updatedAt").GetDateTimeOffset().UtcTicks, record.Length, first));
                }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { return true; }
        catch (Exception ex) { attempt.Failure ??= Describe(ex); return attempt.WatchingTicks is not null; }
        finally { await closed.CancelAsync(); }
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
