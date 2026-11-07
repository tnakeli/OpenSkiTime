using System.Net;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using OpenSkiTime.Application;
using OpenSkiTime.Domain;
using Xunit;

namespace OpenSkiTime.Tests;

public sealed class FisProductionSubmissionTests
{
    private const string Token = "synthetic-member-token";
    private static readonly Guid s_uuid = new("00000000-0000-0000-0000-0000000000b2");

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<string?> TestModeFields { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string? testMode = null;
            if (request.Content is MultipartFormDataContent form)
            {
                foreach (var part in form.Where(x => x.Headers.ContentDisposition!.Name!.Trim('"') == "testMode"))
                { testMode = await part.ReadAsStringAsync(cancellationToken); }
            }
            TestModeFields.Add(testMode);
            return answer(request);
        }
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static string Answer(bool? testMode, bool complete = true) => JsonSerializer.Serialize(new Dictionary<string, object?>
    {
        ["uuid"] = s_uuid, ["outcome"] = "success", ["message"] = "Competition files uploaded successfully.",
        ["summary"] = new { total = 1, accepted = complete ? 0 : 1, processed = complete ? 1 : 0, rejected = 0, isComplete = complete },
        ["files"] = new[] { new { index = 0, fileName = "FIN0592.xml", detectedType = "XML", status = complete ? "processed" : "accepted",
            sectorCode = "AL", seasonCode = 2026, raceCodex = "0592", comments = (string?)null, updatedAt = "2026-11-07T10:38:53+01:00" } },
        ["testMode"] = testMode,
    });

    [Fact]
    public async Task RealUploadLeavesTestModeOutAndAcceptsAResponseWithoutIt()
    {
        using var handler = new RecordingHandler(_ => Json(Answer(testMode: null)));
        using var http = new HttpClient(handler); using var client = new FisResultSubmissionClient(http);
        var response = await client.UploadAsync("FIN0592.xml", [1], Token, testMode: false);
        Assert.Null(Assert.Single(handler.TestModeFields));
        Assert.False(response.Submission.IsTest); Assert.True(response.Submission.Summary.IsComplete);
        var file = Assert.Single(response.Submission.Files);
        Assert.Equal("AL", file.SectorCode); Assert.Equal(2026, file.SeasonCode); Assert.Equal("0592", file.RaceCodex);
        Assert.Equal("XML", file.DetectedType); Assert.NotNull(file.UpdatedAt);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, null)]
    public async Task AResponseThatContradictsTheRequestedModeIsNeverReportedAsSuccess(bool requestedTest, bool? answeredTest)
    {
        using var handler = new RecordingHandler(_ => Json(Answer(answeredTest)));
        using var http = new HttpClient(handler); using var client = new FisResultSubmissionClient(http);
        var failure = await Assert.ThrowsAsync<FisSubmissionException>(() => client.UploadAsync("FIN0592.xml", [1], Token, requestedTest));
        Assert.Equal(FisSubmissionFailure.Unsupported, failure.Kind); Assert.True(failure.UploadMayHaveCompleted);
    }

    [Fact]
    public async Task StatusAcceptsEitherModeBecauseTheIdAloneDoesNotTellIt()
    {
        foreach (var testMode in new bool?[] { true, null })
        {
            using var handler = new RecordingHandler(_ => Json(Answer(testMode)));
            using var http = new HttpClient(handler); using var client = new FisResultSubmissionClient(http);
            Assert.Equal(testMode == true, (await client.PollAsync(s_uuid, Token)).Submission.IsTest);
        }
    }

    [Fact]
    public async Task DuplicateUploadIsReportedWithTheExistingSubmission()
    {
        using var handler = new RecordingHandler(_ => Json($"{{\"message\":\"Duplicate upload detected.\",\"uuid\":\"{s_uuid}\",\"statusUrl\":\"https://x.invalid\"}}", HttpStatusCode.TooManyRequests));
        using var http = new HttpClient(handler); using var client = new FisResultSubmissionClient(http);
        var failure = await Assert.ThrowsAsync<FisSubmissionException>(() => client.UploadAsync("FIN0592.xml", [1], Token, testMode: false));
        Assert.Equal(FisSubmissionFailure.Duplicate, failure.Kind); Assert.Equal(s_uuid, failure.ExistingUuid);
        Assert.False(failure.UploadMayHaveCompleted); Assert.Single(handler.TestModeFields);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "{\"message\":\"Unauthenticated.\"}", FisSubmissionFailure.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden, "{\"message\":\"Invalid scope(s) provided.\"}", FisSubmissionFailure.Forbidden)]
    [InlineData(HttpStatusCode.Unauthorized, "{\"message\":\"Invalid scope(s) provided.\"}", FisSubmissionFailure.Forbidden)]
    [InlineData(HttpStatusCode.UnprocessableEntity, "{\"message\":\"x\",\"errors\":{\"files.0\":[\"Bad name\"],\"replyTo\":[\"Not yours\"]}}", FisSubmissionFailure.Validation)]
    [InlineData(HttpStatusCode.NotFound, "{\"message\":\"Not Found\"}", FisSubmissionFailure.NotFound)]
    [InlineData(HttpStatusCode.TooManyRequests, "{\"message\":\"Too Many Attempts.\"}", FisSubmissionFailure.RateLimited)]
    [InlineData(HttpStatusCode.ServiceUnavailable, "{\"message\":\"Try later\"}", FisSubmissionFailure.Server)]
    public async Task FailuresAreClassifiedForTheOperator(HttpStatusCode status, string body, FisSubmissionFailure kind)
    {
        using var handler = new RecordingHandler(_ => Json(body, status));
        using var http = new HttpClient(handler); using var client = new FisResultSubmissionClient(http);
        var failure = await Assert.ThrowsAsync<FisSubmissionException>(() => client.UploadAsync("FIN0592.xml", [1], Token, testMode: false));
        Assert.Equal(kind, failure.Kind);
        if (kind == FisSubmissionFailure.Validation) { Assert.Equal(["Bad name", "Not yours"], failure.Details); }
        if (kind == FisSubmissionFailure.Forbidden) { Assert.Contains("Read submitted competition files", failure.Message, StringComparison.Ordinal); }
        Assert.Equal(kind == FisSubmissionFailure.Server, failure.UploadMayHaveCompleted);
    }

    private static byte[] Xml(string codex) => Encoding.UTF8.GetBytes($"<?xml version=\"1.0\" encoding=\"UTF-8\"?><Fisresults><Raceheader Sector=\"AL\"><Season>2027</Season><Codex>{codex}</Codex></Raceheader><Other><Codex>keep</Codex></Other></Fisresults>");

    [Theory]
    [InlineData(FisUploadTestScenario.SimulateSuccess, "9991", "FIN9991.xml")]
    [InlineData(FisUploadTestScenario.SimulateNoPermission, "9992", "FIN9992.xml")]
    public void SimulationReplacesTheRaceHeaderCodexAndTheFileNameOnACopy(FisUploadTestScenario scenario, string codex, string expectedName)
    {
        var original = Xml("0592");
        var payload = FisTestPayloads.Prepare("FIN0592.xml", original, scenario);
        Assert.Equal(expectedName, payload.FileName); Assert.Equal("0592", payload.ReplacedCodex); Assert.Equal(codex, payload.SimulatedCodex);
        var document = XDocument.Load(new MemoryStream(payload.Xml));
        Assert.Equal(codex, document.Root!.Element("Raceheader")!.Element("Codex")!.Value);
        Assert.Equal("keep", document.Root.Element("Other")!.Element("Codex")!.Value);
        Assert.Equal("2027", document.Root.Element("Raceheader")!.Element("Season")!.Value);
        Assert.Equal("0592", XDocument.Load(new MemoryStream(original)).Root!.Element("Raceheader")!.Element("Codex")!.Value);
    }

    [Fact]
    public void SimulationKeepsTheCodexPositionOfAnyFileNameAndTheLastOccurrenceWins()
    {
        // 0592 also appears as a year-like prefix; only the codex at the end of the name is replaced.
        var payload = FisTestPayloads.Prepare("05922026AL0592TR.xml", Xml("0592"), FisUploadTestScenario.SimulateSuccess);
        Assert.Equal("05922026AL9991TR.xml", payload.FileName);
        Assert.Equal("FIN-9992.xml", FisTestPayloads.Prepare("FIN.xml", Xml("0592"), FisUploadTestScenario.SimulateNoPermission).FileName);
    }

    [Fact]
    public void StoredResponseTextReadsBackAsATypedResponseAndAnythingElseDoesNot()
    {
        var text = Answer(testMode: true);
        var response = FisResultSubmissionClient.ReadResponse(text);
        Assert.NotNull(response); Assert.True(response.Submission.IsTest); Assert.Equal(s_uuid, response.Submission.Uuid);
        Assert.Equal(text, response.ResponseText); Assert.Equal("0592", Assert.Single(response.Submission.Files).RaceCodex);
        Assert.Null(FisResultSubmissionClient.ReadResponse(null)); Assert.Null(FisResultSubmissionClient.ReadResponse(""));
        Assert.Null(FisResultSubmissionClient.ReadResponse("not json")); Assert.Null(FisResultSubmissionClient.ReadResponse("{\"message\":\"x\"}"));
    }

    [Fact]
    public void ValidateOnlySendsTheFileExactlyAsItIs()
    {
        var original = Xml("0592");
        var payload = FisTestPayloads.Prepare("FIN0592.xml", original, FisUploadTestScenario.ValidateOnly);
        Assert.Same(original, payload.Xml); Assert.Equal("FIN0592.xml", payload.FileName); Assert.Null(payload.SimulatedCodex);
    }

    [Fact]
    public void SimulationNeedsAWellFormedXmlWithARaceCodex()
    {
        Assert.Throws<DomainValidationException>(() => FisTestPayloads.Prepare("FIN0592.xml", Encoding.UTF8.GetBytes("<not xml"), FisUploadTestScenario.SimulateSuccess));
        Assert.Throws<DomainValidationException>(() => FisTestPayloads.Prepare("FIN0592.xml", Encoding.UTF8.GetBytes("<Fisresults />"), FisUploadTestScenario.SimulateSuccess));
    }
}
