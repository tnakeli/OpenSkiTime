using System.Globalization;
using System.Text.Json;
using OpenSkiTime.Desktop;
using OpenSkiTime.Domain;
using OpenSkiTime.LiveTiming;
using OpenSkiTime.Tests.FullRace;
using OpenSkiTime.Timing;
using Xunit;

namespace OpenSkiTime.Tests;

// Exports the live-timing snapshots of the synthetic 100-athlete race, built with the desktop's LiveSnapshotMapper at
// checkpoints during and after both runs, plus independent expectations for the completed runs. The browser E2E
// (tests/live-timing-full-race-e2e.py) publishes them to a real live server. Opt-in: OPENSKITIME_LIVE_FULL_RACE=<dir>.
public sealed class LiveFullRaceSnapshots
{
    [Fact]
    public async Task ExportLiveSnapshotsOfTheFullRace()
    {
        var output = Environment.GetEnvironmentVariable("OPENSKITIME_LIVE_FULL_RACE");
        if (string.IsNullOrWhiteSpace(output)) { return; }
        output = Path.GetFullPath(output);
        Directory.CreateDirectory(output);
        foreach (var old in Directory.EnumerateFiles(output, "*.json")) { File.Delete(old); }
        var root = Path.Combine(Path.GetTempPath(), "openskitime-live-full-race", Guid.NewGuid().ToString("N"));
        var race = new SyntheticRace();
        var lists = new Dictionary<int, StartListRevision>();
        var completed = new Dictionary<int, TimingSnapshot>();
        var index = 0;
        var version = 0L;
        try
        {
            var scenario = new FullRaceScenario(race, root)
            {
                Observe = async (run, list, snapshot, label) =>
                {
                    lists[run] = list;
                    var runs = new List<LiveRun>();
                    for (var number = 1; number < run; number++) { runs.Add(LiveSnapshotMapper.MapRun(lists[number], completed[number], TimeZoneInfo.Utc, Observed(index))); }
                    runs.Add(LiveSnapshotMapper.MapRun(list, snapshot, TimeZoneInfo.Utc, Observed(index)));
                    var c = race.Competition;
                    var state = new LiveSnapshot(++version, new LiveCompetition(c.Name, c.Calendar!.Location, "SL", c.Date, true, c.FisCode!, "L",
                            c.Calendar.Category, c.IntermediateCount, c.CourseName!),
                        LiveSnapshotMapper.Competitors(lists.OrderBy(x => x.Key).Select(x => x.Value)), run, LiveSnapshotMapper.WithTotals(runs), Observed(index));
                    state.Validate();
                    if (label.EndsWith("complete", StringComparison.Ordinal)) { completed[run] = snapshot; }
                    await File.WriteAllTextAsync(Path.Combine(output, $"snapshot-{++index:00}-{label}.json"), JsonSerializer.Serialize(state, LiveJson.Options));
                },
            };
            var outcome = await scenario.RunAsync();
            Assert.Empty(outcome.Discrepancies);
            // Independent expectations of the completed runs and the final standings, keyed by bib.
            var plan1 = outcome.Run1Order;
            var expected1 = ExpectedResults.Run(1, plan1, race.Run1);
            var order2 = lists[2].Plan.Entries.Select(x => (x.Position, x.Bib, x.Entrant.Athlete.FederationCode!)).ToArray();
            var expected2 = ExpectedResults.Run(2, order2, race.Run2);
            var final = ExpectedResults.Final(expected1, expected2);
            var expectation = new
            {
                run1 = expected1.Select(x => new { x.Bib, Status = x.Outcome.ToString(), Time = Format(x.Hundredths), x.Rank, Split = Format(x.IntermediateHundredths) }),
                final = final.Select(x => new { x.Bib, x.Status, Run1 = Format(x.Run1), Run2 = Format(x.Run2), Total = Format(x.Total), x.Rank }),
                run1Ties = race.Run1Ties.Select(t => new[] { Bib(plan1, t.A), Bib(plan1, t.B) }),
                combinedTies = race.CombinedTies.Select(t => new[] { Bib(plan1, t.A), Bib(plan1, t.B) }),
            };
            await File.WriteAllTextAsync(Path.Combine(output, "expected.json"), JsonSerializer.Serialize(expectation, LiveJson.Options));
            Assert.True(index >= 8, $"Expected at least 8 checkpoints, got {index}"); // 25/50/75 % and complete, per run
        }
        finally
        {
            if (Directory.Exists(root))
            {
                foreach (var item in new DirectoryInfo(root).EnumerateFileSystemInfos("*", SearchOption.AllDirectories)) { item.Attributes &= ~FileAttributes.ReadOnly; }
                new DirectoryInfo(root).Attributes &= ~FileAttributes.ReadOnly;
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static DateTimeOffset Observed(int index) => new DateTimeOffset(SyntheticRace.RaceDate.ToDateTime(new TimeOnly(10, 0)), TimeSpan.Zero).AddMinutes(index * 20);

    private static int Bib(IReadOnlyList<(int Position, int Bib, string Code)> order, string code) => order.Single(x => x.Code == code).Bib;

    // The viewer's time notation: m:ss.ff.
    private static string? Format(long? hundredths) => hundredths is { } v
        ? string.Create(CultureInfo.InvariantCulture, $"{v / 6000}:{v / 100 % 60:00}.{v % 100:00}") : null;
}
