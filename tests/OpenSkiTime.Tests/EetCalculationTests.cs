using System.Text.Json;
using OpenSkiTime.Domain;
using OpenSkiTime.Timing;
using Xunit;

namespace OpenSkiTime.Tests;

// Timing Booklet Alpine, Para Alpine 2.67 (2026-06-16), section 14, printed pp. 32–35.
public sealed class EetCalculationTests
{
    private static readonly long Day = TimingRulesTests.Date.ToDateTime(TimeOnly.MinValue).Ticks;

    [Fact]
    public void Example141ThousandthsSkipsTheDnfAndRoundsTheCorrection()
    {
        // Bib 16 did not finish; the other ten before bib 22 are the reference pairs.
        var rows = new (int Bib, string? Backup, string? A)[]
        {
            (11, "13:00:00.483", "13:00:00.263"), (12, "13:00:26.521", "13:00:26.880"), (13, "13:00:47.410", "13:00:47.368"),
            (14, "13:01:04.232", "13:01:04.368"), (15, "13:01:27.544", "13:01:27.775"), (16, null, null),
            (17, "13:02:12.993", "13:02:12.912"), (18, "13:02:42.501", "13:02:42.616"), (19, "13:03:00.211", "13:03:00.944"),
            (20, "13:03:20.694", "13:03:20.280"), (21, "13:03:48.560", "13:03:48.559"), (22, "13:04:12.158", null),
        };
        var selection = EetCalculator.Select(Candidates(rows), 22);
        Assert.Equal([11, 12, 13, 14, 15, 17, 18, 19, 20, 21], selection.Pairs.Select(x => x.Bib));
        Assert.Equal(16, Assert.Single(selection.Skipped).Bib);

        var eet = Calculate(22, "13:04:12.158", selection.Pairs);
        Assert.Equal(-8_160_000, eet.SumTicks);          // -0.816
        Assert.Equal(3, eet.CorrectionPrecision);
        Assert.Equal(-820_000, eet.CorrectionTicks);     // -0.0816 → -0.082
        Assert.Equal("13:04:12.240", Format(eet.ResultTimeOfDay, eet.ResultPrecision));
    }

    [Fact]
    public void EveryCompetitorPassedOverIsSkippedWhereverTheSearchEnds()
    {
        // Bib 1 has no pair; bib 2 has the missing time; bibs 3–12 give ten pairs after it.
        var candidates = Enumerable.Range(1, 12).Select(bib => bib switch
        {
            1 => new EetCandidate(1, null, "No backup time"),
            2 => new EetCandidate(2, null, "Missing time"),
            _ => new EetCandidate(bib, new EetPair(bib, $"a{bib}", Day + bib * TimeSpan.TicksPerMinute, 3, bib * TimeSpan.TicksPerMinute + 10_000, 3))
        }).ToArray();
        var selection = EetCalculator.Select(candidates, 2);
        Assert.Equal(Enumerable.Range(3, 10), selection.Pairs.Select(x => x.Bib));
        Assert.Equal(1, Assert.Single(selection.Skipped).Bib);
    }

    [Fact]
    public void Example142TenThousandthsCompletesWithCompetitorsStartedAfter()
    {
        var rows = Example142("10:00:50.3548", "10:01:52.0189", "10:02:49.4978", "10:03:50.6148", "10:04:49.2741",
            "10:05:50.4702", "10:06:48.9125", "10:07:51.5814", "10:08:49.8751", "10:09:49.2459", "10:10:50.3954");
        var selection = EetCalculator.Select(Candidates(rows), 8);
        Assert.Equal([1, 2, 3, 4, 5, 6, 7, 9, 10, 11], selection.Pairs.Select(x => x.Bib));
        Assert.Empty(selection.Skipped);

        var eet = Calculate(8, "10:07:51.5814", selection.Pairs);
        Assert.Equal(-11_576_000, eet.SumTicks);         // -1.1576
        Assert.Equal(-1_158_000, eet.CorrectionTicks);   // -0.11576 → -0.1158
        Assert.Equal("10:07:51.6972", Format(eet.ResultTimeOfDay, eet.ResultPrecision));
    }

    [Fact]
    public void Example143HundredthHandTimeUsesTheFullSystemAPrecision()
    {
        var rows = Example142("10:00:50.35", "10:01:52.01", "10:02:49.49", "10:03:50.61", "10:04:49.27",
            "10:05:50.47", "10:06:48.91", "10:07:51.58", "10:08:49.87", "10:09:49.24", "10:10:50.39");
        var selection = EetCalculator.Select(Candidates(rows), 8);
        var eet = Calculate(8, "10:07:51.58", selection.Pairs, EetBackupSource.HandTime);
        Assert.Equal(-12_071_000, eet.SumTicks);         // -1.2071
        Assert.Equal(4, eet.CorrectionPrecision);        // A precision, not the hand clock's hundredths
        Assert.Equal(-1_207_000, eet.CorrectionTicks);   // -0.12071 → -0.1207
        Assert.Equal(4, eet.ResultPrecision);
        Assert.Equal("10:07:51.7007", Format(eet.ResultTimeOfDay, eet.ResultPrecision));
    }

    [Theory]
    // Section 14 rounding examples: 0.0444 = 0.044, 0.0455 = 0.046, 1.1575 / 10 → 0.1158, 1.1574 / 10 → 0.1157.
    [InlineData(3, 444_000, 3, 440_000)]
    [InlineData(3, 455_000, 3, 460_000)]
    [InlineData(4, 1_157_500, 4, 1_158_000)]
    [InlineData(4, 1_157_400, 4, 1_157_000)]
    // Negative halves round away from zero too, so a correction and its mirror image are symmetric.
    [InlineData(3, -455_000, 3, -460_000)]
    [InlineData(4, -1_157_500, 4, -1_158_000)]
    public void MeanDifferenceRoundsHalfAwayFromZeroAtTheSystemAPrecision(int aPrecision, long meanTicks, int expectedPrecision, long expected)
    {
        // Ten identical differences make the mean exactly the tested value.
        var pairs = Enumerable.Range(1, 10).Select(i => new EetPair(i, $"a{i}", Day + TimeSpan.FromHours(10).Ticks,
            aPrecision, TimeSpan.FromHours(10).Ticks + meanTicks, 7)).ToArray();
        var eet = EetCalculator.Calculate(20, 1, EetBackupSource.SystemB, TimeSpan.FromHours(11).Ticks, 7, null, pairs, [], "");
        Assert.Equal(expectedPrecision, eet.CorrectionPrecision);
        Assert.Equal(expected, eet.CorrectionTicks);
        Assert.Equal(7, eet.ResultPrecision);
    }

    [Fact]
    public void PairsAcrossMidnightCompareOnTheNeighbouringDay()
    {
        var pairs = Enumerable.Range(1, 10).Select(i => new EetPair(i, $"a{i}", Day + TimeSpan.TicksPerDay - 10_000, 3,
            20_000, 3)).ToArray(); // A 23:59:59.999, backup 00:00:00.002 → +0.003
        var eet = EetCalculator.Calculate(20, 1, EetBackupSource.SystemB, 50_000, 3, null, pairs, [], "");
        Assert.Equal(30_000, eet.CorrectionTicks);
        Assert.Equal(20_000, eet.ResultTimeOfDay);
        var early = EetCalculator.Calculate(20, 1, EetBackupSource.SystemB, 10_000, 3, null, pairs, [], "");
        Assert.Equal(TimeSpan.TicksPerDay - 20_000, early.ResultTimeOfDay);
    }

    [Fact]
    public void FewerThanTenPairsAreUsedOnlyWhenTheRunHasNoMore()
    {
        var rows = Example142("10:00:50.3548", "10:01:52.0189", "10:02:49.4978", null, null, null, null, "10:07:51.5814", null, null, null);
        var selection = EetCalculator.Select(Candidates(rows), 8);
        Assert.Equal([1, 2, 3], selection.Pairs.Select(x => x.Bib));
        Assert.Equal([4, 5, 6, 7, 9, 10, 11], selection.Skipped.Select(x => x.Bib));
        Assert.Equal(3, Calculate(8, "10:07:51.5814", selection.Pairs).Pairs.Count);
    }

    [Fact]
    public void InvalidInputIsRejectedBeforeAnyResultExists()
    {
        var pairs = Enumerable.Range(1, 10).Select(i => new EetPair(i, $"a{i}", Day + TimeSpan.FromHours(10).Ticks, 4,
            TimeSpan.FromHours(10).Ticks, 2)).ToArray();
        var backup = TimeSpan.FromHours(11).Ticks;
        Assert.Throws<DomainValidationException>(() => EetCalculator.Calculate(20, 2, EetBackupSource.SystemB, backup, 2, null, pairs, [], ""));
        Assert.Throws<DomainValidationException>(() => EetCalculator.Calculate(20, 1, EetBackupSource.SystemB, backup, 2, null, [], [], ""));
        Assert.Throws<DomainValidationException>(() => EetCalculator.Calculate(5, 1, EetBackupSource.SystemB, backup, 2, null, pairs, [], ""));
        Assert.Throws<DomainValidationException>(() => EetCalculator.Calculate(20, 1, EetBackupSource.SystemB, backup, 2, null,
            [.. pairs, pairs[0] with { Bib = 11 }], [], ""));
        Assert.Throws<DomainValidationException>(() => EetCalculator.Calculate(20, 1, EetBackupSource.SystemB, backup + 1, 2, null, pairs, [], ""));
        Assert.Throws<DomainValidationException>(() => EetCalculator.Calculate(20, 1, EetBackupSource.SystemB, backup, 2, null,
            [pairs[0] with { APrecision = 2 }], [], ""));
        Assert.Throws<DomainValidationException>(() => EetCalculator.Calculate(20, 1, EetBackupSource.SystemB, backup, 2, null,
            [pairs[0] with { BackupTimeOfDay = TimeSpan.FromHours(10).Ticks + 2 * TimeSpan.TicksPerMinute }], [], ""));
        Assert.Throws<DomainValidationException>(() => EetCalculator.Calculate(20, 1, EetBackupSource.SystemB, backup, 2, null, pairs,
            [new EetExclusion(30, " ")], ""));
    }

    [Fact]
    public void AStoredCalculationSurvivesJsonAndATamperedResultIsInvalid()
    {
        var rows = Example142("10:00:50.3548", "10:01:52.0189", "10:02:49.4978", "10:03:50.6148", "10:04:49.2741",
            "10:05:50.4702", "10:06:48.9125", "10:07:51.5814", "10:08:49.8751", "10:09:49.2459", "10:10:50.3954");
        var eet = Calculate(8, "10:07:51.5814", EetCalculator.Select(Candidates(rows), 8).Pairs)
            with { Exclusions = [new(12, "Backup cell blocked")] };
        eet = EetCalculator.Calculate(eet.Bib, eet.Channel, eet.Source, eet.BackupTimeOfDay, eet.BackupPrecision, eet.BackupKey,
            eet.Pairs, eet.Exclusions, "Wire break");
        var entry = new ManualTimestamp(1, Day + eet.ResultTimeOfDay, eet.ResultPrecision, "clock", "a1")
        { Source = ManualTimeSource.Eet, Eet = eet };
        Assert.True(entry.IsValid);
        var decision = new TimingDecision(DecisionKind.ManualTime, ManualTimestamp.KeyPrefix + Guid.NewGuid().ToString("N"), Timestamp: entry);
        var restored = JsonSerializer.Deserialize<TimingDecision>(JsonSerializer.Serialize(decision))!;
        Assert.Equal(decision, restored);
        Assert.True(restored.Timestamp!.IsValid);

        Assert.False((entry with { DeviceTicks = entry.DeviceTicks + 1000 }).IsValid);
        Assert.False((entry with { Eet = eet with { CorrectionTicks = 0 } }).IsValid);
        Assert.False((entry with { Eet = null }).IsValid);
        Assert.False((entry with { Source = ManualTimeSource.Hand }).IsValid);
        // A hand time saved before sources existed has no source in its JSON and stays a hand time.
        var hand = new TimingDecision(DecisionKind.ManualTime, decision.ObservationKey, Timestamp: new(1, Day + 100_000, 2, "clock"));
        var json = JsonSerializer.Serialize(hand);
        Assert.DoesNotContain("Source", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Eet", json, StringComparison.Ordinal);
        // Exactly the JSON of a manual time before sources existed, so approved-result fingerprints are unchanged.
        Assert.Equal($$"""{"Channel":1,"DeviceTicks":{{Day + 100_000}},"Precision":2,"ClockId":"clock","ReferenceKey":null,"IsValid":true}""",
            JsonSerializer.Serialize(hand.Timestamp));
        Assert.Equal(ManualTimeSource.Hand, JsonSerializer.Deserialize<TimingDecision>(json)!.Timestamp!.Source);
        // A System A device-record time keeps the System A precision rule.
        Assert.False((hand.Timestamp! with { Source = ManualTimeSource.DeviceRecord }).IsValid);
        Assert.True((hand.Timestamp! with { Source = ManualTimeSource.DeviceRecord, Precision = 4 }).IsValid);
    }

    private static (int Bib, string? Backup, string? A)[] Example142(params string?[] backup)
    {
        string?[] a = ["10:00:50.1292", "10:01:52.1921", "10:02:49.4920", "10:03:50.9812", "10:04:49.8729", "10:05:50.5129",
            "10:06:48.8615", null, "10:08:50.0002", "10:09:49.4278", "10:10:50.3473"];
        return backup.Select((x, i) => (i + 1, x, a[i])).ToArray();
    }

    internal static EetCandidate[] Candidates((int Bib, string? Backup, string? A)[] rows) => rows.Select(row =>
    {
        if (row.Backup is null || row.A is null) { return new EetCandidate(row.Bib, null, row.A is null ? "No System A time" : "No backup time"); }
        var (a, aPrecision) = Parse(row.A);
        var (backup, backupPrecision) = Parse(row.Backup);
        return new EetCandidate(row.Bib, new EetPair(row.Bib, $"a{row.Bib}", Day + a, aPrecision, backup, backupPrecision));
    }).ToArray();

    private static EetCalculation Calculate(int bib, string backup, IReadOnlyList<EetPair> pairs, EetBackupSource source = EetBackupSource.SystemB)
    {
        var (ticks, precision) = Parse(backup);
        return EetCalculator.Calculate(bib, 1, source, ticks, precision, null, pairs, [], "");
    }

    private static (long Ticks, int Precision) Parse(string text)
    {
        Assert.True(TimingTime.TryTimeOfDay(text, out var ticks, out var precision));
        return (ticks, precision);
    }

    private static string Format(long ticks, int precision) => TimingTime.FormatTimeOfDay(ticks)[..(9 + precision)];
}
