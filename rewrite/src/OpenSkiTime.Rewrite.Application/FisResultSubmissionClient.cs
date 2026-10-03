using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using OpenSkiTime.Rewrite.Domain;

namespace OpenSkiTime.Rewrite.Application;

public sealed record FisSubmissionSummary(int Total, int Accepted, int Processed, int Rejected, bool IsComplete);
public sealed record FisSubmissionFile(string FileName, string Status, string? Comments);
public sealed record FisSubmission(Guid Uuid, bool TestMode, string Outcome, string? Message,
    FisSubmissionSummary Summary, FisSubmissionFile[] Files);
public sealed record FisSubmissionResponse(FisSubmission Submission, string ResponseText);
public sealed record ApprovedFisXmlArtifact(Guid ApprovalId, string FileName, byte[] Xml);

public sealed class FisSubmissionException(string message, string responseText, bool uploadMayHaveCompleted = false)
    : IOException(message)
{
    public string ResponseText { get; } = responseText;
    public bool UploadMayHaveCompleted { get; } = uploadMayHaveCompleted;
}

/// <summary>Shared FIS XML submission API for approved results and timing reports. Production submissions are unavailable.</summary>
public sealed class FisResultSubmissionClient(HttpClient client, TimeProvider? timeProvider = null) : IDisposable
{
    public const bool TestMode = true;
    private const string BaseUrl = "https://profile.fis-ski.com/api/results/competition-files/";
    private static readonly JsonSerializerOptions s_json = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset _nextPoll;

    public Task<FisSubmissionResponse> UploadAsync(ApprovedFisXmlArtifact artifact, string apiKey, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        if (artifact.ApprovalId == Guid.Empty) { throw new DomainValidationException("Approve the XML before sending it to FIS."); }
        return UploadAsync(artifact.FileName, artifact.Xml, apiKey, ct);
    }

    public async Task<FisSubmissionResponse> UploadAsync(string fileName, byte[] xml, string token, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(xml);
        ValidateToken(token);
        if (string.IsNullOrWhiteSpace(fileName) || fileName != Path.GetFileName(fileName)
            || fileName.Contains('\\') || fileName.Contains('/') || fileName.Any(char.IsControl)
            || !fileName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
        { throw new DomainValidationException("Choose an approved XML file with a plain .xml file name."); }
        if (xml.Length is 0 or > 2_000_000) { throw new DomainValidationException("FIS XML must be between 1 byte and 2 MB."); }
        // Take a copy: no later editor or caller mutation can change the approved payload during upload.
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(xml.ToArray());
        file.Headers.ContentType = new MediaTypeHeaderValue("application/xml");
        form.Add(file, "files[]", fileName);
        form.Add(new StringContent("true"), "testMode");
        using var request = Request(HttpMethod.Post, BaseUrl + "upload", token);
        request.Content = form;
        return await SendAsync(request, token, null, ct);
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
            return await SendAsync(request, token, uuid, ct);
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
        { throw new DomainValidationException("Save a valid FIS API key in Settings first."); }
    }

    public void Dispose() => _gate.Dispose();

    private static HttpRequestMessage Request(HttpMethod method, string url, string token)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.ParseAdd("application/json");
        return request;
    }

    private async Task<FisSubmissionResponse> SendAsync(HttpRequestMessage request, string token, Guid? expectedUuid, CancellationToken ct)
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
            { throw new FisSubmissionException("FIS response exceeded the display limit. Check the submission status before retrying an upload.", "Response exceeds 1 MB.", request.Method == HttpMethod.Post); }
            bytes.Write(buffer, 0, count);
        }
        var text = Encoding.UTF8.GetString(bytes.ToArray()).Replace(token, "[credential removed]", StringComparison.Ordinal);
        if (!response.IsSuccessStatusCode)
        {
            var message = response.StatusCode switch
            {
                HttpStatusCode.Unauthorized => "FIS rejected the API key. Check Settings.",
                HttpStatusCode.Forbidden => "The FIS API key lacks competition file permissions. Required scopes: competition.files.write and competition.files.read.",
                HttpStatusCode.UnprocessableEntity => "FIS rejected the upload validation. Review the response below.",
                HttpStatusCode.NotFound => "FIS submission was not found. Test submissions expire after 24 hours.",
                HttpStatusCode.TooManyRequests => "FIS rate limit reached. Wait before checking status again.",
                _ => $"FIS request failed (HTTP {(int)response.StatusCode}). Review the response below."
            };
            throw new FisSubmissionException(message, text, request.Method == HttpMethod.Post && (int)response.StatusCode >= 500);
        }
        try
        {
            var value = JsonSerializer.Deserialize<FisSubmission>(text, s_json);
            if (value is null || value.Uuid == Guid.Empty || !value.TestMode || value.Summary is null || value.Files is null
                || (expectedUuid is { } id && value.Uuid != id))
            { throw new JsonException(); }
            using var document = JsonDocument.Parse(text);
            return new(value, JsonSerializer.Serialize(document.RootElement, s_json));
        }
        catch (JsonException)
        { throw new FisSubmissionException("FIS returned an unsupported response or did not confirm test mode. Check status before uploading again.", text, request.Method == HttpMethod.Post); }
    }
}
