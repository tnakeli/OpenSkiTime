namespace OpenSkiTime.Application;

public sealed partial class SeriesWorkspace
{
    private static ITimingReportStore Reports(ISeriesFileSession session) => session as ITimingReportStore
        ?? throw new SeriesFileException("Timing report storage is unavailable.");
    public Task<SavedTimingReport?> ReadTimingReportAsync(Guid competitionId, CancellationToken ct = default)
        => WithSessionAsync(s => Reports(s).ReadTimingReportAsync(competitionId, ct), ct);
    public Task<IReadOnlyList<SavedTimingReport>> ReadTimingReportHistoryAsync(Guid competitionId, CancellationToken ct = default)
        => WithSessionAsync(s => Reports(s).ReadTimingReportHistoryAsync(competitionId, ct), ct);
    public Task<long> SaveTimingReportAsync(TimingReportDraft draft, long expectedSeriesRevision, string operatorName,
        string reason, DateTimeOffset at, CancellationToken ct = default)
        => WithSessionAsync(s => Reports(s).SaveTimingReportAsync(draft, expectedSeriesRevision, operatorName, reason, at, ct), ct);
    public Task<long> ApplyTimingReportImportAsync(TimingReportDraft draft, long expectedSeriesRevision, string operatorName,
        string reason, DateTimeOffset at, CancellationToken ct = default)
        => WithSessionAsync(s => Reports(s).ApplyTimingReportImportAsync(draft, expectedSeriesRevision, operatorName, reason, at, ct), ct);
    public Task<IReadOnlyList<TimingReportImage>> ReadTimingReportImagesAsync(Guid competitionId, CancellationToken ct = default)
        => WithSessionAsync(s => Reports(s).ReadTimingReportImagesAsync(competitionId, ct), ct);
    public Task<long> SaveTimingReportImagesAsync(IReadOnlyList<TimingReportImage> images, long expectedSeriesRevision, CancellationToken ct = default)
        // SQLite blob writes can run synchronously even through its async API.
        // Keep receipt archival off the operator's UI thread while capture continues.
        => Task.Run(() => WithSessionAsync(s => Reports(s).SaveTimingReportImagesAsync(images, expectedSeriesRevision, ct), ct), ct);
    public Task<IReadOnlyList<ApprovedTimingReport>> ReadApprovedTimingReportsAsync(Guid competitionId, CancellationToken ct = default)
        => WithSessionAsync(s => Reports(s).ReadApprovedTimingReportsAsync(competitionId, ct), ct);
    public Task<ApprovedTimingReport> ApproveTimingReportAsync(ApproveTimingReportRequest request, CancellationToken ct = default)
        => WithSessionAsync(s => Reports(s).ApproveTimingReportAsync(request,
            timingDecoders ?? throw new SeriesFileException("Timing decoders are required to verify A report evidence."), ct), ct);
    public Task<TimingReportSubmission?> ReadTimingReportSubmissionAsync(Guid approvalId, CancellationToken ct = default)
        => WithSessionAsync(s => Reports(s).ReadTimingReportSubmissionAsync(approvalId, ct), ct);
    public Task<long> SaveTimingReportSubmissionAsync(TimingReportSubmission submission, long expectedSeriesRevision, CancellationToken ct = default)
        => WithSessionAsync(s => Reports(s).SaveTimingReportSubmissionAsync(submission, expectedSeriesRevision, ct), ct);
}
