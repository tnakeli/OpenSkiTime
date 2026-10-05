using System.Collections.Concurrent;
using System.Net;
using System.Xml.Linq;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using OpenSkiTime.LiveTiming.Client;
using OpenSkiTime.LiveTiming.Harness;
using OpenSkiTime.LiveTiming.Publishing;
using Xunit;

namespace OpenSkiTime.LiveTiming.Tests;

// What the publishers put on the wire for a race in progress: FIS live XML v53 messages and the standalone server API.
// Runs with the process tests: the publisher's 5 s request timeout must not race CPU-heavy E2E tests on a busy runner.
[Collection("Live timing processes")]
public sealed class LivePublishingTests
{
    [Fact]
    public async Task FisResendsARunTwoFinishWhenOnlyTheCombinedStandingsMoved()
    {
        var transport = new RecordingTransport();
        await using var publisher = new FisPublisher(transport, "synthetic-test-secret");
        var state = RunOne(SyntheticRace.Create(3), (1, 5200), (2, 4800));
        state = SecondRun(state);
        await publisher.PublishAsync(state, true, CancellationToken.None);
        state = await Publish(publisher, state, (1, LiveStatus.OnCourse, null), (1, LiveStatus.Finished, 4900), (2, LiveStatus.OnCourse, null));
        var mark = transport.Messages.Count;
        // Bib 2 is slower in Run 2, so bib 1's own Run 2 row (rank 1, +0.00) does not change, but bib 2 leads on total.
        state = await Publish(publisher, state, (2, LiveStatus.Finished, 5000));
        Assert.Equal(1, state.Runs[1].Results.Single(x => x.Bib == 1).Rank);
        var finishes = transport.Events(mark, "finish");
        var leader = Assert.Single(finishes, x => x.Attribute("bib")!.Value == "2");
        Assert.Equal(("50.00", "0.00", "1"), Values(leader));
        Assert.Null(leader.Attribute("correction"));
        var moved = Assert.Single(finishes, x => x.Attribute("bib")!.Value == "1");
        Assert.Equal(("49.00", "3.00", "2"), Values(moved));
        Assert.Equal("y", moved.Attribute("correction")?.Value);
    }

    [Fact]
    public async Task FisSendsARunTwoFinishWithoutARunOneTimeWithoutStandings()
    {
        var transport = new RecordingTransport();
        await using var publisher = new FisPublisher(transport, "synthetic-test-secret");
        var state = RunOne(SyntheticRace.Create(3), (1, 5000), (2, 5100), (3, 4700));
        state = SyntheticRace.Update(state, 3, LiveStatus.DSQ);
        state = SecondRun(state);
        await publisher.PublishAsync(state, true, CancellationToken.None);
        var mark = transport.Messages.Count;
        state = await Publish(publisher, state, (1, LiveStatus.OnCourse, null), (1, LiveStatus.Finished, 5000),
            (3, LiveStatus.OnCourse, null), (3, LiveStatus.Finished, 4000), (2, LiveStatus.OnCourse, null), (2, LiveStatus.Finished, 4950));
        var finishes = transport.Events(mark, "finish");
        // Disqualified in Run 1: the Run 2 time is shown, but it is not ranked and does not become the leader's time.
        Assert.All(finishes.Where(x => x.Attribute("bib")!.Value == "3"), x =>
        {
            Assert.Equal("40.00", x.Element("time")!.Value);
            Assert.Null(x.Element("rank")); Assert.Null(x.Element("diff"));
        });
        Assert.Contains(finishes, x => x.Attribute("bib")!.Value == "3");
        Assert.Equal(("50.00", "0.00", "1"), Values(finishes.Last(x => x.Attribute("bib")!.Value == "1")));
        Assert.Equal(("49.50", "0.50", "2"), Values(finishes.Last(x => x.Attribute("bib")!.Value == "2")));
    }

    [Fact]
    public async Task FisMarksOnlyChangesToResultsItAlreadySentAsCorrections()
    {
        var transport = new RecordingTransport();
        await using var publisher = new FisPublisher(transport, "synthetic-test-secret");
        var state = SyntheticRace.Create(3);
        await publisher.PublishAsync(state, true, CancellationToken.None);
        var mark = transport.Messages.Count;
        state = await Publish(publisher, state, (1, LiveStatus.OnCourse, null), (1, LiveStatus.Finished, 5000),
            (2, LiveStatus.OnCourse, null), (2, LiveStatus.DNF, null), (3, LiveStatus.OnCourse, null));
        Assert.All(transport.Messages.Skip(mark).SelectMany(x => x.Descendants()), x => Assert.Null(x.Attribute("correction")));
        mark = transport.Messages.Count;
        state = await Publish(publisher, state, (1, LiveStatus.DSQ, null), (2, LiveStatus.DNS, null), (3, LiveStatus.Finished, 5100));
        Assert.Equal("y", Assert.Single(transport.Events(mark, "dq")).Attribute("correction")?.Value);
        Assert.Equal("y", Assert.Single(transport.Events(mark, "dns")).Attribute("correction")?.Value);
        Assert.Null(Assert.Single(transport.Events(mark, "finish")).Attribute("correction"));
        mark = transport.Messages.Count;
        await Publish(publisher, state, (3, LiveStatus.Finished, 5050));
        Assert.Equal("y", Assert.Single(transport.Events(mark, "finish")).Attribute("correction")?.Value);
    }

    [Fact]
    public async Task FisRebuildsTheRunWhenARacerReturnsToTheCourse()
    {
        var transport = new RecordingTransport();
        await using var publisher = new FisPublisher(transport, "synthetic-test-secret");
        var state = SyntheticRace.Create(3);
        await publisher.PublishAsync(state, true, CancellationToken.None);
        state = await Publish(publisher, state, (1, LiveStatus.OnCourse, null), (1, LiveStatus.Finished, 5000), (2, LiveStatus.OnCourse, null));
        var mark = transport.Messages.Count;
        // The finish was a false impulse: bib 1 is on course again. FIS has no message that takes a finish back.
        await Publish(publisher, state, (1, LiveStatus.OnCourse, null));
        Assert.Contains(transport.Messages.Skip(mark), x => x.Root!.Element("startlist")?.Attribute("runno")?.Value == "1");
        Assert.Empty(transport.Events(mark, "finish"));
        Assert.Contains(transport.Events(mark, "start"), x => x.Attribute("bib")!.Value == "1");
    }

    [Fact]
    public async Task StandaloneSendsOneChangedRowAsAnEventAndSeveralAsOneSnapshot()
    {
        var requests = new ConcurrentQueue<string>();
        await using var server = await StartFakeServer(requests, () => Results.Ok(new LiveSession(Guid.NewGuid(), "synthetic-token",
            DateTimeOffset.UtcNow.AddDays(1), "http://127.0.0.1/r/synthetic")));
        using var publisher = new StandalonePublisher(server.Endpoint);
        var state = SyntheticRace.Create(4);
        await publisher.PublishAsync(state, false, CancellationToken.None);
        Assert.Equal(["session", "state"], requests.ToArray());
        foreach (var (bib, status, time) in new (int, LiveStatus, long?)[] { (1, LiveStatus.OnCourse, null), (1, LiveStatus.Finished, 5000), (2, LiveStatus.OnCourse, null) })
        {
            requests.Clear();
            state = SyntheticRace.Update(state, bib, status, time);
            await publisher.PublishAsync(state, false, CancellationToken.None);
            Assert.Equal(["event"], requests.ToArray());
        }
        requests.Clear();
        // A new leader changes bib 1's rank and difference too: one atomic snapshot, not one event (and broadcast) per row.
        state = SyntheticRace.Update(state, 2, LiveStatus.Finished, 4900);
        await publisher.PublishAsync(state, false, CancellationToken.None);
        Assert.Equal(["state"], requests.ToArray());
    }

    [Fact]
    public async Task StandaloneTreatsAPageInsteadOfASessionAsANetworkFailure()
    {
        var requests = new ConcurrentQueue<string>();
        await using var server = await StartFakeServer(requests, () => Results.Content("<html><body>Sign in to the network</body></html>", "text/html"));
        using var publisher = new StandalonePublisher(server.Endpoint);
        // The worker retries IOException with backoff; anything else would end the publisher process.
        await Assert.ThrowsAsync<IOException>(() => publisher.PublishAsync(SyntheticRace.Create(2), false, CancellationToken.None));
        Assert.Null(publisher.Session);
    }

    [Fact]
    public async Task StandaloneWaitsForAColdStartOnlyUntilTheServerAnswers()
    {
        var requests = new ConcurrentQueue<string>();
        var delays = new ConcurrentDictionary<string, TimeSpan> { ["session"] = TimeSpan.FromSeconds(3) };
        await using var server = await StartFakeServer(requests, Session, delays);
        using var publisher = new StandalonePublisher(server.Endpoint, requestTimeout: TimeSpan.FromSeconds(1), wakeTimeout: TimeSpan.FromSeconds(30));
        var state = SyntheticRace.Create(3);
        // A sleeping server answers the first request late; the wake timeout covers it although it exceeds the request timeout.
        Assert.True(publisher.Waking);
        await publisher.PublishAsync(state, false, CancellationToken.None);
        Assert.False(publisher.Waking);
        Assert.Equal(["session", "state"], requests.ToArray());
        // Once awake, a stalled request fails after the short timeout as a retryable network failure.
        delays["state"] = TimeSpan.FromSeconds(3);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAsync<IOException>(() => publisher.PublishAsync(state with { Version = 2 }, true, CancellationToken.None));
        Assert.InRange(clock.Elapsed, TimeSpan.FromSeconds(0.5), TimeSpan.FromSeconds(2.5));
        // After a failure the server may have scaled to zero again: the next request waits for it to wake.
        Assert.True(publisher.Waking);
        await publisher.PublishAsync(state with { Version = 3 }, true, CancellationToken.None);
        Assert.False(publisher.Waking);
        // A gateway answering while no replica runs is not an awake server.
        delays.Clear(); requests.Clear();
        await using var gateway = await StartFakeServer(requests, () => Results.StatusCode(503));
        using var waking = new StandalonePublisher(gateway.Endpoint, requestTimeout: TimeSpan.FromSeconds(1), wakeTimeout: TimeSpan.FromSeconds(30));
        await Assert.ThrowsAsync<HttpRequestException>(() => waking.PublishAsync(state, false, CancellationToken.None));
        Assert.True(waking.Waking);
        // Without a wake timeout (the managed Local server) every request keeps the short timeout.
        Assert.False(new StandalonePublisher(server.Endpoint).Waking);
    }

    [Fact]
    public async Task CloudWorkerReportsAWakingServerAndPublishesAfterAColdStart()
    {
        var requests = new ConcurrentQueue<string>();
        // Longer than the former five-second limit that reported a cold start as a failure.
        var delays = new ConcurrentDictionary<string, TimeSpan> { ["session"] = TimeSpan.FromSeconds(8) };
        await using var server = await StartFakeServer(requests, Session, delays);
        await using var publisher = new PublisherProcess();
        publisher.Offer(SyntheticRace.Create(3));
        var observed = new ConcurrentQueue<PublisherHealth>();
        using var watching = new CancellationTokenSource();
        var watcher = Task.Run(async () => { while (!watching.IsCancellationRequested) { observed.Enqueue(publisher.Health); await Task.Delay(20); } });
        await publisher.StartAsync(ProcessFixture.Artifact("Worker"), new PublisherOptions(PublisherKind.Cloud, server.Endpoint), ProcessFixture.Host);
        await ProcessFixture.Until(() => publisher.Health.State == PublisherState.Running, 60);
        await watching.CancelAsync(); await watcher;
        Assert.Contains(observed, h => h.Notice?.Contains("Waking", StringComparison.Ordinal) == true && h.Error is null);
        Assert.DoesNotContain(observed, h => h.Error is not null || h.State is PublisherState.Reconnecting or PublisherState.Error);
        Assert.Null(publisher.Health.Notice);
        Assert.Equal(["session", "state"], requests.Take(2).ToArray());
    }

    [Fact]
    public async Task StoppingACloudWorkerDoesNotWaitForAWakingServer()
    {
        var requests = new ConcurrentQueue<string>();
        var delays = new ConcurrentDictionary<string, TimeSpan> { ["session"] = TimeSpan.FromSeconds(90) };
        await using var server = await StartFakeServer(requests, Session, delays);
        await using var publisher = new PublisherProcess();
        publisher.Offer(SyntheticRace.Create(3));
        await publisher.StartAsync(ProcessFixture.Artifact("Worker"), new PublisherOptions(PublisherKind.Cloud, server.Endpoint), ProcessFixture.Host);
        await ProcessFixture.Until(() => publisher.Health.Notice is not null && requests.Contains("session"));
        var clock = System.Diagnostics.Stopwatch.StartNew();
        await publisher.CommandAsync("stop");
        await ProcessFixture.Until(() => publisher.Health.State == PublisherState.Stopped, 10);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10));
        Assert.Null(publisher.Health.Notice);
        Assert.Null(publisher.Health.Error);
    }

    private static IResult Session() => Results.Ok(new LiveSession(Guid.NewGuid(), "synthetic-token",
        DateTimeOffset.UtcNow.AddDays(1), "http://127.0.0.1/r/synthetic"));
    private static LiveSnapshot RunOne(LiveSnapshot state, params (int Bib, long Time)[] finishes)
    {
        foreach (var (bib, time) in finishes)
        { state = SyntheticRace.Update(SyntheticRace.Update(state, bib, LiveStatus.OnCourse), bib, LiveStatus.Finished, time); }
        return state;
    }
    private static LiveSnapshot SecondRun(LiveSnapshot state)
    {
        var first = state.Runs[0];
        return state with { Version = state.Version + 1, CurrentRun = 2, Runs = [first, first with { Number = 2, Results = first.Results.Select(r =>
            r with { Status = LiveStatus.Ready, Hundredths = null, Rank = null, Difference = null, StartedAt = null,
                StartSourceTicks = null, FinishSourceTicks = null, Intermediates = null }).ToArray() }] };
    }
    private static async Task<LiveSnapshot> Publish(FisPublisher publisher, LiveSnapshot state, params (int Bib, LiveStatus Status, long? Time)[] updates)
    {
        foreach (var (bib, status, time) in updates)
        {
            state = SyntheticRace.Update(state, bib, status, time);
            await publisher.PublishAsync(state, false, CancellationToken.None);
        }
        return state;
    }
    private static (string Time, string Diff, string Rank) Values(XElement finish)
        => (finish.Element("time")!.Value, finish.Element("diff")!.Value, finish.Element("rank")!.Value);
    // Delays, keyed by request kind, model a server that answers late, such as one waking from scale-to-zero.
    private static async Task<FakeServer> StartFakeServer(ConcurrentQueue<string> requests, Func<IResult> session,
        ConcurrentDictionary<string, TimeSpan>? delays = null)
    {
        async Task Delay(string kind, HttpContext context) { if (delays?.TryGetValue(kind, out var delay) == true) { await Task.Delay(delay, context.RequestAborted); } }
        var port = ProcessFixture.Port();
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, port));
        var app = builder.Build();
        app.MapPost("/api/sessions", async (HttpContext context) => { requests.Enqueue("session"); await Delay("session", context); return session(); });
        app.MapPut("/api/sessions/{id:guid}/state", async (HttpContext context) => { requests.Enqueue("state"); await Delay("state", context); return Results.Ok(); });
        app.MapPost("/api/sessions/{id:guid}/events", async (HttpContext context) => { requests.Enqueue("event"); await Delay("event", context); return Results.Ok(); });
        app.MapGet("/warmup", () => Results.Ok());
        await app.StartAsync();
        // The first request JIT-compiles the server pipeline; do it here, outside the publisher's 5 s request timeout.
        using (var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) })
        { (await client.GetAsync($"http://127.0.0.1:{port}/warmup")).EnsureSuccessStatusCode(); }
        return new(app, $"http://127.0.0.1:{port}");
    }
    private sealed class FakeServer(WebApplication app, string endpoint) : IAsyncDisposable
    {
        public string Endpoint => endpoint;
        public async ValueTask DisposeAsync() { await app.StopAsync(); await app.DisposeAsync(); }
    }
    private sealed class RecordingTransport : IFisLiveTimingTransport
    {
        public List<XDocument> Messages { get; } = [];
        public XElement[] Events(int from, string tag) => Messages.Skip(from).SelectMany(x => x.Root!.Elements("raceevent").Elements(tag)).ToArray();
        public Task SendAsync(string xml, long sequence, CancellationToken ct) { Messages.Add(XDocument.Parse(xml)); return Task.CompletedTask; }
        public void Disconnect() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
