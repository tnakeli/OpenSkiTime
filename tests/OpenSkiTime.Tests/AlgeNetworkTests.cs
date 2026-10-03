using System.Net;
using System.Text;
using OpenSkiTime.Application;
using OpenSkiTime.Devices;
using Xunit;

namespace OpenSkiTime.Tests;

public sealed class AlgeNetworkTests
{
    [Fact]
    public async Task LoginUsesDocumentedRoleAndTokenThenSnapshotsArePagedWithFixedBoundsAndRawBytes()
    {
        var calls = new List<Uri>();
        var loginCount = 0;
        var pageCount = 0;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var handler = new StubHandler(async request =>
        {
            calls.Add(request.RequestUri!);
            Assert.Equal("https", request.RequestUri!.Scheme);
            if (request.Method == HttpMethod.Post)
            {
                loginCount++;
                Assert.EndsWith("/mt1/api/user/login", request.RequestUri.AbsolutePath, StringComparison.Ordinal);
                Assert.Contains("synthetic-password", await request.Content!.ReadAsStringAsync(), StringComparison.Ordinal);
                var response = Json("{\"status\":0,\"data\":[{\"roles\":[\"TIMING_POINT_ACCOUNT\"]}]}");
                response.Headers.Add("authorization", "synthetic-token");
                return response;
            }
            Assert.Equal("synthetic-token", Assert.Single(request.Headers.GetValues("authorization")));
            if (request.RequestUri.AbsolutePath.EndsWith("/count", StringComparison.Ordinal))
            { return Json("{\"status\":0,\"data\":[{\"value\":201}]}"); }
            pageCount++;
            return Json("{\"status\":0,\"data\":[" + string.Join(",", Enumerable.Repeat("{\"synthetic\":true}", pageCount == 1 ? 200 : 1)) + "]}");
        });
        using var client = new HttpClient(handler);
        var options = new CaptureOptions("MT1", "100;200", TimingRulesTests.Date, StartDeviceId: "100", FinishDeviceId: "200", FromUtc: TimingRulesTests.At);
        await using var source = new AlgeResultsSource(client, "synthetic-user", "synthetic-password", options);
        var packets = new List<TransportPacket>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => source.ReceiveAsync(packet =>
        {
            packets.Add(packet);
            if (packets.Count == 2) { stop.Cancel(); }
            return ValueTask.CompletedTask;
        }, _ => { }, stop.Token));
        Assert.Equal(1, loginCount);
        Assert.Equal(2, packets.Count);
        Assert.All(packets, x => Assert.Equal("alge-results/v1", x.Protocol));
        Assert.DoesNotContain(packets, x => Encoding.UTF8.GetString(x.Bytes).Contains("synthetic-token", StringComparison.Ordinal));
        var pages = calls.Where(x => x.Query.Contains("offset=", StringComparison.Ordinal)).Take(2).ToArray();
        Assert.Contains("offset=0", pages[0].Query, StringComparison.Ordinal);
        Assert.Contains("offset=200", pages[1].Query, StringComparison.Ordinal);
        Assert.Equal(pages[0].Query.Split('&').Single(x => x.StartsWith("timestampTo_ms=", StringComparison.Ordinal)),
            pages[1].Query.Split('&').Single(x => x.StartsWith("timestampTo_ms=", StringComparison.Ordinal)));
        Assert.Contains("timestampFrom_ms=" + TimingRulesTests.At.ToUnixTimeMilliseconds(), pages[0].Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExpiredTokenIsReauthorizedOnceAndMalformedTriggerPayloadIsPreserved()
    {
        var logins = 0;
        var gets = 0;
        var received = new List<TransportPacket>();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var handler = new StubHandler(request =>
        {
            if (request.Method == HttpMethod.Post)
            {
                logins++;
                var response = Json("{\"status\":0,\"data\":[{\"roles\":[\"TIMING_POINT_ACCOUNT\"]}]}");
                response.Headers.Add("authorization", "synthetic-" + logins);
                return Task.FromResult(response);
            }
            if (++gets == 1) { return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)); }
            if (request.RequestUri!.AbsolutePath.EndsWith("/count", StringComparison.Ordinal))
            { return Task.FromResult(Json("{\"status\":0,\"data\":[{\"value\":1}]}")); }
            return Task.FromResult(Json("malformed original body"));
        });
        using var client = new HttpClient(handler);
        await using var source = new AlgeResultsSource(client, "synthetic", "synthetic", new("MT1", "100", TimingRulesTests.Date, StartDeviceId: "100", FinishDeviceId: "100"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => source.ReceiveAsync(packet =>
        {
            received.Add(packet);
            if (packet.Protocol == "alge-results/v1") { stop.Cancel(); }
            return ValueTask.CompletedTask;
        }, _ => { }, stop.Token));
        Assert.Equal(2, logins);
        Assert.Contains(received, x => Encoding.UTF8.GetString(x.Bytes) == "malformed original body");
    }

    [Fact]
    public async Task RejectedAccountHasSanitizedFailureAndNeverJournalsCredentials()
    {
        using var handler = new StubHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)
        { Content = new StringContent("secret server diagnostics") }));
        using var client = new HttpClient(handler);
        await using var source = new AlgeResultsSource(client, "private-user", "private-password",
            new("MT1", "100", TimingRulesTests.Date, StartDeviceId: "100", FinishDeviceId: "100"));
        var packets = new List<TransportPacket>();
        var error = await Assert.ThrowsAsync<IOException>(() => source.ReceiveAsync(p => { packets.Add(p); return ValueTask.CompletedTask; }, _ => { }, CancellationToken.None));
        Assert.Empty(packets);
        Assert.DoesNotContain("private-", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", error.Message, StringComparison.Ordinal);
    }

    private static HttpResponseMessage Json(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return response(request);
        }
    }
}
