using Microsoft.EntityFrameworkCore.Storage;
using OpenSkiTime.Application.Abstractions;

namespace OpenSkiTime.Persistence;

internal sealed class EfCoreUnitOfWork : IUnitOfWork
{
    private readonly OpenSkiTimeDbContext _db;

    public EfCoreUnitOfWork(OpenSkiTimeDbContext db)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    public async Task<IAsyncDisposable> BeginTransactionAsync(CancellationToken ct = default)
    {
        IDbContextTransaction tx = await _db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        return new TransactionScope(tx);
    }

    public Task<int> SaveChangesAsync(CancellationToken ct = default)
        => _db.SaveChangesAsync(ct);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private sealed class TransactionScope : IAsyncDisposable
    {
        private readonly IDbContextTransaction _tx;
        private bool _committed;

        public TransactionScope(IDbContextTransaction tx)
        {
            _tx = tx;
        }

        public async ValueTask CommitAsync(CancellationToken ct = default)
        {
            await _tx.CommitAsync(ct).ConfigureAwait(false);
            _committed = true;
        }

        public async ValueTask DisposeAsync()
        {
            if (!_committed)
            {
                try
                {
                    await _tx.RollbackAsync().ConfigureAwait(false);
                }
                catch (InvalidOperationException)
                {
                    // Connection already closed / nothing to roll back.
                }
            }

            await _tx.DisposeAsync().ConfigureAwait(false);
        }
    }
}
