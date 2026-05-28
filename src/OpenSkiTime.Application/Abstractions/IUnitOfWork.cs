namespace OpenSkiTime.Application.Abstractions;

/// <summary>
/// Transactional boundary. Implementations live in
/// <c>OpenSkiTime.Persistence</c> and wrap an EF Core <c>DbContext</c>.
/// </summary>
public interface IUnitOfWork : IAsyncDisposable
{
    /// <summary>
    /// Begin an explicit transaction. The returned <see cref="IDisposable"/>
    /// commits when disposed cleanly, rolls back if disposed during a
    /// thrown exception (callers MUST use a <c>using</c> block).
    /// </summary>
    Task<IAsyncDisposable> BeginTransactionAsync(CancellationToken ct = default);

    Task<int> SaveChangesAsync(CancellationToken ct = default);
}
