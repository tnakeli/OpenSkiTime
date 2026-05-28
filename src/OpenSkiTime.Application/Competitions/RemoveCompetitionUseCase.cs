using OpenSkiTime.Application.Abstractions;
using OpenSkiTime.Application.Common;

namespace OpenSkiTime.Application.Competitions;

public sealed class RemoveCompetitionUseCase
{
    private readonly IEventSeriesRepository _repository;
    private readonly IUnitOfWork _unitOfWork;

    public RemoveCompetitionUseCase(IEventSeriesRepository repository, IUnitOfWork unitOfWork)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
    }

    public async Task<Result> ExecuteAsync(Guid eventSeriesId, Guid competitionId, CancellationToken ct = default)
    {
        var series = await _repository.GetByIdAsync(eventSeriesId, ct).ConfigureAwait(false);
        if (series is null)
        {
            return Result.Failure($"Event Series '{eventSeriesId}' was not found.");
        }

        if (!series.RemoveCompetition(competitionId))
        {
            return Result.Failure($"Competition '{competitionId}' was not found in this Event Series.");
        }

        _repository.Update(series);
        await _unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
        return Result.Success();
    }
}
