using OpenSkiTime.Application.Abstractions;
using OpenSkiTime.Domain.Series;

namespace OpenSkiTime.Application.Tests.Fakes;

internal sealed class FakeEventSeriesRepository : IEventSeriesRepository
{
    private readonly Dictionary<Guid, EventSeries> _store = [];

    public Task<IReadOnlyList<EventSeriesSummary>> ListAsync(CancellationToken ct = default)
    {
        IReadOnlyList<EventSeriesSummary> result = _store.Values
            .OrderBy(e => e.StartDate)
            .Select(e => new EventSeriesSummary(e.Id, e.Name, e.StartDate, e.EndDate))
            .ToList();
        return Task.FromResult(result);
    }

    public Task<EventSeries?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        _store.TryGetValue(id, out var series);
        return Task.FromResult(series);
    }

    public Task AddAsync(EventSeries series, CancellationToken ct = default)
    {
        _store[series.Id] = series;
        return Task.CompletedTask;
    }

    public void Update(EventSeries series)
    {
        _store[series.Id] = series;
    }

    public void Remove(EventSeries series)
    {
        _store.Remove(series.Id);
    }

    public Task<EventSeriesSnapshot?> LoadSnapshotAsync(Guid id, CancellationToken ct = default)
    {
        if (!_store.TryGetValue(id, out var series))
        {
            return Task.FromResult<EventSeriesSnapshot?>(null);
        }

        var competitions = series.Competitions
            .Select(c => new CompetitionRef(c.Id, c.ShortLabel, c.Date))
            .ToList();

        return Task.FromResult<EventSeriesSnapshot?>(new EventSeriesSnapshot(
            series.Id,
            series.RowVersion,
            competitions,
            Array.Empty<CompetitorRef>(),
            Array.Empty<ParticipationRef>()));
    }
}

internal sealed class FakeUnitOfWork : IUnitOfWork
{
    public int SaveCount { get; private set; }

    public void Reset() => SaveCount = 0;

    public Task<IAsyncDisposable> BeginTransactionAsync(CancellationToken ct = default)
        => Task.FromResult<IAsyncDisposable>(new NoopDisposable());

    public Task<int> SaveChangesAsync(CancellationToken ct = default)
    {
        SaveCount++;
        return Task.FromResult(0);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private sealed class NoopDisposable : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

internal sealed class FixedClock : IClock
{
    private readonly DateOnly _today;
    private readonly DateTime _utcNow;

    public FixedClock(DateOnly today, DateTime utcNow)
    {
        _today = today;
        _utcNow = utcNow;
    }

    public DateOnly Today() => _today;
    public DateTime UtcNow() => _utcNow;
}
