using Microsoft.EntityFrameworkCore;
using OpenSkiTime.Domain.Competitors;
using OpenSkiTime.Domain.Series;
using OpenSkiTime.Persistence.Repositories;
using OpenSkiTime.Persistence.Tests.Infrastructure;

namespace OpenSkiTime.Persistence.Tests;

public class CompetitorPersistenceTests
{
    private static EventSeries NewSeries() =>
        EventSeries.Create(
            Guid.NewGuid(), "Winter Cup", "Lahti", "Club",
            new DateOnly(2026, 1, 10), new DateOnly(2026, 1, 12),
            "FIN", "2025/26",
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

    private static Competitor NewCompetitor(Guid seriesId,
        string last = "SMITH", string first = "John", int yob = 2005)
        => Competitor.Create(Guid.NewGuid(), seriesId, last, first, yob,
            nationCode: "FIN", clubName: "Lahti SC");

    [Fact]
    public async Task Roundtrip_competitor_with_uppercased_last_name()
    {
        await using var fx = new TempSqliteFixture();
        var repo = new EventSeriesRepository(fx.Context);

        var series = NewSeries();
        var competitor = NewCompetitor(series.Id, last: "müller", first: "Hannes", yob: 2007);
        series.AddCompetitor(competitor);

        await repo.AddAsync(series);
        await fx.Context.SaveChangesAsync();

        var options = new DbContextOptionsBuilder<OpenSkiTimeDbContext>()
            .UseSqlite($"Data Source={fx.DbPath}")
            .Options;
        await using var db2 = new OpenSkiTimeDbContext(options);
        var loaded = await db2.EventSeries
            .Include(e => e.Competitors)
            .FirstAsync(e => e.Id == series.Id);

        loaded.Competitors.Should().ContainSingle();
        var c = loaded.Competitors[0];
        c.LastName.Value.Should().Be("MÜLLER");
        c.FirstName.Should().Be("Hannes");
        c.YearOfBirth.Should().Be(2007);
        c.NationCode.Should().Be("FIN");
        c.ClubName.Should().Be("Lahti SC");
    }

    [Fact]
    public async Task Remove_series_cascades_to_competitors()
    {
        await using var fx = new TempSqliteFixture();
        var repo = new EventSeriesRepository(fx.Context);

        var series = NewSeries();
        series.AddCompetitor(NewCompetitor(series.Id));
        series.AddCompetitor(NewCompetitor(series.Id, "JONES", "Alice", 2008));

        await repo.AddAsync(series);
        await fx.Context.SaveChangesAsync();

        // Verify they exist.
        (await fx.Context.Competitors.CountAsync()).Should().Be(2);

        repo.Remove(series);
        await fx.Context.SaveChangesAsync();

        var options = new DbContextOptionsBuilder<OpenSkiTimeDbContext>()
            .UseSqlite($"Data Source={fx.DbPath}")
            .Options;
        await using var db2 = new OpenSkiTimeDbContext(options);
        (await db2.Competitors.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Duplicate_name_yob_raises_db_constraint()
    {
        await using var fx = new TempSqliteFixture();
        var repo = new EventSeriesRepository(fx.Context);

        var series = NewSeries();
        series.AddCompetitor(NewCompetitor(series.Id, "SMITH", "John", 2005));

        await repo.AddAsync(series);
        await fx.Context.SaveChangesAsync();

        var options = new DbContextOptionsBuilder<OpenSkiTimeDbContext>()
            .UseSqlite($"Data Source={fx.DbPath}")
            .Options;
        await using var db2 = new OpenSkiTimeDbContext(options);
        var loaded = await db2.EventSeries
            .Include(e => e.Competitors)
            .FirstAsync(e => e.Id == series.Id);

        var duplicate = NewCompetitor(loaded.Id, "SMITH", "John", 2005);
        var act = () => loaded.AddCompetitor(duplicate);
        act.Should().Throw<InvalidOperationException>().WithMessage("*already exists*");
    }

    [Fact]
    public async Task Import_apply_transaction_rolls_back_on_failure()
    {
        await using var fx = new TempSqliteFixture();
        var repo = new EventSeriesRepository(fx.Context);

        var series = NewSeries();
        await repo.AddAsync(series);
        await fx.Context.SaveChangesAsync();

        int competitorsBefore = await fx.Context.Competitors.CountAsync();

        var options = new DbContextOptionsBuilder<OpenSkiTimeDbContext>()
            .UseSqlite($"Data Source={fx.DbPath}")
            .Options;
        await using var db2 = new OpenSkiTimeDbContext(options);
        var loaded = await db2.EventSeries.FirstAsync(e => e.Id == series.Id);

        loaded.AddCompetitor(NewCompetitor(loaded.Id, "SMITH", "John", 2005));
        loaded.AddCompetitor(NewCompetitor(loaded.Id, "JONES", "Alice", 2006));

        var act = async () =>
        {
            await using var tx = await db2.Database.BeginTransactionAsync();
            try
            {
                await db2.SaveChangesAsync();
                loaded.AddCompetitor(NewCompetitor(loaded.Id, "SMITH", "John", 2005));
                await db2.SaveChangesAsync();
                await tx.CommitAsync();
            }
            catch
            {
                await tx.RollbackAsync();
                throw;
            }
        };

        await act.Should().ThrowAsync<Exception>();

        var options3 = new DbContextOptionsBuilder<OpenSkiTimeDbContext>()
            .UseSqlite($"Data Source={fx.DbPath}")
            .Options;
        await using var db3 = new OpenSkiTimeDbContext(options3);
        (await db3.Competitors.CountAsync()).Should().Be(competitorsBefore,
            "rollback should leave the DB unchanged");
    }

    [Fact]
    public async Task Bib_assignment_persists_correctly()
    {
        await using var fx = new TempSqliteFixture();
        var repo = new EventSeriesRepository(fx.Context);

        var series = NewSeries();
        var c1 = NewCompetitor(series.Id, "SMITH", "John", 2005);
        var c2 = NewCompetitor(series.Id, "JONES", "Alice", 2008);
        series.AddCompetitor(c1);
        series.AddCompetitor(c2);
        series.AssignBib(c1.Id, 1);
        series.AssignBib(c2.Id, 2);

        await repo.AddAsync(series);
        await fx.Context.SaveChangesAsync();

        var options = new DbContextOptionsBuilder<OpenSkiTimeDbContext>()
            .UseSqlite($"Data Source={fx.DbPath}")
            .Options;
        await using var db2 = new OpenSkiTimeDbContext(options);
        var loaded = await db2.EventSeries
            .Include(e => e.Competitors)
            .FirstAsync(e => e.Id == series.Id);

        loaded.Competitors.Should().HaveCount(2);
        loaded.Competitors.OrderBy(c => c.BibNumber)
            .Select(c => c.BibNumber)
            .Should().Equal(1, 2);
    }
}
