using OpenSkiTime.Application.Abstractions;
using OpenSkiTime.Domain.Participation;

namespace OpenSkiTime.Persistence.Repositories;

internal sealed class ParticipationRepository : IParticipationRepository
{
    private readonly OpenSkiTimeDbContext _db;

    public ParticipationRepository(OpenSkiTimeDbContext db)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    public async Task AddAsync(Participation participation, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(participation);
        await _db.Participations.AddAsync(participation, ct).ConfigureAwait(false);
    }
}
