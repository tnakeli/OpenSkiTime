using OpenSkiTime.Application.Abstractions;
using OpenSkiTime.Application.Common;
using OpenSkiTime.Domain.Series;

namespace OpenSkiTime.Application.Series;

public sealed record CreateEventSeriesCommand(
    string Name,
    string Location,
    string Organizer,
    DateOnly StartDate,
    DateOnly EndDate,
    string Nation,
    string Season);

public sealed class CreateEventSeriesUseCase
{
    private readonly IEventSeriesRepository _repository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IClock _clock;

    public CreateEventSeriesUseCase(
        IEventSeriesRepository repository,
        IUnitOfWork unitOfWork,
        IClock clock)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async Task<Result<Guid>> ExecuteAsync(
        CreateEventSeriesCommand command,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        EventSeries series;
        try
        {
            series = EventSeries.Create(
                Guid.NewGuid(),
                command.Name,
                command.Location,
                command.Organizer,
                command.StartDate,
                command.EndDate,
                command.Nation,
                command.Season,
                _clock.UtcNow());
        }
        catch (ArgumentException ex)
        {
            return Result<Guid>.Failure(ex.Message);
        }

        await _repository.AddAsync(series, ct).ConfigureAwait(false);
        await _unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);

        return Result<Guid>.Success(series.Id);
    }
}
