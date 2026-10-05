using OpenSkiTime.Desktop;
using OpenSkiTime.LiveTiming;
using Xunit;

namespace OpenSkiTime.Tests;

// The live browser view ranks Run 2 by the combined time, like the timing view's Run 2 ranking, not by the Run 2 time.
public sealed class LiveTotalsTests
{
    private static readonly DateTimeOffset s_at = new(2026, 10, 5, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void RunTwoCarriesCombinedTimesRankedExAequoAndOnlyForRacersWhoFinishedEveryRun()
    {
        var runs = LiveSnapshotMapper.WithTotals([
            Run(2, (1, LiveStatus.Finished, 5000), (2, LiveStatus.Finished, 4900), (3, LiveStatus.Finished, 5200),
                (4, LiveStatus.Finished, 4500), (5, LiveStatus.OnCourse, null)),
            Run(1, (1, LiveStatus.Finished, 5000), (2, LiveStatus.Finished, 5100), (3, LiveStatus.Finished, 4900),
                (4, LiveStatus.DSQ, null), (5, LiveStatus.Finished, 5200))]);
        Assert.Equal([1, 2], runs.Select(x => x.Number));
        Assert.All(runs[0].Results, x => { Assert.Null(x.TotalHundredths); Assert.Null(x.TotalRank); Assert.Null(x.TotalDifference); });
        // Bibs 1 and 2 share 1:40.00; bib 3 is third. Bib 4 was disqualified in Run 1 and bib 5 is still on course.
        Assert.Equal(
            [(1, 10000L, 1, 0L), (2, 10000L, 1, 0L), (3, 10100L, 3, 100L), (4, null, null, null), (5, null, null, null)],
            runs[1].Results.OrderBy(x => x.Bib).Select(x => (x.Bib, x.TotalHundredths, x.TotalRank, x.TotalDifference)));
        // The fastest Run 2 time (bib 4) does not lead the combined standings.
        Assert.Equal(1, runs[1].Results.Single(x => x.Bib == 4).Rank);
        Snapshot(runs).Validate();

        // A Run 1 correction during Run 2 changes the totals on the next projection.
        var corrected = runs[0] with { Results = runs[0].Results.Select(x => x.Bib == 3 ? x with { Hundredths = 4700 } : x).ToArray() };
        var again = LiveSnapshotMapper.WithTotals([corrected, runs[1]]);
        Assert.Equal([(3, 9900L, 1, 0L), (1, 10000L, 2, 100L), (2, 10000L, 2, 100L)],
            again[1].Results.Where(x => x.TotalRank is not null).OrderBy(x => x.TotalRank).ThenBy(x => x.Bib)
                .Select(x => (x.Bib, x.TotalHundredths!.Value, x.TotalRank!.Value, x.TotalDifference!.Value)));
    }

    [Fact]
    public void ContractRejectsInconsistentTotals()
    {
        var finished = new LiveResult(1, LiveStatus.Finished, 5000, 1, 0, s_at);
        LiveSnapshot.ValidateResult(finished with { TotalHundredths = 10000, TotalRank = 1, TotalDifference = 0 }, 0);
        foreach (var invalid in new[]
        {
            finished with { TotalHundredths = 10000 },
            finished with { TotalHundredths = 10000, TotalRank = 0, TotalDifference = 0 },
            finished with { TotalHundredths = 10000, TotalRank = 1, TotalDifference = -1 },
            finished with { TotalHundredths = 4000, TotalRank = 1, TotalDifference = 0 },
            finished with { Status = LiveStatus.DNF, Hundredths = null, Rank = null, Difference = null, TotalHundredths = 10000, TotalRank = 1, TotalDifference = 0 },
        })
        { Assert.Throws<LiveValidationException>(() => LiveSnapshot.ValidateResult(invalid, 0)); }
    }

    private static LiveRun Run(int number, params (int Bib, LiveStatus Status, long? Time)[] rows)
    {
        var best = rows.Where(x => x.Status == LiveStatus.Finished).Select(x => x.Time).Min();
        return new(number, s_at, rows.Select(x => x.Bib).ToArray(), rows.Select(x => new LiveResult(x.Bib, x.Status, x.Time,
            x.Status == LiveStatus.Finished ? rows.Count(y => y.Status == LiveStatus.Finished && y.Time < x.Time) + 1 : null,
            x.Status == LiveStatus.Finished ? x.Time - best : null, s_at)).ToArray());
    }
    private static LiveSnapshot Snapshot(LiveRun[] runs) => new(1, new("Synthetic", "Test slope", "GS", new(2026, 10, 5), false, "", "M", "", 0),
        Enumerable.Range(1, 5).Select(i => new LiveCompetitor(i, "TOTAL" + i, "Synthetic", "FIN", "Club", "")).ToArray(), 2, runs, s_at);
}
