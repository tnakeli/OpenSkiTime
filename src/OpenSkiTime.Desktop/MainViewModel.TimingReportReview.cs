using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenSkiTime.Application;
using OpenSkiTime.Domain;

namespace OpenSkiTime.Desktop;

public sealed partial class MainViewModel
{
    public ObservableCollection<SavedTimingReport> ReportHistory { get; } = [];
    [ObservableProperty] private SavedTimingReport? _selectedReportHistory;
    [ObservableProperty] private string _reportSubmissionStatus = "FIS submissions are test-only.";
    private CancellationTokenSource? _reportSubmissionCancellation;
    private async Task LoadReportHistoryAsync()
    {
        ReportHistory.Clear();
        if (_reportDraft is null) { return; }
        foreach (var item in (await workspace.ReadTimingReportHistoryAsync(_reportDraft.CompetitionId)).Reverse()) { ReportHistory.Add(item); }
        SelectedReportHistory = ReportHistory.Skip(1).FirstOrDefault();
    }

    [RelayCommand]
    private async Task RestoreReportRevisionAsync()
    {
        if (IsReportBusy || SelectedReportHistory is not { } old || _reportDraft is null) { return; }
        if (!await FlushTimingReportAsync()) { return; }
        IsReportBusy = true;
        await GuardAsync(async () =>
        {
            if (old.CompetitionId != _reportDraft.CompetitionId) { throw new DomainValidationException("Choose a revision from this competition."); }
            var series = await workspace.ReadAsync();
            var restored = old.Values with { Reviewed = false, CertifyFis = false };
            await workspace.SaveTimingReportAsync(restored, series.Revision, TimingOperator,
                $"Restore report revision {old.Revision}: {ReportChangeReason}", DateTimeOffset.UtcNow);
            _reportDraft = restored; _reportSaved = await workspace.ReadTimingReportAsync(old.CompetitionId);
            await PopulateReportAsync(restored); await LoadReportHistoryAsync();
            HasTimingReportEdits = false; NotifyReportDefaults();
            ReportStatus = "Previous values restored as a new revision. Current timing data is updated automatically; review before creating XML.";
        });
        IsReportBusy = false;
        await LoadTimingReportAsync();
    }

    partial void OnSelectedReportApprovalChanged(ApprovedTimingReport? value)
    {
        ReportSubmissionStatus = "FIS submissions are test-only.";
        _ = LoadReportSubmissionAsync(value);
    }
    private async Task LoadReportSubmissionAsync(ApprovedTimingReport? approval)
    {
        if (approval is null || _reportFile != workspace.FilePath) { return; }
        await GuardAsync(async () =>
        {
            var saved = await workspace.ReadTimingReportSubmissionAsync(approval.Id);
            if (SelectedReportApproval?.Id == approval.Id && saved is not null) { ReportSubmissionStatus = saved.Status; }
        });
    }

    [RelayCommand] private Task SendTimingReportTestAsync() => RunReportSubmissionAsync(upload: true);
    [RelayCommand] private Task CheckTimingReportSubmissionAsync() => RunReportSubmissionAsync(upload: false);
    [RelayCommand] private void StopTimingReportStatusCheck() => _reportSubmissionCancellation?.Cancel();
    private async Task RunReportSubmissionAsync(bool upload)
    {
        if (IsReportBusy || IsSubmissionBusy || SelectedReportApproval is not { } approval) { return; }
        var file = workspace.FilePath;
        IsReportBusy = true;
        using var cancellation = new CancellationTokenSource(); _reportSubmissionCancellation = cancellation;
        try
        {
            var previous = await workspace.ReadTimingReportSubmissionAsync(approval.Id);
            if (!upload && previous is null) { throw new DomainValidationException("No submission ID is saved for this XML revision. Send it in test mode first."); }
            ReportSubmissionStatus = upload ? "Uploading the exact approved XML - test mode only…" : "Checking the saved FIS submission…";
            var response = upload
                ? await UploadApprovedFisArtifactTestAsync(new(approval.Id, approval.XmlFileName, approval.Xml), cancellation.Token)
                : await PollFisArtifactTestAsync(previous!.Uuid, cancellation.Token);
            for (var poll = 0; ; poll++)
            {
                var result = response.Submission;
                var status = $"TEST · {result.Uuid:D} · {result.Outcome} · processed {result.Summary.Processed}, rejected {result.Summary.Rejected}"
                    + (result.Summary.IsComplete ? " · complete" : " · awaiting FIS processing")
                    + "\n" + string.Join("\n", result.Files.Select(x => $"{x.FileName}: {x.Status} · {x.Comments}"));
                if (workspace.FilePath != file) { return; }
                if (SelectedReportApproval?.Id == approval.Id) { ReportSubmissionStatus = status; }
                var series = await workspace.ReadAsync();
                await workspace.SaveTimingReportSubmissionAsync(new(approval.Id, result.Uuid, status, response.ResponseText, DateTimeOffset.UtcNow), series.Revision);
                if (result.Summary.IsComplete || poll >= 23) { break; }
                await Task.Delay(TimeSpan.FromSeconds(5), cancellation.Token);
                response = await PollFisArtifactTestAsync(result.Uuid, cancellation.Token);
            }
        }
        catch (Exception ex) when (ex is DomainValidationException or SeriesFileException or SeriesConflictException
            or IOException or UnauthorizedAccessException or HttpRequestException or OperationCanceledException or PlatformNotSupportedException)
        {
            if (workspace.FilePath == file && SelectedReportApproval?.Id == approval.Id)
            {
                ReportSubmissionStatus = ex is HttpRequestException or OperationCanceledException
                    ? "FIS request stopped or failed. The upload may have arrived; check the saved submission ID before sending again."
                    : ex.Message;
            }
        }
        finally { _reportSubmissionCancellation = null; IsReportBusy = false; }
    }

    public async Task<bool> FlushTimingReportAsync()
    {
        if (IsReportBusy) { SetStatus("Wait for the timing report operation to finish, or stop image reading / FIS status checking, before closing this file.", true); return false; }
        _reportSaveDelay?.Cancel();
        await _reportSaveTask;
        return await DrainReportSavesAsync();
    }
}
