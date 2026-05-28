using Microsoft.EntityFrameworkCore;
using OpenSkiTime.Application.Abstractions;
using OpenSkiTime.Domain.Series;

namespace OpenSkiTime.Persistence.Repositories;

internal sealed class EventSeriesRepository : IEventSeriesRepository
{
    private readonly OpenSkiTimeDbContext _db;

    public EventSeriesRepository(OpenSkiTimeDbContext db)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    public async Task<IReadOnlyList<EventSeriesSummary>> ListAsync(CancellationToken ct = default)
    {
        var rows = await _db.EventSeries
            .OrderBy(e => e.StartDate)
            .Select(e => new EventSeriesSummary(e.Id, e.Name, e.StartDate, e.EndDate))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return rows;
    }

    public async Task<EventSeries?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        return await _db.EventSeries
            .Include(e => e.Competitions)
            .FirstOrDefaultAsync(e => e.Id == id, ct)
            .ConfigureAwait(false);
    }

    public async Task AddAsync(EventSeries series, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(series);
        await _db.EventSeries.AddAsync(series, ct).ConfigureAwait(false);
    }

    public void Update(EventSeries series)
    {
        ArgumentNullException.ThrowIfNull(series);
        // EF change tracker handles dirty fields automatically; calling
        // Update would mark every property modified, which is wasteful.
        // No-op suffices when the series was loaded via GetByIdAsync.
        _ = series;
    }

    public void Remove(EventSeries series)
    {
        ArgumentNullException.ThrowIfNull(series);
        _db.EventSeries.Remove(series);
    }

    public async Task<EventSeriesSnapshot?> LoadSnapshotAsync(Guid id, CancellationToken ct = default)
    {
        var series = await _db.EventSeries
            .Include(e => e.Competitions)
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == id, ct)
            .ConfigureAwait(false);

        if (series is null)
        {
            return null;
        }

        var competitions = series.Competitions
            .Select(c => new CompetitionRef(c.Id, c.ShortLabel, c.Date))
            .ToList();

        // Competitors and Participations are added in Phase 4 (US2).
        return new EventSeriesSnapshot(
            series.Id,
            series.RowVersion,
            competitions,
            Array.Empty<CompetitorRef>(),
            Array.Empty<ParticipationRef>());
    }
}
