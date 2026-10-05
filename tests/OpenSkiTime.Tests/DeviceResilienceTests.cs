using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;
using OpenSkiTime.Application;
using OpenSkiTime.Devices;
using OpenSkiTime.Timing;
using Xunit;

namespace OpenSkiTime.Tests;

// Device input that must neither end capture nor change timing: unexpected cloud responses and push frames, a response
// that breaks off, rate limits, late device uploads, deleted triggers, device correction lines, clock resets and line noise.
public sealed class DeviceResilienceTests
{
    private const string Device = "231203016";
    private static readonly string[] s_devices = ["Start", "Finish"];

    [Fact]
    public async Task PushFramesOfAnyShapeAreJournalledUnchangedAndCaptureContinues()
    {
        var cloud = new FakeCloud();
        var push = new QueueRealtime();
        string[] frames = ["[1,2]", "\"text\"", """{"type":5,"dto":"x"}""", """{"type":"ENTITY_ADD","dto":{"deviceId":231203016}}""",
            Trigger(DateTimeOffset.UtcNow)];
        foreach (var frame in frames) { push.Messages.Enqueue(frame); }
        var pushed = await ReceiveUntil(cloud, () => push, AlgeRealtimeTests.Fast, (_, received) => received.Count(x => x.Stream == "push") == frames.Length);
        Assert.Equal(frames, pushed.Where(x => x.Stream == "push").Select(x => Encoding.UTF8.GetString(x.Bytes)));
        var session = new CaptureSession(Guid.NewGuid(), Guid.Empty, Options(), DateTimeOffset.UtcNow, null, false);
        var decoded = pushed.Where(x => x.Stream == "push").Select((x, i) => Assert.Single(new AlgeResultsDecoder(session)
            .Feed(new(session.Id, i + 1, DateTimeOffset.UtcNow, x.Protocol, x.Source, x.Stream, x.Bytes))).Kind).ToArray();
        Assert.Equal([ObservationKind.Invalid, ObservationKind.Invalid, ObservationKind.Invalid, ObservationKind.Invalid, ObservationKind.Impulse], decoded);
    }

    [Fact]
    public async Task UnexpectedRestResponseShapesAreRetriedInsteadOfEndingCapture()
    {
        var counts = 0;
        var push = new QueueRealtime();
        var cloud = new FakeCloud
        {
            Fault = request => request.RequestUri!.AbsolutePath.EndsWith("/count", StringComparison.Ordinal) && ++counts <= 2
                ? Json(counts == 1 ? """{"status":0,"data":[]}""" : """{"data":[]}""") : null
        };
        var trigger = Trigger(DateTimeOffset.UtcNow);
        var received = await ReceiveUntil(cloud, () => { if (counts >= 2) { push.Messages.Enqueue(trigger); } return push; },
            AlgeRealtimeTests.Fast, (_, packets) => packets.Any(x => x.Stream == "push"));
        Assert.Contains(received, x => x.Stream == "push" && Encoding.UTF8.GetString(x.Bytes) == trigger);
    }

    [Fact]
    public async Task AResponseThatBreaksOffMidBodyIsRetriedLikeANetworkFailure()
    {
        var histories = 0;
        var push = new QueueRealtime();
        var cloud = new FakeCloud
        {
            Fault = request => !request.RequestUri!.AbsolutePath.EndsWith("/count", StringComparison.Ordinal) && request.Method == HttpMethod.Get
                && ++histories == 1 ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new BreakingStream()) } : null
        };
        var trigger = Trigger(DateTimeOffset.UtcNow);
        var received = await ReceiveUntil(cloud, () => { if (histories >= 1) { push.Messages.Enqueue(trigger); } return push; },
            AlgeRealtimeTests.Fast, (_, packets) => packets.Any(x => x.Stream == "push"));
        Assert.Contains(received, x => x.Stream == "push" && Encoding.UTF8.GetString(x.Bytes) == trigger);
    }

    [Fact]
    public async Task ARateLimitedPeriodicCheckKeepsThePushConnection()
    {
        var counts = 0; var connects = 0;
        var push = new QueueRealtime();
        var trigger = Trigger(DateTimeOffset.UtcNow);
        var cloud = new FakeCloud
        {
            Fault = request =>
            {
                if (!request.RequestUri!.AbsolutePath.EndsWith("/count", StringComparison.Ordinal) || ++counts != 2) { return null; }
                push.Messages.Enqueue(trigger); // arrives while the history check is rate limited
                return Json("""{"status":-9000,"data":[]}""");
            }
        };
        var statuses = new List<string>();
        var received = await ReceiveUntil(cloud, () => { connects++; return push; }, AlgeRealtimeTests.Fast with { ReconcileInterval = TimeSpan.FromMilliseconds(200) },
            (_, packets) => packets.Any(x => x.Stream == "push"), statuses);
        Assert.Contains(received, x => x.Stream == "push" && Encoding.UTF8.GetString(x.Bytes) == trigger);
        Assert.Equal(1, connects);
        Assert.Contains(statuses, x => x.Contains("rate limit", StringComparison.Ordinal) && x.Contains("push continues", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ANetworkInterruptionIsReportedOnceAcrossReconnectAttempts()
    {
        var cloud = new FakeCloud
        {
            Fault = request => request.RequestUri!.AbsolutePath.EndsWith("/count", StringComparison.Ordinal)
                ? throw new HttpRequestException("Synthetic network failure.") : null
        };
        var attempts = 0;
        var received = await ReceiveUntil(cloud, () => { attempts++; return new QueueRealtime { RefuseConnect = true }; }, AlgeRealtimeTests.Fast,
            (_, _) => attempts >= 6);
        Assert.Single(received, x => x.Protocol == "transport-status" && Encoding.UTF8.GetString(x.Bytes).StartsWith("Network interruption", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PollingFindsATriggerTheDeviceUploadedLateWithAnOlderTimestamp()
    {
        var cloud = new FakeCloud();
        var late = DateTimeOffset.UtcNow.AddMinutes(-30); // older than the recent window a changed count reads
        var trigger = Trigger(late);
        var added = false;
        var received = await ReceiveUntil(cloud, () => new QueueRealtime { RefuseConnect = true },
            AlgeRealtimeTests.Fast with { ReconcileInterval = TimeSpan.FromMilliseconds(300) }, (_, packets) =>
            {
                if (!added && cloud.Histories >= 1) { cloud.Add(late, trigger); added = true; }
                return packets.Any(x => x.Stream == "cloud" && Encoding.UTF8.GetString(x.Bytes).Contains(Stamp(late), StringComparison.Ordinal));
            }, seconds: 5);
        Assert.Contains(received, x => x.Stream == "cloud" && Encoding.UTF8.GetString(x.Bytes).Contains(Stamp(late), StringComparison.Ordinal));
    }

    [Fact]
    public void ATriggerDeletedOnAlgeResultsIsADeviceCorrectionNotADuplicateImpulse()
    {
        var session = new CaptureSession(Guid.NewGuid(), Guid.Empty, Options(), DateTimeOffset.UtcNow, null, false);
        var decoder = new AlgeResultsDecoder(session);
        var at = DateTimeOffset.UtcNow;
        var added = Assert.Single(decoder.Feed(Packet(session, 1, Trigger(at))));
        var deleted = Assert.Single(decoder.Feed(Packet(session, 2, Trigger(at, change: "ENTITY_DELETE"))));
        Assert.Equal(ObservationKind.Impulse, added.Kind);
        Assert.Equal(ObservationKind.DeviceCorrection, deleted.Kind);
        Assert.NotEqual(added.Fingerprint, deleted.Fingerprint);
        Assert.Equal((added.Channel, added.DeviceTicks), (deleted.Channel, deleted.DeviceTicks));
        Assert.Contains("deleted on ALGE Results", deleted.Message, StringComparison.Ordinal);
        // A deletion on a channel no role uses stays signal-monitor information, like the trigger itself.
        var unrouted = Assert.Single(decoder.Feed(Packet(session, 3, Trigger(at, "C3", "ENTITY_DELETE"))));
        Assert.Equal(ObservationKind.Information, unrouted.Kind);
    }

    [Fact]
    public void ACorrectionLineForAnEarlierImpulseDoesNotResetTheDeviceClock()
    {
        var list = TimingRulesTests.List();
        var session = TimingRulesTests.Session(list);
        var observations = new AlgeAsciiDecoder(session, "Timy", "1").Feed(TimingRulesTests.Packet(session, 1,
            " 0001 C0 10:00:00.0000 00\r 0002 C1 10:20:00.0000 00\r c0003 C1 10:05:00.0000 00\r 0004 C1 10:21:00.0000 00\r"));
        Assert.Equal([ObservationKind.Impulse, ObservationKind.Impulse, ObservationKind.DeviceCorrection, ObservationKind.Impulse],
            observations.Select(x => x.Kind));
        Assert.Equal(TimeSpan.FromHours(10).Add(TimeSpan.FromMinutes(5)).Ticks, observations[2].DeviceTicks % TimeSpan.TicksPerDay);
        Assert.All(observations, x => Assert.Equal(observations[0].ClockId, x.ClockId));
    }

    [Fact]
    public void DevicesOfAClockGroupDoNotShareAClockAfterIndependentResets()
    {
        var list = TimingRulesTests.List();
        var ids = s_devices.Select(name =>
        {
            var plain = TimingRulesTests.Session(list);
            var session = plain with { Options = plain.Options with { ClockGroup = "g" } };
            // Each device's clock is reset once (its time jumps back an hour), independently of the other.
            return new AlgeAsciiDecoder(session, name, "1").Feed(TimingRulesTests.Packet(session, 1,
                " 0001 C0 10:00:00.0000 00\r 0002 C0 09:00:00.0000 00\r 0003 C0 09:00:10.0000 00\r")).Select(x => x.ClockId).ToArray();
        }).ToArray();
        Assert.Equal("sync:g:0", ids[0][0]);
        Assert.Equal(ids[0][0], ids[1][0]);
        Assert.NotEqual(ids[0][2], ids[1][2]);
        Assert.NotEqual(ids[0][0], ids[0][2]);
    }

    [Fact]
    public void ControlBytesInADeviceLineAreIgnoredWhenReadingIt()
    {
        var list = TimingRulesTests.List();
        var session = TimingRulesTests.Session(list);
        var raw = "\u0002 0001 C1 10:00:00.1234 00\u0003\r\u0011 0002 C0 10:00:05.0000\u0000 00\r\u0013TIMY: V2.0\r";
        var packet = TimingRulesTests.Packet(session, 1, raw);
        var observations = new AlgeAsciiDecoder(session, "Timy", "1").Feed(packet);
        Assert.Equal([ObservationKind.Impulse, ObservationKind.Impulse, ObservationKind.Information], observations.Select(x => x.Kind));
        Assert.Equal(TimeSpan.FromHours(10).Ticks + 1234 * 1000, observations[0].DeviceTicks % TimeSpan.TicksPerDay);
        Assert.Equal(4, observations[0].Precision);
        Assert.Equal(raw, Encoding.UTF8.GetString(packet.Bytes)); // the journalled bytes are not altered
    }

    private static CaptureOptions Options() => new(TimingSourceTypes.AlgeResultsLabel, Device + "/1", DateOnly.FromDateTime(DateTime.UtcNow),
        FromUtc: DateTimeOffset.UtcNow.AddHours(-3)) { Routes = [new(1, 2, Device)], ClockGroup = "g" };
    private static string Stamp(DateTimeOffset at) => (at.UtcTicks - DateTime.UnixEpoch.Ticks).ToString(CultureInfo.InvariantCulture);
    private static string Trigger(DateTimeOffset at, string channel = "C1", string change = "ENTITY_ADD") =>
        $$"""{"type":"{{change}}","dto":{"deviceId":"{{Device}}","timestamp":{{Stamp(at)}},"timingChannel":"{{channel}}","fallingEdge":true,"valid":true,"blocked":false,"type":"StartNumberTrigger","startNumber":{"startNumber":1604,"type":"SEQUENTIAL"},"timeOffset":0},"entityType":"TriggerDto"}""";
    private static RawTimingPacket Packet(CaptureSession session, long sequence, string body) =>
        new(session.Id, sequence, DateTimeOffset.UtcNow, "alge-results/v1", Device, "push", Encoding.UTF8.GetBytes(body));
    private static HttpResponseMessage Json(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "application/json") };

    // Runs the source until done(...) is true for the received packets; capture ending any other way fails the test.
    private static async Task<List<TransportPacket>> ReceiveUntil(FakeCloud cloud, Func<IAlgeRealtimeConnection> realtime, AlgeResultsTimings timings,
        Func<TransportPacket?, IReadOnlyList<TransportPacket>, bool> done, List<string>? statuses = null, int seconds = 10)
    {
        using var client = new HttpClient(cloud);
        await using var source = new AlgeResultsSource(client, "club@example.test", "secret", Options(), realtime, timings);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        var packets = new List<TransportPacket>();
        var running = source.ReceiveAsync(packet =>
        {
            lock (packets) { packets.Add(packet); if (done(packet, packets)) { stop.Cancel(); } }
            return ValueTask.CompletedTask;
        }, text => { lock (packets) { statuses?.Add(text); if (done(null, packets)) { stop.Cancel(); } } }, stop.Token);
        // Conditions that depend on time rather than packets (reconnect attempts) are checked here too.
        while (!running.IsCompleted)
        {
            await Task.WhenAny(running, Task.Delay(20, CancellationToken.None));
            lock (packets) { if (!stop.IsCancellationRequested && done(null, packets)) { stop.Cancel(); } }
        }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        lock (packets) { return packets.ToList(); }
    }

    private sealed class QueueRealtime : IAlgeRealtimeConnection
    {
        public ConcurrentQueue<string> Messages { get; } = new();
        public bool RefuseConnect { get; init; }
        public Task ConnectAsync(string token, IReadOnlyList<string> destinations, CancellationToken ct)
            => RefuseConnect ? throw new AlgeRealtimeException("Synthetic refusal.") : Task.CompletedTask;
        public async Task<AlgeRealtimeFrame?> ReceiveAsync(TimeSpan timeout, CancellationToken ct)
        {
            if (Messages.TryDequeue(out var message))
            { return new(AlgeRealtimeFrameKind.Message, $"/topic/device/{Device}/trigger", Encoding.UTF8.GetBytes(message)); }
            await Task.Delay(TimeSpan.FromMilliseconds(20), ct);
            return new AlgeRealtimeFrame(AlgeRealtimeFrameKind.Heartbeat);
        }
        public Task SendHeartbeatAsync(CancellationToken ct) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    // ALGE Results REST with the documented time filters: login, per-channel count and history of the stored triggers.
    private sealed class FakeCloud : HttpMessageHandler
    {
        private readonly object _gate = new();
        private readonly List<(long Ms, string Body)> _history = [];
        public Func<HttpRequestMessage, HttpResponseMessage?>? Fault { get; init; }
        public int Histories { get { lock (_gate) { return _histories; } } }
        private int _histories;
        public void Add(DateTimeOffset at, string body) { lock (_gate) { _history.Add((at.ToUnixTimeMilliseconds(), body)); } }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (request.Method == HttpMethod.Post)
                {
                    var login = Json("""{"status":0,"data":[{"id":"user-1","roles":["TIMING_POINT_ACCOUNT"]}]}""");
                    login.Headers.Add("authorization", "token-1");
                    return Task.FromResult(login);
                }
                if (Fault?.Invoke(request) is { } fault) { return Task.FromResult(fault); }
                var query = request.RequestUri!.Query.TrimStart('?').Split('&').Select(x => x.Split('=')).ToDictionary(x => x[0], x => x[1]);
                var from = long.Parse(query["timestampFrom_ms"], CultureInfo.InvariantCulture);
                var to = query.TryGetValue("timestampTo_ms", out var until) ? long.Parse(until, CultureInfo.InvariantCulture) : long.MaxValue;
                var matching = _history.Where(x => x.Ms >= from && x.Ms <= to).Select(x => x.Body).ToArray();
                if (request.RequestUri.AbsolutePath.EndsWith("/count", StringComparison.Ordinal))
                { return Task.FromResult(Json($$"""{"status":0,"data":[{"value":{{matching.Length}}}]}""")); }
                _histories++;
                var dtos = matching.Select(x => x[(x.IndexOf("\"dto\":", StringComparison.Ordinal) + 6)..x.LastIndexOf(",\"entityType\"", StringComparison.Ordinal)]);
                return Task.FromResult(Json("{\"status\":0,\"data\":[" + string.Join(",", dtos) + "]}"));
            }
        }
    }

    private sealed class BreakingStream : Stream
    {
        public override int Read(byte[] buffer, int offset, int count) => throw new HttpIOException(HttpRequestError.ResponseEnded, "Synthetic response ended early.");
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => ValueTask.FromException<int>(new HttpIOException(HttpRequestError.ResponseEnded, "Synthetic response ended early."));
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
