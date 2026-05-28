using OpenSkiTime.Application.Abstractions;
using OpenSkiTime.Application.Common;
using OpenSkiTime.Domain.Common;

namespace OpenSkiTime.Application.Competitors;

public sealed class EditCompetitorRequest
{
    public required Guid EventSeriesId { get; init; }
    public required Guid CompetitorId { get; init; }
    public required string LastName { get; init; }
    public required string FirstName { get; init; }
    public required int YearOfBirth { get; init; }
    public string? FisCode { get; init; }
    public string? NationCode { get; init; }
    public string? ClubName { get; init; }
    public Gender? Gender { get; init; }
}

public sealed class EditCompetitorUseCase(
    IEventSeriesRepository repository,
    IUnitOfWork unitOfWork)
{
    public async Task<Result> ExecuteAsync(
        EditCompetitorRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var series = await repository.GetByIdAsync(request.EventSeriesId, ct);
        if (series is null)
        {
            return Result.Failure($"Event Series {request.EventSeriesId} not found.");
        }

        var competitor = series.Competitors.FirstOrDefault(c => c.Id == request.CompetitorId);
        if (competitor is null)
        {
            return Result.Failure($"Competitor {request.CompetitorId} not found.");
        }

        try
        {
            competitor.UpdatePersonalData(
                request.LastName,
                request.FirstName,
                request.YearOfBirth,
                request.FisCode,
                request.NationCode,
                request.ClubName,
                request.Gender);
        }
        catch (Exception ex) when (ex is ArgumentException or ArgumentOutOfRangeException)
        {
            return Result.Failure(ex.Message);
        }

        repository.Update(series);
        await unitOfWork.SaveChangesAsync(ct);
        return Result.Success();
    }
}
