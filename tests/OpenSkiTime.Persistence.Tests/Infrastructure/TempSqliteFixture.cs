using Microsoft.EntityFrameworkCore;

namespace OpenSkiTime.Persistence.Tests.Infrastructure;

/// <summary>
/// Test helper that creates a throwaway SQLite database file, opens a
/// <see cref="OpenSkiTimeDbContext"/> against it, and deletes the file on
/// dispose. We use a real file (not <c>:memory:</c>) per
/// <c>research.md</c> — in-memory SQLite has subtle behavioral differences
/// from on-disk that have bitten EF Core projects in the past.
/// </summary>
public sealed class TempSqliteFixture : IAsyncDisposable
{
    public string DbPath { get; }

    public OpenSkiTimeDbContext Context { get; }

    public TempSqliteFixture()
    {
        DbPath = Path.Combine(
            Path.GetTempPath(),
            $"openskitime-test-{Guid.NewGuid():N}.db");

        var options = new DbContextOptionsBuilder<OpenSkiTimeDbContext>()
            .UseSqlite($"Data Source={DbPath}")
            .Options;

        Context = new OpenSkiTimeDbContext(options);
        Context.Database.EnsureCreated();
    }

    public async ValueTask DisposeAsync()
    {
        await Context.DisposeAsync();

        // Best-effort cleanup; on Windows the file may briefly stay locked.
        try
        {
            if (File.Exists(DbPath))
            {
                File.Delete(DbPath);
            }
        }
        catch (IOException)
        {
            // Ignored: temp file will be reaped by OS housekeeping.
        }
    }
}
