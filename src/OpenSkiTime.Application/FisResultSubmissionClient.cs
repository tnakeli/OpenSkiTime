using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using OpenSkiTime.Domain;

namespace OpenSkiTime.Application;

public sealed record FisSubmissionSummary(int Total, int Accepted, int Processed, int Rejected, bool IsComplete);
public sealed record FisSubmissionFile(string FileName, string Status, string? Comments, string? DetectedType = null,
    string? SectorCode = null, int? SeasonCode = null, string? RaceCodex = null, DateTimeOffset? UpdatedAt = null);
// TestMode is true only for a submission created with testMode=true; FIS leaves the field out (or null) for a real one.
public sealed record FisSubmission(Guid Uuid, bool? TestMode, string Outcome, string? Message,
    FisSubmissionSummary Summary, FisSubmissionFile[] Files)
{
    public bool IsTest => TestMode == true;
}
public sealed record FisSubmissionResponse(FisSubmission Submission, string ResponseText);
public sealed record ApprovedFisXmlArtifact(Guid ApprovalId, string FileName, byte[] Xml);

/// <summary>What kind of failure FIS or the network reported; the desktop turns this into an actionable message.</summary>
public enum FisSubmissionFailure { Other, Unauthorized, Forbidden, Validation, NotFound, RateLimited, Duplicate, Server, Unsupported }

public sealed class FisSubmissionException(string message, string responseText, bool uploadMayHaveCompleted = false,
    FisSubmissionFailure kind = FisSubmissionFailure.Other, Guid? existingUuid = null, IReadOnlyList<string>? details = null)
    : IOException(message)
{
    public string ResponseText { get; } = responseText;
    public bool UploadMayHaveCompleted { get; } = uploadMayHaveCompleted;
    public FisSubmissionFailure Kind { get; } = kind;
    /// <summary>The submission FIS already holds when an identical upload was refused as a duplicate.</summary>
    public Guid? ExistingUuid { get; } = existingUuid;
    /// <summary>Validation messages FIS returned, one per problem.</summary>
    public IReadOnlyList<string> Details { get; } = details ?? [];
}

/// <summary>
/// Shared FIS competition-file submission API for results and timing reports. A real upload is stored and processed by
/// FIS; with testMode FIS validates the file and stores nothing. Callers choose the mode explicitly: the overloads
/// without a mode are test only.
/// </summary>
public sealed class FisResultSubmissionClient(HttpClient client, TimeProvider? timeProvider = null) : IDisposable
{
    private const string BaseUrl = "https://profile.fis-ski.com/api/results/competition-files/";
    private static readonly JsonSerializerOptions s_json = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset _nextPoll;

    /// <summary>Test-mode upload of an approved artifact (timing report and tests).</summary>
    public Task<FisSubmissionResponse> UploadAsync(ApprovedFisXmlArtifact artifact, string apiKey, CancellationToken ct = default)
        => UploadAsync(artifact, apiKey, testMode: true, ct);

    public Task<FisSubmissionResponse> UploadAsync(ApprovedFisXmlArtifact artifact, string apiKey, bool testMode, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        if (artifact.ApprovalId == Guid.Empty) { throw new DomainValidationException("Create the XML before sending it to FIS."); }
        return UploadAsync(artifact.FileName, artifact.Xml, apiKey, testMode, ct);
    }

    /// <summary>Test-mode upload.</summary>
    public Task<FisSubmissionResponse> UploadAsync(string fileName, byte[] xml, string token, CancellationToken ct = default)
        => UploadAsync(fileName, xml, token, testMode: true, ct);

    public async Task<FisSubmissionResponse> UploadAsync(string fileName, byte[] xml, string token, bool testMode, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(xml);
        ValidateToken(token);
        if (string.IsNullOrWhiteSpace(fileName) || fileName != Path.GetFileName(fileName)
            || fileName.Contains('\\') || fileName.Contains('/') || fileName.Any(char.IsControl)
            || !fileName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
        { throw new DomainValidationException("Choose an XML file with a plain .xml file name."); }
        if (xml.Length is 0 or > 2_000_000) { throw new DomainValidationException("FIS XML must be between 1 byte and 2 MB."); }
        // Take a copy: no later editor or caller mutation can change the payload during upload.
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(xml.ToArray());
        file.Headers.ContentType = new MediaTypeHeaderValue("application/xml");
        form.Add(file, "files[]", fileName);
        // A real upload leaves testMode out, so it can never be mistaken for a test.
        if (testMode) { form.Add(new StringContent("true"), "testMode"); }
        using var request = Request(HttpMethod.Post, BaseUrl + "upload", token);
        request.Content = form;
        return await SendAsync(request, token, null, testMode, ct);
    }

    public async Task<FisSubmissionResponse> PollAsync(Guid uuid, string token, CancellationToken ct = default)
    {
        ValidateToken(token);
        if (uuid == Guid.Empty) { throw new DomainValidationException("No FIS submission identifier is available."); }
        // All submissions share one limiter. Do not send credentials to a returned statusUrl.
        await _gate.WaitAsync(ct);
        try
        {
            var delay = _nextPoll - _time.GetUtcNow();
            while (delay > TimeSpan.Zero)
            { await Task.Delay(delay, _time, ct); delay = _nextPoll - _time.GetUtcNow(); }
            using var request = Request(HttpMethod.Get, BaseUrl + "status/" + uuid.ToString("D"), token);
            // The mode of a submission is not known from its identifier alone, so the status may be either.
            return await SendAsync(request, token, uuid, null, ct);
        }
        finally
        {
            // Measure from completion: request construction or a slow response cannot shorten the spacing.
            var next = _time.GetUtcNow().AddSeconds(5);
            if (next > _nextPoll) { _nextPoll = next; }
            _gate.Release();
        }
    }

    private static void ValidateToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Any(char.IsWhiteSpace) || token.Any(char.IsControl))
        { throw new DomainValidationException("Save a FIS Member Section personal access token in Settings first."); }
    }

    public void Dispose() => _gate.Dispose();

    /// <summary>
    /// Reads a stored FIS response (the text kept with a saved submission) back into a typed response; null when it is not
    /// a FIS submission answer.
    /// </summary>
    public static FisSubmissionResponse? ReadResponse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) { return null; }
        try
        {
            var value = JsonSerializer.Deserialize<FisSubmission>(text, s_json);
            return value is null || value.Uuid == Guid.Empty || value.Summary is null || value.Files is null ? null : new(value, text);
        }
        catch (JsonException) { return null; }
    }

    private static HttpRequestMessage Request(HttpMethod method, string url, string token)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.ParseAdd("application/json");
        return request;
    }

    // expectedTestMode: true or false when the request itself decides the mode (an upload); null for a status check.
    private async Task<FisSubmissionResponse> SendAsync(HttpRequestMessage request, string token, Guid? expectedUuid, bool? expectedTestMode, CancellationToken ct)
    {
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        var retry = response.Headers.RetryAfter;
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var next = retry?.Date ?? _time.GetUtcNow().Add(retry?.Delta ?? TimeSpan.FromSeconds(5));
            if (next > _nextPoll) { _nextPoll = next; }
        }
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var bytes = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(buffer, ct)) > 0)
        {
            if (bytes.Length + count > 1_000_000)
            { throw new FisSubmissionException("FIS response exceeded the display limit. Check the submission status before retrying an upload.", "Response exceeds 1 MB.", request.Method == HttpMethod.Post, FisSubmissionFailure.Unsupported); }
            bytes.Write(buffer, 0, count);
        }
        var text = Encoding.UTF8.GetString(bytes.ToArray()).Replace(token, "[credential removed]", StringComparison.Ordinal);
        if (!response.IsSuccessStatusCode) { throw Failure(request, response.StatusCode, text); }
        try
        {
            var value = JsonSerializer.Deserialize<FisSubmission>(text, s_json);
            if (value is null || value.Uuid == Guid.Empty || value.Summary is null || value.Files is null
                || (expectedUuid is { } id && value.Uuid != id)
                // A response that contradicts the requested mode is never reported as success: a real upload must not
                // look like a test, and a test must not look like a stored submission.
                || (expectedTestMode is { } mode && value.IsTest != mode))
            { throw new JsonException(); }
            using var document = JsonDocument.Parse(text);
            return new(value, JsonSerializer.Serialize(document.RootElement, s_json));
        }
        catch (JsonException)
        {
            throw new FisSubmissionException(expectedTestMode == true
                ? "FIS returned an unsupported response or did not confirm test mode. Check status before uploading again."
                : "FIS returned an unsupported response. Check the submission status before uploading again.",
                text, request.Method == HttpMethod.Post, FisSubmissionFailure.Unsupported);
        }
    }

    private static FisSubmissionException Failure(HttpRequestMessage request, HttpStatusCode status, string text)
    {
        var post = request.Method == HttpMethod.Post;
        string? apiMessage = null; Guid? existing = null; List<string> details = [];
        try
        {
            using var document = JsonDocument.Parse(text);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                var root = document.RootElement;
                if (root.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String) { apiMessage = message.GetString(); }
                if (root.TryGetProperty("uuid", out var uuid) && uuid.ValueKind == JsonValueKind.String && Guid.TryParse(uuid.GetString(), out var parsed) && parsed != Guid.Empty) { existing = parsed; }
                if (root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Object)
                {
                    foreach (var field in errors.EnumerateObject())
                    {
                        if (field.Value.ValueKind != JsonValueKind.Array) { continue; }
                        foreach (var item in field.Value.EnumerateArray())
                        { if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } line) { details.Add(line); } }
                    }
                }
            }
        }
        catch (JsonException) { /* The raw text is kept for the response view. */ }
        // A token without the needed scope is answered as "Invalid scope(s) provided."; it is a permission problem, not a bad token.
        var scopeProblem = apiMessage?.Contains("scope", StringComparison.OrdinalIgnoreCase) == true;
        var (kind, message2) = status switch
        {
            HttpStatusCode.Unauthorized when scopeProblem => (FisSubmissionFailure.Forbidden, ScopeMessage),
            HttpStatusCode.Unauthorized => (FisSubmissionFailure.Unauthorized,
                "FIS did not accept the token. Save a personal access token from FIS Member Section in Settings; the public API key does not work for submissions."),
            HttpStatusCode.Forbidden => (FisSubmissionFailure.Forbidden, ScopeMessage),
            HttpStatusCode.UnprocessableEntity => (FisSubmissionFailure.Validation, "FIS rejected the upload validation."),
            HttpStatusCode.NotFound => (FisSubmissionFailure.NotFound, "FIS submission was not found. Test submissions expire after 24 hours."),
            HttpStatusCode.TooManyRequests when post && existing is not null => (FisSubmissionFailure.Duplicate,
                "FIS already has an identical upload from the last hour."),
            HttpStatusCode.TooManyRequests => (FisSubmissionFailure.RateLimited, "FIS rate limit reached. Wait before checking status again."),
            >= HttpStatusCode.InternalServerError => (FisSubmissionFailure.Server, $"FIS could not process the request (HTTP {(int)status})."),
            _ => (FisSubmissionFailure.Other, $"FIS request failed (HTTP {(int)status})."),
        };
        return new(message2, text, post && (int)status >= 500, kind, existing, details);
    }

    private const string ScopeMessage = "The FIS token lacks competition file permissions. Enable both scopes: Submit competition files and results, and Read submitted competition files and their processing status.";
}
