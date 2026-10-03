using System.Text.Json;
using Microsoft.Data.Sqlite;
using OpenSkiTime.Application;

namespace OpenSkiTime.Persistence;

internal sealed partial class SqliteSeriesFileSession : IPdfFactoryStore
{
    // Optional extension, read without DDL. Older supported files are unchanged on open.
    public async Task<PdfFactorySettings> ReadPdfFactoryAsync(CancellationToken ct = default)
    {
        CheckOpen();
        await using var connection = new SqliteConnection(SqliteSeriesFileStore.ConnectionString(FilePath, SqliteOpenMode.ReadOnly));
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='PdfFactory'";
        if ((long)(await command.ExecuteScalarAsync(ct))! == 0) { return new(new(), []); }
        command.CommandText = "SELECT Id, Json FROM PdfFactory ORDER BY Id";
        await using var reader = await command.ExecuteReaderAsync(ct);
        EventPrintProfile profile = new();
        var reports = new List<GeneratedPdf>();
        while (await reader.ReadAsync(ct))
        {
            if (reader.GetString(0) == "profile") { profile = JsonSerializer.Deserialize<EventPrintProfile>(reader.GetString(1)) ?? new(); }
            else { reports.Add(JsonSerializer.Deserialize<GeneratedPdf>(reader.GetString(1)) ?? throw new SeriesFileException("Invalid PDF generation metadata.")); }
        }
        return new(profile, reports.ToArray());
    }
    public Task SavePrintProfileAsync(EventPrintProfile profile, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        profile.Validate();
        return SavePdfSettingAsync("profile", JsonSerializer.Serialize(profile), ct);
    }
    public Task SaveGeneratedPdfAsync(GeneratedPdf report, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        return SavePdfSettingAsync("report/" + report.ReportId, JsonSerializer.Serialize(report), ct);
    }
    private async Task SavePdfSettingAsync(string id, string json, CancellationToken ct)
    {
        CheckOpen();
        await using var connection = new SqliteConnection(SqliteSeriesFileStore.ConnectionString(FilePath, SqliteOpenMode.ReadWrite));
        await connection.OpenAsync(ct);
        await using (var durability = connection.CreateCommand())
        {
            durability.CommandText = "PRAGMA synchronous=FULL";
            await durability.ExecuteNonQueryAsync(ct);
        }
        await using var transaction = connection.BeginTransaction();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        // Explicit profile/report save opts into the optional extension; never upgrades race schema.
        command.CommandText = "CREATE TABLE IF NOT EXISTS PdfFactory (Id TEXT PRIMARY KEY NOT NULL, Json TEXT NOT NULL)";
        await command.ExecuteNonQueryAsync(ct);
        command.CommandText = "INSERT INTO PdfFactory (Id, Json) VALUES ($id, $json) ON CONFLICT(Id) DO UPDATE SET Json=excluded.Json";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$json", json);
        await command.ExecuteNonQueryAsync(ct);
        await transaction.CommitAsync(ct);
    }
}
