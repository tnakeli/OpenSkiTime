using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using OpenSkiTime.Application;
using OpenSkiTime.Domain;

namespace OpenSkiTime.Persistence;

/// <summary>
/// Reads a Vola Ski-Alp-Pro event read-only. EventNNN.scdb holds the event and its competitors; start lists, results and
/// impulses are in EventNNNEx.scdb beside it, or in the same file when the event is not saved as separate files. The files
/// are never written. The format is described in docs/ski-alp-pro-import.md.
/// </summary>
public static class SkiAlpProFileReader
{
    private const long MaxFileBytes = 64 * 1024 * 1024;
    private const int MaxRows = 20_000;
    private const int MaxRuns = 2;
    private const long NoPoints = 100_000;
    private static readonly Encoding s_windows = CodePagesEncodingProvider.Instance.GetEncoding(1252)!;
    private static readonly UTF8Encoding s_utf8 = new(false, true);

    public static async Task<SkiAlpProRace> ReadAsync(string path, CancellationToken ct = default)
    {
        try { return await ReadFilesAsync(Path.GetFullPath(path), ct); }
        catch (SqliteException ex) when (ex.SqliteErrorCode is 5 or 6)
        { throw new SeriesFileException("The Ski-Alp-Pro file is in use. Close the event in Ski-Alp-Pro and try again.", ex); }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or FormatException or OverflowException)
        { throw new SeriesFileException("The Ski-Alp-Pro file could not be read. Choose an undamaged EventNNN.scdb file.", ex); }
    }

    private static async Task<SkiAlpProRace> ReadFilesAsync(string path, CancellationToken ct)
    {
        var (main, times) = Files(path);
        await using var mainFile = await Source.OpenAsync(main, ct);
        await using var timesFile = times is null ? null : await Source.OpenAsync(times, ct);
        if (!mainFile.Has("TPARAMETERS") || !mainFile.Has("TCOMPETITORS"))
        { throw new SeriesFileException("This is not a Ski-Alp-Pro event. Choose an EventNNN.scdb file."); }
        var parameters = (await mainFile.RowsAsync("TPARAMETERS", ["C_PARAM", "C_VALUE"], ct))
            .GroupBy(x => x["C_PARAM"]).ToDictionary(x => x.Key, x => x.Last()["C_VALUE"], StringComparer.Ordinal);
        string Text(string name) => parameters.GetValueOrDefault(name)?.Trim() ?? "";
        int Int(string name) => int.TryParse(Text(name), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value) ? value : 0;
        // The original spelling of the parameter name.
        var software = Text("SOTWARE");
        if (!software.Replace(" ", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal).Contains("SKIALP", StringComparison.OrdinalIgnoreCase))
        { throw new SeriesFileException("This file was not saved by Ski-Alp-Pro. Choose an EventNNN.scdb file of Ski-Alp-Pro."); }

        var notices = new List<string>();
        if (times is null && Text("SEPARATE_FILES") == "1")
        { notices.Add($"{Path.GetFileNameWithoutExtension(main)}Ex.scdb is not beside the file: it holds the start lists and times. Copy both files to the same folder to import them."); }
        var dateNumber = Int("DATE");
        if (dateNumber is < 1 or > 100_000) { throw new SeriesFileException("The Ski-Alp-Pro event has no valid date."); }
        var runCount = Math.Clamp(Int("HEAT_NUMBER"), 1, 99);
        if (runCount > MaxRuns) { notices.Add($"The event has {runCount} runs; Run 1 and Run 2 can be imported."); }
        var intermediates = Math.Clamp(Int("INTER_NUMBER"), 0, 9);
        if (Int("CODEX_COUNT") > 1) { notices.Add("The event has several codices; the first one is used for the whole file."); }

        var competitors = await CompetitorsAsync(mainFile, ct);
        var runs = new List<SkiAlpProRun>();
        for (var number = 1; number <= Math.Min(runCount, MaxRuns); number++)
        { runs.Add(await RunAsync(number, intermediates, timesFile, mainFile, ct)); }
        if (runs[0].StartBibs.Count == 0 && competitors.Any(x => x.Bib > 0))
        {
            // Without a saved start list the bibs are all that is known about the order.
            runs[0] = runs[0] with { StartBibs = competitors.Where(x => x.Bib > 0).Select(x => x.Bib).Distinct().Order().ToArray() };
            notices.Add("The file has no Run 1 start list: bib order is used as the start order.");
        }

        var codex = Text("CODEX");
        var parsed = SkiAlpProCodex.Parse(codex);
        static string Nation(string value) => value.Length == 3 && value.All(char.IsAsciiLetter) ? value.ToUpperInvariant() : "";
        static string Limit(string value, int length) => value.Length > length ? value[..length].Trim() : value;
        int? Metres(string name) => Int(name) is > 0 and <= 9000 and var value ? value : null;
        var surname = Text("HEADER_FIS_DT");
        var tdNumber = Text("HEADER_FIS_DTC") is { Length: > 0 and <= 20 } number1 && number1.All(char.IsAsciiDigit) ? number1 : parsed?.TdNumber ?? "";
        var td = surname.Length == 0 ? null : new CompetitionTechnicalDelegateInfo(surname, Text("HEADER_FIS_DT_FIRSTNAME"), Nation(Text("HEADER_FIS_DT_NATION")), tdNumber);
        // "Level 3 : FIS" names the category after the colon.
        var category = Text("FIS_CATEGORY");
        category = Limit(category[(category.LastIndexOf(':') + 1)..].Trim(), 32);
        var course = new SkiAlpProCourse(Limit(Text("HEADER_FIS_COURSE_1"), 160), Metres("HEADER_FIS_START_1"), Metres("HEADER_FIS_FINISH_1"),
            Metres("HEADER_FIS_DROP_1"), Metres("HEADER_FIS_LENGTH_1"), Limit(Text("HEADER_FIS_HOMOLOGATION_1"), 50));
        return new(Path.GetFileName(main), mainFile.Hash, times is null ? null : Path.GetFileName(times), timesFile?.Hash, Text("SOFTWARE_VERSION"),
            Limit(codex, 50), Limit(Text("TITLE1"), 160), Limit(Text("TITLE2"), 160), DateOnly.FromDateTime(DateTime.FromOADate(dateNumber)),
            Discipline(Text("DISCIPLINE")), Limit(Text("LOCATION"), 160), parsed?.Nation ?? Nation(Text("ORGA_NATION")), category, runCount, intermediates,
            Math.Clamp(Int("TIME_ACCURACY"), 2, 6), Text("IMPORT_LIST_NUMBER") is "0" ? "" : Text("IMPORT_LIST_NUMBER"), course, td, competitors, runs, notices);
    }

    // The event file and its times file. Choosing the times file of an event opens the event.
    private static (string Main, string? Times) Files(string path)
    {
        var folder = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        if (name.Length > 2 && name.EndsWith("Ex", StringComparison.OrdinalIgnoreCase))
        {
            var main = Path.Combine(folder, name[..^2] + extension);
            if (File.Exists(main)) { return (main, path); }
            throw new SeriesFileException($"{Path.GetFileName(path)} holds only start lists and times. Copy {Path.GetFileName(main)} to the same folder and choose it.");
        }
        if (!File.Exists(path)) { throw new SeriesFileException("The Ski-Alp-Pro file does not exist."); }
        var times = Path.Combine(folder, name + "Ex" + extension);
        return (path, File.Exists(times) ? times : null);
    }

    // The discipline numbers follow the order of the points columns: DH, SG, GS, SL, combined.
    private static Discipline Discipline(string value) => value switch
    {
        "0" => Domain.Discipline.Downhill, "1" => Domain.Discipline.SuperG, "2" => Domain.Discipline.GiantSlalom,
        "3" => Domain.Discipline.Slalom, "4" => Domain.Discipline.AlpineCombined, _ => Domain.Discipline.Other
    };

    private static async Task<SkiAlpProCompetitor[]> CompetitorsAsync(Source file, CancellationToken ct)
    {
        var rows = await file.RowsAsync("TCOMPETITORS", ["C_NUM", "C_LAST_NAME", "C_FIRST_NAME", "C_CODE", "C_SEX", "C_YEAR", "C_NATION", "C_CLUB",
            "C_I22", "C_I29", "C_I30", "C_I31", "C_I32", "C_I33"], ct);
        static string Clean(string value) => value.Trim();
        return rows.Select(x =>
        {
            decimal? Points(string column) => Number(x[column]) is >= 0 and < NoPoints and var value ? value / 100m : null;
            var listed = Clean(x["C_I22"]) is { Length: > 0 } text && text != "0" ? text : "";
            return new SkiAlpProCompetitor((int)Math.Clamp(Number(x["C_NUM"]), 0, int.MaxValue), Clean(x["C_LAST_NAME"]), Clean(x["C_FIRST_NAME"]),
                Clean(x["C_CODE"]), Number(x["C_YEAR"]) is >= 1850 and <= 2999 and var year ? (int)year : null, Clean(x["C_NATION"]), Clean(x["C_CLUB"]),
                Clean(x["C_SEX"]) switch { "1" => Gender.Male, "2" => Gender.Female, _ => null },
                listed, Points("C_I29"), Points("C_I30"), Points("C_I31"), Points("C_I32"), Points("C_I33"));
        }).ToArray();
    }

    private static async Task<SkiAlpProRun> RunAsync(int number, int intermediates, Source? times, Source main, CancellationToken ct)
    {
        var heat = "HEAT" + number.ToString(CultureInfo.InvariantCulture);
        async Task<List<Dictionary<string, string>>> Rows(string table, string[] columns)
        {
            var source = times?.Has(table) == true ? times : main.Has(table) ? main : null;
            return source is null ? [] : await source.RowsAsync(table, columns, ct);
        }
        var starts = (await Rows("TSTARTLIST_" + heat, ["C_LINE", "C_NUM"])).Select(x => (Line: Number(x["C_LINE"]), Bib: (int)Math.Clamp(Number(x["C_NUM"]), 0, 99_999)))
            .Where(x => x.Bib > 0).OrderBy(x => x.Line).Select(x => x.Bib).Distinct().ToArray();
        var results = (await Rows("TTIMEINFOS_" + heat, ["C_NUM", "C_STATUS", "C_TIME", "C_PENALTY", "C_DATA1", "C_REASON"]))
            .Select(x => new SkiAlpProResult(Bib(x), (int)Math.Clamp(Number(x["C_STATUS"]), int.MinValue, int.MaxValue), Number(x["C_TIME"]), Number(x["C_PENALTY"]),
                (int)Math.Clamp(Number(x["C_DATA1"]), 0, int.MaxValue), x["C_REASON"])).ToArray();
        var records = new List<SkiAlpProRecord>();
        var positions = new List<(int Position, string Name)> { (0, "START"), (1, "FINISH") };
        for (var i = 1; i <= intermediates; i++) { positions.Add((i + 1, "INTER" + i.ToString(CultureInfo.InvariantCulture))); }
        foreach (var (position, name) in positions)
        {
            records.AddRange((await Rows($"TTIMERECORDS_{heat}_{name}", ["C_LINE", "C_NUM", "C_STATUS", "C_HOUR1", "C_HOUR2"]))
                .Select(x => new SkiAlpProRecord(position, (int)Math.Clamp(Number(x["C_LINE"]), 0, int.MaxValue), Bib(x), Number(x["C_STATUS"]), x["C_HOUR1"], x["C_HOUR2"])));
        }
        return new(number, starts, results, records);
        static int Bib(Dictionary<string, string> row) => (int)Math.Clamp(Number(row["C_NUM"]), 0, int.MaxValue);
    }

    private static long Number(string value)
        => long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number) ? number : 0;

    // Names are stored in the Windows code page; files that hold valid UTF-8 are read as such.
    private static string Decode(byte[] bytes)
    {
        try { return s_utf8.GetString(bytes); }
        catch (DecoderFallbackException) { return s_windows.GetString(bytes); }
    }

    // One database file, read in a single read transaction so its hash and rows belong to the same content.
    private sealed class Source : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private SqliteTransaction? _transaction;
        private readonly HashSet<string> _tables = new(StringComparer.Ordinal);
        public string Hash { get; private set; } = "";

        private Source(SqliteConnection connection) => _connection = connection;

        public static async Task<Source> OpenAsync(string path, CancellationToken ct)
        {
            if (new FileInfo(path).Length > MaxFileBytes) { throw new SeriesFileException("The Ski-Alp-Pro file is larger than 64 MB."); }
            var source = new Source(new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false, DefaultTimeout = 5 }.ToString()));
            try
            {
                await source._connection.OpenAsync(ct);
                foreach (var pragma in new[] { "PRAGMA trusted_schema=OFF", "PRAGMA query_only=ON" })
                {
                    await using var command = source._connection.CreateCommand();
                    command.CommandText = pragma;
                    await command.ExecuteNonQueryAsync(ct);
                }
                source._transaction = source._connection.BeginTransaction(deferred: true);
                await using (var command = source.Command("SELECT name FROM sqlite_master WHERE type = 'table'"))
                await using (var reader = await command.ExecuteReaderAsync(ct))
                { while (await reader.ReadAsync(ct)) { source._tables.Add(reader.GetString(0)); } }
                await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                source.Hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct));
                return source;
            }
            catch { await source.DisposeAsync(); throw; }
        }

        public bool Has(string table) => _tables.Contains(table);

        // Only known names reach the SQL text. A column the file does not have reads as empty.
        public async Task<List<Dictionary<string, string>>> RowsAsync(string table, string[] columns, CancellationToken ct)
        {
            if (!Has(table)) { throw new SeriesFileException("This is not a supported Ski-Alp-Pro event."); }
            var available = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            await using (var info = Command($"PRAGMA table_info(\"{table}\")"))
            await using (var reader = await info.ExecuteReaderAsync(ct))
            { while (await reader.ReadAsync(ct)) { available.Add(reader.GetString(1)); } }
            var fields = columns.Select(x => available.Contains(x) ? $"CAST(\"{x}\" AS BLOB)" : "NULL");
            await using var command = Command($"SELECT {string.Join(",", fields)} FROM \"{table}\" ORDER BY rowid LIMIT {(MaxRows + 1).ToString(CultureInfo.InvariantCulture)}");
            await using var data = await command.ExecuteReaderAsync(ct);
            var rows = new List<Dictionary<string, string>>();
            while (await data.ReadAsync(ct))
            {
                if (rows.Count == MaxRows) { throw new SeriesFileException("The Ski-Alp-Pro file holds more rows than an event can have."); }
                var row = new Dictionary<string, string>(StringComparer.Ordinal);
                for (var i = 0; i < columns.Length; i++) { row[columns[i]] = data.IsDBNull(i) ? "" : Decode((byte[])data.GetValue(i)); }
                rows.Add(row);
            }
            return rows;
        }

        private SqliteCommand Command(string sql)
        {
            var command = _connection.CreateCommand();
            command.Transaction = _transaction;
            command.CommandText = sql;
            return command;
        }

        public async ValueTask DisposeAsync()
        {
            if (_transaction is not null) { await _transaction.DisposeAsync(); }
            await _connection.DisposeAsync();
        }
    }
}
