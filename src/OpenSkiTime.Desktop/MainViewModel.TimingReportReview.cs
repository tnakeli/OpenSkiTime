using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenSkiTime.Application;
using OpenSkiTime.Domain;

namespace OpenSkiTime.Desktop;

public sealed partial class MainViewModel
{
    [ObservableProperty] private FisUploadOutcome? _reportSubmissionOutcome;
    [ObservableProperty] private bool _isReportSendConfirmationOpen;
    [ObservableProperty] private bool _isReportFisResponseVisible;
    [ObservableProperty] private bool _isReportSubmitting;
    [ObservableProperty] private string _reportSubmissionId = "";
    [ObservableProperty] private string _reportXmlFileName = "";
    [ObservableProperty] private string _reportXmlState = "";
    [ObservableProperty] private string _reportXmlSavedNote = "";
    private readonly Dictionary<Guid, (string Id, FisUploadOutcome? Outcome)> _reportSubmissionDisplays = [];

    public bool CanSendReportToFis => !IsReportBusy && _reportDraft is not null;
    public bool CanCheckReportSubmission => !IsReportBusy && Guid.TryParse(ReportSubmissionId, out var id) && id != Guid.Empty;
    public bool HasReportSubmissionOutcome => ReportSubmissionOutcome is not null;
    public bool NoReportSubmissionOutcome => ReportSubmissionOutcome is null;
    public string ReportFisResponseButtonLabel => IsReportFisResponseVisible ? "Hide FIS response" : "View FIS response";
    public string ReportSendConfirmationText => $"Send {ReportXmlFileName} to FIS as the timing report of {ReportCompetition?.Values.Name}? FIS stores and processes it.";

    partial void OnIsReportBusyChanged(bool value) { OnPropertyChanged(nameof(CanSendReportToFis)); OnPropertyChanged(nameof(CanCheckReportSubmission)); }
    partial void OnReportSubmissionIdChanged(string value) => OnPropertyChanged(nameof(CanCheckReportSubmission));
    partial void OnIsReportFisResponseVisibleChanged(bool value) => OnPropertyChanged(nameof(ReportFisResponseButtonLabel));
    partial void OnIsReportSendConfirmationOpenChanged(bool value) => OnPropertyChanged(nameof(ReportSendConfirmationText));
    partial void OnReportXmlFileNameChanged(string value) => OnPropertyChanged(nameof(ReportSendConfirmationText));
    partial void OnReportSubmissionOutcomeChanged(FisUploadOutcome? value)
    {
        OnPropertyChanged(nameof(HasReportSubmissionOutcome)); OnPropertyChanged(nameof(NoReportSubmissionOutcome));
        if (value is null) { IsReportFisResponseVisible = false; }
    }
    private CancellationTokenSource? _reportSubmissionCancellation;
    partial void OnSelectedReportApprovalChanged(ApprovedTimingReport? value)
    {
        IsReportSendConfirmationOpen = false;
        ShowReportSubmission(value);
        RefreshReportXmlInfo();
        _ = LoadReportSubmissionAsync(value);
    }

    // The answers FIS gave to this XML revision: from this session, or read back from the event file.
    // Without an XML revision the key is the competition itself, so an answer never shows on another report.
    private Guid ReportDisplayKey(ApprovedTimingReport? approval) => approval?.Id ?? ReportCompetition?.Id ?? Guid.Empty;

    private void ShowReportSubmission(ApprovedTimingReport? approval)
    {
        if (_reportSubmissionDisplays.TryGetValue(ReportDisplayKey(approval), out var shown)) { ReportSubmissionId = shown.Id; ReportSubmissionOutcome = shown.Outcome; }
        else { ReportSubmissionId = ""; ReportSubmissionOutcome = null; }
    }

    private async Task LoadReportSubmissionAsync(ApprovedTimingReport? approval)
    {
        if (approval is null || _reportFile != workspace.FilePath || _reportSubmissionDisplays.ContainsKey(approval.Id)) { return; }
        await GuardAsync(async () =>
        {
            var saved = await workspace.ReadTimingReportSubmissionAsync(approval.Id);
            if (saved is null || SelectedReportApproval?.Id != approval.Id) { return; }
            var response = FisResultSubmissionClient.ReadResponse(saved.Response);
            var outcome = response is null ? null
                : FisUploadOutcomes.OutcomeOf(response, response.Submission.IsTest ? "TEST" : "LIVE", approval.XmlFileName, false)
                    with { SentAt = saved.At.ToLocalTime().ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture) };
            _reportSubmissionDisplays[approval.Id] = (saved.Uuid.ToString("D"), outcome);
            ReportSubmissionId = saved.Uuid.ToString("D"); ReportSubmissionOutcome = outcome;
        });
    }

    // What the left card says about the XML: its file name and whether a saved revision exists.
    private void RefreshReportXmlInfo()
    {
        OnPropertyChanged(nameof(CanSendReportToFis));
        if (_reportDraft is null) { ReportXmlFileName = ""; ReportXmlState = ""; return; }
        ReportXmlFileName = TimingReportXml.FileName(_reportDraft);
        ReportXmlState = SelectedReportApproval is { } latest
            ? $"Latest saved revision {latest.Revision} · {latest.ApprovedAt.ToLocalTime():yyyy-MM-dd HH:mm}. A new revision is saved when the report or timing data change."
            : "The XML is created from the current report when you save or send it.";
    }

    private void PublishReport(Guid approvalId, FisUploadOutcome outcome)
    {
        _reportSubmissionDisplays[approvalId] = (outcome.SubmissionId, outcome);
        if (ReportDisplayKey(SelectedReportApproval) != approvalId) { return; }
        ReportSubmissionId = outcome.SubmissionId;
        ReportSubmissionOutcome = outcome;
    }

    // Creates the timing report XML of the current report and stores it as an immutable revision in the event file. When the
    // saved report and the latest XML revision are already current, they are reused, so sending twice adds nothing.
    // Returns null when required fields are missing; ReportValidation then lists them.
    private async Task<ApprovedTimingReport?> EnsureReportXmlAsync()
    {
        if (_reportDraft is null || _reportFile != workspace.FilePath) { throw new DomainValidationException("Open the timing report of this series first."); }
        // Another window may have saved a newer report since this one was loaded.
        if ((await workspace.ReadTimingReportAsync(_reportDraft.CompetitionId))?.Revision != _reportSaved?.Revision)
        { throw new DomainValidationException("The saved report changed. Reopen the report before sending it."); }
        var persistedSeries = await workspace.ReadAsync();
        if (persistedSeries.Competitions.FirstOrDefault(x => x.Id == _reportDraft.CompetitionId)?.Values != ReportCompetition?.Values || persistedSeries.Values != _current?.Values)
        { throw new DomainValidationException("The competition or series details changed since the report was opened. Reopen the report before sending it."); }
        var current = ReadReportDraft();
        var unchanged = _reportSaved is not null && !HasTimingReportEdits
            && System.Text.Json.JsonSerializer.Serialize(current) == System.Text.Json.JsonSerializer.Serialize(_reportSaved.Values);
        if (unchanged) { ReportValidation = string.Join("\n", TimingReportXml.Validate(current)); _current = await workspace.ReadAsync(); }
        else { await SaveReportCoreAsync(); }
        if (ReportValidation.Length > 0) { return null; }
        var latest = ReportApprovals.FirstOrDefault();
        // Timing can be committed after the report was loaded (clocks stay connected): reuse a revision only when the
        // persisted timing still matches it. Otherwise the approval below refuses the stale report as before.
        var committed = await CurrentReportFingerprintAsync(_reportDraft!.CompetitionId);
        if (latest is not null && latest.DraftRevision == _reportSaved!.Revision && latest.SourceFingerprint == _reportDraft.SourceFingerprint
            && _reportDraft.SourceFingerprint == committed) { return latest; }
        var approved = await workspace.ApproveTimingReportAsync(new(_reportDraft!.CompetitionId, _reportSaved!.Revision,
            _current!.Revision, TimingOperator, DateTimeOffset.UtcNow, ReportSoftwareVersion));
        ReportApprovals.Insert(0, approved); SelectedReportApproval = approved;
        _current = await workspace.ReadAsync();
        return approved;
    }

    private async Task<string> CurrentReportFingerprintAsync(Guid competitionId)
    {
        var desk = await workspace.ReadStartListsAsync(competitionId);
        var sources = new List<TimingReplayData>();
        foreach (var list in desk.Revisions.GroupBy(x => x.Plan.RunNumber).Select(g => g.MaxBy(x => x.Revision)!))
        { sources.Add(await workspace.ReadTimingAsync(list.Id)); }
        return TimingReportProjection.Fingerprint(sources);
    }

    [RelayCommand]
    private async Task SaveTimingReportXmlAsync()
    {
        if (IsReportBusy || IsReportSubmitting) { return; }
        if (!await FlushTimingReportAsync()) { return; }
        IsReportBusy = true;
        await GuardAsync(async () =>
        {
            var approval = await EnsureReportXmlAsync();
            if (approval is null) { ReportStatus = "Complete the listed fields before creating XML."; return; }
            var path = await dialogs.ChooseResultXmlExportAsync(Path.GetFileNameWithoutExtension(approval.XmlFileName) + "-timing-report.xml");
            if (path is null) { ReportXmlSavedNote = ""; ReportStatus = $"Timing report XML revision {approval.Revision} is saved in the event file."; return; }
            await ExportFile.WriteAsync(path, approval.Xml);
            ReportXmlSavedNote = $"Saved {Path.GetFileName(path)} · revision {approval.Revision}";
            ReportStatus = $"Timing report XML revision {approval.Revision} saved to {path}.";
        });
        IsReportBusy = false;
    }

    [RelayCommand]
    private async Task SendTimingReportToFisAsync()
    {
        if (IsReportBusy || IsReportSubmitting || IsSubmissionBusy || _reportDraft is null) { return; }
        // A real upload is stored by FIS: ask once, in place. A test needs no confirmation.
        if (!FisUploadTestMode) { RefreshReportXmlInfo(); IsReportSendConfirmationOpen = true; return; }
        await SendReportCoreAsync(test: true);
    }

    [RelayCommand]
    private async Task ConfirmSendTimingReportAsync()
    {
        if (!IsReportSendConfirmationOpen) { return; }
        IsReportSendConfirmationOpen = false;
        // The confirmation was for a real send; it never turns into one after test mode was switched on.
        if (FisUploadTestMode) { return; }
        await SendReportCoreAsync(test: false);
    }

    [RelayCommand] private void CancelSendTimingReport() => IsReportSendConfirmationOpen = false;
    [RelayCommand] private void ToggleReportFisResponse() => IsReportFisResponseVisible = ReportSubmissionOutcome is { HasRawResponse: true } && !IsReportFisResponseVisible;
    [RelayCommand] private Task CheckTimingReportSubmissionAsync() => CheckReportSubmissionCoreAsync();
    [RelayCommand] private void StopTimingReportStatusCheck() => _reportSubmissionCancellation?.Cancel();

    private async Task SendReportCoreAsync(bool test)
    {
        if (IsReportBusy || IsSubmissionBusy) { return; }
        if (!await FlushTimingReportAsync()) { return; }
        var file = workspace.FilePath;
        IsReportBusy = true; IsReportSubmitting = true;
        using var cancellation = new CancellationTokenSource(); _reportSubmissionCancellation = cancellation;
        var mode = test ? "TEST · " + SelectedFisTestScenario.Label.ToLowerInvariant() : "LIVE";
        var key = ReportDisplayKey(SelectedReportApproval);
        try
        {
            ApprovedTimingReport? approval;
            try { approval = await EnsureReportXmlAsync(); }
            catch (Exception ex) when (ex is DomainValidationException or SeriesFileException or SeriesConflictException or IOException)
            {
                var message = ex is IOException ? "The event file could not be written. Check the file and try again." : ex.Message;
                PublishReport(key, FisUploadOutcomes.Notice(FisUploadTone.Error, "XML not created", message, "Nothing was sent to FIS.", test, mode, "", ""));
                return;
            }
            if (approval is null)
            {
                PublishReport(key, FisUploadOutcomes.Notice(FisUploadTone.Error, "XML not created", "Complete these fields first:", "Nothing was sent to FIS.", test, mode, "", "",
                    ReportValidation.Split('\n', StringSplitOptions.RemoveEmptyEntries)));
                return;
            }
            key = approval.Id;
            await SendXmlToFisAsync(approval.XmlFileName, approval.Xml, test, mode, outcome => { if (workspace.FilePath == file) { PublishReport(approval.Id, outcome); } },
                response => PersistReportSubmissionAsync(approval, response, file), cancellation.Token);
        }
        catch (Exception ex) when (ex is DomainValidationException or FisSubmissionException or SeriesFileException or SeriesConflictException
            or IOException or UnauthorizedAccessException or HttpRequestException or OperationCanceledException or PlatformNotSupportedException or ArgumentException)
        {
            if (workspace.FilePath == file)
            { PublishReport(key, FisUploadOutcomes.FailureOutcome(ex, test, mode, _reportSubmissionDisplays.GetValueOrDefault(key).Id ?? "", ReportSubmissionOutcome?.FileName ?? "")); }
        }
        finally { _reportSubmissionCancellation = null; IsReportSubmitting = false; IsReportBusy = false; }
    }

    // The answer stays with the XML revision in the event file, so it can be read again after a restart.
    private async Task PersistReportSubmissionAsync(ApprovedTimingReport approval, FisSubmissionResponse response, string? file)
    {
        if (workspace.FilePath != file) { return; }
        try
        {
            var series = await workspace.ReadAsync();
            await workspace.SaveTimingReportSubmissionAsync(new(approval.Id, response.Submission.Uuid,
                (response.Submission.IsTest ? "TEST" : "LIVE") + " · " + response.Submission.Outcome, response.ResponseText, DateTimeOffset.UtcNow), series.Revision);
        }
        catch (Exception ex) when (ex is SeriesFileException or SeriesConflictException or DomainValidationException or IOException)
        { SetStatus("FIS answered, but the answer could not be saved in the event file. Keep the submission ID: " + response.Submission.Uuid.ToString("D"), error: true); }
    }

    private async Task CheckReportSubmissionCoreAsync()
    {
        if (IsReportBusy || IsSubmissionBusy) { return; }
        var approval = SelectedReportApproval;
        var uuid = Guid.TryParse(ReportSubmissionId, out var typed) && typed != Guid.Empty ? typed : (Guid?)null;
        var file = workspace.FilePath;
        IsReportBusy = true; IsReportSubmitting = true;
        using var cancellation = new CancellationTokenSource(); _reportSubmissionCancellation = cancellation;
        var key = ReportDisplayKey(approval);
        var adHoc = true;
        void Show(FisUploadOutcome outcome)
        {
            if (workspace.FilePath != file) { return; }
            if (!adHoc) { PublishReport(key, outcome); }
            else if (ReportDisplayKey(SelectedReportApproval) == key) { ReportSubmissionOutcome = outcome; }
        }
        try
        {
            var associated = approval is null ? null : (await workspace.ReadTimingReportSubmissionAsync(approval.Id))?.Uuid;
            if (uuid is null) { uuid = associated; }
            if (uuid is null) { throw new DomainValidationException("No submission ID is saved for this XML revision. Send it first."); }
            // Only the submission this revision sent is saved with it (also one whose answer could not be saved before);
            // any other ID is looked up and shown.
            var sentHere = approval is not null && _reportSubmissionDisplays.GetValueOrDefault(approval.Id).Id == uuid.Value.ToString("D");
            adHoc = approval is null || (associated != uuid && !sentHere);
            _submissionClient ??= new(_submissionHttp);
            var token = _fisStore.ReadMemberToken() ?? "";
            var response = await _submissionClient.PollAsync(uuid.Value, token, cancellation.Token);
            await FollowFisSubmissionAsync(_submissionClient, token, response, response.Submission.IsTest ? "TEST" : "LIVE", approval?.XmlFileName ?? "",
                Show, adHoc ? null : r => PersistReportSubmissionAsync(approval!, r, file), cancellation.Token);
        }
        catch (Exception ex) when (ex is DomainValidationException or FisSubmissionException or SeriesFileException or SeriesConflictException
            or IOException or UnauthorizedAccessException or HttpRequestException or OperationCanceledException or PlatformNotSupportedException or ArgumentException)
        {
            Show(FisUploadOutcomes.FailureOutcome(ex, ReportSubmissionOutcome?.IsTest ?? false, ReportSubmissionOutcome?.Mode ?? "", uuid?.ToString("D") ?? "", ReportSubmissionOutcome?.FileName ?? ""));
        }
        finally { _reportSubmissionCancellation = null; IsReportSubmitting = false; IsReportBusy = false; }
    }

    public async Task<bool> FlushTimingReportAsync()
    {
        if (IsReportBusy) { SetStatus("Wait for the timing report operation to finish, or stop image reading / FIS status checking, before closing this file.", true); return false; }
        _reportSaveDelay?.Cancel();
        await _reportSaveTask;
        return await DrainReportSavesAsync();
    }
}
