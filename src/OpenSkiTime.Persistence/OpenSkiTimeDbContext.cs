using Microsoft.EntityFrameworkCore;

namespace OpenSkiTime.Persistence;

/// <summary>
/// Root EF Core context for Open Ski Time. Phase 2 introduces only the
/// skeleton; entity sets are added in Phase 3 (US1) and Phase 4 (US2).
/// </summary>
public class OpenSkiTimeDbContext : DbContext
{
    public OpenSkiTimeDbContext(DbContextOptions<OpenSkiTimeDbContext> options)
        : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(OpenSkiTimeDbContext).Assembly);
    }
}
