using OpenSkiTime.Rewrite.Domain;

namespace OpenSkiTime.Rewrite.Application;

public interface ISeriesFileStore
{
    Task<ISeriesFileSession> CreateAsync(string filePath, SeriesValues values, CancellationToken ct = default);
    Task<ISeriesFileSession> OpenAsync(string filePath, CancellationToken ct = default);
}

public interface ISeriesFileSession : IAsyncDisposable
{
    string FilePath { get; }
    Task<SeriesDetails> ReadAsync(CancellationToken ct = default);
    Task<SeriesDetails> SaveSeriesAsync(SeriesValues values, long expectedRevision, CancellationToken ct = default);
    Task<SeriesDetails> SaveCompetitionAsync(Guid? id, CompetitionValues values, long expectedRevision, CancellationToken ct = default);
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
    Task<StartListDesk> ApproveStartListAsync(Guid id, long expectedRevision, DateTimeOffset at, CancellationToken ct = default);
    Task<StartListDesk> MarkRunStartedAsync(Guid listId, long expectedRevision, string operatorName, DateTimeOffset at, CancellationToken ct = default);
}

public sealed class SeriesFileException(string message, Exception? inner = null) : Exception(message, inner);
public sealed class SeriesConflictException() : Exception("The series changed since it was displayed. Reopen it and review the latest data.");

public sealed class SeriesWorkspace(ISeriesFileStore store) : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ISeriesFileSession? _session;
    public string? FilePath => _session?.FilePath;
    public bool IsOpen => _session is not null;

    public async Task<SeriesDetails> CreateAsync(string path, SeriesValues values, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var replacement = await store.CreateAsync(path, values, ct);
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
                await _session.DisposeAsync();
            }

            _session = replacement;
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
                await _session.DisposeAsync();
                _session = null;
            }
        }
        finally { _gate.Release(); }
    }

    public Task<SeriesDetails> ReadAsync(CancellationToken ct = default) => WithSessionAsync(s => s.ReadAsync(ct), ct);
    public Task<SeriesDetails> SaveSeriesAsync(SeriesValues values, long expectedRevision, CancellationToken ct = default)
        => WithSessionAsync(s => s.SaveSeriesAsync(values, expectedRevision, ct), ct);
    public Task<SeriesDetails> SaveCompetitionAsync(Guid? id, CompetitionValues values, long expectedRevision, CancellationToken ct = default)
        => WithSessionAsync(s => s.SaveCompetitionAsync(id, values, expectedRevision, ct), ct);
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
    public Task<StartListDesk> ApproveStartListAsync(Guid id, long expectedRevision, DateTimeOffset at, CancellationToken ct = default)
        => WithSessionAsync(s => s.ApproveStartListAsync(id, expectedRevision, at, ct), ct);
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
