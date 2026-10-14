using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using OpenSkiTime.Application;
using OpenSkiTime.Domain;

namespace OpenSkiTime.Persistence;

/// <summary>
/// Saved FIS points of competitors and the lists they came from. Both tables are part of the current development
/// format (OpenSkiTime.Development/5) and are created with every new series file; older files are rejected, never
/// upgraded. A competitor's points are removed with the competitor (foreign key cascade).
/// </summary>
internal static class FisPointsStore
{
    private const string Unreadable = "Saved FIS points are unreadable. Restore a backup or contact support.";

    // Created by SqliteSeriesFileStore.CreateAsync together with the EF model.
    internal const string Schema = "CREATE TABLE CompetitorFisPoints ("
        + "CompetitorId TEXT PRIMARY KEY NOT NULL REFERENCES Competitors(Id) ON DELETE CASCADE, Json TEXT NOT NULL);"
        + "CREATE TABLE FisPointsLists (Code TEXT PRIMARY KEY NOT NULL, Json TEXT NOT NULL);";

    public static async Task<(Dictionary<Guid, CompetitorFisPoints> Points, List<FisPointsListInfo> Lists)> ReadAsync(SeriesDbContext db, CancellationToken ct)
    {
        var points = new Dictionary<Guid, CompetitorFisPoints>();
        var lists = new List<FisPointsListInfo>();
        var connection = db.Database.GetDbConnection();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
            command.CommandText = "SELECT CompetitorId, Json FROM CompetitorFisPoints";
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                try
                {
                    points[Guid.Parse(reader.GetString(0), CultureInfo.InvariantCulture)] = JsonSerializer.Deserialize<CompetitorFisPoints>(reader.GetString(1))
                        ?? throw new SeriesFileException(Unreadable);
                }
                catch (Exception ex) when (ex is JsonException or FormatException) { throw new SeriesFileException(Unreadable, ex); }
            }
        }
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
            command.CommandText = "SELECT Json FROM FisPointsLists ORDER BY Code";
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                try { lists.Add(JsonSerializer.Deserialize<FisPointsListInfo>(reader.GetString(0)) ?? throw new SeriesFileException(Unreadable)); }
                catch (JsonException ex) { throw new SeriesFileException(Unreadable, ex); }
            }
        }
        return (points, lists);
    }

    /// <summary>
    /// Checks the changes before anything is written: valid points and a known list (in this save or already saved) for every
    /// list code. Returns the validated values to <see cref="WriteAsync"/>.
    /// </summary>
    public static async Task<(List<(Guid CompetitorId, CompetitorFisPoints? Points)> Changes, List<FisPointsListInfo> Lists)> ValidateAsync(
        SeriesDbContext db, IEnumerable<(Guid CompetitorId, CompetitorFisPoints? Points)> changes, IEnumerable<FisPointsListInfo>? lists, CancellationToken ct)
    {
        var checkedChanges = changes.Select(x => (x.CompetitorId, x.Points?.Validated())).ToList();
        var checkedLists = (lists ?? []).Select(x => x.Validated()).DistinctBy(x => x.Code, StringComparer.OrdinalIgnoreCase).ToList();
        var codes = checkedChanges.Where(x => x.Item2 is not null).Select(x => x.Item2!.ListCode).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (codes.Length > 0)
        {
            var saved = (await ReadAsync(db, ct)).Lists;
            var missing = codes.FirstOrDefault(code => !checkedLists.Concat(saved).Any(x => string.Equals(x.Code, code, StringComparison.OrdinalIgnoreCase)));
            if (missing is not null) { throw new DomainValidationException($"FIS points list {missing} is not known in this series; nothing was saved."); }
        }
        return (checkedChanges, checkedLists);
    }

    public static async Task WriteAsync(SeriesDbContext db, IReadOnlyList<(Guid CompetitorId, CompetitorFisPoints? Points)> changes,
        IReadOnlyList<FisPointsListInfo> lists, CancellationToken ct)
    {
        if (changes.Count == 0 && lists.Count == 0) { return; }
        foreach (var list in lists)
        {
            await db.Database.ExecuteSqlRawAsync("INSERT INTO FisPointsLists (Code, Json) VALUES ($code, $json) ON CONFLICT(Code) DO UPDATE SET Json = excluded.Json",
                [new SqliteParameter("$code", list.Code), new SqliteParameter("$json", JsonSerializer.Serialize(list))], ct);
        }
        foreach (var (competitorId, points) in changes)
        {
            var id = new SqliteParameter("$id", competitorId.ToString().ToUpperInvariant());
            if (points is null) { await db.Database.ExecuteSqlRawAsync("DELETE FROM CompetitorFisPoints WHERE CompetitorId = $id", [id], ct); }
            else
            {
                await db.Database.ExecuteSqlRawAsync("INSERT INTO CompetitorFisPoints (CompetitorId, Json) VALUES ($id, $json) ON CONFLICT(CompetitorId) DO UPDATE SET Json = excluded.Json",
                    [id, new SqliteParameter("$json", JsonSerializer.Serialize(points))], ct);
            }
        }
    }

    // Saved points belong to the athlete of the FIS code they were taken for. A competitor whose code changes without
    // new points loses them in the same transaction, so another athlete never inherits them (every save path).
    public static async Task ClearAsync(SeriesDbContext db, IReadOnlyCollection<Guid> competitorIds, CancellationToken ct)
    {
        if (competitorIds.Count == 0) { return; }
        foreach (var competitorId in competitorIds)
        {
            await db.Database.ExecuteSqlRawAsync("DELETE FROM CompetitorFisPoints WHERE CompetitorId = $id",
                [new SqliteParameter("$id", competitorId.ToString().ToUpperInvariant())], ct);
        }
    }
}
