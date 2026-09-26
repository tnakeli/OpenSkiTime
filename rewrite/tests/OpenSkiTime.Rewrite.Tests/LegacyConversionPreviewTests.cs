using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using OpenSkiTime.Rewrite.Persistence;
using Xunit;

namespace OpenSkiTime.Rewrite.Tests;

public class LegacyConversionPreviewTests
{
    [Fact]
    public async Task LegacyPreviewMapsFieldsAndFlagsConflictsWithoutChangingSource()
    {
        var folder = Path.Combine(Path.GetTempPath(), "openskitime-m2-legacy", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var file = Path.Combine(folder, "legacy.db");
            var seriesA = Guid.NewGuid();
            var seriesB = Guid.NewGuid();
            var raceA = Guid.NewGuid();
            var raceB = Guid.NewGuid();
            var athleteA = Guid.NewGuid();
            var athleteB = Guid.NewGuid();
            await using (var db = new SqliteConnection($"Data Source={file};Pooling=False"))
            {
                await db.OpenAsync();
                await using var command = db.CreateCommand();
                command.CommandText = $"""
                    CREATE TABLE EventSeries (Id TEXT, Name TEXT, Location TEXT, Organizer TEXT, StartDate TEXT, EndDate TEXT, Nation TEXT, Season TEXT);
                    CREATE TABLE Competitions (Id TEXT, EventSeriesId TEXT, Name TEXT, ShortLabel TEXT, Date TEXT, Discipline INTEGER, RaceType INTEGER,
                      FisCode TEXT, LocalRaceCode TEXT, Gender INTEGER, CourseName TEXT, StartAltitudeMeters INTEGER, FinishAltitudeMeters INTEGER,
                      VerticalDropMeters INTEGER, HomologationNumber TEXT, NumberOfRuns INTEGER, NumberOfIntermediateTimes INTEGER);
                    CREATE TABLE Competitors (Id TEXT, EventSeriesId TEXT, LastName TEXT, FirstName TEXT, YearOfBirth INTEGER,
                      FisCode TEXT, NationCode TEXT, ClubName TEXT, Gender TEXT, BibNumber INTEGER);
                    CREATE TABLE Participations (CompetitorId TEXT, CompetitionId TEXT, IsParticipating INTEGER, StartOrder INTEGER);
                    CREATE TABLE CategoryRules (Id TEXT, EventSeriesId TEXT, Label TEXT, BirthYearMin INTEGER, BirthYearMax INTEGER, Gender TEXT, DisplayOrder INTEGER);
                    INSERT INTO EventSeries VALUES ('{seriesA}', 'Levi', 'Levi', 'Club', '2026-01-10', '2026-01-11', 'FIN', '2025/26');
                    INSERT INTO EventSeries VALUES ('{seriesB}', 'Ruka', 'Ruka', 'Club', '2026-02-10', '2026-02-11', 'FIN', '2025/26');
                    INSERT INTO Competitions VALUES ('{raceA}', '{seriesA}', 'Slalom', 'SL', '2026-01-10', 0, 2,
                      NULL, 'LOCAL-1', NULL, 'Front slope', 800, 600, 200, 'H-1', 2, 1);
                    INSERT INTO Competitions VALUES ('{raceB}', '{seriesB}', 'Giant slalom', 'GS', '2026-02-10', 1, 2,
                      NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, 2, 0);
                    INSERT INTO Competitors VALUES ('{athleteA}', '{seriesA}', 'Mäkelä', 'Aino', 2010, 'FIN123', 'FIN', 'Club A', 'Female', 7);
                    INSERT INTO Competitors VALUES ('{athleteB}', '{seriesA}', 'Laine', 'Liisa', 2011, 'FIN124', 'FIN', 'Club B', 'Female', 7);
                    INSERT INTO Participations VALUES ('{athleteA}', '{raceA}', 1, 3);
                    INSERT INTO Participations VALUES ('{athleteB}', '{raceA}', 0, NULL);
                    INSERT INTO Participations VALUES ('{athleteA}', '{raceB}', 1, NULL);
                    INSERT INTO Participations VALUES ('{Guid.NewGuid()}', '{Guid.NewGuid()}', 1, NULL);
                    INSERT INTO CategoryRules VALUES ('{Guid.NewGuid()}', '{seriesA}', 'Girls U16', 2010, 2011, 'Female', 1);
                    """;
                await command.ExecuteNonQueryAsync();
            }

            var before = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(file)));
            var source = await new SqliteLegacyConversionPreviewer().PreviewAsync(file);
            Assert.Equal(before, Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(file))));
            Assert.Equal(2, source.Series.Count);
            Assert.Contains(source.Warnings, x => x.Contains("Orphan participation", StringComparison.Ordinal));
            var levi = source.Series.Single(x => x.SourceId == seriesA);
            Assert.Equal("Front slope", Assert.Single(levi.Competitions).Values.CourseName);
            Assert.Equal("LOCAL-1", levi.Competitions[0].Values.LocalRaceCode);
            Assert.Equal(2010, levi.Competitors.Single(x => x.SourceId == athleteA).Values.BirthYear);
            Assert.Equal("FIN123", levi.Competitors.Single(x => x.SourceId == athleteA).Values.FederationCode);
            Assert.Equal(7, levi.Competitors.Single(x => x.SourceId == athleteA).SeriesBibReference);
            Assert.Equal(2, levi.Entries.Count);
            Assert.False(levi.Entries.Single(x => x.CompetitorId == athleteB).Participates);
            Assert.Equal(3, levi.Entries.Single(x => x.CompetitorId == athleteA).StartOrder);
            Assert.Equal("Girls U16", Assert.Single(levi.Categories).Values.Label);
            Assert.Contains(levi.Warnings, x => x.Contains("outside this series", StringComparison.Ordinal));
            Assert.Contains(levi.Warnings, x => x.Contains("repeated imported bib", StringComparison.Ordinal));
            Assert.Empty(source.Series.Single(x => x.SourceId == seriesB).Competitors);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }
}
