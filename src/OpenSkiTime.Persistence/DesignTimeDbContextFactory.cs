using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace OpenSkiTime.Persistence;

/// <summary>
/// Used by EF Core tools (<c>dotnet ef migrations add ...</c>) to spin up
/// a <see cref="OpenSkiTimeDbContext"/> without booting the Avalonia
/// application. The connection string here is design-time only — it is
/// never used at runtime and points at a throwaway file path.
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<OpenSkiTimeDbContext>
{
    public OpenSkiTimeDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<OpenSkiTimeDbContext>()
            .UseSqlite("Data Source=design-time-only.db")
            .Options;
        return new OpenSkiTimeDbContext(options);
    }
}
