using OpenSkiTime.Application.Abstractions;
using OpenSkiTime.Application.Common;

namespace OpenSkiTime.Application.Series;

public sealed class DeleteEventSeriesUseCase
{
    private readonly IEventSeriesRepository _repository;
    private readonly IUnitOfWork _unitOfWork;

    public DeleteEventSeriesUseCase(IEventSeriesRepository repository, IUnitOfWork unitOfWork)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
    }

    public async Task<Result> ExecuteAsync(Guid eventSeriesId, CancellationToken ct = default)
    {
        var series = await _repository.GetByIdAsync(eventSeriesId, ct).ConfigureAwait(false);
        if (series is null)
        {
            return Result.Failure($"Event Series '{eventSeriesId}' was not found.");
        }

        _repository.Remove(series);
        await _unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
        return Result.Success();
    }
}
