using OpenSkiTime.Application.Abstractions;
using OpenSkiTime.Application.Common;
using OpenSkiTime.Domain.Common;
using OpenSkiTime.Domain.Competitors;

namespace OpenSkiTime.Application.Competitors;

public sealed class AddCompetitorRequest
{
    public required Guid EventSeriesId { get; init; }
    public required string LastName { get; init; }
    public required string FirstName { get; init; }
    public required int YearOfBirth { get; init; }
    public string? FisCode { get; init; }
    public string? NationCode { get; init; }
    public string? ClubName { get; init; }
    public Gender? Gender { get; init; }
}

public sealed class AddCompetitorUseCase(
    IEventSeriesRepository repository,
    IUnitOfWork unitOfWork)
{
    public async Task<Result<Guid>> ExecuteAsync(
        AddCompetitorRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var series = await repository.GetByIdAsync(request.EventSeriesId, ct);
        if (series is null)
        {
            return Result<Guid>.Failure($"Event Series {request.EventSeriesId} not found.");
        }

        Competitor competitor;
        try
        {
            competitor = Competitor.Create(
                Guid.NewGuid(),
                request.EventSeriesId,
                request.LastName,
                request.FirstName,
                request.YearOfBirth,
                request.FisCode,
                request.NationCode,
                request.ClubName,
                request.Gender);

            series.AddCompetitor(competitor);
        }
        catch (Exception ex) when (ex is ArgumentException or ArgumentOutOfRangeException or InvalidOperationException)
        {
            return Result<Guid>.Failure(ex.Message);
        }

        await unitOfWork.SaveChangesAsync(ct);
        return Result<Guid>.Success(competitor.Id);
    }
}
