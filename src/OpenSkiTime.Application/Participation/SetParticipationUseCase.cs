using OpenSkiTime.Application.Abstractions;
using OpenSkiTime.Application.Common;
using OpenSkiTime.Domain.Participation;

namespace OpenSkiTime.Application.Participations;

public sealed class SetParticipationRequest
{
    public required Guid EventSeriesId { get; init; }
    public required Guid CompetitorId { get; init; }
    public required Guid CompetitionId { get; init; }
    public required bool IsParticipating { get; init; }
}

public sealed class SetParticipationUseCase(
    IEventSeriesRepository repository,
    IParticipationRepository participationRepository,
    IUnitOfWork unitOfWork)
{
    public async Task<Result> ExecuteAsync(
        SetParticipationRequest request,
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

        var competition = series.Competitions.FirstOrDefault(c => c.Id == request.CompetitionId);
        if (competition is null)
        {
            return Result.Failure($"Competition {request.CompetitionId} not found.");
        }

        var existing = competitor.Participations
            .FirstOrDefault(p => p.CompetitionId == request.CompetitionId);

        if (existing is not null)
        {
            existing.SetParticipating(request.IsParticipating);
            repository.Update(series);
        }
        else
        {
            var participation = Participation.Create(
                Guid.NewGuid(),
                request.CompetitorId,
                request.CompetitionId,
                request.IsParticipating);
            await participationRepository.AddAsync(participation, ct);
        }

        await unitOfWork.SaveChangesAsync(ct);
        return Result.Success();
    }
}
