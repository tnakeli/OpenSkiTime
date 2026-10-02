using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenSkiTime.Rewrite.Application;
using OpenSkiTime.Rewrite.Domain;

namespace OpenSkiTime.Rewrite.Desktop;

public sealed partial class MainViewModel
{
    private readonly HttpClient _submissionHttp = new(new HttpClientHandler { AllowAutoRedirect = false })
        { Timeout = TimeSpan.FromSeconds(30) };
    private FisResultSubmissionClient? _submissionClient;
    private CancellationTokenSource? _submissionCancellation;
    private bool _submissionDisposed;
    private readonly Dictionary<(string?, Guid), (string Id, string Status, string Response)> _submissionDisplays = [];
    private (string?, Guid)? _submissionDisplayKey;
    [ObservableProperty] private string _fisMemberTokenInput = "";
    [ObservableProperty] private bool _isSubmissionBusy;
    [ObservableProperty] private string _resultSubmissionId = "";
    [ObservableProperty] private string _resultSubmissionStatus = "Test mode is fixed on. Select an approved XML revision to send.";
    [ObservableProperty] private string _resultSubmissionResponse = "";
    public bool CanSendResultsXml => SelectedResultApproval is not null && !IsSubmissionBusy;
    public bool CanCheckResultsSubmission => SelectedResultApproval is not null && !IsSubmissionBusy
        && Guid.TryParse(ResultSubmissionId, out var id) && id != Guid.Empty;

    partial void OnIsSubmissionBusyChanged(bool value)
    { OnPropertyChanged(nameof(CanSendResultsXml)); OnPropertyChanged(nameof(CanCheckResultsSubmission)); }
    partial void OnResultSubmissionIdChanged(string value) => OnPropertyChanged(nameof(CanCheckResultsSubmission));
    partial void OnSelectedResultApprovalChanged(ApprovedResult? value)
    {
        RememberSubmissionDisplay();
        _submissionDisplayKey = value is null ? null : (workspace.FilePath, value.Id);
        if (_submissionDisplayKey is { } key && _submissionDisplays.TryGetValue(key, out var saved))
        { ResultSubmissionId = saved.Id; ResultSubmissionStatus = saved.Status; ResultSubmissionResponse = saved.Response; }
        else
        {
            ResultSubmissionId = ""; ResultSubmissionResponse = "";
            ResultSubmissionStatus = "Test mode is fixed on. Select an approved XML revision to send.";
        }
        OnPropertyChanged(nameof(CanSendResultsXml));
    }

    private void RememberSubmissionDisplay()
    {
        if (_submissionDisplayKey is { } key)
        { _submissionDisplays[key] = (ResultSubmissionId, ResultSubmissionStatus, ResultSubmissionResponse); }
    }

    [RelayCommand] private void SaveFisMemberToken()
    {
        try { _fisStore.SaveMemberToken(FisMemberTokenInput); FisMemberTokenInput = ""; SetStatus("FIS Member Section token saved in Windows Credential Manager."); }
        catch (Exception ex) when (ex is IOException or ArgumentException or PlatformNotSupportedException) { SetStatus(ex.Message, error: true); }
    }
    [RelayCommand] private void RemoveFisMemberToken()
    {
        try { _fisStore.RemoveMemberToken(); FisMemberTokenInput = ""; SetStatus("FIS Member Section token removed."); }
        catch (Exception ex) when (ex is IOException or PlatformNotSupportedException) { SetStatus(ex.Message, error: true); }
    }

    [RelayCommand]
    private async Task SendApprovedXmlTestAsync()
    {
        if (SelectedResultApproval is not { } approval || IsSubmissionBusy) { return; }
        var key = (workspace.FilePath, approval.Id);
        await SubmissionRequestAsync(key, async (client, token, ct) =>
        {
            PublishSubmission(key, "", "Uploading approved XML · testMode=true…", "");
            // Exact approved bytes and filename; no codex replacement and no retry of POST.
            var response = await client.UploadAsync(approval.XmlFileName, approval.Xml, token, ct);
            await FollowSubmissionAsync(key, client, token, response, ct);
        });
    }

    [RelayCommand]
    private async Task CheckResultsSubmissionAsync()
    {
        if (IsSubmissionBusy || !Guid.TryParse(ResultSubmissionId, out var uuid) || uuid == Guid.Empty) { return; }
        var key = _submissionDisplayKey;
        if (key is null) { return; }
        RememberSubmissionDisplay();
        await SubmissionRequestAsync(key.Value, async (client, token, ct) =>
        {
            var response = await client.PollAsync(uuid, token, ct);
            await FollowSubmissionAsync(key.Value, client, token, response, ct);
        });
    }

    private async Task FollowSubmissionAsync((string?, Guid) key, FisResultSubmissionClient client, string token,
        FisSubmissionResponse response, CancellationToken ct)
    {
        for (var poll = 0; ; poll++)
        {
            var s = response.Submission;
            var status = $"TEST · {s.Uuid:D} · {s.Outcome} · accepted {s.Summary.Accepted}, processed {s.Summary.Processed}, rejected {s.Summary.Rejected}"
                + (s.Summary.IsComplete ? " · complete" : " · waiting for processing");
            status += "\n" + string.Join("\n", s.Files.Select(x => $"{x.FileName}: {x.Status} · {x.Comments}"));
            if (s.Summary.IsComplete || poll >= 24)
            {
                if (!s.Summary.IsComplete) { status += "\nAutomatic polling paused after two minutes. Use Check status to continue."; }
                PublishSubmission(key, s.Uuid.ToString("D"), status, response.ResponseText);
                return;
            }
            PublishSubmission(key, s.Uuid.ToString("D"), status, response.ResponseText);
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
            response = await client.PollAsync(s.Uuid, token, ct);
        }
    }

    private void PublishSubmission((string?, Guid) key, string id, string status, string response)
    {
        _submissionDisplays[key] = (id, status, response);
        if (_submissionDisplayKey != key) { return; }
        ResultSubmissionId = id; ResultSubmissionStatus = status; ResultSubmissionResponse = response;
    }

    private async Task SubmissionRequestAsync((string?, Guid) key,
        Func<FisResultSubmissionClient, string, CancellationToken, Task> action)
    {
        if (IsSubmissionBusy) { return; }
        IsSubmissionBusy = true;
        using var cancellation = new CancellationTokenSource();
        _submissionCancellation = cancellation;
        try
        {
            var token = _fisStore.ReadMemberToken() ?? "";
            _submissionClient ??= new(_submissionHttp);
            await action(_submissionClient, token, cancellation.Token);
        }
        catch (Exception ex) when (ex is DomainValidationException or IOException or HttpRequestException
            or OperationCanceledException or PlatformNotSupportedException or ArgumentException)
        {
            var previous = _submissionDisplays.GetValueOrDefault(key);
            var message = ex switch
            {
                OperationCanceledException => "Request stopped or timed out. An upload may have reached FIS. Keep the submission ID and check status before sending again.",
                HttpRequestException => "FIS network request failed. An upload may have reached FIS. Check status before sending again.",
                _ => ex.Message
            };
            PublishSubmission(key, previous.Id ?? "", message, ex is FisSubmissionException failure ? failure.ResponseText : previous.Response ?? "");
        }
        finally { _submissionCancellation = null; IsSubmissionBusy = false; if (_submissionDisposed) { _submissionClient?.Dispose(); } }
    }

    [RelayCommand] private void StopResultsSubmissionPolling() => _submissionCancellation?.Cancel();
    private void DisposeResultSubmission()
    {
        _submissionDisposed = true; _submissionCancellation?.Cancel(); _submissionHttp.Dispose();
        if (!IsSubmissionBusy) { _submissionClient?.Dispose(); }
    }
}
