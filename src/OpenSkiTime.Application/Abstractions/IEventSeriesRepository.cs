namespace OpenSkiTime.Application.Abstractions;

/// <summary>
/// Aggregate-root repository for Event Series. The full graph
/// (competitions, competitors, participations, category rules) is
/// reachable through <see cref="EventSeriesSnapshot"/>; mutations are
/// committed via <see cref="IUnitOfWork.SaveChangesAsync"/>.
/// </summary>
/// <remarks>
/// The actual aggregate type lives in <c>OpenSkiTime.Domain</c> and is
/// added in Phase 3 (User Story 1). At Phase 2 we publish only the
/// transport records needed by callers (UI, importer) so they can be
/// implemented in parallel.
/// </remarks>
public interface IEventSeriesRepository
{
    Task<IReadOnlyList<EventSeriesSummary>> ListAsync(CancellationToken ct = default);

    Task<EventSeriesSnapshot?> LoadSnapshotAsync(Guid id, CancellationToken ct = default);
}

public sealed record EventSeriesSummary(
    Guid Id,
    string Name,
    DateOnly StartDate,
    DateOnly EndDate);

/// <summary>
/// Read-only snapshot of an Event Series and its owned children, taken at
/// a specific <see cref="Version"/>. Used by the importer's preview→apply
/// flow to detect concurrent changes.
/// </summary>
public sealed record EventSeriesSnapshot(
    Guid Id,
    long Version,
    IReadOnlyList<CompetitionRef> Competitions,
    IReadOnlyList<CompetitorRef> Competitors,
    IReadOnlyList<ParticipationRef> Participations);

public sealed record CompetitionRef(Guid Id, string ShortLabel, DateOnly Date);
public sealed record CompetitorRef(Guid Id, string Code, string LastNameUpper, string FirstName, int YearOfBirth);
public sealed record ParticipationRef(Guid CompetitorId, Guid CompetitionId, bool IsParticipating);
