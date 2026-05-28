using OpenSkiTime.Application.Abstractions;
using OpenSkiTime.Application.Common;

namespace OpenSkiTime.Application.Competitors;

public sealed class AssignBibUseCase(
    IEventSeriesRepository repository,
    IUnitOfWork unitOfWork)
{
    /// <summary>
    /// Assigns (or clears when <paramref name="bib"/> is null) a bib number
    /// to the specified competitor within the series.
    /// Uniqueness is enforced by <c>EventSeries.AssignBib</c>.
    /// </summary>
    public async Task<Result> ExecuteAsync(
        Guid eventSeriesId,
        Guid competitorId,
        int? bib,
        CancellationToken ct = default)
    {
        var series = await repository.GetByIdAsync(eventSeriesId, ct);
        if (series is null)
        {
            return Result.Failure($"Event Series {eventSeriesId} not found.");
        }

        try
        {
            series.AssignBib(competitorId, bib);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentOutOfRangeException)
        {
            return Result.Failure(ex.Message);
        }

        await unitOfWork.SaveChangesAsync(ct);
        return Result.Success();
    }
}
