using OpenSkiTime.Application.Abstractions;

namespace OpenSkiTime.Application.Series;

public sealed class ListEventSeriesUseCase
{
    private readonly IEventSeriesRepository _repository;

    public ListEventSeriesUseCase(IEventSeriesRepository repository)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
    }

    public Task<IReadOnlyList<EventSeriesSummary>> ExecuteAsync(CancellationToken ct = default)
        => _repository.ListAsync(ct);
}
