using OpenSkiTime.Domain.Series;

namespace OpenSkiTime.Application.Abstractions;

/// <summary>
/// Aggregate-root repository for Event Series. Read paths return either
/// summaries (lightweight list view) or the full aggregate including
/// owned competitions (and, in later phases, competitors and
/// participations). Write paths use EF-style change tracking; commit
/// happens via <see cref="IUnitOfWork.SaveChangesAsync"/>.
/// </summary>
public interface IEventSeriesRepository
{
    Task<IReadOnlyList<EventSeriesSummary>> ListAsync(CancellationToken ct = default);

    Task<EventSeries?> GetByIdAsync(Guid id, CancellationToken ct = default);

    Task AddAsync(EventSeries series, CancellationToken ct = default);

    void Update(EventSeries series);

    void Remove(EventSeries series);

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
public sealed record CompetitorRef(Guid Id, string Code, string LastNameUpper, string FirstName, int YearOfBirth, string? NationCode, string? ClubName);
public sealed record ParticipationRef(Guid CompetitorId, Guid CompetitionId, bool IsParticipating);
