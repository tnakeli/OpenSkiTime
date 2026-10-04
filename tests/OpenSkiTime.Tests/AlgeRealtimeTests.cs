using System.Net;
using System.Text;
using OpenSkiTime.Application;
using OpenSkiTime.Devices;
using OpenSkiTime.Timing;
using Xunit;

namespace OpenSkiTime.Tests;

public sealed class AlgeRealtimeTests
{
    internal static readonly AlgeResultsTimings Fast = new(TimeSpan.FromSeconds(60), TimeSpan.FromMilliseconds(100),
        TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(200));
    private static readonly DateTime Instant = new(2026, 10, 4, 17, 6, 44, DateTimeKind.Utc);
    private static long Stamp(int extraMs = 0) => Instant.AddMilliseconds(extraMs).Ticks - DateTime.UnixEpoch.Ticks;

    private static string Trigger(string device, string channel, long stamp, string change = "ENTITY_ADD") =>
        $$"""{"type":"{{change}}","dto":{"deviceId":"{{device}}","timestamp":{{stamp}},"timingChannel":"{{channel}}","fallingEdge":true,"valid":true,"blocked":false,"type":"StartNumberTrigger","startNumber":{"startNumber":1604,"type":"SEQUENTIAL"},"timeOffset":180},"entityType":"TriggerDto"}""";

    private static CaptureOptions Options() => new(TimingSourceTypes.AlgeResultsLabel, "231203016/1", new DateOnly(2026, 10, 4),
        FromUtc: new DateTimeOffset(Instant).AddMinutes(-5)) { Routes = [new(1, 2, "231203016")], ClockGroup = "g" };

    [Fact]
    public async Task PushedTriggersArriveImmediatelyOnTheDeviceTopicAndUnroutedChannelsAreInformationOnly()
    {
        var http = new FakeAlge();
        var push = new FakeRealtime(
            Trigger("231203016", "C3", Stamp()), // another channel of the same device: not routed here
            Trigger("231203016", "C1", Stamp()),
            Trigger("231203016", "C1", Stamp(), "ENTITY_DELETE"));
        using var client = new HttpClient(http);
        await using var source = new AlgeResultsSource(client, "club@example.test", "secret", Options(), () => push, Fast);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var packets = new List<TransportPacket>(); var statuses = new List<string>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => source.ReceiveAsync(p =>
        {
            packets.Add(p);
            if (packets.Count(x => x.Stream == "push") == 3) { stop.Cancel(); }
            return ValueTask.CompletedTask;
        }, statuses.Add, stop.Token));

        Assert.Equal(["/topic/device/231203016/trigger"], push.Topics);
        Assert.Equal("token-1", push.Token);
        var pushed = packets.Where(x => x.Protocol == "alge-results/v1" && x.Stream == "push").ToArray();
        Assert.Equal(3, pushed.Length); // every channel of the subscribed device is journalled
        Assert.Contains(packets, x => x.Protocol == "transport-status" && Encoding.UTF8.GetString(x.Bytes).Contains("deleted", StringComparison.Ordinal));
        Assert.Contains(statuses, x => x.Contains("realtime push", StringComparison.Ordinal));
        Assert.Equal(1, http.Logins);

        // The push body decodes with the same decoder as REST history, on the device clock time (timestamp + timeOffset).
        var session = new CaptureSession(Guid.NewGuid(), Guid.Empty, Options(), DateTimeOffset.UtcNow, null, false);
        var unrouted = Assert.Single(new AlgeResultsDecoder(session).Feed(new(session.Id, 1, DateTimeOffset.UtcNow, pushed[0].Protocol, pushed[0].Source, pushed[0].Stream, pushed[0].Bytes)));
        Assert.Equal((ObservationKind.Information, 3), (unrouted.Kind, unrouted.PhysicalChannel)); // C3 is no role: signal monitor only
        var observation = Assert.Single(new AlgeResultsDecoder(session).Feed(new(session.Id, 2, DateTimeOffset.UtcNow, pushed[1].Protocol, pushed[1].Source, pushed[1].Stream, pushed[1].Bytes)));
        Assert.Equal(1, observation.PhysicalChannel);
        Assert.Equal((ObservationKind.Impulse, 2), (observation.Kind, observation.Channel));
        Assert.Equal(new DateTime(2026, 10, 4, 20, 6, 44).Ticks, observation.DeviceTicks);
    }

    [Fact]
    public async Task DroppedPushIsReconnectedAndTriggersMissedMeanwhileAreRecoveredFromHistory()
    {
        var http = new FakeAlge();
        var first = new FakeRealtime { FailAfterFrames = true };
        var second = new FakeRealtime();
        var connections = new Queue<FakeRealtime>([first, second]);
        using var client = new HttpClient(http);
        await using var source = new AlgeResultsSource(client, "club@example.test", "secret", Options(),
            () => { var next = connections.Dequeue(); if (next == second) { http.Add(Trigger("231203016", "C1", Stamp(500))); } return next; }, Fast);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var packets = new List<TransportPacket>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => source.ReceiveAsync(p =>
        {
            packets.Add(p);
            if (p.Stream == "cloud" && Encoding.UTF8.GetString(p.Bytes).Contains("231203016", StringComparison.Ordinal)) { stop.Cancel(); }
            return ValueTask.CompletedTask;
        }, _ => { }, stop.Token));
        Assert.True(second.Connected);
        Assert.Contains(packets, x => x.Protocol == "transport-status" && Encoding.UTF8.GetString(x.Bytes).Contains("realtime interrupted", StringComparison.Ordinal));
        Assert.Contains(packets, x => x.Stream == "cloud" && Encoding.UTF8.GetString(x.Bytes).Contains("\"C1\"", StringComparison.Ordinal));
        Assert.Equal(1, http.Logins); // The token is reused across reconnects.
    }

    [Fact]
    public async Task HealthyPushKeepsRestLoadMinimalAndSendsHeartbeats()
    {
        var http = new FakeAlge();
        var push = new FakeRealtime();
        using var client = new HttpClient(http);
        await using var source = new AlgeResultsSource(client, "club@example.test", "secret", Options(), () => push, Fast);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(1.5));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => source.ReceiveAsync(_ => ValueTask.CompletedTask, _ => { }, stop.Token));
        Assert.Equal(1, http.Counts); // one count check right after subscribing; the next one is due after a minute
        Assert.InRange(push.Heartbeats, 5, 20);
    }

    [Fact]
    public async Task SilentPushConnectionIsReplacedAndUnavailablePushFallsBackToPolling()
    {
        var http = new FakeAlge();
        var created = 0;
        using var client = new HttpClient(http);
        var timings = Fast with { SilenceLimit = TimeSpan.FromMilliseconds(300) };
        await using var source = new AlgeResultsSource(client, "club@example.test", "secret", Options(),
            () => ++created == 1 ? new FakeRealtime { Silent = true } : new FakeRealtime { RefuseConnect = true }, timings);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var statuses = new List<string>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => source.ReceiveAsync(_ => ValueTask.CompletedTask, s =>
        {
            statuses.Add(s);
            if (http.Counts >= 4) { stop.Cancel(); }
        }, stop.Token));
        Assert.True(created >= 2);
        Assert.Contains(statuses, x => x.Contains("REST polling", StringComparison.Ordinal));
        Assert.Equal(1, http.Logins);
    }

    [Fact]
    public async Task ARefusedDeviceIsReportedByNameWhileOtherRolesKeepReceiving()
    {
        var http = new FakeAlge { RefusedDevice = "231203037" };
        var push = new FakeRealtime(Trigger("231203016", "C1", Stamp()));
        using var client = new HttpClient(http);
        var options = Options() with { Routes = [new(1, 2, "231203016"), new(3, 3, "231203037")] };
        await using var source = new AlgeResultsSource(client, "club@example.test", "secret", options, () => push, Fast);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var packets = new List<TransportPacket>(); var statuses = new List<string>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => source.ReceiveAsync(p =>
        {
            packets.Add(p);
            if (p.Stream == "push") { stop.Cancel(); }
            return ValueTask.CompletedTask;
        }, statuses.Add, stop.Token));
        var notice = Assert.Single(packets, x => x.Protocol == "transport-status");
        Assert.Contains("231203037 C3", Encoding.UTF8.GetString(notice.Bytes), StringComparison.Ordinal);
        Assert.Contains("-2011", Encoding.UTF8.GetString(notice.Bytes), StringComparison.Ordinal);
        Assert.Contains(packets, x => x.Stream == "push");
        Assert.Contains(statuses, x => x.Contains("231203037 C3 refused (-2011)", StringComparison.Ordinal));
        Assert.Equal(1, http.Logins);
    }

    [Fact]
    public void AlgeTimesUseTheTimeOfDayOnTheDeviceDateLikeOtherDevices()
    {
        var options = Options() with { DeviceDate = new DateOnly(2026, 9, 13) };
        var session = new CaptureSession(Guid.NewGuid(), Guid.Empty, options, DateTimeOffset.UtcNow, null, false);
        var body = Encoding.UTF8.GetBytes(Trigger("231203016", "C1", Stamp()));
        foreach (var stream in new[] { "push", "cloud" })
        {
            var observation = Assert.Single(new AlgeResultsDecoder(session).Feed(new(session.Id, 1, DateTimeOffset.UtcNow, "alge-results/v1", "231203016", stream, body)));
            // Only the clock time counts: 20:06:44 device time, on the capture device date, whatever the calendar day was.
            Assert.Equal((ObservationKind.Impulse, new DateTime(2026, 9, 13, 20, 6, 44).Ticks), (observation.Kind, observation.DeviceTicks));
            Assert.Equal(new DateOnly(2026, 10, 4), observation.CalendarDate);
            Assert.DoesNotContain("date", observation.Message, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task ReadingDeviceMemoryForTheReportKeepsOnlyTheChosenRaceDay()
    {
        var today = Trigger("231203016", "C1", Stamp());
        var yesterday = Trigger("231203016", "C1", Stamp() - TimeSpan.TicksPerDay);
        var options = new CaptureOptions(TimingSourceTypes.AlgeResultsLabel, "231203016/1", new DateOnly(2026, 10, 4), 1, 1,
            StartDeviceId: "231203016", FinishDeviceId: "231203016");
        await using var read = new TimingDeviceRead(new PacketsSource(today, yesterday), AuxiliaryTimingRole.HandFinish, options);
        read.Start(); await read.Completion;
        var impulse = Assert.Single(read.Decode(new AlgeDecoderFactory()), x => x.Observation.Kind == ObservationKind.Impulse);
        Assert.Equal(new DateOnly(2026, 10, 4), impulse.Observation.CalendarDate);
    }

    private sealed class PacketsSource(params string[] bodies) : ITimingSource
    {
        public async Task ReceiveAsync(Func<TransportPacket, ValueTask> receive, Action<string> status, CancellationToken ct)
        { foreach (var body in bodies) { await receive(new("alge-results/v1", "231203016", "cloud", Encoding.UTF8.GetBytes(body))); } }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task RegularChecksReadOnlyTheRecentWindowWhileSubscriptionReadsFullHistory()
    {
        var http = new FakeAlge();
        var push = new FakeRealtime();
        using var client = new HttpClient(http);
        var timings = Fast with { ReconcileInterval = TimeSpan.FromMilliseconds(150) };
        var options = Options() with { FromUtc = DateTimeOffset.UtcNow.AddHours(-3) };
        await using var source = new AlgeResultsSource(client, "club@example.test", "secret", options, () => push, timings);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var running = source.ReceiveAsync(_ => ValueTask.CompletedTask, _ => { }, stop.Token);
        while (http.HistoryQueries.Count < 1) { await Task.Delay(10, stop.Token); }
        http.Add(Trigger("231203016", "C1", Stamp())); // the count changes; the next regular check reads history
        while (http.HistoryQueries.Count < 2) { await Task.Delay(10, stop.Token); }
        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        long From(string query) => long.Parse(query.Split('&').Single(x => x.TrimStart('?').StartsWith("timestampFrom_ms=", StringComparison.Ordinal)).Split('=')[1], System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(options.FromUtc!.Value.ToUnixTimeMilliseconds(), From(http.HistoryQueries[0])); // full history on subscribe
        Assert.InRange(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - From(http.HistoryQueries[1]), 9 * 60_000, 11 * 60_000); // recent window only
    }

    [Fact]
    public async Task AccountDevicesAreListedForChoosingDeviceIds()
    {
        var http = new FakeAlge();
        using var client = new HttpClient(http);
        var devices = await AlgeResultsSource.ListDevicesAsync(client, "club@example.test", "secret");
        Assert.Equal(["231203016", "231203037"], devices.Select(x => x.Id));
        Assert.Equal("231203037 · Club B split", devices[1].Label);
        Assert.Equal("231203016", devices[0].Label);
        Assert.Equal((1, 1), (http.Logins, http.DeviceLists));
    }

    internal sealed class FakeRealtime(params string[] messages) : IAlgeRealtimeConnection
    {
        private readonly Queue<string> _messages = new(messages);
        public bool FailAfterFrames { get; init; }
        public bool Silent { get; init; }
        public bool RefuseConnect { get; init; }
        public IReadOnlyList<string> Topics { get; private set; } = [];
        public string? Token { get; private set; }
        public bool Connected { get; private set; }
        public int Heartbeats { get; private set; }
        public Task ConnectAsync(string token, IReadOnlyList<string> destinations, CancellationToken ct)
        {
            if (RefuseConnect) { throw new AlgeRealtimeException("refused"); }
            Token = token; Topics = destinations; Connected = true; return Task.CompletedTask;
        }
        public async Task<AlgeRealtimeFrame?> ReceiveAsync(TimeSpan timeout, CancellationToken ct)
        {
            if (_messages.TryDequeue(out var message)) { return new(AlgeRealtimeFrameKind.Message, "/topic/device/231203016/trigger", Encoding.UTF8.GetBytes(message)); }
            if (FailAfterFrames) { throw new AlgeRealtimeException("connection lost"); }
            await Task.Delay(timeout, ct);
            return Silent ? null : new AlgeRealtimeFrame(AlgeRealtimeFrameKind.Heartbeat);
        }
        public Task SendHeartbeatAsync(CancellationToken ct) { Heartbeats++; return Task.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    // Minimal ALGE Results REST: login, per-channel count and history of the added triggers.
    private sealed class FakeAlge : HttpMessageHandler
    {
        private readonly List<string> _history = [];
        private readonly object _gate = new();
        public int Logins { get; private set; }
        public int Counts { get; private set; }
        public int DeviceLists { get; private set; }
        public List<string> HistoryQueries { get; } = [];
        public string? RefusedDevice { get; init; }
        public void Add(string trigger) { lock (_gate) { _history.Add(trigger); } }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (request.Method == HttpMethod.Post)
                {
                    Logins++;
                    var response = Json("""{"status":0,"data":[{"id":"user-1","roles":["TIMING_POINT_ACCOUNT"]}]}""");
                    response.Headers.Add("authorization", "token-" + Logins);
                    return Task.FromResult(response);
                }
                if (request.RequestUri!.AbsolutePath.EndsWith("/user/user-1/devices", StringComparison.Ordinal))
                {
                    DeviceLists++;
                    Assert.Equal("token-" + Logins, Assert.Single(request.Headers.GetValues("authorization")));
                    return Task.FromResult(Json("""{"status":0,"data":[{"id":"231203037","type":"MT1","deviceComponents":{"info":{"name":"Club B split"}}},{"id":"231203016","type":"MT1","deviceComponents":{}}]}"""));
                }
                if (RefusedDevice is { } refused && request.RequestUri!.AbsolutePath.Contains("/devices/" + refused + "/", StringComparison.Ordinal))
                { return Task.FromResult(Json("""{"status":-2011,"message":"Not allowed","data":[]}""")); }
                if (request.RequestUri!.AbsolutePath.EndsWith("/count", StringComparison.Ordinal))
                { Counts++; return Task.FromResult(Json($$"""{"status":0,"data":[{"value":{{_history.Count}}}]}""")); }
                HistoryQueries.Add(request.RequestUri.Query);
                var dtos = _history.Select(x => x[(x.IndexOf("\"dto\":", StringComparison.Ordinal) + 6)..x.LastIndexOf(",\"entityType\"", StringComparison.Ordinal)]);
                return Task.FromResult(Json("{\"status\":0,\"data\":[" + string.Join(",", dtos) + "]}"));
            }
        }
        private static HttpResponseMessage Json(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
    }
}
