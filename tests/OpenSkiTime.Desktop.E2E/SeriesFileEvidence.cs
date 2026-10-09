using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace OpenSkiTime.Desktop.E2E;

// Reads what the running application saved, read-only and outside its process: the drawn start order of a run.
// The application owns the file; a read-only SQLite connection sees committed WAL data without writing.
internal static partial class SeriesFileEvidence
{
    public static IReadOnlyList<(int Position, int Bib, string Code)> StartOrder(string file, int run)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = file, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT e.Position, e.Bib, e.EntryJson FROM StartListEntries e
            JOIN StartLists l ON l.Id = e.ListId JOIN Runs r ON r.Id = l.RunId
            WHERE r.Number = $run AND l.Revision = (SELECT MAX(l2.Revision) FROM StartLists l2 WHERE l2.RunId = l.RunId)
            ORDER BY e.Position
            """;
        command.Parameters.AddWithValue("$run", run);
        using var reader = command.ExecuteReader();
        var rows = new List<(int, int, string)>();
        while (reader.Read())
        {
            var code = CodePattern().Match(reader.GetString(2)).Groups[1].Value;
            rows.Add((reader.GetInt32(0), reader.GetInt32(1), code));
        }
        return rows;
    }

    public static string Format(long? hundredths) => hundredths is { } v
        ? v >= 6000 ? string.Create(CultureInfo.InvariantCulture, $"{v / 6000}:{v / 100 % 60:00}.{v % 100:00}")
            : string.Create(CultureInfo.InvariantCulture, $"0:{v / 100:00}.{v % 100:00}")
        : string.Empty;

    [GeneratedRegex("\"FederationCode\"\\s*:\\s*\"([^\"]+)\"")]
    private static partial Regex CodePattern();
}
