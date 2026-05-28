using OpenSkiTime.Application.Abstractions;
using OpenSkiTime.Application.Common;

namespace OpenSkiTime.Application.Competitors;

public sealed class RemoveCompetitorUseCase(
    IEventSeriesRepository repository,
    IUnitOfWork unitOfWork)
{
    public async Task<Result> ExecuteAsync(
        Guid eventSeriesId,
        Guid competitorId,
        CancellationToken ct = default)
    {
        var series = await repository.GetByIdAsync(eventSeriesId, ct);
        if (series is null)
        {
            return Result.Failure($"Event Series {eventSeriesId} not found.");
        }

        var removed = series.RemoveCompetitor(competitorId);
        if (!removed)
        {
            return Result.Failure($"Competitor {competitorId} not found in Event Series {eventSeriesId}.");
        }

        await unitOfWork.SaveChangesAsync(ct);
        return Result.Success();
    }
}
