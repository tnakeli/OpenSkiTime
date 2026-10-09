using System.Globalization;
using System.Text;
using Xunit;

namespace OpenSkiTime.Tests.FullRace;

public sealed class FullRaceScenarioTests
{
    private static readonly int[] s_run1Counts = [100, 95, 2, 3, 1];
    private static readonly int[] s_run2Counts = [94, 91, 2, 1];
    private static readonly string[] s_executions = ["execution-1", "execution-2"];

    [Fact]
    public void GeneratorIsDeterministicAndContainsTheDesignedEdgeCases()
    {
        var race = new SyntheticRace();
        var again = new SyntheticRace();
        Assert.Equal(race.CompetitorTsv(), again.CompetitorTsv());
        Assert.Equal(race.PointsListArchive().Length, again.PointsListArchive().Length);
        Assert.Equal(race.Run1.OrderBy(x => x.Key).Select(x => x.Value), again.Run1.OrderBy(x => x.Key).Select(x => x.Value));
        Assert.Equal(race.Run2.OrderBy(x => x.Key).Select(x => x.Value), again.Run2.OrderBy(x => x.Key).Select(x => x.Value));
        Assert.NotEqual(race.CompetitorTsv(), new SyntheticRace(7).CompetitorTsv());
        Assert.Equal(SyntheticRace.AthleteCount, race.Athletes.Select(x => x.Code).Distinct().Count());
        Assert.Equal(SyntheticRace.AthleteCount, race.Athletes.Select(x => x.Surname + x.FirstName).Distinct().Count());
        Assert.Equal(8, race.Athletes.Count(x => x.SlalomPoints is null));
        Assert.All(race.Run1Ties, t =>
        {
            Assert.Equal(race.Run1[t.A].Hundredths, race.Run1[t.B].Hundredths);
            Assert.NotEqual(race.Run1[t.A].FinishTicks, race.Run1[t.B].FinishTicks);
        });
        Assert.All(race.CombinedTies, t => Assert.Equal(race.Run1[t.A].Hundredths + race.Run2[t.A].Hundredths,
            race.Run1[t.B].Hundredths + race.Run2[t.B].Hundredths));
        Assert.All(race.Run1.Values.Concat(race.Run2.Values).Where(x => x.Outcome != RunOutcome.DNS), x =>
        {
            Assert.InRange(x.FinishTicks, TimeSpan.FromSeconds(45).Ticks, TimeSpan.FromSeconds(62).Ticks);
            Assert.Equal(0, x.FinishTicks % 1_000); // ALGE 0.0001 s grid
        });
    }

    // The complete synthetic race. Set OPENSKITIME_FULL_RACE_REPORT to a directory to keep the evidence report and the
    // .ost files; otherwise everything is written to a temporary directory and removed.
    [Fact]
    public async Task HundredAthleteTwoRunSlalomMatchesIndependentExpectationsAndIsReproducible()
    {
        var reportDirectory = Environment.GetEnvironmentVariable("OPENSKITIME_FULL_RACE_REPORT");
        var root = reportDirectory is { Length: > 0 } ? Path.GetFullPath(reportDirectory)
            : Path.Combine(Path.GetTempPath(), "openskitime-full-race", Guid.NewGuid().ToString("N"));
        var keep = reportDirectory is { Length: > 0 };
        try
        {
            // A kept report directory is reused: only the scenario's own execution folders are replaced.
            foreach (var execution in s_executions.Select(x => Path.Combine(root, x)).Where(Directory.Exists))
            { DeleteTree(execution); }
            var first = await new FullRaceScenario(new SyntheticRace(), Path.Combine(root, "execution-1")).RunAsync();
            var second = await new FullRaceScenario(new SyntheticRace(), Path.Combine(root, "execution-2")).RunAsync();
            if (keep) { await File.WriteAllTextAsync(Path.Combine(root, "full-race-report.md"), Report(first, second)); }
            Assert.Empty(first.Discrepancies);
            Assert.Empty(second.Discrepancies);
            Assert.Equal(first.Canonical(), second.Canonical());
            Assert.Equal(100, first.Run1Order.Count);
            Assert.Equal(94, first.Run2Order.Count);
            Assert.Equal(s_run1Counts, new[] { first.Run1.Starters, first.Run1.Finished + first.Run1.Dsq, first.Run1.Dns, first.Run1.Dnf, first.Run1.Dsq });
            Assert.Equal(s_run2Counts, new[] { first.Run2.Starters, first.Run2.Finished, first.Run2.Dnf, first.Run2.Dsq });
        }
        finally
        {
            if (!keep && Directory.Exists(root)) { DeleteTree(root); }
        }
    }

    // Folders under a user's Documents can inherit the read-only attribute, which blocks Directory.Delete; clear it on
    // the test's own files and folders before removing them.
    private static void DeleteTree(string path)
    {
        var root = new DirectoryInfo(path);
        root.Attributes &= ~FileAttributes.ReadOnly;
        foreach (var item in root.EnumerateFileSystemInfos("*", SearchOption.AllDirectories))
        { item.Attributes &= ~FileAttributes.ReadOnly; }
        Directory.Delete(path, recursive: true);
    }

    private static string Report(FullRaceOutcome first, FullRaceOutcome second)
    {
        var text = new StringBuilder();
        text.AppendLine("# Synthetic 100-athlete slalom — execution report").AppendLine();
        text.AppendLine(CultureInfo.InvariantCulture, $"Generated {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss} UTC by `FullRaceScenarioTests`. Seed {SyntheticRace.DefaultSeed}, draw seed `{SyntheticRace.DrawSeed}`.").AppendLine();
        text.AppendLine(CultureInfo.InvariantCulture, $"Reproducibility: two executions in separate series files produced {(first.Canonical() == second.Canonical() ? "identical" : "DIFFERENT")} canonical outputs (start orders, all result rows, penalty, XML counts).").AppendLine();
        text.AppendLine("## Execution log").AppendLine();
        foreach (var line in first.Log) { text.AppendLine("- " + line); }
        text.AppendLine().AppendLine("## Counts").AppendLine();
        text.AppendLine("| Run | On list | Finished | DNS | DNF | DSQ | Raw packets | Observations | Audit entries | Manual timestamps | Ignored impulses |");
        text.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|");
        foreach (var (name, e) in new[] { ("1", first.Run1), ("2", first.Run2) })
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"| {name} | {e.Starters} | {e.Finished} | {e.Dns} | {e.Dnf} | {e.Dsq} | {e.RawPackets} | {e.Observations} | {e.AuditEntries} | {e.ManualTimestamps} | {e.IgnoredImpulses} |");
        }
        text.AppendLine().AppendLine(CultureInfo.InvariantCulture, $"Penalty: calculated {first.CalculatedPenalty}, applied {first.AppliedPenalty}. XML `{first.XmlFileName}`: {first.XmlRankedResults} ranked, {first.XmlNotRanked} not ranked.").AppendLine();
        text.AppendLine(CultureInfo.InvariantCulture, $"Discrepancies: {(first.Discrepancies.Count + second.Discrepancies.Count == 0 ? "none" : "")}");
        foreach (var d in first.Discrepancies.Concat(second.Discrepancies)) { text.AppendLine("- " + d); }
        text.AppendLine().AppendLine("## Final results: expected vs actual").AppendLine();
        text.AppendLine("| Listed | Bib | Code | Expected status | Expected run 1 / run 2 / total | Expected rank | Actual status | Actual run 1 / run 2 / total | Actual rank | Match |");
        text.AppendLine("|---|---|---|---|---|---|---|---|---|---|");
        var listed = 0;
        foreach (var e in first.Expected)
        {
            var a = first.Actual.Single(x => x.Entry.Bib == e.Bib);
            var match = a.Status.ToString() == e.Status && a.Run1Hundredths == e.Run1 && a.Run2Hundredths == e.Run2 && a.TotalHundredths == e.Total && a.Rank == e.Rank;
            text.AppendLine(CultureInfo.InvariantCulture,
                $"| {++listed} | {e.Bib} | {e.Code} | {e.Status} | {F(e.Run1)} / {F(e.Run2)} / {F(e.Total)} | {e.Rank?.ToString(CultureInfo.InvariantCulture) ?? "-"} | {a.Status} | {F(a.Run1Hundredths)} / {F(a.Run2Hundredths)} / {F(a.TotalHundredths)} | {a.Rank?.ToString(CultureInfo.InvariantCulture) ?? "-"} | {(match ? "yes" : "**NO**")} |");
        }
        return text.ToString();
    }

    private static string F(long? value) => ExpectedResults.FormatHundredths(value);
}
