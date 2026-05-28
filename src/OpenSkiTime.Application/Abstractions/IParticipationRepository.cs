using OpenSkiTime.Domain.Participation;

namespace OpenSkiTime.Application.Abstractions;

/// <summary>
/// Write-only repository for <see cref="Participation"/> records.
/// Read access goes through <see cref="IEventSeriesRepository.GetByIdAsync"/>
/// (Participations are owned by Competitor).
/// </summary>
public interface IParticipationRepository
{
    Task AddAsync(Participation participation, CancellationToken ct = default);
}
