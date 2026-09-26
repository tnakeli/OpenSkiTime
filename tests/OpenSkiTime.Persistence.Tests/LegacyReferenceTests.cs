using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using OpenSkiTime.Application.Abstractions;
using OpenSkiTime.Application.Competitions;
using OpenSkiTime.Application.Competitors;
using OpenSkiTime.Application.Participations;
using OpenSkiTime.Application.Series;
using OpenSkiTime.Domain.CategoryRules;
using OpenSkiTime.Domain.Common;
using OpenSkiTime.Domain.Competitions;
using OpenSkiTime.Domain.Series;
using OpenSkiTime.Import;

namespace OpenSkiTime.Persistence.Tests;

public class LegacyReferenceTests
{
    [Fact]
    public async Task Initial_schema_upgrades_without_losing_existing_series_and_competition()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"openskitime-m0-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<OpenSkiTimeDbContext>()
            .UseSqlite($"Data Source={dbPath}").Options;

        try
        {
            var series = EventSeries.Create(Guid.NewGuid(), "Levi M0", "Levi", "Club",
                new DateOnly(2026, 1, 10), new DateOnly(2026, 1, 11), "FIN", "2025/26",
                new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            series.AddCompetition(Competition.Create(Guid.NewGuid(), series.Id, "Slalom", "3.1 SL",
                new DateOnly(2026, 1, 10), Discipline.SL, RaceType.Club, 2, 0));

            await using (var db = new OpenSkiTimeDbContext(options))
            {
                await db.GetService<IMigrator>().MigrateAsync("20260528160602_0001_Initial");
                db.EventSeries.Add(series);
                await db.SaveChangesAsync();
                (await db.Database.GetAppliedMigrationsAsync()).Should().ContainSingle();
                await db.Database.MigrateAsync();
                await db.Database.MigrateAsync();
            }

            await using var reopened = new OpenSkiTimeDbContext(options);
            (await reopened.Database.GetPendingMigrationsAsync()).Should().BeEmpty();
            (await reopened.Database.GetAppliedMigrationsAsync()).Should().HaveCount(2);
            var loaded = await reopened.EventSeries.Include(s => s.Competitions)
                .SingleAsync(s => s.Id == series.Id);
            loaded.Name.Should().Be("Levi M0");
            loaded.Competitions.Should().ContainSingle(c => c.ShortLabel == "3.1 SL");
            (await reopened.CategoryRules.CountAsync()).Should().Be(0);
            (await reopened.Competitors.CountAsync()).Should().Be(0);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath))
            {
                File.Delete(dbPath);
            }
        }
    }

    [Fact]
    public async Task Series_competition_category_and_import_reopen_from_migrated_sqlite()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"openskitime-m0-{Guid.NewGuid():N}.db");
        try
        {
            var services = new ServiceCollection()
                .AddOpenSkiTimePersistence(dbPath)
                .AddSingleton<IClock, SystemClock>()
                .AddScoped<CreateEventSeriesUseCase>()
                .AddScoped<UpdateEventSeriesUseCase>()
                .AddScoped<AddCompetitionUseCase>()
                .AddScoped<UpdateCompetitionUseCase>()
                .AddScoped<AddCompetitorUseCase>()
                .AddScoped<EditCompetitorUseCase>()
                .AddScoped<AssignBibUseCase>()
                .AddScoped<SetParticipationUseCase>()
                .AddScoped<ImportPreviewService>()
                .AddScoped<ImportApplyService>()
                .BuildServiceProvider();
            await using (services)
            {
                await using (var migrationScope = services.CreateAsyncScope())
                {
                    await migrationScope.ServiceProvider.GetRequiredService<OpenSkiTimeDbContext>()
                        .Database.MigrateAsync();
                }

                Guid seriesId;
                Guid competitionId;
                Guid manualCompetitorId;
                await using (var scope = services.CreateAsyncScope())
                {
                    var provider = scope.ServiceProvider;
                    var created = await provider.GetRequiredService<CreateEventSeriesUseCase>()
                        .ExecuteAsync(new CreateEventSeriesCommand("Levi M0", "Levi", "Club",
                            new DateOnly(2026, 1, 10), new DateOnly(2026, 1, 11), "FIN", "2025/26"));
                    created.Succeeded.Should().BeTrue();
                    seriesId = created.Value;

                    var changed = await provider.GetRequiredService<UpdateEventSeriesUseCase>()
                        .ExecuteAsync(new UpdateEventSeriesCommand(seriesId, "Levi M0 edited", "Levi", "Club",
                            new DateOnly(2026, 1, 10), new DateOnly(2026, 1, 11), "FIN", "2025/26"));
                    changed.Succeeded.Should().BeTrue();

                    var competition = await provider.GetRequiredService<AddCompetitionUseCase>()
                        .ExecuteAsync(new AddCompetitionCommand(seriesId, "Slalom", "3.1 SL",
                            new DateOnly(2026, 1, 10), Discipline.SL, RaceType.Club, 2, 0));
                    competition.Succeeded.Should().BeTrue();
                    competitionId = competition.Value;

                    changed = await provider.GetRequiredService<UpdateCompetitionUseCase>()
                        .ExecuteAsync(new UpdateCompetitionCommand(seriesId, competitionId, "Edited slalom", "3.1 SL",
                            new DateOnly(2026, 1, 10), Discipline.SL, RaceType.Club, 2, 0,
                            null, null, null, null, null, null, null, null));
                    changed.Succeeded.Should().BeTrue();

                    var repo = provider.GetRequiredService<IEventSeriesRepository>();
                    var series = await repo.GetByIdAsync(seriesId);
                    series!.AddCategoryRule(CategoryRule.Create(Guid.NewGuid(), seriesId, "U22",
                        2005, 2008, Gender.Male, 1));
                    await provider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();

                    const string tsv = "LastName\tFirstName\tYOB\tNat\tBib\t3.1 SL\n"
                        + "Müller\tHannes\t2007\tFIN\t7\tx";
                    var preview = await provider.GetRequiredService<ImportPreviewService>()
                        .PreviewAsync(seriesId, tsv);
                    preview.Succeeded.Should().BeTrue();
                    preview.Value!.Diff.NewCompetitors.Should().ContainSingle();
                    var applied = await provider.GetRequiredService<ImportApplyService>()
                        .ApplyAsync(seriesId, preview.Value.SnapshotVersion, preview.Value.Diff);
                    applied.Succeeded.Should().BeTrue();
                    applied.Value!.HasErrors.Should().BeFalse();

                    var manual = await provider.GetRequiredService<AddCompetitorUseCase>()
                        .ExecuteAsync(new AddCompetitorRequest
                        {
                            EventSeriesId = seriesId, LastName = "Korhonen", FirstName = "Anna",
                            YearOfBirth = 2006, NationCode = "FIN",
                        });
                    manual.Succeeded.Should().BeTrue();
                    manualCompetitorId = manual.Value;
                    var edited = await provider.GetRequiredService<EditCompetitorUseCase>()
                        .ExecuteAsync(new EditCompetitorRequest
                        {
                            EventSeriesId = seriesId, CompetitorId = manualCompetitorId,
                            LastName = "Virtanen", FirstName = "Anna", YearOfBirth = 2006,
                            NationCode = "FIN", ClubName = "Levi SC",
                        });
                    edited.Succeeded.Should().BeTrue();
                    var participation = provider.GetRequiredService<SetParticipationUseCase>();
                    (await participation.ExecuteAsync(new SetParticipationRequest
                    {
                        EventSeriesId = seriesId, CompetitorId = manualCompetitorId,
                        CompetitionId = competitionId, IsParticipating = true,
                    })).Succeeded.Should().BeTrue();
                    (await participation.ExecuteAsync(new SetParticipationRequest
                    {
                        EventSeriesId = seriesId, CompetitorId = manualCompetitorId,
                        CompetitionId = competitionId, IsParticipating = false,
                    })).Succeeded.Should().BeTrue();
                }

                await using (var reopened = services.CreateAsyncScope())
                {
                    var repo = reopened.ServiceProvider.GetRequiredService<IEventSeriesRepository>();
                    var series = await repo.GetByIdAsync(seriesId);
                    series.Should().NotBeNull();
                    series!.Name.Should().Be("Levi M0 edited");
                    series.Competitions.Should().ContainSingle(c => c.Id == competitionId && c.Name == "Edited slalom");
                    series.CategoryRules.Should().ContainSingle(c => c.Label == "U22" && c.Matches(2007, Gender.Male));
                    var imported = series.Competitors.Single(c => c.LastName.Value == "MÜLLER");
                    imported.BibNumber.Should().Be(7);
                    imported.Participations.Should().ContainSingle(p =>
                        p.CompetitionId == competitionId && p.IsParticipating);
                    var manual = series.Competitors.Single(c => c.Id == manualCompetitorId);
                    manual.LastName.Value.Should().Be("VIRTANEN");
                    manual.ClubName.Should().Be("Levi SC");
                    manual.Participations.Should().ContainSingle(p =>
                        p.CompetitionId == competitionId && !p.IsParticipating);
                }
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath))
            {
                File.Delete(dbPath);
            }
        }
    }
}
