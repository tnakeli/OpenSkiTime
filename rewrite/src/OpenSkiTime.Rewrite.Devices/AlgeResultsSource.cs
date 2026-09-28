using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using OpenSkiTime.Rewrite.Application;

namespace OpenSkiTime.Rewrite.Devices;

// Read-only integration: login and GETs only. Never modify events, devices, bibs or pulses on ALGE Results.
public sealed class AlgeResultsSource(HttpClient client, string username, string password, CaptureOptions options) : ITimingSource
{
    private string? _token;
    private static readonly Uri s_root = new("https://alge-results.com/");

    public async Task ReceiveAsync(Func<TransportPacket, ValueTask> receive, Action<string> status, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(receive); ArgumentNullException.ThrowIfNull(status);
        var endpoints = new[] { (Device: options.StartDeviceId, Channel: options.StartChannel),
            (Device: options.FinishDeviceId, Channel: options.FinishChannel) }.Distinct().ToArray();
        if (endpoints.Any(x => string.IsNullOrWhiteSpace(x.Device) || !x.Device.All(char.IsAsciiDigit)))
        { throw new IOException("Enter the MT1 device IDs for start and finish."); }
        var from = (options.FromUtc ?? DateTimeOffset.UtcNow).ToUnixTimeMilliseconds();
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        var cycles = 0;
        var retryDelay = 3;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                _token ??= await LoginAsync(client, username, password, ct);
                foreach (var endpoint in endpoints)
                {
                    var prefix = $"mt1/api/devices/{endpoint.Device}/channel/{endpoint.Channel}/trigger";
                    var filter = "timestampFrom_ms=" + from.ToString(CultureInfo.InvariantCulture);
                    var countBytes = await GetAsync(prefix + "/count?" + filter, ct);
                    using var countDoc = JsonDocument.Parse(countBytes);
                    var count = countDoc.RootElement.GetProperty("data")[0].GetProperty("value").GetInt64();
                    if (counts.TryGetValue(prefix, out var old) && count < old)
                    { await receive(new("transport-status", endpoint.Device!, "cloud", Encoding.UTF8.GetBytes("ALGE Results history count decreased. Review server/device changes; local raw records were retained."))); }
                    if (!counts.TryGetValue(prefix, out old) || old != count || cycles % 30 == 0)
                    {
                        // Stable timestamp upper bound prevents newly arriving pulses shifting offset pagination.
                        var until = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                        var offset = 0;
                        while (true)
                        {
                            var query = string.Create(CultureInfo.InvariantCulture,
                                $"{prefix}?{filter}&timestampTo_ms={until}&limit=200&offset={offset}");
                            var bytes = await GetAsync(query, ct, bytes => receive(new("alge-results/v1", endpoint.Device!, "cloud", bytes)));
                            using var data = JsonDocument.Parse(bytes);
                            var length = data.RootElement.GetProperty("data").GetArrayLength();
                            if (length < 200) { break; }
                            offset += length;
                            if (offset > 100000) { throw new IOException("MT1 history is too large. Choose a later receive-from time."); }
                        }
                        counts[prefix] = count;
                    }
                }
                status("Connected · ALGE Results · 2 s polling + history recovery");
                cycles++; retryDelay = 3;
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }
            catch (AlgeAuthenticationException)
            { _token = null; throw new IOException("ALGE Results sign-in expired or was rejected. Check the account/password and reconnect."); }
            catch (AlgeQuotaException)
            {
                status("ALGE Results rate limit · retrying in 90 seconds; history will be recovered");
                await Task.Delay(TimeSpan.FromSeconds(90), ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
            {
                status("ALGE Results unavailable · reconnecting; history will be recovered");
                await receive(new("transport-status", "ALGE Results", "cloud", Encoding.UTF8.GetBytes("Network interruption. Check recovered observations before finalizing this run.")));
                await Task.Delay(TimeSpan.FromSeconds(retryDelay), ct);
                retryDelay = Math.Min(30, retryDelay * 2);
            }
        }
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
        if (code != 0) { throw new IOException("ALGE Results returned an unsuccessful response (" + code.ToString(CultureInfo.InvariantCulture) + ")."); }
    }

    public ValueTask DisposeAsync() { _token = null; return ValueTask.CompletedTask; }
    private sealed class AlgeAuthenticationException : IOException;
    private sealed class AlgeQuotaException : IOException;
}
