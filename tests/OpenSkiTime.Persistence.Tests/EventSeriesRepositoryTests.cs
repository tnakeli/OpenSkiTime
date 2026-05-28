using Microsoft.EntityFrameworkCore;
using OpenSkiTime.Domain.Common;
using OpenSkiTime.Domain.Competitions;
using OpenSkiTime.Domain.Series;
using OpenSkiTime.Persistence.Repositories;
using OpenSkiTime.Persistence.Tests.Infrastructure;

namespace OpenSkiTime.Persistence.Tests;

public class EventSeriesRepositoryTests
{
    private static EventSeries NewSeries() =>
        EventSeries.Create(
            Guid.NewGuid(), "Levi Cup", "Levi", "Club",
            new DateOnly(2026, 4, 3), new DateOnly(2026, 4, 5),
            "FIN", "2025/26",
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

    private static Competition NewCompetition(Guid seriesId, string label) =>
        Competition.Create(
            Guid.NewGuid(), seriesId,
            "Slalom", label, new DateOnly(2026, 4, 3),
            Discipline.SL, RaceType.Club, 2, 0);

    [Fact]
    public async Task Roundtrip_with_competitions()
    {
        await using var fx = new TempSqliteFixture();
        var repo = new EventSeriesRepository(fx.Context);

        var series = NewSeries();
        series.AddCompetition(NewCompetition(series.Id, "3.1 SL"));
        series.AddCompetition(NewCompetition(series.Id, "4.1 GS"));

        await repo.AddAsync(series);
        await fx.Context.SaveChangesAsync();

        // New context to ensure we hit the DB, not the change tracker.
        var options = new DbContextOptionsBuilder<OpenSkiTimeDbContext>()
            .UseSqlite($"Data Source={fx.DbPath}")
            .Options;
        await using var db2 = new OpenSkiTimeDbContext(options);
        var repo2 = new EventSeriesRepository(db2);

        var loaded = await repo2.GetByIdAsync(series.Id);

        loaded.Should().NotBeNull();
        loaded!.Name.Should().Be("Levi Cup");
        loaded.Nation.Should().Be("FIN");
        loaded.Competitions.Should().HaveCount(2);
        loaded.Competitions.Select(c => c.ShortLabel).Should().BeEquivalentTo("3.1 SL", "4.1 GS");
    }

    [Fact]
    public async Task ListAsync_returns_summaries_in_start_date_order()
    {
        await using var fx = new TempSqliteFixture();
        var repo = new EventSeriesRepository(fx.Context);

        var earlier = EventSeries.Create(
            Guid.NewGuid(), "Earlier", "Levi", "Club",
            new DateOnly(2026, 1, 5), new DateOnly(2026, 1, 6),
            "FIN", "2025/26", DateTime.UtcNow);
        var later = EventSeries.Create(
            Guid.NewGuid(), "Later", "Ruka", "Club",
            new DateOnly(2026, 4, 5), new DateOnly(2026, 4, 6),
            "FIN", "2025/26", DateTime.UtcNow);

        await repo.AddAsync(later);
        await repo.AddAsync(earlier);
        await fx.Context.SaveChangesAsync();

        var summaries = await repo.ListAsync();
        summaries.Select(s => s.Name).Should().Equal("Earlier", "Later");
    }

    [Fact]
    public async Task RowVersion_increments_when_competition_added_later()
    {
        await using var fx = new TempSqliteFixture();
        var repo = new EventSeriesRepository(fx.Context);

        var series = NewSeries();
        await repo.AddAsync(series);
        await fx.Context.SaveChangesAsync();
        var v1 = series.RowVersion;

        // Re-load in fresh context, add a competition, save, re-read.
        var options = new DbContextOptionsBuilder<OpenSkiTimeDbContext>()
            .UseSqlite($"Data Source={fx.DbPath}")
            .Options;
        await using (var db2 = new OpenSkiTimeDbContext(options))
        {
            var loaded = await db2.EventSeries
                .Include(e => e.Competitions)
                .FirstAsync(e => e.Id == series.Id);
            loaded.AddCompetition(NewCompetition(series.Id, "3.1 SL"));
            await db2.SaveChangesAsync();
        }

        await using var db3 = new OpenSkiTimeDbContext(options);
        var reloaded = await db3.EventSeries.AsNoTracking().FirstAsync(e => e.Id == series.Id);

        reloaded.RowVersion.Should().BeGreaterThan(v1);
    }

    [Fact]
    public async Task Remove_cascades_to_competitions()
    {
        await using var fx = new TempSqliteFixture();
        var repo = new EventSeriesRepository(fx.Context);

        var series = NewSeries();
        series.AddCompetition(NewCompetition(series.Id, "3.1 SL"));
        await repo.AddAsync(series);
        await fx.Context.SaveChangesAsync();

        repo.Remove(series);
        await fx.Context.SaveChangesAsync();

        var options = new DbContextOptionsBuilder<OpenSkiTimeDbContext>()
            .UseSqlite($"Data Source={fx.DbPath}")
            .Options;
        await using var db2 = new OpenSkiTimeDbContext(options);

        (await db2.EventSeries.CountAsync()).Should().Be(0);
        (await db2.Competitions.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Duplicate_short_label_in_db_violates_unique_index()
    {
        await using var fx = new TempSqliteFixture();
        var repo = new EventSeriesRepository(fx.Context);

        var s1 = NewSeries();
        s1.AddCompetition(NewCompetition(s1.Id, "3.1 SL"));
        await repo.AddAsync(s1);
        await fx.Context.SaveChangesAsync();

        // Try to add a second competition with the same label by bypassing
        // the in-memory aggregate guard (simulate an out-of-band insert).
        var rogue = NewCompetition(s1.Id, "3.1 SL");
        fx.Context.Competitions.Add(rogue);

        var act = () => fx.Context.SaveChangesAsync();
        await act.Should().ThrowAsync<DbUpdateException>();
    }
}
