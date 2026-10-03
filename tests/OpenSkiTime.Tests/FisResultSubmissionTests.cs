using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using OpenSkiTime.Application;
using OpenSkiTime.Domain;
using Xunit;

namespace OpenSkiTime.Tests;

public sealed class FisResultSubmissionTests
{
    [Theory]
    [InlineData("FIN9991.xml")]
    [InlineData("FIN9991.timing.xml")]
    public async Task ApprovedResultAndTimingReportArtifactsShareTestOnlySubmission(string fileName)
    {
        var xml = Encoding.UTF8.GetBytes("<SyntheticApprovedXml />");
        using var handler = new Handler(async (request, ct) =>
        {
            var parts = Assert.IsType<MultipartFormDataContent>(request.Content).ToArray();
            var file = parts.Single(x => x.Headers.ContentDisposition!.Name!.Trim('"') == "files[]");
            Assert.Equal(fileName, file.Headers.ContentDisposition!.FileName!.Trim('"'));
            Assert.Equal(xml, await file.ReadAsByteArrayAsync(ct));
            Assert.Equal("true", await parts.Single(x => x.Headers.ContentDisposition!.Name!.Trim('"') == "testMode").ReadAsStringAsync(ct));
            Assert.Equal(Token, request.Headers.Authorization!.Parameter);
            return Response(Payload());
        });
        using var http = new HttpClient(handler); using var client = new FisResultSubmissionClient(http);
        Assert.True((await client.UploadAsync(new ApprovedFisXmlArtifact(Guid.NewGuid(), fileName, xml), Token)).Submission.TestMode);
        await Assert.ThrowsAsync<DomainValidationException>(() => client.UploadAsync(new ApprovedFisXmlArtifact(Guid.Empty, fileName, xml), Token));
    }

    private static readonly Guid s_uuid = new("00000000-0000-0000-0000-000000000123");
    private const string Token = "synthetic-member-token";
    private static string Payload(bool complete = false, bool testMode = true) => JsonSerializer.Serialize(new
    {
        uuid = s_uuid, testMode, outcome = complete ? "success" : "partial", message = "Synthetic response",
        statusUrl = "https://untrusted.example/credential-trap",
        summary = new { total = 1, accepted = complete ? 0 : 1, processed = complete ? 1 : 0, rejected = 0, isComplete = complete },
        files = new[] { new { fileName = "2027AL9991.xml", status = complete ? "processed" : "accepted", comments = "Synthetic processing comment" } }
    });

    [Fact]
    public async Task UploadAlwaysSendsTestModeAndExactApprovedBytesWithMemberAuthentication()
    {
        var xml = Encoding.UTF8.GetBytes("<Fisresults><Raceheader><Codex>9991</Codex></Raceheader></Fisresults>");
        var calls = 0;
        using var handler = new Handler(async (request, ct) =>
        {
            calls++;
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://profile.fis-ski.com/api/results/competition-files/upload", request.RequestUri!.ToString());
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal(Token, request.Headers.Authorization.Parameter);
            Assert.False(request.Headers.Contains("X-Api-Key"));
            var parts = Assert.IsType<MultipartFormDataContent>(request.Content).ToArray();
            Assert.Equal(2, parts.Length);
            var file = parts.Single(x => x.Headers.ContentDisposition!.Name!.Trim('"') == "files[]");
            Assert.Equal("2027AL9991.xml", file.Headers.ContentDisposition!.FileName!.Trim('"'));
            Assert.Equal(xml, await file.ReadAsByteArrayAsync(ct));
            Assert.Equal("true", await parts.Single(x => x.Headers.ContentDisposition!.Name!.Trim('"') == "testMode").ReadAsStringAsync(ct));
            return Response(Payload());
        });
        using var http = new HttpClient(handler); using var client = new FisResultSubmissionClient(http);
        var response = await client.UploadAsync("2027AL9991.xml", xml, Token);
        Assert.Equal(1, calls); Assert.True(response.Submission.TestMode); Assert.False(response.Submission.Summary.IsComplete);
        Assert.Contains("Synthetic processing comment", response.ResponseText);
    }

    [Fact]
    public async Task StatusUsesFixedTrustedEndpointAndStopsAtProcessedResponse()
    {
        using var handler = new Handler((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal($"https://profile.fis-ski.com/api/results/competition-files/status/{s_uuid:D}", request.RequestUri!.ToString());
            Assert.Equal(Token, request.Headers.Authorization!.Parameter);
            return Task.FromResult(Response(Payload(true)));
        });
        using var http = new HttpClient(handler); using var client = new FisResultSubmissionClient(http);
        var response = await client.PollAsync(s_uuid, Token);
        Assert.True(response.Submission.Summary.IsComplete);
        Assert.Equal("processed", Assert.Single(response.Submission.Files).Status);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.UnprocessableEntity)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task FailureDisplaysValidationBodyRedactsCredentialAndNeverRetriesPost(HttpStatusCode status)
    {
        var calls = 0;
        using var handler = new Handler((_, _) => { calls++; return Task.FromResult(Response($"{{\"message\":\"Invalid XML {Token}\",\"errors\":{{\"files.0\":[\"Invalid file name\"]}}}}", status)); });
        using var http = new HttpClient(handler); using var client = new FisResultSubmissionClient(http);
        var failure = await Assert.ThrowsAsync<FisSubmissionException>(() => client.UploadAsync("FIN9991.xml", [1], Token));
        Assert.Equal(1, calls); Assert.Contains("Invalid file name", failure.ResponseText); Assert.DoesNotContain(Token, failure.ResponseText);
        Assert.Equal(status == HttpStatusCode.ServiceUnavailable, failure.UploadMayHaveCompleted);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnconfirmedTestModeOrMalformedResponseNeverReportsSuccess(bool malformed)
    {
        using var handler = new Handler((_, _) => Task.FromResult(Response(malformed ? "not JSON" : Payload(testMode: false))));
        using var http = new HttpClient(handler); using var client = new FisResultSubmissionClient(http);
        var failure = await Assert.ThrowsAsync<FisSubmissionException>(() => client.UploadAsync("FIN9991.xml", [1], Token));
        Assert.True(failure.UploadMayHaveCompleted);
    }

    [Fact]
    public async Task MismatchedStatusIdAndExpiredSubmissionRemainRecoverable()
    {
        using var handler = new Handler((_, _) => Task.FromResult(Response(Payload())));
        using var http = new HttpClient(handler); using var client = new FisResultSubmissionClient(http);
        await Assert.ThrowsAsync<FisSubmissionException>(() => client.PollAsync(Guid.NewGuid(), Token));
        using var expiredHandler = new Handler((_, _) => Task.FromResult(Response("{\"message\":\"Not found\"}", HttpStatusCode.NotFound)));
        using var expiredHttp = new HttpClient(expiredHandler); using var expired = new FisResultSubmissionClient(expiredHttp);
        var error = await Assert.ThrowsAsync<FisSubmissionException>(() => expired.PollAsync(s_uuid, Token));
        Assert.Contains("24 hours", error.Message);
    }

    [Fact]
    public async Task PollingAcrossDifferentSubmissionsSharesFiveSecondLimit()
    {
        var calls = new List<long>(); var timer = Stopwatch.StartNew();
        using var handler = new Handler((_, _) => { calls.Add(timer.ElapsedMilliseconds); return Task.FromResult(Response(Payload(true))); });
        using var http = new HttpClient(handler); using var client = new FisResultSubmissionClient(http);
        await client.PollAsync(s_uuid, Token);
        await Assert.ThrowsAsync<FisSubmissionException>(() => client.PollAsync(Guid.NewGuid(), Token));
        Assert.True(calls[1] - calls[0] >= 4900);
    }

    [Fact]
    public async Task RateLimitRetryAfterPreventsImmediateRetryAndWaitingCanBeCancelled()
    {
        var calls = 0;
        using var handler = new Handler((_, _) =>
        {
            calls++; var response = Response("{\"message\":\"Rate limited\"}", HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new(TimeSpan.FromSeconds(30)); return Task.FromResult(response);
        });
        using var http = new HttpClient(handler); using var client = new FisResultSubmissionClient(http);
        await Assert.ThrowsAsync<FisSubmissionException>(() => client.PollAsync(s_uuid, Token));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.PollAsync(s_uuid, Token, cancellation.Token));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task InvalidLocalInputMakesNoNetworkRequests()
    {
        using var handler = new Handler((_, _) => throw new InvalidOperationException("Network must not be called"));
        using var http = new HttpClient(handler); using var client = new FisResultSubmissionClient(http);
        await Assert.ThrowsAsync<DomainValidationException>(() => client.UploadAsync("../FIN9991.xml", [1], Token));
        await Assert.ThrowsAsync<DomainValidationException>(() => client.UploadAsync("FIN9991.xml", new byte[2_000_001], Token));
        await Assert.ThrowsAsync<DomainValidationException>(() => client.UploadAsync("FIN9991.xml", [1], ""));
        await Assert.ThrowsAsync<DomainValidationException>(() => client.PollAsync(Guid.Empty, Token));
    }

    private static HttpResponseMessage Response(string json, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => respond(request, cancellationToken);
    }
}
