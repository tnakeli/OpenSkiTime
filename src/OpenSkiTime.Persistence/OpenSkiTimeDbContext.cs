using Microsoft.EntityFrameworkCore;
using OpenSkiTime.Domain.Competitions;
using OpenSkiTime.Domain.Series;

namespace OpenSkiTime.Persistence;

public class OpenSkiTimeDbContext : DbContext
{
    public OpenSkiTimeDbContext(DbContextOptions<OpenSkiTimeDbContext> options)
        : base(options)
    {
    }

    public DbSet<EventSeries> EventSeries => Set<EventSeries>();

    public DbSet<Competition> Competitions => Set<Competition>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(OpenSkiTimeDbContext).Assembly);
    }

    /// <summary>
    /// Bumps <see cref="EventSeries.RowVersion"/> for every series whose
    /// owned graph has any pending change in this save. Per
    /// <c>data-model.md</c> this enables the importer's
    /// <c>EventSeriesSnapshotVersion</c> conflict detection.
    /// </summary>
    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        BumpRowVersions();
        return base.SaveChangesAsync(cancellationToken);
    }

    public override int SaveChanges()
    {
        BumpRowVersions();
        return base.SaveChanges();
    }

    private const string RowVersionProperty = "RowVersion";

    private void BumpRowVersions()
    {
        // Collect series ids touched by this change set:
        //  - EventSeries entries themselves (any state change)
        //  - Competition entries via EventSeriesId
        var touched = new HashSet<Guid>();

        foreach (var entry in ChangeTracker.Entries())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted))
            {
                continue;
            }

            switch (entry.Entity)
            {
                case EventSeries series:
                    touched.Add(series.Id);
                    break;
                case Competition competition:
                    touched.Add(competition.EventSeriesId);
                    break;
            }
        }

        if (touched.Count == 0)
        {
            return;
        }

        var seriesSet = Set<EventSeries>();

        foreach (var id in touched)
        {
            var tracked = ChangeTracker.Entries<EventSeries>()
                .FirstOrDefault(e => e.Entity.Id == id);

            if (tracked is null)
            {
                // Series itself is unchanged but a child changed. Try the
                // local cache first to avoid an unnecessary round-trip.
                var existing = seriesSet.Local.FirstOrDefault(e => e.Id == id)
                    ?? seriesSet.FirstOrDefault(e => e.Id == id);
                if (existing is null)
                {
                    continue;
                }

                existing.IncrementRowVersion();
                Entry(existing).Property(RowVersionProperty).IsModified = true;
            }
            else
            {
                tracked.Entity.IncrementRowVersion();
                tracked.Property(RowVersionProperty).IsModified = true;
            }
        }
    }
}
