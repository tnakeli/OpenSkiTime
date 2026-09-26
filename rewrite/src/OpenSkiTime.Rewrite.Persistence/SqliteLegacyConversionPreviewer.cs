using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using OpenSkiTime.Rewrite.Application;
using OpenSkiTime.Rewrite.Domain;

namespace OpenSkiTime.Rewrite.Persistence;

public sealed class SqliteLegacyConversionPreviewer : ILegacyConversionPreviewer
{
    private static readonly string[] s_legacyTables =
        ["EventSeries", "Competitions", "Competitors", "Participations", "CategoryRules"];

    public async Task<LegacySourcePreview> PreviewAsync(string filePath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            throw new SeriesFileException("Choose an existing legacy database file.");
        }
        var sourcePath = Path.GetFullPath(filePath);
        var snapshotPath = Path.Combine(Path.GetTempPath(), $"openskitime-legacy-preview-{Guid.NewGuid():N}.db");
        try
        {
            await using (var source = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = sourcePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false,
            }.ToString()))
            await using (var snapshot = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = snapshotPath, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false,
            }.ToString()))
            {
                await source.OpenAsync(ct);
                await snapshot.OpenAsync(ct);
                source.BackupDatabase(snapshot);
            }

            await using var file = File.OpenRead(snapshotPath);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(file, ct));
            await using var db = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = snapshotPath, Mode = SqliteOpenMode.ReadOnly, Pooling = false,
            }.ToString());
            await db.OpenAsync(ct);
            await VerifyLegacySchemaAsync(db, ct);
            var series = await ReadSeriesAsync(db, ct);
            var competitions = await ReadCompetitionsAsync(db, ct);
            var competitors = await ReadCompetitorsAsync(db, ct);
            var entries = await ReadEntriesAsync(db, ct);
            var categories = await ReadCategoriesAsync(db, ct);

            var previews = series.Select(item => BuildPreview(item, competitions, competitors, entries, categories)).ToArray();
            var seriesIds = series.Select(x => x.Id).ToHashSet();
            var competitionIds = competitions.Select(x => x.Id).ToHashSet();
            var competitorIds = competitors.Select(x => x.Id).ToHashSet();
            var sourceWarnings = new List<string>();
            foreach (var entry in entries.Where(x => !competitorIds.Contains(x.CompetitorId)
                         || !competitionIds.Contains(x.CompetitionId)))
            {
                sourceWarnings.Add($"Orphan participation {entry.CompetitorId}/{entry.CompetitionId} needs review.");
            }
            foreach (var race in competitions.Where(x => !seriesIds.Contains(x.SeriesId)))
            {
                sourceWarnings.Add($"Competition {race.Id} has no event series.");
            }
            foreach (var athlete in competitors.Where(x => !seriesIds.Contains(x.SeriesId)))
            {
                sourceWarnings.Add($"Competitor {athlete.Id} has no event series.");
            }
            foreach (var rule in categories.Where(x => !seriesIds.Contains(x.SeriesId)))
            {
                sourceWarnings.Add($"Category {rule.Id} has no event series.");
            }
            return new LegacySourcePreview(sourcePath, hash, previews, sourceWarnings);
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException or FormatException)
        {
            throw new SeriesFileException("The legacy database could not be previewed. Check the file and its schema.", ex);
        }
        finally
        {
            if (File.Exists(snapshotPath)) { File.Delete(snapshotPath); }
        }
    }

    private static LegacySeriesPreview BuildPreview(SeriesSource item,
        IReadOnlyList<CompetitionSource> competitions, IReadOnlyList<CompetitorSource> competitors,
        IReadOnlyList<EntrySource> entries, IReadOnlyList<CategorySource> categories)
    {
        var warnings = new List<string>();
        var races = competitions.Where(x => x.SeriesId == item.Id).ToArray();
        var athletes = competitors.Where(x => x.SeriesId == item.Id).ToArray();
        var rules = categories.Where(x => x.SeriesId == item.Id).ToArray();
        var raceIds = races.Select(x => x.Id).ToHashSet();
        var athleteIds = athletes.Select(x => x.Id).ToHashSet();
        var validEntries = entries.Where(x => athleteIds.Contains(x.CompetitorId) || raceIds.Contains(x.CompetitionId)).ToArray();
        foreach (var entry in validEntries.Where(x => !athleteIds.Contains(x.CompetitorId) || !raceIds.Contains(x.CompetitionId)))
        {
            warnings.Add($"Participation {entry.CompetitorId}/{entry.CompetitionId} refers outside this series or is orphaned.");
        }
        var mappedEntries = validEntries.Where(x => athleteIds.Contains(x.CompetitorId) && raceIds.Contains(x.CompetitionId))
            .Select(x => new LegacyEntryPreview(x.CompetitorId, x.CompetitionId, x.Participates,
                athletes.Single(a => a.Id == x.CompetitorId).Values.SeriesBibReference, x.StartOrder)).ToArray();
        foreach (var duplicate in mappedEntries.Where(x => x.ImportedBib is not null)
            .GroupBy(x => (x.CompetitionId, x.ImportedBib)).Where(x => x.Count() > 1))
        {
            warnings.Add($"Competition {duplicate.Key.CompetitionId} has repeated imported bib {duplicate.Key.ImportedBib}.");
        }
        foreach (var duplicate in athletes.Where(x => x.Values.Values.FederationCode is not null)
            .GroupBy(x => x.Values.Values.FederationCode!, StringComparer.OrdinalIgnoreCase).Where(x => x.Count() > 1))
        {
            warnings.Add($"Federation code {duplicate.Key} is repeated in this series.");
        }
        foreach (var race in races)
        {
            if (race.LegacyDiscipline == 5) { warnings.Add($"Competition {race.Id} uses legacy KOMBI; review its discipline before conversion."); }
            if (race.LegacyDiscipline is < 0 or > 6) { warnings.Add($"Competition {race.Id} has an unknown discipline."); }
            if (race.LegacyRaceType is < 0 or > 3) { warnings.Add($"Competition {race.Id} has an unknown race type."); }
            if (race.LegacyGender is not null) { warnings.Add($"Competition {race.Id} has a gender restriction not represented in the new competition model."); }
            TryValidate(race.Values, warnings, $"Competition {race.Id}");
        }
        foreach (var athlete in athletes)
        {
            if (athlete.UnknownGender) { warnings.Add($"Competitor {athlete.Id} has an unrecognized gender value."); }
            try { athlete.Values.Values.Validated(item.Values.EndDate.Year); }
            catch (DomainValidationException ex) { warnings.Add($"Competitor {athlete.Id}: {ex.Message}"); }
        }
        foreach (var category in rules)
        {
            try { category.Values.Values.Validated(item.Values.EndDate.Year); }
            catch (DomainValidationException ex) { warnings.Add($"Category {category.Id}: {ex.Message}"); }
            if (category.UnknownGender) { warnings.Add($"Category {category.Id} has an unrecognized gender value."); }
        }
        var ruleDetails = rules.Select(x => new CategoryRuleDetails(x.Id, x.Values.Values)).ToArray();
        foreach (var athlete in athletes.Where(x => CategoryResolver.Resolve(x.Values.Values, ruleDetails) == "Ambiguous"))
        {
            warnings.Add($"Competitor {athlete.Id} matches overlapping category rules.");
        }
        try { item.Values.Validated(); }
        catch (DomainValidationException ex) { warnings.Add($"Series {item.Id}: {ex.Message}"); }
        return new LegacySeriesPreview(item.Id, item.Values,
            races.Select(x => new LegacyCompetitionPreview(x.Id, x.Values)).ToArray(),
            athletes.Select(x => x.Values).ToArray(), mappedEntries,
            rules.Select(x => x.Values).ToArray(), warnings);
    }

    private static void TryValidate(CompetitionValues values, List<string> warnings, string label)
    {
        try { values.Validated(); }
        catch (DomainValidationException ex) { warnings.Add($"{label}: {ex.Message}"); }
    }

    private static async Task VerifyLegacySchemaAsync(SqliteConnection db, CancellationToken ct)
    {
        await using var command = db.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type='table'";
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) { names.Add(reader.GetString(0)); }
        if (!s_legacyTables.All(names.Contains))
        {
            throw new SeriesFileException("This is not a supported legacy OpenSkiTime database.");
        }
    }

    private static async Task<List<SeriesSource>> ReadSeriesAsync(SqliteConnection db, CancellationToken ct)
    {
        await using var command = db.CreateCommand();
        command.CommandText = "SELECT Id, Name, Location, Organizer, StartDate, EndDate, Nation, Season FROM EventSeries";
        await using var reader = await command.ExecuteReaderAsync(ct);
        var result = new List<SeriesSource>();
        while (await reader.ReadAsync(ct))
        {
            result.Add(new SeriesSource(Id(reader, 0), new SeriesValues(reader.GetString(1), reader.GetString(2),
                reader.GetString(3), Date(reader, 4), Date(reader, 5), reader.GetString(6), reader.GetString(7))));
        }
        return result;
    }

    private static async Task<List<CompetitionSource>> ReadCompetitionsAsync(SqliteConnection db, CancellationToken ct)
    {
        await using var command = db.CreateCommand();
        command.CommandText = """
            SELECT Id, EventSeriesId, Name, ShortLabel, Date, Discipline, RaceType, FisCode,
              LocalRaceCode, Gender, CourseName, StartAltitudeMeters, FinishAltitudeMeters,
              VerticalDropMeters, HomologationNumber, NumberOfRuns, NumberOfIntermediateTimes
            FROM Competitions
            """;
        await using var reader = await command.ExecuteReaderAsync(ct);
        var result = new List<CompetitionSource>();
        while (await reader.ReadAsync(ct))
        {
            var discipline = reader.GetInt32(5) switch
            {
                0 => Discipline.Slalom, 1 => Discipline.GiantSlalom, 2 => Discipline.SuperG,
                3 => Discipline.Downhill, 4 => Discipline.AlpineCombined, _ => Discipline.Other,
            };
            var raceType = reader.GetInt32(6) switch
            {
                0 => RaceType.Fis, 1 => RaceType.National, 2 => RaceType.Club,
                3 => RaceType.Training, _ => RaceType.Club,
            };
            var values = new CompetitionValues(reader.GetString(2), reader.GetString(3), Date(reader, 4),
                discipline, raceType, reader.GetInt32(15), reader.GetInt32(16),
                OptionalString(reader, 7), OptionalString(reader, 8), OptionalString(reader, 10),
                OptionalInt(reader, 11), OptionalInt(reader, 12), OptionalInt(reader, 13), OptionalString(reader, 14));
            result.Add(new CompetitionSource(Id(reader, 0), Id(reader, 1), values,
                reader.GetInt32(5), reader.GetInt32(6), OptionalInt(reader, 9)));
        }
        return result;
    }

    private static async Task<List<CompetitorSource>> ReadCompetitorsAsync(SqliteConnection db, CancellationToken ct)
    {
        await using var command = db.CreateCommand();
        command.CommandText = "SELECT Id, EventSeriesId, LastName, FirstName, YearOfBirth, FisCode, NationCode, ClubName, Gender, BibNumber FROM Competitors";
        await using var reader = await command.ExecuteReaderAsync(ct);
        var result = new List<CompetitorSource>();
        while (await reader.ReadAsync(ct))
        {
            var values = new CompetitorValues(reader.GetString(2), reader.GetString(3), reader.GetInt32(4),
                OptionalString(reader, 5), OptionalString(reader, 6), OptionalString(reader, 7),
                ParseGender(OptionalString(reader, 8)));
            result.Add(new CompetitorSource(Id(reader, 0), Id(reader, 1),
                new LegacyCompetitorPreview(Id(reader, 0), values, OptionalInt(reader, 9)),
                OptionalString(reader, 8) is { } genderText && ParseGender(genderText) is null));
        }
        return result;
    }

    private static async Task<List<EntrySource>> ReadEntriesAsync(SqliteConnection db, CancellationToken ct)
    {
        await using var command = db.CreateCommand();
        command.CommandText = "SELECT CompetitorId, CompetitionId, IsParticipating, StartOrder FROM Participations";
        await using var reader = await command.ExecuteReaderAsync(ct);
        var result = new List<EntrySource>();
        while (await reader.ReadAsync(ct))
        {
            result.Add(new EntrySource(Id(reader, 0), Id(reader, 1), reader.GetBoolean(2), OptionalInt(reader, 3)));
        }
        return result;
    }

    private static async Task<List<CategorySource>> ReadCategoriesAsync(SqliteConnection db, CancellationToken ct)
    {
        await using var command = db.CreateCommand();
        command.CommandText = "SELECT Id, EventSeriesId, Label, BirthYearMin, BirthYearMax, Gender, DisplayOrder FROM CategoryRules";
        await using var reader = await command.ExecuteReaderAsync(ct);
        var result = new List<CategorySource>();
        while (await reader.ReadAsync(ct))
        {
            var genderText = OptionalString(reader, 5);
            var gender = ParseGender(genderText);
            var values = new CategoryRuleValues(reader.GetString(2), reader.GetInt32(3), reader.GetInt32(4),
                gender, reader.GetInt32(6));
            result.Add(new CategorySource(Id(reader, 0), Id(reader, 1),
                new LegacyCategoryPreview(Id(reader, 0), values), genderText is not null && gender is null));
        }
        return result;
    }

    private static Guid Id(SqliteDataReader reader, int index) => Guid.Parse(reader.GetString(index));
    private static DateOnly Date(SqliteDataReader reader, int index)
        => DateOnly.ParseExact(reader.GetString(index), "yyyy-MM-dd", CultureInfo.InvariantCulture);
    private static string? OptionalString(SqliteDataReader reader, int index)
        => reader.IsDBNull(index) ? null : reader.GetString(index);
    private static int? OptionalInt(SqliteDataReader reader, int index)
        => reader.IsDBNull(index) ? null : reader.GetInt32(index);
    private static Gender? ParseGender(string? text) => text?.Trim().ToUpperInvariant() switch
    {
        "FEMALE" => Gender.Female, "MALE" => Gender.Male, "OTHER" => Gender.Other, _ => null,
    };

    private sealed record SeriesSource(Guid Id, SeriesValues Values);
    private sealed record CompetitionSource(Guid Id, Guid SeriesId, CompetitionValues Values,
        int LegacyDiscipline, int LegacyRaceType, int? LegacyGender);
    private sealed record CompetitorSource(Guid Id, Guid SeriesId, LegacyCompetitorPreview Values, bool UnknownGender);
    private sealed record EntrySource(Guid CompetitorId, Guid CompetitionId, bool Participates, int? StartOrder);
    private sealed record CategorySource(Guid Id, Guid SeriesId, LegacyCategoryPreview Values, bool UnknownGender);
}
