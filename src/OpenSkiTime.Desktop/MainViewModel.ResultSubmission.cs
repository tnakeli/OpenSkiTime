using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenSkiTime.Application;
using OpenSkiTime.Domain;

namespace OpenSkiTime.Desktop;

public sealed partial class MainViewModel
{
    private readonly bool _ownsSubmissionHttp = submissionHttp is null;
    private readonly HttpClient _submissionHttp = submissionHttp ?? new(new HttpClientHandler { AllowAutoRedirect = false })
        { Timeout = TimeSpan.FromSeconds(30) };
    private FisResultSubmissionClient? _submissionClient;
    private CancellationTokenSource? _submissionCancellation;
    private bool _submissionDisposed;
    private int _submissionOperations;
    private bool _submissionClientDisposed;
    private readonly Dictionary<(string?, Guid), (string Id, FisUploadOutcome? Outcome)> _submissionDisplays = [];
    private (string?, Guid)? _submissionDisplayKey;
    // True while the display shows a pasted submission this revision never sent: it is not remembered.
    private bool _showingLookup;
    // Without a saved XML revision the key is the competition itself, so an answer never shows on another race.
    private (string?, Guid) CurrentSubmissionKey => _submissionDisplayKey ?? (workspace.FilePath, ResultsCompetition?.Id ?? Guid.Empty);

    // Real submissions are the default; test mode is a deliberate, visible choice that is not remembered between runs.
    [ObservableProperty] private bool _fisUploadTestMode;
    [ObservableProperty] private FisTestScenarioChoice _selectedFisTestScenario = s_fisTestScenarios[0];
    [ObservableProperty] private bool _isSendConfirmationOpen;
    [ObservableProperty] private bool _isSubmissionBusy;
    [ObservableProperty] private bool _isFisResponseVisible;
    [ObservableProperty] private string _resultSubmissionId = "";
    [ObservableProperty] private FisUploadOutcome? _resultSubmissionOutcome;
    [ObservableProperty] private string _resultsXmlFileName = "";
    [ObservableProperty] private string _resultsXmlState = "";
    [ObservableProperty] private string _resultsXmlSavedNote = "";

    private static readonly FisTestScenarioChoice[] s_fisTestScenarios =
    [
        new(FisUploadTestScenario.SimulateSuccess, "Simulate success", "Sends a copy with codex 9991. FIS accepts it and reports it processed about a minute later."),
        new(FisUploadTestScenario.SimulateNoPermission, "Simulate no permission", "Sends a copy with codex 9992. FIS rejects it as not allowed for this race."),
        new(FisUploadTestScenario.ValidateOnly, "Validate the XML as it is", "Sends the real XML. FIS checks its structure; the real codex is an unknown race in test mode."),
    ];
    public IReadOnlyList<FisTestScenarioChoice> FisTestScenarios { get; } = s_fisTestScenarios;

    public bool CanSendToFis => ResultsReady && !IsSubmissionBusy;
    public bool CanSaveFisXml => ResultsReady && !IsSubmissionBusy;
    public bool CanCheckResultsSubmission => !IsSubmissionBusy && Guid.TryParse(ResultSubmissionId, out var id) && id != Guid.Empty;
    public bool HasSubmissionOutcome => ResultSubmissionOutcome is not null;
    public bool NoSubmissionOutcome => ResultSubmissionOutcome is null;
    public string SendToFisLabel => FisUploadTestMode ? "Send to FIS · test" : "Send to FIS";
    public string SendConfirmationText => $"Send {ResultsXmlFileName} to FIS as the results of {ResultsCompetition?.Values.Name}? FIS stores and processes it.";
    public string FisTestScenarioDetail => SelectedFisTestScenario.Detail;
    public string FisResponseButtonLabel => IsFisResponseVisible ? "Hide FIS response" : "View FIS response";
    public string ResultsXmlReadyText => ResultsReady ? "" : "The XML can be created when the results and the penalty are available: " + (string.IsNullOrWhiteSpace(ResultsRuleSource) ? ResultsState : ResultsRuleSource);

    internal Task<FisSubmissionResponse> UploadApprovedFisArtifactTestAsync(ApprovedFisXmlArtifact artifact, CancellationToken ct = default)
        => SharedFisRequestAsync((client, key) => client.UploadAsync(artifact, key, ct));

    internal Task<FisSubmissionResponse> PollFisArtifactTestAsync(Guid uuid, CancellationToken ct = default)
        => SharedFisRequestAsync((client, key) => client.PollAsync(uuid, key, ct));

    private async Task<FisSubmissionResponse> SharedFisRequestAsync(Func<FisResultSubmissionClient, string, Task<FisSubmissionResponse>> action)
    {
        ObjectDisposedException.ThrowIf(_submissionDisposed, this);
        _submissionOperations++;
        try
        {
            _submissionClient ??= new(_submissionHttp);
            return await action(_submissionClient, _fisStore.ReadMemberToken() ?? "");
        }
        finally { _submissionOperations--; TryDisposeSubmissionClient(); }
    }

    partial void OnIsFisResponseVisibleChanged(bool value) => OnPropertyChanged(nameof(FisResponseButtonLabel));
    partial void OnSelectedFisTestScenarioChanged(FisTestScenarioChoice value) => OnPropertyChanged(nameof(FisTestScenarioDetail));
    partial void OnFisUploadTestModeChanged(bool value)
    { IsSendConfirmationOpen = false; IsReportSendConfirmationOpen = false; OnPropertyChanged(nameof(SendToFisLabel)); }
    partial void OnIsSendConfirmationOpenChanged(bool value) => OnPropertyChanged(nameof(SendConfirmationText));
    partial void OnResultsXmlFileNameChanged(string value) => OnPropertyChanged(nameof(SendConfirmationText));
    partial void OnResultSubmissionOutcomeChanged(FisUploadOutcome? value)
    { OnPropertyChanged(nameof(HasSubmissionOutcome)); OnPropertyChanged(nameof(NoSubmissionOutcome)); if (value is null) { IsFisResponseVisible = false; } }
    partial void OnIsSubmissionBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanSendToFis)); OnPropertyChanged(nameof(CanSaveFisXml)); OnPropertyChanged(nameof(CanCheckResultsSubmission));
    }
    partial void OnResultSubmissionIdChanged(string value) => OnPropertyChanged(nameof(CanCheckResultsSubmission));

    partial void OnSelectedResultApprovalChanged(ApprovedResult? value)
    {
        RememberSubmissionDisplay();
        _submissionDisplayKey = value is null ? null : (workspace.FilePath, value.Id);
        IsSendConfirmationOpen = false;
        ShowRememberedSubmission();
        RefreshResultsXmlInfo();
    }

    private void ShowRememberedSubmission()
    {
        _showingLookup = false;
        if (_submissionDisplays.TryGetValue(CurrentSubmissionKey, out var saved)) { ResultSubmissionId = saved.Id; ResultSubmissionOutcome = saved.Outcome; }
        else { ResultSubmissionId = ""; ResultSubmissionOutcome = null; }
    }

    private void RememberSubmissionDisplay()
    {
        if (_submissionDisplayKey is { } key && !_showingLookup) { _submissionDisplays[key] = (ResultSubmissionId, ResultSubmissionOutcome); }
    }

    // What the left card says about the XML: its file name and whether the saved revision still matches the results.
    private void RefreshResultsXmlInfo()
    {
        OnPropertyChanged(nameof(CanSendToFis)); OnPropertyChanged(nameof(CanSaveFisXml)); OnPropertyChanged(nameof(ResultsXmlReadyText));
        if (_resultRace is null || _current is null || ResultsCompetition is null) { ResultsXmlFileName = ""; ResultsXmlState = ""; return; }
        try { ResultsXmlFileName = FisResultXml.FileName(_resultRace with { FirstList = _resultRace.FirstList with
            { Plan = _resultRace.FirstList.Plan with { Competition = ResultsCompetition.Values } } }, _current.Values); }
        catch (DomainValidationException ex) { ResultsXmlFileName = ""; ResultsXmlState = ex.Message; return; }
        ResultsXmlState = SelectedResultApproval is { } latest
            ? $"Latest saved revision {latest.Revision} · {latest.ApprovedAt.ToLocalTime():yyyy-MM-dd HH:mm}. A new revision is saved when the results or race information change."
            : "The XML is created from the current results when you save or send it.";
    }

    // Creates the XML of the current results and stores it as an immutable revision in the event file. When the latest
    // revision is already exactly this XML for the same timing and race information, that revision is reused, so sending
    // twice never adds revisions.
    private async Task<ApprovedResult> EnsureResultsXmlAsync()
    {
        if (_resultRace is null || _resultFingerprint is null || ResultsCompetition is null || _current is null)
        { throw new DomainValidationException("Reload complete race results first."); }
        if (!await FlushRaceInformationAsync()) { throw new DomainValidationException("Resolve unsaved race information before creating the XML."); }
        if (_resultsRuleProfile is null || _reviewedPenalty is null)
        { throw new DomainValidationException("The penalty is not available: " + (string.IsNullOrWhiteSpace(ResultsRuleSource) ? ResultsState : ResultsRuleSource)); }
        var penalty = CalculateCurrentPenalty();
        var information = CurrentRaceInformation();
        var td = information.Jury.Single(x => x.Function == "TechnicalDelegate").Person;
        var chief = information.Jury.Single(x => x.Function == "ChiefRace").Person;
        var metadata = new FisXmlDetails(information.Category, td, chief,
            information.Runs.Select(x => new FisRunXmlDetails(x.Gates ?? 0, x.TurningGates ?? 0, x.StartTime, x.CourseSetter)).ToArray(), information);
        var xmlRace = _resultRace with { FirstList = _resultRace.FirstList with
        { Plan = _resultRace.FirstList.Plan with { Competition = ResultsCompetition.Values } } };
        var xml = FisResultXml.Create(xmlRace, _current.Values, penalty, metadata);
        var latest = ResultApprovals.FirstOrDefault();
        if (latest is not null && latest.SourceFingerprint == _resultFingerprint && latest.Xml.AsSpan().SequenceEqual(xml)
            && latest.AppliedPenalty == penalty.Applied && latest.CalculatedPenalty == penalty.Calculated
            && latest.Information is not null && System.Text.Json.JsonSerializer.Serialize(latest.Information) == System.Text.Json.JsonSerializer.Serialize(information))
        { return latest; }
        var competitionId = ResultsCompetition.Id;
        var path = workspace.FilePath;
        var expectedRevision = _current.Revision;
        var operatorName = string.IsNullOrWhiteSpace(TimingOperator) ? "Results operator" : TimingOperator;
        var saved = await workspace.ApproveResultAsync(new(competitionId, _resultRace.FirstList.Id, _resultRace.SecondList?.Id,
            _resultFingerprint, _current.Revision, operatorName, FisResultXml.FileName(xmlRace, _current.Values), xml,
            penalty.Calculated, penalty.Applied, information));
        if (workspace.FilePath == path && _current is not null)
        { _current = _current with { Revision = Math.Max(_current.Revision, expectedRevision + 1) }; }
        if (workspace.FilePath == path && ResultsCompetition?.Id == competitionId)
        { ResultApprovals.Insert(0, saved); SelectedResultApproval = saved; }
        return saved;
    }

    [RelayCommand]
    private async Task SaveFisXmlAsync()
    {
        await GuardAsync(async () =>
        {
            var approval = await EnsureResultsXmlAsync();
            var path = await dialogs.ChooseResultXmlExportAsync(approval.XmlFileName);
            if (path is null) { ResultsXmlSavedNote = ""; SetStatus($"FIS XML revision {approval.Revision} is saved in the event file."); return; }
            await ExportFile.WriteAsync(path, approval.Xml);
            ResultsXmlSavedNote = $"Saved {Path.GetFileName(path)} · revision {approval.Revision}";
            SetStatus($"FIS XML revision {approval.Revision} saved to {path}.");
        });
    }

    [RelayCommand]
    private async Task SendToFisAsync()
    {
        if (IsSubmissionBusy || !ResultsReady) { return; }
        // A real upload is stored by FIS: ask once, in place. A test needs no confirmation.
        if (!FisUploadTestMode) { RefreshResultsXmlInfo(); IsSendConfirmationOpen = true; return; }
        await SendCoreAsync(test: true);
    }

    [RelayCommand]
    private async Task ConfirmSendToFisAsync()
    {
        if (!IsSendConfirmationOpen) { return; }
        IsSendConfirmationOpen = false;
        // The confirmation was for a real send; it never turns into one after test mode was switched on.
        if (FisUploadTestMode) { return; }
        await SendCoreAsync(test: false);
    }

    [RelayCommand] private void CancelSendToFis() => IsSendConfirmationOpen = false;
    [RelayCommand] private void ToggleFisResponse() => IsFisResponseVisible = ResultSubmissionOutcome is { HasRawResponse: true } && !IsFisResponseVisible;

    private async Task SendCoreAsync(bool test)
    {
        if (IsSubmissionBusy) { return; }
        IsSubmissionBusy = true;
        _submissionOperations++;
        using var cancellation = new CancellationTokenSource();
        _submissionCancellation = cancellation;
        var mode = test ? "TEST · " + SelectedFisTestScenario.Label.ToLowerInvariant() : "LIVE";
        var key = CurrentSubmissionKey;
        try
        {
            ApprovedResult approval;
            try { approval = await EnsureResultsXmlAsync(); }
            catch (Exception ex) when (ex is DomainValidationException or SeriesFileException or SeriesConflictException or IOException)
            {
                var message = ex is DomainValidationException or SeriesFileException or SeriesConflictException ? ex.Message
                    : "The event file could not be written. Check the file and try again.";
                Publish(key, FisUploadOutcomes.Notice(FisUploadTone.Error, "XML not created", message, "Nothing was sent to FIS.", test, mode, "", ""));
                return;
            }
            key = (workspace.FilePath, approval.Id);
            await SendXmlToFisAsync(approval.XmlFileName, approval.Xml, test, mode, outcome => Publish(key, outcome), null, cancellation.Token);
        }
        catch (Exception ex) when (ex is DomainValidationException or FisSubmissionException or IOException or HttpRequestException
            or OperationCanceledException or PlatformNotSupportedException or ArgumentException)
        {
            Publish(key, FisUploadOutcomes.FailureOutcome(ex, test, mode, _submissionDisplays.GetValueOrDefault(key).Id ?? "", ResultSubmissionOutcome?.FileName ?? ""));
        }
        finally { _submissionCancellation = null; IsSubmissionBusy = false; _submissionOperations--; TryDisposeSubmissionClient(); }
    }

    [RelayCommand]
    private async Task CheckResultsSubmissionAsync()
    {
        if (IsSubmissionBusy || !Guid.TryParse(ResultSubmissionId, out var uuid) || uuid == Guid.Empty) { return; }
        var key = CurrentSubmissionKey;
        // The answer of a submission this revision never sent is shown, not remembered under it.
        var known = _submissionDisplays.GetValueOrDefault(key).Id == uuid.ToString("D");
        void Show(FisUploadOutcome outcome)
        {
            if (known) { Publish(key, outcome); }
            else if (CurrentSubmissionKey == key) { _showingLookup = true; ResultSubmissionOutcome = outcome; }
        }
        IsSubmissionBusy = true;
        _submissionOperations++;
        using var cancellation = new CancellationTokenSource();
        _submissionCancellation = cancellation;
        var mode = ResultSubmissionOutcome?.Mode ?? "";
        try
        {
            _submissionClient ??= new(_submissionHttp);
            var token = _fisStore.ReadMemberToken() ?? "";
            var response = await _submissionClient.PollAsync(uuid, token, cancellation.Token);
            await FollowFisSubmissionAsync(_submissionClient, token, response, response.Submission.IsTest ? "TEST" : "LIVE", "", Show, null, cancellation.Token);
        }
        catch (Exception ex) when (ex is DomainValidationException or FisSubmissionException or IOException or HttpRequestException
            or OperationCanceledException or PlatformNotSupportedException or ArgumentException)
        {
            Show(FisUploadOutcomes.FailureOutcome(ex, ResultSubmissionOutcome?.IsTest ?? false, mode, uuid.ToString("D"), ResultSubmissionOutcome?.FileName ?? ""));
        }
        finally { _submissionCancellation = null; IsSubmissionBusy = false; _submissionOperations--; TryDisposeSubmissionClient(); }
    }

    // The XML of any FIS view goes out through here: the stored bytes in normal mode, the simulation copy in test mode.
    // Every state is published; a persisted answer is handed to the caller (the Timing report keeps its answers).
    private async Task SendXmlToFisAsync(string fileName, byte[] xml, bool test, string mode, Action<FisUploadOutcome> publish,
        Func<FisSubmissionResponse, Task>? persist, CancellationToken ct)
    {
        var payload = test ? FisTestPayloads.Prepare(fileName, xml, SelectedFisTestScenario.Scenario) : new FisTestPayload(fileName, xml, null, null);
        publish(FisUploadOutcomes.Notice(FisUploadTone.Progress, test ? "Sending test upload…" : "Sending to FIS…", "Waiting for the answer from FIS.", "", test, mode, "", payload.FileName));
        _submissionClient ??= new(_submissionHttp);
        var token = _fisStore.ReadMemberToken() ?? "";
        var response = await _submissionClient.UploadAsync(payload.FileName, payload.Xml, token, test, ct);
        await FollowFisSubmissionAsync(_submissionClient, token, response, mode, payload.FileName, publish, persist, ct);
    }

    private static async Task FollowFisSubmissionAsync(FisResultSubmissionClient client, string token, FisSubmissionResponse response, string mode,
        string sentFileName, Action<FisUploadOutcome> publish, Func<FisSubmissionResponse, Task>? persist, CancellationToken ct)
    {
        for (var poll = 0; ; poll++)
        {
            var paused = !response.Submission.Summary.IsComplete && poll >= 24;
            publish(FisUploadOutcomes.OutcomeOf(response, mode, sentFileName, paused));
            if (persist is not null) { await persist(response); }
            if (response.Submission.Summary.IsComplete || paused) { return; }
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
            response = await client.PollAsync(response.Submission.Uuid, token, ct);
        }
    }


    private void Publish((string?, Guid) key, FisUploadOutcome outcome)
    {
        _submissionDisplays[key] = (outcome.SubmissionId, outcome);
        if (CurrentSubmissionKey != key) { return; }
        _showingLookup = false;
        ResultSubmissionId = outcome.SubmissionId;
        ResultSubmissionOutcome = outcome;
    }

    [RelayCommand] private void StopResultsSubmissionPolling() => _submissionCancellation?.Cancel();
    private void DisposeResultSubmission()
    {
        _submissionDisposed = true; _submissionCancellation?.Cancel(); if (_ownsSubmissionHttp) { _submissionHttp.Dispose(); }
        TryDisposeSubmissionClient();
    }

    private void TryDisposeSubmissionClient()
    {
        if (_submissionDisposed && _submissionOperations == 0 && !_submissionClientDisposed)
        { _submissionClientDisposed = true; _submissionClient?.Dispose(); }
    }
}
