using OpenSkiTime.Application.Abstractions;
using OpenSkiTime.Application.Common;

namespace OpenSkiTime.Application.Series;

public sealed record UpdateEventSeriesCommand(
    Guid Id,
    string Name,
    string Location,
    string Organizer,
    DateOnly StartDate,
    DateOnly EndDate,
    string Nation,
    string Season);

public sealed class UpdateEventSeriesUseCase
{
    private readonly IEventSeriesRepository _repository;
    private readonly IUnitOfWork _unitOfWork;

    public UpdateEventSeriesUseCase(IEventSeriesRepository repository, IUnitOfWork unitOfWork)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
    }

    public async Task<Result> ExecuteAsync(UpdateEventSeriesCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var series = await _repository.GetByIdAsync(command.Id, ct).ConfigureAwait(false);
        if (series is null)
        {
            return Result.Failure($"Event Series '{command.Id}' was not found.");
        }

        try
        {
            series.UpdateBasicData(
                command.Name,
                command.Location,
                command.Organizer,
                command.StartDate,
                command.EndDate,
                command.Nation,
                command.Season);
        }
        catch (ArgumentException ex)
        {
            return Result.Failure(ex.Message);
        }

        _repository.Update(series);
        await _unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
        return Result.Success();
    }
}
