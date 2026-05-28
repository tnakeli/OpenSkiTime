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
            .Include(e => e.Competitors)
                .ThenInclude(c => c.Participations)
            .Include(e => e.CategoryRules)
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
            .Include(e => e.Competitors)
                .ThenInclude(c => c.Participations)
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

        var competitors = series.Competitors
            .Select(c => new CompetitorRef(c.Id, c.FisCode ?? string.Empty,
                c.LastName.Value, c.FirstName, c.YearOfBirth, c.NationCode, c.ClubName))
            .ToList();

        var participations = series.Competitors
            .SelectMany(c => c.Participations
                .Select(p => new ParticipationRef(p.CompetitorId, p.CompetitionId, p.IsParticipating)))
            .ToList();

        return new EventSeriesSnapshot(
            series.Id,
            series.RowVersion,
            competitions,
            competitors,
            participations);
    }
}
