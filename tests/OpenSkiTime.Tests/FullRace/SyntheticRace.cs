using System.Globalization;
using System.IO.Compression;
using System.Text;
using OpenSkiTime.Domain;

namespace OpenSkiTime.Tests.FullRace;

// Deterministic, completely fictional 100-athlete women's slalom used by the full-race tests, the real-window desktop
// automation and the demonstration video. Names are assembled from invented syllables; codes use the synthetic 99xxxx
// range; nothing is derived from a real FIS list. The same seed always produces the same athletes, points and times.
internal enum RunOutcome { Finished, DNS, DNF, DSQ }

internal sealed record SyntheticAthlete(int Index, string Code, string Surname, string FirstName, int BirthYear,
    string Nation, string Club, decimal? SlalomPoints)
{
    public CompetitorValues Values => new(Surname, FirstName, BirthYear, Code, Nation, Club, Gender.Female);
}

// One planned run of one athlete. Ticks are 100 ns units on the 0.0001 s grid of an ALGE device.
// Finish/intermediate offsets are relative to the start impulse. Missing impulses are still part of the plan so
// the expected time of a hand-timed finish can be calculated independently.
internal sealed record SyntheticRun(RunOutcome Outcome, long FinishTicks, long IntermediateTicks,
    bool MissingIntermediate = false, bool MissingFinish = false, bool DuplicateFinish = false,
    int? DsqGate = null, string? DsqReason = null)
{
    // Alpine net time: subtraction first, then truncation to hundredths.
    public long Hundredths => FinishTicks / TimeSpan.TicksPerMillisecond / 10;
    public long IntermediateHundredths => IntermediateTicks / TimeSpan.TicksPerMillisecond / 10;
}

internal sealed class SyntheticRace
{
    public const int DefaultSeed = 20261212;
    public const int AthleteCount = 100;
    public const string DrawSeed = "OST-FULL-RACE-E2E-20261212";
    public const string ShortLabel = "SLW";
    public const string PointsListCode = "0527";
    public static readonly DateOnly RaceDate = new(2026, 12, 12);
    // Hand-timing precision for a missing finish impulse: the hand clock reports hundredths.
    public const int HandTimingPrecision = 2;
    public static readonly long StartIntervalTicks = TimeSpan.FromSeconds(45).Ticks;
    public static readonly long Run1FirstStart = TimeSpan.Parse("10:00:00", CultureInfo.InvariantCulture).Ticks;
    public static readonly long Run2FirstStart = TimeSpan.Parse("13:00:00", CultureInfo.InvariantCulture).Ticks;

    // Indices into the points-ranked athlete list (0 = best points). Exceptions avoid the deliberate equal groups.
    public static readonly int[] Run1Dns = [57, 88];
    public static readonly int[] Run1Dnf = [9, 46, 77];
    public const int Run1Dsq = 33;
    public const int Run1MissingFinish = 21;
    public const int Run1MissingIntermediate = 64;
    public const int Run1DuplicateFinish = 38;
    public const int Run1UndoneMistake = 50;
    public static readonly int[] Run2Dnf = [5, 60];
    public const int Run2Dsq = 80;
    public static readonly int[][] EqualPointsGroups = [[13, 14, 15], [39, 40], [69, 70, 71]];
    public const int PointsAthletes = 92;

    public SeriesValues Series { get; } = new("Synthetic Alpine Cup 2026", "Synthetic Fell", "Synthetic Ski Club",
        RaceDate, RaceDate, "FIN", "2026/27");

    public CompetitionValues Competition { get; } = new("Synthetic Women's Slalom", ShortLabel, RaceDate, Discipline.Slalom,
        RaceType.Fis, 2, 1, "9123", CourseName: "Synthetic Fell Slalom", StartAltitudeMeters: 520, FinishAltitudeMeters: 380,
        VerticalDropMeters: 140, HomologationNumber: "99999/12/26");

    public IReadOnlyList<SyntheticAthlete> Athletes { get; }
    public IReadOnlyDictionary<string, SyntheticRun> Run1 { get; }
    public IReadOnlyDictionary<string, SyntheticRun> Run2 { get; }
    // Code pairs deliberately tied after truncation (different raw ticks, equal hundredths).
    public IReadOnlyList<(string A, string B)> Run1Ties { get; }
    public IReadOnlyList<(string A, string B)> CombinedTies { get; }

    public SyntheticRace(int seed = DefaultSeed)
    {
        var random = new SplitMix(seed);
        Athletes = CreateAthletes(random);
        var run1 = new Dictionary<string, SyntheticRun>();
        foreach (var athlete in Athletes) { run1[athlete.Code] = PlanRun(random, athlete, 1); }
        foreach (var index in Run1Dns) { run1[Athletes[index].Code] = new(RunOutcome.DNS, 0, 0); }
        foreach (var index in Run1Dnf)
        {
            var planned = run1[Athletes[index].Code];
            // The fallen favourite (index 9) never reaches the intermediate; the others do.
            run1[Athletes[index].Code] = planned with { Outcome = RunOutcome.DNF, MissingIntermediate = index == 9 };
        }
        run1[Athletes[Run1Dsq].Code] = run1[Athletes[Run1Dsq].Code] with { Outcome = RunOutcome.DSQ, DsqGate = 27, DsqReason = "Straddled gate 27" };
        run1[Athletes[Run1MissingFinish].Code] = run1[Athletes[Run1MissingFinish].Code] with { MissingFinish = true };
        run1[Athletes[Run1MissingIntermediate].Code] = run1[Athletes[Run1MissingIntermediate].Code] with { MissingIntermediate = true };
        run1[Athletes[Run1DuplicateFinish].Code] = run1[Athletes[Run1DuplicateFinish].Code] with { DuplicateFinish = true };
        Run1Ties = [MakeTie(run1, 11), MakeTie(run1, 29)]; // ranks 12/13 and 30/31 (the reversal boundary)
        Run1 = run1;

        var run2 = new Dictionary<string, SyntheticRun>();
        foreach (var athlete in Athletes.Where(x => run1[x.Code].Outcome == RunOutcome.Finished))
        { run2[athlete.Code] = PlanRun(random, athlete, 2); }
        foreach (var index in Run2Dnf) { run2[Athletes[index].Code] = run2[Athletes[index].Code] with { Outcome = RunOutcome.DNF }; }
        run2[Athletes[Run2Dsq].Code] = run2[Athletes[Run2Dsq].Code] with { Outcome = RunOutcome.DSQ, DsqGate = 41, DsqReason = "Missed gate 41" };
        CombinedTies = [MakeCombinedTie(run1, run2, 2), MakeCombinedTie(run1, run2, 14)]; // final ranks 3/4 and 15/16
        Run2 = run2;
    }

    public SyntheticAthlete Athlete(string code) => Athletes.Single(x => x.Code == code);

    // Start time of day for a 1-based start position: a 45 s interval plus a deterministic 0–2.9999 s gate delay.
    public static long StartTimeOfDay(int run, int position)
        => (run == 1 ? Run1FirstStart : Run2FirstStart) + (position - 1) * StartIntervalTicks
            + position * 7_919L % 30_000 * 1_000;

    // Hand-timed finish as reported for the missing impulse: the true finish truncated to the hand clock's hundredths.
    public static long HandTimedFinish(long startTimeOfDay, SyntheticRun run)
    {
        var finish = startTimeOfDay + run.FinishTicks;
        return finish - finish % (TimeSpan.TicksPerMillisecond * 10);
    }

    // Expected net time of a run, independent of the application. A hand-timed finish truncates the finish time of day
    // to hundredths before subtraction, exactly as the operator types it.
    public static long ExpectedHundredths(long startTimeOfDay, SyntheticRun run)
    {
        var finish = run.MissingFinish ? HandTimedFinish(startTimeOfDay, run) : startTimeOfDay + run.FinishTicks;
        return (finish - startTimeOfDay) / (TimeSpan.TicksPerMillisecond * 10);
    }

    public string CompetitorTsv()
    {
        var builder = new StringBuilder("Code\tSurname\tFirst name\tYear\tGender\tNation\tClub\t" + ShortLabel + "\n");
        foreach (var a in Athletes)
        {
            builder.Append(CultureInfo.InvariantCulture,
                $"{a.Code}\t{a.Surname}\t{a.FirstName}\t{a.BirthYear}\tWomen\t{a.Nation}\t{a.Club}\tX\n");
        }
        return builder.ToString();
    }

    // A synthetic FIS points-list ZIP in the documented download layout, including penalty rule tables.
    public byte[] PointsListArchive()
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            var prefix = "AL" + PointsListCode;
            Add(zip, prefix + "hdr.csv", "Listid\tSeasoncode\tListnumber\tListname\tCalculationdate\tStartracedate\tEndracedate\tValidfrom\tValidto\tLastupdate\n"
                + "905\t2027\t5\t5th synthetic FIS points list 2026/27\t2026-12-01\t2026-07-01\t2026-11-30\t2026-12-03\t2026-12-16\t2026-12-01 04:00:00\n");
            var com = new StringBuilder("Competitorid\tSectorcode\tFiscode\tLastname\tFirstname\tGender\tBirthdate\tNationcode\tNationalcode\tSkiclub\tAssociation\tStatus\n");
            var pts = new StringBuilder("Recid\tListid\tCompetitorid\tDisciplinecode\tFispoints\tPosition\tPenalty\tLastupdate\n");
            foreach (var a in Athletes)
            {
                com.Append(CultureInfo.InvariantCulture,
                    $"{a.Index + 1}\tAL\t{a.Code}\t{a.Surname}\t{a.FirstName}\tW\t{a.BirthYear}-03-01\t{a.Nation}\t\t{a.Club}\t\tA\n");
                if (a.SlalomPoints is { } points)
                {
                    pts.Append(CultureInfo.InvariantCulture,
                        $"{a.Index + 1}\t905\t{a.Index + 1}\tSL\t{points:0.00}\t{a.Index + 1}\t\t2026-12-01\n");
                }
            }
            Add(zip, prefix + "com.csv", com.ToString());
            Add(zip, prefix + "pts.csv", pts.ToString());
            Add(zip, prefix + "cat.csv", "Recid\tListid\tSeasoncode\tCatcode\tMinfispoints\tMaxfispoints\tLastupdate\n"
                + "1\t905\t2027\tFIS\t23.00\t999.99\t2026-12-01\n");
            Add(zip, prefix + "dis.csv", "Recid\tListid\tSeasoncode\tDisciplinecode\tGender\tZvalue\tFvalue\tMaxpoints\tAdder0\tAdder1\tAdder2\tAdder3\tAdder4\n"
                + "1\t905\t2027\tSL\tW\t0.00\t730\t165\t0\t0\t0\t0\t0\n");
            Add(zip, "Fiscategory.txt", "Recid\tSectorcode\tCatcode\tDescription\tDisplayorder\tInuse\tPublished\tRacelevel\tCalautoload\tLastupdate\n"
                + "1\tAL\tFIS\tFIS race\t1\t1\t1\t3\t1\t2026-12-01\n");
        }
        return output.ToArray();
    }

    private static void Add(ZipArchive zip, string name, string contents)
    {
        using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
        writer.Write(contents);
    }

    private static readonly string[] s_first = ["Aino", "Briita", "Cilla", "Dagny", "Eevi", "Freja", "Greta", "Hilla", "Ines", "Jonna",
        "Kaisu", "Liisa", "Maija", "Noora", "Oona", "Pihla", "Riina", "Saga", "Tilda", "Ulla", "Venla", "Wilma", "Ylva", "Zelda"];
    private static readonly string[] s_head = ["KAR", "VAL", "SOR", "MER", "TUN", "LUM", "HAV", "RIN", "SAL", "PEL", "KOR", "NIV", "ORA", "JAL", "TAV", "VIR"];
    private static readonly string[] s_tail = ["ANDER", "OVIK", "ELLI", "ASTO", "UNEN", "ERMA", "ITTI", "OLAN", "AVIK", "ESKI", "ULLA", "ONEN"];
    private static readonly string[] s_nations = ["FIN", "SWE", "NOR", "AUT", "SUI", "ITA", "FRA", "GER", "SLO", "CZE", "USA", "CAN"];
    private static readonly string[] s_clubs = ["Aurora Alpine", "Boreal SC", "Cirrus Ski", "Driftwood Racing", "Ember Peak", "Frost Valley",
        "Glacier Line", "Highland Edge", "Icefall Club", "Juniper Slopes"];

    private static List<SyntheticAthlete> CreateAthletes(SplitMix random)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        var points = new decimal?[AthleteCount];
        var value = 6.20m;
        for (var i = 0; i < PointsAthletes; i++)
        {
            value += 0.80m + random.Next(0, 120) / 100m;
            points[i] = value;
        }
        foreach (var group in EqualPointsGroups)
        { foreach (var index in group.Skip(1)) { points[index] = points[group[0]]; } }
        var athletes = new List<SyntheticAthlete>();
        for (var i = 0; i < AthleteCount; i++)
        {
            string surname, first;
            do
            {
                surname = s_head[random.Next(0, s_head.Length)] + s_tail[random.Next(0, s_tail.Length)];
                first = s_first[random.Next(0, s_first.Length)];
            } while (!names.Add(surname + "|" + first));
            athletes.Add(new(i, (990101 + i).ToString(CultureInfo.InvariantCulture), surname, first,
                1998 + random.Next(0, 11), s_nations[random.Next(0, s_nations.Length)],
                s_clubs[random.Next(0, s_clubs.Length)], points[i]));
        }
        return athletes;
    }

    private static SyntheticRun PlanRun(SplitMix random, SyntheticAthlete athlete, int run)
    {
        // Realistic slalom: 47–60 s, stronger points ski faster; Run 2 is a little slower on a rutted course.
        var basis = 47.20m + (athlete.SlalomPoints ?? 130m) * 0.055m + (run == 2 ? 0.75m : 0m);
        var seconds = basis + (random.Next(0, 2401) - 1200) / 1000m;
        var finish = (long)(seconds * 10_000m) * 1_000; // ALGE 0.0001 s grid
        var fraction = 0.43m + random.Next(0, 41) / 1000m;
        var intermediate = (long)(seconds * fraction * 10_000m) * 1_000;
        return new(RunOutcome.Finished, finish, intermediate);
    }

    // Gives the finisher at the given 0-based rank the same hundredths as the one above it, keeping raw ticks different.
    private (string, string) MakeTie(Dictionary<string, SyntheticRun> runs, int rank)
    {
        // Every classified finisher counts towards the rank, including the hand-timed one; it is never one of the pair.
        var ordered = Athletes.Where(x => runs[x.Code].Outcome == RunOutcome.Finished)
            .OrderBy(x => runs[x.Code].FinishTicks).ThenBy(x => x.Code, StringComparer.Ordinal).ToArray();
        var above = ordered[rank]; var below = ordered[rank + 1];
        if (runs[above.Code].MissingFinish || runs[below.Code].MissingFinish)
        { throw new InvalidOperationException("Choose another tie rank; the hand-timed finisher cannot be part of it."); }
        var target = runs[above.Code].Hundredths * TimeSpan.TicksPerMillisecond * 10;
        // above keeps .xx01; below gets .xx08 of the same hundredth.
        runs[above.Code] = runs[above.Code] with { FinishTicks = target + 1_000 };
        runs[below.Code] = runs[below.Code] with { FinishTicks = target + 8_000 };
        return (above.Code, below.Code);
    }

    private (string, string) MakeCombinedTie(Dictionary<string, SyntheticRun> run1, Dictionary<string, SyntheticRun> run2, int rank)
    {
        long Total(string code) => run1[code].Hundredths + run2[code].Hundredths;
        var ordered = run2.Where(x => x.Value.Outcome == RunOutcome.Finished && !run1[x.Key].MissingFinish).Select(x => x.Key)
            .OrderBy(Total).ThenBy(x => x, StringComparer.Ordinal).ToArray();
        var above = ordered[rank]; var below = ordered[rank + 1];
        var wanted = Total(above) - run1[below].Hundredths;
        run2[below] = run2[below] with { FinishTicks = wanted * TimeSpan.TicksPerMillisecond * 10 + 6_000 };
        return (above, below);
    }

    // SplitMix64: small, fully specified and stable across runtimes (System.Random's seeded sequence is not a contract).
    private sealed class SplitMix(int seed)
    {
        private ulong _state = (ulong)seed;
        public int Next(int min, int max)
        {
            _state += 0x9E3779B97F4A7C15UL;
            var z = _state;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            z ^= z >> 31;
            return min + (int)(z % (ulong)(max - min));
        }
    }
}
