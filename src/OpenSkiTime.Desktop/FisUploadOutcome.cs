using System.Globalization;
using OpenSkiTime.Application;

namespace OpenSkiTime.Desktop;

public enum FisUploadTone { Neutral, Progress, Success, Warning, Error }

/// <summary>One file of a FIS submission as the operator reads it.</summary>
public sealed record FisFileOutcome(string FileName, string StatusLabel, FisUploadTone Tone, string Comment, string Race, string Updated)
{
    public bool IsSuccess => Tone == FisUploadTone.Success;
    public bool IsError => Tone == FisUploadTone.Error;
    public bool IsWarning => Tone == FisUploadTone.Warning;
    public bool IsProgress => Tone == FisUploadTone.Progress;
    public bool HasComment => Comment.Length > 0;
}

/// <summary>
/// The result of sending (or checking) a FIS submission, in the words of the operator: a verdict, what it means, what to
/// do next and the per-file outcome. The raw FIS response is kept for the response view only.
/// </summary>
public sealed record FisUploadOutcome(FisUploadTone Tone, string Title, string Detail, string Hint, bool IsTest, string Mode,
    string SubmissionId, string FileName, string SentAt, string Counts, IReadOnlyList<FisFileOutcome> Files,
    IReadOnlyList<string> Problems, string RawResponse)
{
    public bool IsSuccess => Tone == FisUploadTone.Success;
    public bool IsError => Tone == FisUploadTone.Error;
    public bool IsWarning => Tone == FisUploadTone.Warning;
    public bool IsProgress => Tone == FisUploadTone.Progress;
    public bool HasDetail => Detail.Length > 0;
    public bool HasHint => Hint.Length > 0;
    public bool HasSubmissionId => SubmissionId.Length > 0;
    public bool HasFiles => Files.Count > 0;
    public bool HasProblems => Problems.Count > 0;
    public bool HasCounts => Counts.Length > 0;
    public bool HasRawResponse => RawResponse.Length > 0;
}

public sealed record FisTestScenarioChoice(FisUploadTestScenario Scenario, string Label, string Detail)
{
    public override string ToString() => Label;
}

public static class FisUploadOutcomes
{
    public static FisUploadOutcome Notice(FisUploadTone tone, string title, string detail, string hint, bool test, string mode, string id, string fileName,
        IReadOnlyList<string>? problems = null, string raw = "")
        => new(tone, title, detail, hint, test, mode, id, fileName, DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture), "", [], problems ?? [], raw);

    // Turns the FIS answer into a verdict. A real submission and a test read the same; the title says which it was.
    public static FisUploadOutcome OutcomeOf(FisSubmissionResponse response, string mode, string sentFileName, bool pollingPaused)
    {
        ArgumentNullException.ThrowIfNull(response); ArgumentNullException.ThrowIfNull(mode); ArgumentNullException.ThrowIfNull(sentFileName);
        var s = response.Submission; var test = s.IsTest;
        var files = s.Files.Select(FileOutcome).ToArray();
        var counts = $"{s.Summary.Total} file{(s.Summary.Total == 1 ? "" : "s")} · {s.Summary.Accepted} queued · {s.Summary.Processed} processed · {s.Summary.Rejected} rejected";
        var comment = files.Select(x => x.Comment).FirstOrDefault(x => x.Length > 0) ?? "";
        FisUploadTone tone; string title, detail, hint = "";
        if (s.Summary.IsComplete && s.Summary.Rejected == s.Summary.Total)
        {
            tone = FisUploadTone.Error;
            title = test ? "FIS rejected the test upload" : "FIS rejected the results";
            detail = comment.Length > 0 ? comment : s.Message ?? "";
            hint = RejectionHint(comment, mode, test);
        }
        else if (s.Summary.IsComplete && s.Summary.Processed == s.Summary.Total)
        {
            tone = FisUploadTone.Success;
            title = test ? "Test passed · FIS processed the file" : "Results accepted by FIS";
            detail = test ? "Nothing was stored at FIS: this was a test." : "FIS has stored and processed the results.";
        }
        else if (s.Summary.IsComplete)
        {
            tone = FisUploadTone.Warning; title = "FIS accepted only part of the upload"; detail = comment.Length > 0 ? comment : s.Message ?? "";
            hint = "Check the files below.";
        }
        else if (pollingPaused)
        {
            tone = FisUploadTone.Warning; title = "FIS is still processing";
            detail = "Automatic checking stopped after two minutes.";
            hint = "Press Check status to continue.";
        }
        else
        {
            tone = FisUploadTone.Progress; title = "FIS received the file";
            detail = test ? "Waiting for the simulated processing, about a minute." : "Waiting for FIS to process it.";
        }
        return new(tone, title, detail, hint, test, test ? mode : "LIVE", s.Uuid.ToString("D"),
            files.Length == 1 && sentFileName.Length == 0 ? files[0].FileName : sentFileName, DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
            counts, files, [], response.ResponseText);
    }

    private static string RejectionHint(string comment, string mode, bool test)
    {
        if (comment.Contains("not found", StringComparison.OrdinalIgnoreCase))
        {
            return test ? "In test mode FIS knows only the reserved codices. Use Simulate success to test the whole route."
                : "FIS does not know this race codex. Check the codex in Competitions.";
        }
        if (comment.Contains("Not allowed", StringComparison.OrdinalIgnoreCase))
        {
            return test && mode.Contains("no permission", StringComparison.OrdinalIgnoreCase)
                ? "This is the simulated permission failure (codex 9992), as expected."
                : "Your FIS account may not upload results for this race.";
        }
        return "Fix the problem and send again.";
    }

    private static FisFileOutcome FileOutcome(FisSubmissionFile file)
    {
        var (label, tone) = file.Status switch
        {
            "processed" => ("Processed", FisUploadTone.Success),
            "rejected" => ("Rejected", FisUploadTone.Error),
            "accepted" => ("Queued", FisUploadTone.Progress),
            _ => (file.Status, FisUploadTone.Neutral),
        };
        var race = file.RaceCodex is { Length: > 0 } codex
            ? $"{file.SectorCode} {file.SeasonCode} · codex {codex}".Trim() : "";
        return new(file.FileName, label, tone, file.Comments ?? "", race,
            file.UpdatedAt?.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture) ?? "");
    }

    // Every failure is shown as a verdict with the next step; the raw answer stays behind View FIS response.
    public static FisUploadOutcome FailureOutcome(Exception ex, bool test, string mode, string id, string file)
    {
        var raw = (ex as FisSubmissionException)?.ResponseText ?? "";
        FisUploadOutcome Make(FisUploadTone tone, string title, string detail, string hint, string submission = "", IReadOnlyList<string>? problems = null)
            => Notice(tone, title, detail, hint, test, mode, submission.Length > 0 ? submission : id, file, problems, raw);
        switch (ex)
        {
            case FisSubmissionException { Kind: FisSubmissionFailure.Unauthorized } f:
                return Make(FisUploadTone.Error, "FIS did not accept the token", f.Message, "Save a valid token in Settings → FIS Member Section token.");
            case FisSubmissionException { Kind: FisSubmissionFailure.Forbidden } f:
                return Make(FisUploadTone.Error, "The token lacks permission", f.Message, "Create a new token with both scopes and save it in Settings.");
            case FisSubmissionException { Kind: FisSubmissionFailure.Validation } f:
                return Make(FisUploadTone.Error, "FIS rejected the file", f.Details.Count == 0 ? f.Message : "FIS found these problems:", "Fix them and send again.", problems: f.Details);
            case FisSubmissionException { Kind: FisSubmissionFailure.Duplicate } f:
                return Make(FisUploadTone.Warning, "Already sent", f.Message, "Press Check status to see the result of the earlier upload.", f.ExistingUuid?.ToString("D") ?? "");
            case FisSubmissionException { Kind: FisSubmissionFailure.NotFound } f:
                return Make(FisUploadTone.Warning, "FIS no longer has this submission", f.Message, "Send the XML again.");
            case FisSubmissionException { Kind: FisSubmissionFailure.RateLimited } f:
                return Make(FisUploadTone.Warning, "Too many requests", f.Message, "Wait a few seconds and press Check status.");
            case FisSubmissionException f:
                return Make(FisUploadTone.Error, "FIS gave no usable answer", f.Message,
                    f.UploadMayHaveCompleted ? "The upload may have reached FIS. Check status before sending again." : "Try again in a moment.");
            case OperationCanceledException:
                return Make(FisUploadTone.Warning, "Stopped waiting for FIS", "The request was stopped or timed out.",
                    "An upload may have reached FIS. Press Check status before sending again.");
            case HttpRequestException:
                return Make(FisUploadTone.Error, "Could not reach FIS", "The network request failed.",
                    "An upload may have reached FIS. Check the connection, then press Check status before sending again.");
            default:
                return Make(FisUploadTone.Error, "Could not send", ex.Message, "Nothing was sent to FIS.");
        }
    }
}
