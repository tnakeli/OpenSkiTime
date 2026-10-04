using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using OpenSkiTime.Application;

namespace OpenSkiTime.Devices;

// Intervals for the realtime connection and its REST safety net. Tests supply shorter values.
public sealed record AlgeResultsTimings(TimeSpan ReconcileInterval, TimeSpan HeartbeatInterval, TimeSpan SilenceLimit,
    TimeSpan PollInterval, TimeSpan FirstReconnectDelay, TimeSpan MaxReconnectDelay)
{
    public static AlgeResultsTimings Default { get; } = new(TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30),
        TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(60));
}

// Read-only integration: login and GETs only. Never modify events, devices, bibs or pulses on ALGE Results.
// Triggers arrive by realtime push (STOMP over SockJS, per-device topics). A REST count/history check runs right after
// every (re)subscription and periodically, so a trigger the push missed is still recovered; duplicates are recognized by
// the decoder's semantic fingerprint. While push is unavailable, REST polling keeps timing flowing and push is retried
// with growing, jittered delays to protect the service.
public sealed class AlgeResultsSource(HttpClient client, string username, string password, CaptureOptions options,
    Func<IAlgeRealtimeConnection>? realtime = null, AlgeResultsTimings? timings = null) : ITimingSource
{
    private string? _token;
    private static readonly Uri s_root = new("https://alge-results.com/");
    private readonly Func<IAlgeRealtimeConnection> _realtime = realtime ?? (() => new SockJsStompConnection());
    private readonly AlgeResultsTimings _timings = timings ?? AlgeResultsTimings.Default;
    private readonly Dictionary<string, long> _counts = new(StringComparer.Ordinal);
    private (string? Device, int Channel)[] _endpoints = [];
    private long _from;
    private int _reconciles;
    private readonly Dictionary<string, int> _refused = new(StringComparer.Ordinal);
    private Action<string>? _status;
    private string _baseStatus = "";

    public async Task ReceiveAsync(Func<TransportPacket, ValueTask> receive, Action<string> status, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(receive); ArgumentNullException.ThrowIfNull(status);
        _status = status;
        // Every routed role (start, finish, intermediates) is its own device/channel endpoint.
        _endpoints = (options.Routes is { } routes
            ? routes.Select(x => (Device: x.DeviceId, x.Channel))
            : [(Device: options.StartDeviceId, Channel: options.StartChannel), (Device: options.FinishDeviceId, Channel: options.FinishChannel)])
            .Distinct().ToArray();
        if (_endpoints.Any(x => string.IsNullOrWhiteSpace(x.Device) || !x.Device.All(char.IsAsciiDigit)))
        { throw new IOException("Enter the MT1 device ID for every ALGE Results timing role."); }
        _from = (options.FromUtc ?? DateTimeOffset.UtcNow).ToUnixTimeMilliseconds();
        var topics = _endpoints.Select(x => x.Device!).Distinct(StringComparer.Ordinal).Select(x => $"/topic/device/{x}/trigger").ToArray();
        var reconnectDelay = _timings.FirstReconnectDelay;
        var wasConnected = false;
        while (!ct.IsCancellationRequested)
        {
            IAlgeRealtimeConnection? connection = null;
            try
            {
                _token ??= await LoginAsync(client, username, password, ct);
                status("Connecting · ALGE Results realtime");
                connection = _realtime();
                using (var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    connectTimeout.CancelAfter(TimeSpan.FromSeconds(20));
                    await connection.ConnectAsync(_token, topics, connectTimeout.Token);
                }
                // Subscribed first, then reconciled: a trigger between the history read and the push cannot be missed.
                await ReconcileAsync(receive, forceHistory: true, ct);
                wasConnected = true; reconnectDelay = _timings.FirstReconnectDelay;
                _baseStatus = "Connected · ALGE Results realtime push · REST check every "
                    + _timings.ReconcileInterval.TotalSeconds.ToString("0", CultureInfo.InvariantCulture) + " s";
                status(_baseStatus + RefusedSummary);
                await ListenAsync(connection, receive, ct);
            }
            catch (AlgeAuthenticationException)
            { _token = null; throw new IOException("ALGE Results sign-in expired or was rejected. Check the account/password and reconnect."); }
            catch (AlgeQuotaException)
            {
                status("ALGE Results rate limit · retrying in 90 seconds; history will be recovered");
                await Task.Delay(TimeSpan.FromSeconds(90), ct);
            }
            catch (Exception ex) when (IsTransient(ex, ct))
            {
                if (wasConnected)
                {
                    await receive(new("transport-status", "ALGE Results", "cloud", Encoding.UTF8.GetBytes(
                        "ALGE Results realtime interrupted. Missed triggers are recovered from history; check recovered observations before finalizing this run.")));
                    wasConnected = false;
                }
                var wait = reconnectDelay + TimeSpan.FromMilliseconds(Random.Shared.Next((int)Math.Max(1, reconnectDelay.TotalMilliseconds / 5)));
                status($"ALGE Results realtime unavailable · REST polling every {_timings.PollInterval.TotalSeconds:0} s · reconnecting in {wait.TotalSeconds:0} s");
                await PollAsync(receive, DateTimeOffset.UtcNow + wait, ct);
                reconnectDelay = TimeSpan.FromTicks(Math.Min(_timings.MaxReconnectDelay.Ticks, reconnectDelay.Ticks * 2));
            }
            finally
            {
                if (connection is not null)
                {
                    try { await connection.DisposeAsync(); }
                    catch (Exception ex) when (ex is WebSocketException or IOException or ObjectDisposedException) { }
                }
            }
        }
        ct.ThrowIfCancellationRequested();
    }

    private static bool IsTransient(Exception ex, CancellationToken ct) => ex is AlgeRealtimeException or WebSocketException
        or HttpRequestException or JsonException || (ex is OperationCanceledException && !ct.IsCancellationRequested);

    private async Task ListenAsync(IAlgeRealtimeConnection connection, Func<TransportPacket, ValueTask> receive, CancellationToken ct)
    {
        var lastServer = DateTimeOffset.UtcNow; var lastHeartbeat = lastServer;
        var nextReconcile = lastServer + _timings.ReconcileInterval;
        var wait = TimeSpan.FromTicks(Math.Min(TimeSpan.TicksPerSecond, _timings.HeartbeatInterval.Ticks));
        while (!ct.IsCancellationRequested)
        {
            var now = DateTimeOffset.UtcNow;
            if (now - lastHeartbeat >= _timings.HeartbeatInterval) { await connection.SendHeartbeatAsync(ct); lastHeartbeat = now; }
            var frame = await connection.ReceiveAsync(wait, ct);
            now = DateTimeOffset.UtcNow;
            if (frame is not null)
            {
                lastServer = now;
                if (frame.Kind == AlgeRealtimeFrameKind.Message && frame.Body is { } body) { await EmitPushAsync(body, receive); }
            }
            else if (now - lastServer > _timings.SilenceLimit)
            { throw new AlgeRealtimeException("No data or heartbeat from ALGE Results realtime."); }
            if (now >= nextReconcile)
            {
                // Count check per endpoint; history only on change, plus a periodic full check for server-side edits.
                await ReconcileAsync(receive, forceHistory: ++_reconciles % 10 == 0, ct);
                nextReconcile = DateTimeOffset.UtcNow + _timings.ReconcileInterval;
            }
        }
    }

    // Push bodies are journalled as received, for every channel of a subscribed device. Channels no role uses are
    // decoded as information only (shown in the Settings signal monitor, never timed).
    private static async Task EmitPushAsync(byte[] body, Func<TransportPacket, ValueTask> receive)
    {
        string? device = null; string? change = null;
        try
        {
            using var json = JsonDocument.Parse(body);
            var root = json.RootElement;
            change = root.TryGetProperty("type", out var type) ? type.GetString() : null;
            var dto = root.TryGetProperty("dto", out var inner) ? inner : root;
            device = dto.TryGetProperty("deviceId", out var id) ? id.GetString() : null;
        }
        catch (JsonException) { /* malformed input is still journalled for review */ }
        await receive(new("alge-results/v1", device ?? "ALGE Results", "push", body));
        if (change == "ENTITY_DELETE")
        {
            await receive(new("transport-status", device ?? "ALGE Results", "push", Encoding.UTF8.GetBytes(
                "A trigger was deleted on ALGE Results. The original record is retained; review the affected assignment.")));
        }
    }

    // REST safety net while push is unavailable: keeps capture flowing until the next reconnect attempt.
    private async Task PollAsync(Func<TransportPacket, ValueTask> receive, DateTimeOffset until, CancellationToken ct)
    {
        var reported = false;
        while (DateTimeOffset.UtcNow < until)
        {
            // Sign-in happens only in the outer loop, with backoff: repeated logins count heavily against the API quota.
            if (_token is null) { await Task.Delay(_timings.PollInterval, ct); continue; }
            try { await ReconcileAsync(receive, forceHistory: false, ct); reported = false; }
            catch (AlgeAuthenticationException)
            { _token = null; throw new IOException("ALGE Results sign-in expired or was rejected. Check the account/password and reconnect."); }
            catch (AlgeQuotaException)
            { await Task.Delay(TimeSpan.FromSeconds(90), ct); }
            catch (Exception ex) when (ex is HttpRequestException or JsonException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
            {
                if (!reported)
                {
                    await receive(new("transport-status", "ALGE Results", "cloud", Encoding.UTF8.GetBytes("Network interruption. Check recovered observations before finalizing this run.")));
                    reported = true;
                }
            }
            await Task.Delay(_timings.PollInterval, ct);
        }
    }

    // Count per endpoint; when it changed (or forced), read history from the capture bound with a fixed upper bound so
    // newly arriving triggers cannot shift offset pagination. A device channel that ALGE Results refuses (for example a
    // device not registered to this account) is reported once by name and skipped; the other roles keep working, and the
    // refused one is retried with the periodic full check.
    private async Task ReconcileAsync(Func<TransportPacket, ValueTask> receive, bool forceHistory, CancellationToken ct)
    {
        _token ??= await LoginAsync(client, username, password, ct);
        foreach (var endpoint in _endpoints)
        {
            var name = $"{endpoint.Device} C{endpoint.Channel}";
            if (_refused.ContainsKey(name) && !forceHistory) { continue; }
            try
            {
                await ReconcileEndpointAsync(endpoint, receive, forceHistory, ct);
                if (_refused.Remove(name))
                {
                    await receive(new("transport-status", endpoint.Device!, "cloud", Encoding.UTF8.GetBytes($"ALGE Results now accepts device {name}. Its history was recovered.")));
                    if (_baseStatus.Length != 0) { _status?.Invoke(_baseStatus + RefusedSummary); }
                }
            }
            catch (AlgeStatusException ex)
            {
                if (_refused.TryAdd(name, ex.Code))
                {
                    await receive(new("transport-status", endpoint.Device!, "cloud", Encoding.UTF8.GetBytes(
                        $"ALGE Results refused device {name} (status {ex.Code.ToString(CultureInfo.InvariantCulture)}). Check that the device is registered to this account and the channel is correct. Other roles continue.")));
                    if (_baseStatus.Length != 0) { _status?.Invoke(_baseStatus + RefusedSummary); }
                }
            }
        }
    }

    // Refused device channels for the status line, e.g. "231203037 C3 refused (-2011)".
    private string RefusedSummary => _refused.Count == 0 ? ""
        : " · " + string.Join(", ", _refused.Select(x => $"{x.Key} refused ({x.Value.ToString(CultureInfo.InvariantCulture)})"));

    private async Task ReconcileEndpointAsync((string? Device, int Channel) endpoint, Func<TransportPacket, ValueTask> receive, bool forceHistory, CancellationToken ct)
    {
        var prefix = $"mt1/api/devices/{endpoint.Device}/channel/{endpoint.Channel}/trigger";
        var filter = "timestampFrom_ms=" + _from.ToString(CultureInfo.InvariantCulture);
        var countBytes = await GetAsync(prefix + "/count?" + filter, ct);
        using var countDoc = JsonDocument.Parse(countBytes);
        var count = countDoc.RootElement.GetProperty("data")[0].GetProperty("value").GetInt64();
        var known = _counts.TryGetValue(prefix, out var old);
        if (known && count < old)
        { await receive(new("transport-status", endpoint.Device!, "cloud", Encoding.UTF8.GetBytes("ALGE Results history count decreased. Review server/device changes; local raw records were retained."))); }
        if (known && old == count && !forceHistory) { return; }
        var until = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var offset = 0;
        while (true)
        {
            var query = string.Create(CultureInfo.InvariantCulture, $"{prefix}?{filter}&timestampTo_ms={until}&limit=200&offset={offset}");
            var bytes = await GetAsync(query, ct, page => receive(new("alge-results/v1", endpoint.Device!, "cloud", page)));
            using var data = JsonDocument.Parse(bytes);
            var length = data.RootElement.GetProperty("data").GetArrayLength();
            if (length < 200) { break; }
            offset += length;
            if (offset > 100000) { throw new IOException("MT1 history is too large. Choose a later receive-from time."); }
        }
        _counts[prefix] = count;
    }

    public static async Task<string> LoginAsync(HttpClient client, string username, string password, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        using var response = await client.PostAsJsonAsync(new Uri(s_root, "mt1/api/user/login"), new { username, password }, ct);
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized) { throw new AlgeAuthenticationException(); }
        response.EnsureSuccessStatusCode();
        using var body = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(ct));
        CheckStatus(body.RootElement);
        var user = body.RootElement.GetProperty("data")[0];
        if (!user.GetProperty("roles").EnumerateArray().Any(x => x.GetString() == "TIMING_POINT_ACCOUNT"))
        { throw new IOException("Enable Timekeeper in your ALGE Results account before connecting."); }
        if (!response.Headers.TryGetValues("authorization", out var values) || string.IsNullOrWhiteSpace(values.FirstOrDefault()))
        { throw new AlgeAuthenticationException(); }
        return values.First();
    }


    // Lists the MT1 devices associated with an ALGE Results account, for choosing device IDs in the UI.
    // One login and one GET; nothing is stored.
    public static async Task<IReadOnlyList<AlgeResultsDevice>> ListDevicesAsync(HttpClient client, string username, string password, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        try
        {
            using var response = await client.PostAsJsonAsync(new Uri(s_root, "mt1/api/user/login"), new { username, password }, ct);
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized) { throw new AlgeAuthenticationException(); }
            response.EnsureSuccessStatusCode();
            using var body = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(ct));
            CheckStatus(body.RootElement);
            var userId = body.RootElement.GetProperty("data")[0].GetProperty("id").GetString();
            if (!response.Headers.TryGetValues("authorization", out var values) || string.IsNullOrWhiteSpace(values.FirstOrDefault()) || string.IsNullOrWhiteSpace(userId))
            { throw new AlgeAuthenticationException(); }
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(s_root, $"mt1/api/user/{Uri.EscapeDataString(userId)}/devices"));
            request.Headers.Add("authorization", values.First());
            using var devices = await client.SendAsync(request, ct);
            var bytes = await ReadResponseAsync(devices, null, ct);
            using var json = JsonDocument.Parse(bytes);
            return json.RootElement.GetProperty("data").EnumerateArray()
                .Select(x => new AlgeResultsDevice(x.GetProperty("id").GetString() ?? "", FindName(x), x.TryGetProperty("type", out var t) ? t.GetString() ?? "" : ""))
                .Where(x => x.Id.Length != 0).OrderBy(x => x.Id, StringComparer.Ordinal).ToArray();
        }
        catch (AlgeAuthenticationException) { throw new IOException("ALGE Results sign-in was rejected. Check the username and password."); }
        catch (AlgeQuotaException) { throw new IOException("ALGE Results rate limit reached. Wait a minute and try again."); }
    }

    // The device name is not part of the documented DeviceDto shape; use one if the components contain it.
    private static string FindName(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.Name is "name" or "deviceName" && property.Value.ValueKind == JsonValueKind.String) { return property.Value.GetString() ?? ""; }
                if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array && FindName(property.Value) is { Length: > 0 } nested) { return nested; }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        { foreach (var item in element.EnumerateArray()) { if (FindName(item) is { Length: > 0 } nested) { return nested; } } }
        return "";
    }

    private async Task<byte[]> GetAsync(string relative, CancellationToken ct, Func<byte[], ValueTask>? capture = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(s_root, relative));
        request.Headers.Add("authorization", _token);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
        {
            // Reauthorize once on expiry; never repeatedly log in on a failed account.
            _token = await LoginAsync(client, username, password, ct);
            using var retry = new HttpRequestMessage(HttpMethod.Get, new Uri(s_root, relative));
            retry.Headers.Add("authorization", _token);
            using var retried = await client.SendAsync(retry, HttpCompletionOption.ResponseHeadersRead, ct);
            if (retried.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized) { throw new AlgeAuthenticationException(); }
            return await ReadResponseAsync(retried, capture, ct);
        }
        return await ReadResponseAsync(response, capture, ct);
    }

    private static async Task<byte[]> ReadResponseAsync(HttpResponseMessage response, Func<byte[], ValueTask>? capture, CancellationToken ct)
    {
        if ((int)response.StatusCode == 429) { throw new AlgeQuotaException(); }
        response.EnsureSuccessStatusCode();
        await using var input = await response.Content.ReadAsStreamAsync(ct);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await input.ReadAsync(buffer, ct)) > 0)
        {
            if (output.Length + read > 4_000_000) { throw new IOException("ALGE Results response exceeds the size limit."); }
            output.Write(buffer, 0, read);
        }
        var bytes = output.ToArray();
        if (capture is not null) { await capture(bytes); }
        using var json = JsonDocument.Parse(bytes);
        CheckStatus(json.RootElement);
        return bytes;
    }

    private static void CheckStatus(JsonElement root)
    {
        var code = root.GetProperty("status").GetInt32();
        if (code == -9000) { throw new AlgeQuotaException(); }
        if (code == -1007) { throw new AlgeAuthenticationException(); }
        if (code != 0) { throw new AlgeStatusException(code); }
    }

    public ValueTask DisposeAsync() { _token = null; return ValueTask.CompletedTask; }
    private sealed class AlgeAuthenticationException : IOException;
    private sealed class AlgeQuotaException : IOException;
    private sealed class AlgeStatusException(int code) : IOException("ALGE Results returned an unsuccessful response (" + code.ToString(CultureInfo.InvariantCulture) + ").")
    { public int Code { get; } = code; }
}

public sealed record AlgeResultsDevice(string Id, string Name, string Type)
{
    public string Label => Name.Length == 0 ? Id : $"{Id} · {Name}";
    public override string ToString() => Label;
}
