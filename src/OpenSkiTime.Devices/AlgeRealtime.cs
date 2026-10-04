using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace OpenSkiTime.Devices;

public enum AlgeRealtimeFrameKind { Message, Heartbeat }

// One frame from the realtime channel. Body is the original STOMP MESSAGE body bytes (the trigger JSON).
public sealed record AlgeRealtimeFrame(AlgeRealtimeFrameKind Kind, string Destination = "", byte[]? Body = null);

// Realtime trigger push as documented by ALGE Results: STOMP over SockJS at https://www.alge-results.com/devices.
// Implementations must keep one pending receive across timeouts: cancelling a WebSocket receive aborts the socket.
public interface IAlgeRealtimeConnection : IAsyncDisposable
{
    Task ConnectAsync(string token, IReadOnlyList<string> destinations, CancellationToken ct);
    // Returns the next frame, or null when nothing arrived within the timeout. Throws when the connection closed or failed.
    Task<AlgeRealtimeFrame?> ReceiveAsync(TimeSpan timeout, CancellationToken ct);
    Task SendHeartbeatAsync(CancellationToken ct);
}

public sealed class AlgeRealtimeException(string message) : IOException(message);

// SockJS raw WebSocket transport (wss://…/devices/{server}/{session}/websocket) carrying STOMP 1.2 frames.
public sealed class SockJsStompConnection(Uri? endpoint = null) : IAlgeRealtimeConnection
{
    private readonly Uri _endpoint = endpoint ?? new Uri("wss://www.alge-results.com/devices/");
    private readonly ClientWebSocket _socket = new();
    private readonly byte[] _buffer = new byte[65536];
    private readonly StringBuilder _partial = new();
    private readonly Queue<AlgeRealtimeFrame> _ready = new();
    private readonly SemaphoreSlim _send = new(1, 1);
    private Task<WebSocketReceiveResult>? _pending;
    private const int MaxMessageCharacters = 4_000_000;

    public async Task ConnectAsync(string token, IReadOnlyList<string> destinations, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(token); ArgumentNullException.ThrowIfNull(destinations);
        var server = Random.Shared.Next(1000).ToString("000", CultureInfo.InvariantCulture);
        var session = Guid.NewGuid().ToString("N")[..12];
        _socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        await _socket.ConnectAsync(new Uri(_endpoint, $"{server}/{session}/websocket"), ct);
        var open = await ReceiveSockJsAsync(TimeSpan.FromSeconds(10), ct);
        if (open != "o") { throw new AlgeRealtimeException("ALGE Results realtime did not open the SockJS session."); }
        // The token is sent only inside the encrypted connection and never logged or journalled.
        await SendStompAsync($"CONNECT\naccept-version:1.2\nheart-beat:10000,10000\nauthorization:{token}\n\n\0", ct);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        var connected = false;
        while (!connected)
        {
            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero) { throw new AlgeRealtimeException("ALGE Results realtime did not confirm the STOMP connection."); }
            var text = await ReceiveSockJsAsync(remaining, ct) ?? throw new AlgeRealtimeException("ALGE Results realtime did not confirm the STOMP connection.");
            foreach (var frame in StompFrames(text))
            {
                if (frame.StartsWith("CONNECTED", StringComparison.Ordinal)) { connected = true; break; }
                if (frame.StartsWith("ERROR", StringComparison.Ordinal)) { throw new AlgeRealtimeException("ALGE Results realtime rejected the connection."); }
            }
        }
        for (var i = 0; i < destinations.Count; i++)
        { await SendStompAsync($"SUBSCRIBE\nid:sub-{i}\ndestination:{destinations[i]}\nack:auto\n\n\0", ct); }
    }

    public async Task<AlgeRealtimeFrame?> ReceiveAsync(TimeSpan timeout, CancellationToken ct)
    {
        if (_ready.Count != 0) { return _ready.Dequeue(); }
        var text = await ReceiveSockJsAsync(timeout, ct);
        if (text is null) { return null; }
        if (text == "h") { return new(AlgeRealtimeFrameKind.Heartbeat); }
        if (text.StartsWith('c')) { throw new AlgeRealtimeException("ALGE Results realtime closed the session."); }
        foreach (var frame in StompFrames(text))
        {
            if (frame.Trim('\n', '\r').Length == 0) { _ready.Enqueue(new(AlgeRealtimeFrameKind.Heartbeat)); continue; }
            var split = frame.IndexOf("\n\n", StringComparison.Ordinal);
            var head = split < 0 ? frame : frame[..split];
            var lines = head.Split('\n');
            if (lines[0] == "ERROR") { throw new AlgeRealtimeException("ALGE Results realtime reported an error and closed the subscription."); }
            if (lines[0] != "MESSAGE") { _ready.Enqueue(new(AlgeRealtimeFrameKind.Heartbeat)); continue; }
            var destination = lines.FirstOrDefault(x => x.StartsWith("destination:", StringComparison.Ordinal))?["destination:".Length..] ?? "";
            var body = split < 0 ? "" : frame[(split + 2)..].TrimEnd('\0');
            _ready.Enqueue(new(AlgeRealtimeFrameKind.Message, destination, Encoding.UTF8.GetBytes(body)));
        }
        return _ready.Count == 0 ? new(AlgeRealtimeFrameKind.Heartbeat) : _ready.Dequeue();
    }

    public Task SendHeartbeatAsync(CancellationToken ct) => SendStompAsync("\n", ct);

    private async Task SendStompAsync(string frame, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new[] { frame }));
        await _send.WaitAsync(ct);
        try { await _socket.SendAsync(bytes, WebSocketMessageType.Text, true, ct); }
        finally { _send.Release(); }
    }

    // One pending receive survives timeouts; only the wait is bounded.
    private async Task<string?> ReceiveSockJsAsync(TimeSpan timeout, CancellationToken ct)
    {
        while (true)
        {
            _pending ??= _socket.ReceiveAsync(new ArraySegment<byte>(_buffer), CancellationToken.None);
            var finished = await Task.WhenAny(_pending, Task.Delay(timeout, ct));
            ct.ThrowIfCancellationRequested();
            if (finished != _pending) { return null; }
            var result = await _pending;
            _pending = null;
            if (result.MessageType == WebSocketMessageType.Close) { throw new AlgeRealtimeException("ALGE Results realtime connection closed."); }
            _partial.Append(Encoding.UTF8.GetString(_buffer, 0, result.Count));
            if (_partial.Length > MaxMessageCharacters) { throw new AlgeRealtimeException("ALGE Results realtime message exceeds the size limit."); }
            if (!result.EndOfMessage) { continue; }
            var text = _partial.ToString(); _partial.Clear();
            return text;
        }
    }

    private static string[] StompFrames(string sockJs)
    {
        if (!sockJs.StartsWith('a')) { return []; }
        return JsonSerializer.Deserialize<string[]>(sockJs.AsSpan(1)) ?? [];
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_socket.State == WebSocketState.Open)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", timeout.Token);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException) { }
        _socket.Dispose();
        _send.Dispose();
    }
}
