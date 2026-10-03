using OpenSkiTime.Rewrite.Domain;

namespace OpenSkiTime.Rewrite.Application;

public interface ISeriesFileStore
{
    Task<ISeriesFileSession> CreateAsync(string filePath, SeriesValues values,
        IReadOnlyList<CompetitionValues>? competitions = null, CancellationToken ct = default);
    Task<ISeriesFileSession> OpenAsync(string filePath, CancellationToken ct = default);
}

public interface ISeriesFileSession : IAsyncDisposable
{
    string FilePath { get; }
    Task<SeriesDetails> ReadAsync(CancellationToken ct = default);
    Task<SeriesDetails> SaveSeriesAsync(SeriesValues values, long expectedRevision, CancellationToken ct = default);
    Task<SeriesDetails> ApplyCalendarAsync(SeriesValues values, IReadOnlyList<CompetitionValues> competitions,
        long expectedRevision, CancellationToken ct = default);
    Task<SeriesDetails> SaveCompetitionAsync(Guid? id, CompetitionValues values, long expectedRevision, CancellationToken ct = default);
    Task<SeriesDetails> SaveCompetitionAsync(Guid? id, CompetitionValues values, long expectedRevision,
        bool saveCourseToAllRaces, CancellationToken ct = default);
    Task<SeriesDetails> SaveCompetitionAsync(Guid? id, CompetitionValues values, long expectedRevision,
        bool saveCourseToAllRaces, bool saveTdToAllRaces, CancellationToken ct = default);
    Task<SeriesDetails> RemoveCompetitionAsync(Guid id, long expectedRevision, CancellationToken ct = default);
    Task<CompetitorDeskDetails> ReadCompetitorDeskAsync(CancellationToken ct = default);
    Task<DeskMutationResult<CompetitorDetails>> SaveDeskRowAsync(Guid? id, CompetitorValues values,
        Guid? competitionId, bool participates, int? importedBib, long expectedRevision, CancellationToken ct = default);
    Task<long> RemoveCompetitorAsync(Guid id, long expectedRevision, CancellationToken ct = default);
    Task<DeskMutationResult<CategoryRuleDetails>> SaveCategoryRuleAsync(Guid? id, CategoryRuleValues values,
        long expectedRevision, CancellationToken ct = default);
    Task<long> RemoveCategoryRuleAsync(Guid id, long expectedRevision, CancellationToken ct = default);
    Task<long> ReplaceCategoryRulesAsync(IReadOnlyList<CategoryRuleValues> rules, long expectedRevision, CancellationToken ct = default);
    Task<ImportCommitResult> ApplyImportAsync(ImportCommit commit, CancellationToken ct = default);
    Task<DeskBatchResult> ApplyDeskBatchAsync(DeskBatch batch, CancellationToken ct = default);
    Task BackupAsync(string destinationPath, CancellationToken ct = default);
    Task<StartListDesk> ReadStartListsAsync(Guid competitionId, CancellationToken ct = default);
    Task<StartListDesk> SaveStartListAsync(SaveStartList request, CancellationToken ct = default);
    Task<StartListDesk> MarkRunStartedAsync(Guid listId, long expectedRevision, string operatorName, DateTimeOffset at, CancellationToken ct = default);
}

public sealed class SeriesFileException(string message, Exception? inner = null) : Exception(message, inner);
public sealed class SeriesConflictException() : Exception("The series changed since it was displayed. Reopen it and review the latest data.");

public sealed partial class SeriesWorkspace(ISeriesFileStore store, ITimingDecoderFactory? timingDecoders = null) : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ISeriesFileSession? _session;
    public TimingWorkspace? Timing { get; private set; }
    public AuxiliaryTimingWorkspace? Auxiliary { get; private set; }

    public Task<AuxiliaryTimingData> ReadAuxiliaryTimingAsync(Guid listId, CancellationToken ct = default)
        => WithSessionAsync(s => (s as IAuxiliaryTimingStore ?? throw new SeriesFileException("Auxiliary timing storage is unavailable.")).ReadAuxiliaryTimingAsync(listId, ct), ct);

    private async Task DisposeCaptureWorkspacesAsync()
    {
        // Request both drains even if one needs operator recovery; do not dispose either workspace until both succeed.
        await Task.WhenAll(Timing?.StopAsync() ?? Task.CompletedTask, Auxiliary?.StopAllAsync() ?? Task.CompletedTask);
        if (Timing is not null) { await Timing.DisposeAsync(); Timing = null; }
        if (Auxiliary is not null) { await Auxiliary.DisposeAsync(); Auxiliary = null; }
    }

    public Task<TimingReplayData> ReadTimingAsync(Guid listId, CancellationToken ct = default)
        => WithSessionAsync(s => (s as ITimingStore ?? throw new SeriesFileException("Timing storage is unavailable.")).ReadTimingAsync(listId, ct), ct);
    public Task<IReadOnlyList<ApprovedResult>> ReadApprovedResultsAsync(Guid competitionId, CancellationToken ct = default)
        => WithSessionAsync(s => (s as IResultStore ?? throw new SeriesFileException("Result storage is unavailable.")).ReadApprovedResultsAsync(competitionId, ct), ct);
    public Task<ApprovedResult> ApproveResultAsync(ApproveResultRequest request, CancellationToken ct = default)
        => WithSessionAsync(s => (s as IResultStore ?? throw new SeriesFileException("Result storage is unavailable.")).ApproveResultAsync(request, ct), ct);
    public string? FilePath => _session?.FilePath;
    public bool IsOpen => _session is not null;
    public Task<SavedRaceInformation?> ReadRaceInformationAsync(Guid competitionId, CancellationToken ct = default)
        => WithSessionAsync(s => (s as IRaceInformationStore ?? throw new SeriesFileException("Race information storage is unavailable.")).ReadRaceInformationAsync(competitionId, ct), ct);
    public Task<long> SaveRaceInformationAsync(Guid competitionId, RaceInformation values, long expectedRevision,
        DateTimeOffset at, CancellationToken ct = default)
        => WithSessionAsync(s => (s as IRaceInformationStore ?? throw new SeriesFileException("Race information storage is unavailable.")).SaveRaceInformationAsync(competitionId, values, expectedRevision, at, ct), ct);

    public async Task<SeriesDetails> CreateAsync(string path, SeriesValues values,
        IReadOnlyList<CompetitionValues>? competitions = null, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var replacement = await store.CreateAsync(path, values, competitions, ct);
            return await SwitchAsync(replacement, ct);
        }
        finally { _gate.Release(); }
    }

    public async Task<SeriesDetails> OpenAsync(string path, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var replacement = await store.OpenAsync(path, ct);
            return await SwitchAsync(replacement, ct);
        }
        finally { _gate.Release(); }
    }

    private async Task<SeriesDetails> SwitchAsync(ISeriesFileSession replacement, CancellationToken ct)
    {
        try
        {
            var details = await replacement.ReadAsync(ct);
            if (_session is not null)
            {
                await DisposeCaptureWorkspacesAsync();
                await _session.DisposeAsync();
            }

            _session = replacement;
            if (timingDecoders is not null && replacement is ITimingStore timingStore) { Timing = new(timingStore, timingDecoders); }
            if (timingDecoders is not null && replacement is IAuxiliaryTimingStore auxiliaryStore) { Auxiliary = new(auxiliaryStore, timingDecoders); }
            return details;
        }
        catch
        {
            await replacement.DisposeAsync();
            throw;
        }
    }

    public async Task CloseAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_session is not null)
            {
                await DisposeCaptureWorkspacesAsync();
                await _session.DisposeAsync();
                _session = null;
            }
        }
        finally { _gate.Release(); }
    }

    public Task<SeriesDetails> ReadAsync(CancellationToken ct = default) => WithSessionAsync(s => s.ReadAsync(ct), ct);
    public Task<SeriesDetails> SaveSeriesAsync(SeriesValues values, long expectedRevision, CancellationToken ct = default)
        => WithSessionAsync(s => s.SaveSeriesAsync(values, expectedRevision, ct), ct);
    public Task<SeriesDetails> ApplyCalendarAsync(SeriesValues values, IReadOnlyList<CompetitionValues> competitions,
        long expectedRevision, CancellationToken ct = default)
        => WithSessionAsync(s => s.ApplyCalendarAsync(values, competitions, expectedRevision, ct), ct);
    public Task<SeriesDetails> SaveCompetitionAsync(Guid? id, CompetitionValues values, long expectedRevision, CancellationToken ct = default)
        => WithSessionAsync(s => s.SaveCompetitionAsync(id, values, expectedRevision, ct), ct);

    public Task<SeriesDetails> SaveCompetitionAsync(Guid? id, CompetitionValues values, long expectedRevision,
        bool saveCourseToAllRaces, CancellationToken ct = default)
        => WithSessionAsync(s => s.SaveCompetitionAsync(id, values, expectedRevision, saveCourseToAllRaces, ct), ct);

    public Task<SeriesDetails> SaveCompetitionAsync(Guid? id, CompetitionValues values, long expectedRevision,
        bool saveCourseToAllRaces, bool saveTdToAllRaces, CancellationToken ct = default)
        => WithSessionAsync(s => s.SaveCompetitionAsync(id, values, expectedRevision, saveCourseToAllRaces, saveTdToAllRaces, ct), ct);
    public Task<SeriesDetails> RemoveCompetitionAsync(Guid id, long expectedRevision, CancellationToken ct = default)
        => WithSessionAsync(s => s.RemoveCompetitionAsync(id, expectedRevision, ct), ct);
    public Task<CompetitorDeskDetails> ReadCompetitorDeskAsync(CancellationToken ct = default)
        => WithSessionAsync(s => s.ReadCompetitorDeskAsync(ct), ct);
    public Task<DeskMutationResult<CompetitorDetails>> SaveDeskRowAsync(Guid? id, CompetitorValues values,
        Guid? competitionId, bool participates, int? importedBib, long expectedRevision, CancellationToken ct = default)
        => WithSessionAsync(s => s.SaveDeskRowAsync(id, values, competitionId, participates, importedBib, expectedRevision, ct), ct);
    public Task<long> RemoveCompetitorAsync(Guid id, long expectedRevision, CancellationToken ct = default)
        => WithSessionAsync(s => s.RemoveCompetitorAsync(id, expectedRevision, ct), ct);
    public Task<DeskMutationResult<CategoryRuleDetails>> SaveCategoryRuleAsync(Guid? id, CategoryRuleValues values,
        long expectedRevision, CancellationToken ct = default)
        => WithSessionAsync(s => s.SaveCategoryRuleAsync(id, values, expectedRevision, ct), ct);
    public Task<long> RemoveCategoryRuleAsync(Guid id, long expectedRevision, CancellationToken ct = default)
        => WithSessionAsync(s => s.RemoveCategoryRuleAsync(id, expectedRevision, ct), ct);
    public Task<long> ReplaceCategoryRulesAsync(IReadOnlyList<CategoryRuleValues> rules, long expectedRevision, CancellationToken ct = default)
        => WithSessionAsync(s => s.ReplaceCategoryRulesAsync(rules, expectedRevision, ct), ct);
    public Task<ImportCommitResult> ApplyImportAsync(ImportCommit commit, CancellationToken ct = default)
        => WithSessionAsync(s => s.ApplyImportAsync(commit, ct), ct);
    public Task<DeskBatchResult> ApplyDeskBatchAsync(DeskBatch batch, CancellationToken ct = default)
        => WithSessionAsync(s => s.ApplyDeskBatchAsync(batch, ct), ct);
    public Task BackupAsync(string destinationPath, CancellationToken ct = default)
        => WithSessionAsync(async s => { await s.BackupAsync(destinationPath, ct); return true; }, ct);
    public Task<StartListDesk> ReadStartListsAsync(Guid competitionId, CancellationToken ct = default)
        => WithSessionAsync(s => s.ReadStartListsAsync(competitionId, ct), ct);
    public Task<StartListDesk> SaveStartListAsync(SaveStartList request, CancellationToken ct = default)
        => WithSessionAsync(s => s.SaveStartListAsync(request, ct), ct);
    public Task<StartListDesk> MarkRunStartedAsync(Guid listId, long expectedRevision, string operatorName, DateTimeOffset at, CancellationToken ct = default)
        => WithSessionAsync(s => s.MarkRunStartedAsync(listId, expectedRevision, operatorName, at, ct), ct);

    private async Task<T> WithSessionAsync<T>(Func<ISeriesFileSession, Task<T>> operation, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try { return await operation(_session ?? throw new SeriesFileException("Open an event series first.")); }
        finally { _gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAsync();
        _gate.Dispose();
    }
}
