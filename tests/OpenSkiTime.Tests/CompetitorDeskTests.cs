using Microsoft.Data.Sqlite;
using OpenSkiTime.Application;
using OpenSkiTime.Domain;
using OpenSkiTime.Persistence;
using Xunit;

namespace OpenSkiTime.Tests;

public class CompetitorDeskTests
{
    private static readonly SeriesValues s_series = new("Levi", "Levi", "Club",
        new DateOnly(2026, 1, 10), new DateOnly(2026, 1, 11), "FIN", "2025/26");
    private static readonly CompetitionValues s_race = new("Slalom", "SL", new DateOnly(2026, 1, 10),
        Discipline.Slalom, RaceType.Club, 2, 0);
    private static readonly CompetitorValues s_athlete = new("Mäkelä", "Aino", 2010, "FIN123",
        "fin", "Ski Club", Gender.Female);

    [Fact]
    public async Task DeskBatchIsAtomicAcrossEditsEntriesAndDeletion()
    {
        var folder = Path.Combine(Path.GetTempPath(), "openskitime-batch", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore());
            var series = await workspace.CreateAsync(Path.Combine(folder, "series.ost"), s_series);
            var race = await workspace.SaveCompetitionAsync(null, s_race, series.Revision);
            var raceId = Assert.Single(race.Competitions).Id;
            var existing = await workspace.SaveDeskRowAsync(null, s_athlete, null, false, null, race.Revision);
            var revision = existing.Revision;
            var first = new DeskBatchRow(null, s_athlete with
            {
                Surname = "Laine", FirstName = "Lea", FederationCode = "FIN456"
            }, [new ImportEntryPatch(raceId, true, false, null)]);
            var invalid = new DeskBatchRow(null, s_athlete with
            {
                Surname = "Korhonen", FirstName = "Kai", FederationCode = "FIN456"
            }, []);
            await Assert.ThrowsAsync<DomainValidationException>(() => workspace.ApplyDeskBatchAsync(
                new DeskBatch(series.Id, revision, [first, invalid], [existing.Value.Id])));
            var unchanged = await workspace.ReadCompetitorDeskAsync();
            Assert.Equal(revision, unchanged.Revision);
            Assert.Equal(existing.Value.Id, Assert.Single(unchanged.Competitors).Id);
            Assert.Empty(unchanged.Participations);

            var result = await workspace.ApplyDeskBatchAsync(new DeskBatch(series.Id, revision,
                [first], [existing.Value.Id]));
            Assert.Equal((1, 0, 1), (result.Created, result.Updated, result.Deleted));
            var committed = await workspace.ReadCompetitorDeskAsync();
            Assert.Equal(result.Revision, committed.Revision);
            var competitor = Assert.Single(committed.Competitors);
            Assert.Equal("LAINE", competitor.Values.Surname);
            Assert.True(Assert.Single(committed.Participations).Participates);
            await Assert.ThrowsAsync<SeriesConflictException>(() => workspace.ApplyDeskBatchAsync(
                new DeskBatch(series.Id, revision, [first], [])));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public async Task DeskPersistsEditsAndEntriesWithCompetitionScopedImportedBibs()
    {
        var folder = Path.Combine(Path.GetTempPath(), "openskitime-m2", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, "series.ost");
            await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore());
            var series = await workspace.CreateAsync(path, s_series);
            var first = await workspace.SaveCompetitionAsync(null, s_race, series.Revision);
            var race1 = Assert.Single(first.Competitions).Id;
            var second = await workspace.SaveCompetitionAsync(null,
                s_race with { Name = "Giant slalom", ShortLabel = "GS" }, first.Revision);
            var race2 = second.Competitions.Single(x => x.Id != race1).Id;

            var added = await workspace.SaveDeskRowAsync(null, s_athlete, race1, true, 12, second.Revision);
            Assert.Equal("MÄKELÄ", added.Value.Values.Surname);
            var athleteId = added.Value.Id;
            var updated = await workspace.SaveDeskRowAsync(athleteId, s_athlete with { Club = "New Club" },
                race2, true, 12, added.Revision);
            Assert.Equal("New Club", updated.Value.Values.Club);
            await workspace.CloseAsync();
            await workspace.OpenAsync(path);
            var desk = await workspace.ReadCompetitorDeskAsync();
            Assert.Equal(athleteId, Assert.Single(desk.Competitors).Id);
            Assert.Equal(2, desk.Participations.Count);
            Assert.All(desk.Participations, entry => Assert.Equal(12, entry.ImportedBib));
            Assert.All(desk.Participations, entry => Assert.True(entry.Participates));

            var other = await workspace.SaveDeskRowAsync(null,
                s_athlete with { Surname = "Korhonen", FederationCode = null }, race1, false, null, desk.Revision);
            await Assert.ThrowsAsync<DomainValidationException>(() => workspace.SaveDeskRowAsync(other.Value.Id,
                s_athlete with { Surname = "Korhonen", FederationCode = null }, race1, true, 12, other.Revision));
            Assert.Null((await workspace.ReadCompetitorDeskAsync()).Participations.Single(x => x.CompetitorId == other.Value.Id).ImportedBib);
            await Assert.ThrowsAsync<DomainValidationException>(() => workspace.SaveDeskRowAsync(null,
                s_athlete with { Surname = "Another", FederationCode = "fin123" }, null, false, null, other.Revision));
            Assert.Equal(2, (await workspace.ReadCompetitorDeskAsync()).Competitors.Count);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public async Task CategoryRulesUseBirthYearAndGenderWithoutSilentlyChoosingOverlaps()
    {
        var folder = Path.Combine(Path.GetTempPath(), "openskitime-m2", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore());
            var created = await workspace.CreateAsync(Path.Combine(folder, "series.ost"), s_series);
            var girls = await workspace.SaveCategoryRuleAsync(null,
                new CategoryRuleValues("Girls U16", 2010, 2011, Gender.Female, 1), created.Revision);
            var desk = await workspace.ReadCompetitorDeskAsync();
            Assert.Equal("Girls U16", CategoryResolver.Resolve(s_athlete, desk.Categories));
            Assert.Equal("Unclassified", CategoryResolver.Resolve(s_athlete with { Gender = Gender.Male }, desk.Categories));
            var overlapping = await workspace.SaveCategoryRuleAsync(null,
                new CategoryRuleValues("Open U16", 2010, 2011, null, 2), girls.Revision);
            Assert.Equal("Ambiguous", CategoryResolver.Resolve(s_athlete,
                (await workspace.ReadCompetitorDeskAsync()).Categories));
            Assert.Equal("Review: gender", (s_athlete with { Gender = null }).Readiness(true));
            Assert.Equal("Not entered", s_athlete.Readiness(false));
            await workspace.RemoveCategoryRuleAsync(overlapping.Value.Id, overlapping.Revision);
            Assert.Single((await workspace.ReadCompetitorDeskAsync()).Categories);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public async Task ReplacingSavedCategoryRulesIsAtomicAndSurvivesReopen()
    {
        var folder = Path.Combine(Path.GetTempPath(), "openskitime-category-rules", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, "series.ost");
            await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore());
            var series = await workspace.CreateAsync(path, s_series);
            var original = await workspace.SaveCategoryRuleAsync(null,
                new CategoryRuleValues("Old", 2010, 2011, Gender.Female, 0), series.Revision);
            var replacement = new CategoryRuleValues("Women U16", 2010, 2011, Gender.Female, 1);
            await Assert.ThrowsAsync<DomainValidationException>(() => workspace.ReplaceCategoryRulesAsync(
                [replacement, replacement with { Label = "women u16" }], original.Revision));
            var unchanged = await workspace.ReadCompetitorDeskAsync();
            Assert.Equal(original.Revision, unchanged.Revision);
            Assert.Equal("Old", Assert.Single(unchanged.Categories).Values.Label);
            var revision = await workspace.ReplaceCategoryRulesAsync([replacement], original.Revision);
            await workspace.CloseAsync();
            await workspace.OpenAsync(path);
            var reopened = await workspace.ReadCompetitorDeskAsync();
            Assert.Equal(revision, reopened.Revision);
            Assert.Equal("Women U16", Assert.Single(reopened.Categories).Values.Label);
            Assert.Equal("Women U16", CategoryResolver.Resolve(s_athlete, reopened.Categories));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public async Task DatabaseRejectsParticipationAcrossSeries()
    {
        var folder = Path.Combine(Path.GetTempPath(), "openskitime-m2", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var file = Path.Combine(folder, "series.ost");
            await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore());
            var series = await workspace.CreateAsync(file, s_series);
            var saved = await workspace.SaveCompetitionAsync(null, s_race, series.Revision);
            var athlete = await workspace.SaveDeskRowAsync(null, s_athlete, null, false, null, saved.Revision);
            await using var connection = new SqliteConnection($"Data Source={file};Foreign Keys=True;Pooling=False");
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO Participations (SeriesId, CompetitorId, CompetitionId, Participates) VALUES ($series, $athlete, $race, 1)";
            command.Parameters.AddWithValue("$series", Guid.NewGuid().ToString().ToUpperInvariant());
            command.Parameters.AddWithValue("$athlete", athlete.Value.Id.ToString().ToUpperInvariant());
            command.Parameters.AddWithValue("$race", saved.Competitions[0].Id.ToString().ToUpperInvariant());
            await Assert.ThrowsAsync<SqliteException>(() => command.ExecuteNonQueryAsync());
        }
        finally { Directory.Delete(folder, recursive: true); }
    }
}
