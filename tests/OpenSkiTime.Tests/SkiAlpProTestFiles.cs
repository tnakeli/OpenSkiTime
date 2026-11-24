using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;

namespace OpenSkiTime.Tests;

// Synthetic Ski-Alp-Pro events: the tables and columns the reader uses, with invented competitors.
internal sealed record SkiAlpProTestAthlete(int Bib, string Surname, string FirstName, string Code, int Sex = 1, int Year = 2005, int Points = 5000);

internal sealed class SkiAlpProTestEvent
{
    public Dictionary<string, string> Parameters { get; } = new(StringComparer.Ordinal)
    {
        ["SOTWARE"] = "SKIALP PRO", ["SOFTWARE_VERSION"] = "14.1.02", ["CODEX"] = "FIN0592.718", ["CODEX_COUNT"] = "1", ["DATE"] = "46102",
        ["DISCIPLINE"] = "3", ["HEAT_NUMBER"] = "2", ["INTER_NUMBER"] = "1", ["TIME_ACCURACY"] = "4", ["IMPORT_LIST_NUMBER"] = "1926",
        ["SEPARATE_FILES"] = "1", ["TITLE1"] = "Synthetic Race - Men", ["TITLE2"] = "Slalom", ["LOCATION"] = "Test slope", ["FIS_CATEGORY"] = "Level 3 : FIS",
        ["HEADER_FIS_DT"] = "Delegate", ["HEADER_FIS_DT_FIRSTNAME"] = "Test", ["HEADER_FIS_DT_NATION"] = "DEN", ["HEADER_FIS_DTC"] = "718",
        ["HEADER_FIS_COURSE_1"] = "Slope 4", ["HEADER_FIS_START_1"] = "498", ["HEADER_FIS_FINISH_1"] = "317", ["HEADER_FIS_DROP_1"] = "181",
        ["HEADER_FIS_LENGTH_1"] = "640", ["HEADER_FIS_HOMOLOGATION_1"] = "14679/11/22",
    };
    public List<SkiAlpProTestAthlete> Athletes { get; } = [];
    public Dictionary<int, List<int>> Starts { get; } = [];
    public Dictionary<int, List<(int Bib, int Status, long Time, int Gate)>> Results { get; } = [];
    public Dictionary<int, List<(string Position, int Bib, long Status, long Microseconds)>> Records { get; } = [];

    /// <summary>Six men, start order 3,1,2,4,5,6: four finish Run 1 (bib 5 DNF, 6 DNS); Run 2: bib 4 DSQ at gate 12.</summary>
    public static SkiAlpProTestEvent TwoRuns()
    {
        var e = new SkiAlpProTestEvent();
        for (var i = 1; i <= 6; i++) { e.Athletes.Add(new(i, i == 2 ? "JÄRVI" : "TESTI" + (char)('A' + i), "First" + (char)('A' + i), "18000" + i, Points: 4000 + i * 100)); }
        e.Starts[1] = [3, 1, 2, 4, 5, 6];
        e.AddRun(1, 36_000_000_000, [(3, 50_238_700), (1, 49_405_100), (2, 48_802_300), (4, 48_034_600), (5, -2), (6, -1)]);
        e.Starts[2] = [3, 1, 2, 4];
        e.AddRun(2, 45_000_000_000, [(3, 48_987_000), (1, 48_404_700), (2, 48_236_800), (4, -3)]);
        return e;
    }

    // A positive value is the net time in microseconds, a negative one the status (1 DNS, 2 DNF, 3 DSQ).
    public void AddRun(int run, long firstStart, (int Bib, long Value)[] rows)
    {
        var results = Results[run] = [];
        var records = Records[run] = [];
        records.Add(("START", 901, 0, firstStart - 60_000_000)); // a forerunner
        records.Add(("FINISH", 901, 0, firstStart - 10_000_000));
        for (var i = 0; i < rows.Length; i++)
        {
            var (bib, value) = rows[i];
            var start = firstStart + i * 90_000_000L;
            results.Add((bib, value < 0 ? (int)-value : 0, value < 0 ? 0 : value / 1000, value == -3 ? 12 : 0));
            if (value == -1) { records.Add(("START", bib, 131328, start)); continue; }
            if (i == 1) { records.Add(("START", bib, 1, start - 30_000_000)); } // a false start Ski-Alp-Pro does not count
            records.Add(("START", bib, 0, start));
            if (value < 0) { records.Add(("FINISH", bib, value == -3 ? 131840 : 131584, start + 40_000_000)); continue; }
            records.Add(("INTER1", bib, 0, start + 30_000_010));
            records.Add(("FINISH", bib, 0, start + value));
        }
        records.Add(("FINISH", 0, 0, firstStart + 3_600_000_000)); // an impulse nobody was given
    }

    public string Write(string folder, string name = "Event901", bool times = true)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name + ".scdb");
        using (var db = Open(path))
        {
            Run(db, "CREATE TABLE TPARAMETERS (C_PARAM CHAR(32) NOT NULL, C_VALUE VARCHAR(510), PRIMARY KEY(C_PARAM))");
            foreach (var (key, value) in Parameters) { Run(db, "INSERT INTO TPARAMETERS VALUES ($a, $b)", ("$a", key), ("$b", value)); }
            Run(db, "CREATE TABLE TCOMPETITORS (C_IDX INTEGER NOT NULL, C_NUM INTEGER, C_LAST_NAME VARCHAR(60), C_FIRST_NAME VARCHAR(60), C_CODE VARCHAR(60), "
                + "C_SEX INTEGER, C_YEAR INTEGER, C_NATION VARCHAR(10), C_CLUB VARCHAR(60), C_I22 VARCHAR(40), C_I29 INTEGER, C_I30 INTEGER, C_I31 INTEGER, C_I32 INTEGER, C_I33 INTEGER, PRIMARY KEY(C_IDX))");
            foreach (var (a, i) in Athletes.Select((a, i) => (a, i)))
            {
                // Ski-Alp-Pro stores text in the Windows code page, not UTF-8.
                Run(db, "INSERT INTO TCOMPETITORS VALUES ($i, $bib, CAST($name AS TEXT), $first, $code, $sex, $year, 'FIN', 'Test Club', $list, 100000, 100000, 100000, $points, 100000)",
                    ("$i", i + 1), ("$bib", a.Bib), ("$name", Encoding.Latin1.GetBytes(a.Surname)), ("$first", a.FirstName), ("$code", a.Code), ("$sex", a.Sex), ("$year", a.Year),
                    ("$list", Parameters.GetValueOrDefault("IMPORT_LIST_NUMBER") ?? ""), ("$points", a.Points));
            }
        }
        if (!times) { return path; }
        using (var db = Open(Path.Combine(folder, name + "Ex.scdb")))
        {
            foreach (var run in new[] { 1, 2 })
            {
                Run(db, $"CREATE TABLE TSTARTLIST_HEAT{run} (C_LINE INTEGER NOT NULL, C_NUM INTEGER, C_START INTEGER, PRIMARY KEY(C_LINE))");
                Run(db, $"CREATE TABLE TTIMEINFOS_HEAT{run} (C_NUM INTEGER NOT NULL, C_STATUS INTEGER, C_TIME INTEGER, C_PENALTY INTEGER, C_DATA1 INTEGER, C_REASON VARCHAR(256), PRIMARY KEY(C_NUM))");
                foreach (var position in new[] { "START", "FINISH", "INTER1" })
                { Run(db, $"CREATE TABLE TTIMERECORDS_HEAT{run}_{position} (C_LINE INTEGER NOT NULL, C_NUM INTEGER, C_STATUS INTEGER, C_HOUR1 VARCHAR(30), C_HOUR2 VARCHAR(30), PRIMARY KEY(C_LINE))"); }
                foreach (var (bib, i) in Starts.GetValueOrDefault(run, []).Select((x, i) => (x, i)))
                { Run(db, $"INSERT INTO TSTARTLIST_HEAT{run} VALUES ($i, $bib, 0)", ("$i", i + 1), ("$bib", bib)); }
                foreach (var r in Results.GetValueOrDefault(run, []))
                { Run(db, $"INSERT INTO TTIMEINFOS_HEAT{run} VALUES ($bib, $status, $time, 0, $gate, '')", ("$bib", r.Bib), ("$status", r.Status), ("$time", r.Time), ("$gate", r.Gate)); }
                foreach (var group in Records.GetValueOrDefault(run, []).GroupBy(x => x.Position))
                {
                    foreach (var (r, i) in group.Select((x, i) => (x, i)))
                    {
                        Run(db, $"INSERT INTO TTIMERECORDS_HEAT{run}_{group.Key} VALUES ($i, $bib, $status, '0', $time)",
                            ("$i", i + 1), ("$bib", r.Bib), ("$status", r.Status), ("$time", r.Microseconds.ToString(CultureInfo.InvariantCulture)));
                    }
                }
            }
        }
        return path;
    }

    private static SqliteConnection Open(string path)
    {
        var db = new SqliteConnection($"Data Source={path};Pooling=False");
        db.Open();
        return db;
    }

    private static void Run(SqliteConnection db, string sql, params (string Name, object Value)[] values)
    {
        using var command = db.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in values) { command.Parameters.AddWithValue(name, value); }
        command.ExecuteNonQuery();
    }
}
