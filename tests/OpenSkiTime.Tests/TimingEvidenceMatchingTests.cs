using OpenSkiTime.Domain;
using OpenSkiTime.Timing;
using Xunit;

namespace OpenSkiTime.Tests;

public sealed class TimingEvidenceMatchingTests
{
    private static readonly DateOnly s_date = new(2026, 10, 3);
    private static readonly string[] s_nearby = ["12:00:00.1", "12:00:00.2"];
    private static long Tick(string text) => Assert.Single(TimingEvidenceMatching.ParseLine("test", text, s_date)).Ticks;

    [Theory]
    [InlineData("0001 C0 9:03:02.1234567", 0, 7, "09:03:02.1234567")]
    [InlineData("0001 c1M 09.03.02,12", 1, 2, "09:03:02.1200000")]
    [InlineData("09:03:02.123", null, 3, "09:03:02.1230000")]
    public void ParsesPrintedTimeOfDayWithoutLosingPrecision(string text, int? channel, int precision, string expected)
    {
        var result = Assert.Single(TimingEvidenceMatching.ParseLine("image:1:line:7", text, s_date));
        Assert.Equal(channel, result.Channel); Assert.Equal(precision, result.Precision);
        Assert.Equal(expected, new DateTime(result.Ticks).ToString("HH:mm:ss.fffffff", System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(s_date, DateOnly.FromDateTime(new DateTime(result.Ticks)));
        Assert.Equal(text, result.Text); Assert.StartsWith("image:1:line:7:", result.Key, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("24:00:00.00")]
    [InlineData("12:60:00.000")]
    [InlineData("12:00:60.000")]
    [InlineData("12:00:00.12345678")]
    [InlineData("123:00:00.12")]
    [InlineData("NET 01:02.34")]
    [InlineData("81 C0M O9:42:16.37")]
    [InlineData("81 C0M I09:42:16.37")]
    [InlineData("81 C0M 09:42:16.3I")]
    [InlineData("81 C0M 09:42:16.37O")]
    [InlineData("81 C0M 25:09:42:16.37")]
    [InlineData("81 C0M 999.09:42:16.37")]
    [InlineData("81 C0M 09:42:16.37.8")]
    [InlineData("81 C0M 09:42:16.37:8")]
    public void RejectsInvalidOrNetTimesRatherThanInventingTimeOfDay(string line)
        => Assert.Empty(TimingEvidenceMatching.ParseLine("source", line, s_date));

    [Theory]
    [InlineData("(09:42:16.37)")]
    [InlineData("START:09:42:16.37")]
    [InlineData("09:42:16.37 seconds")]
    public void CanonicalTimesAllowOrdinaryLabelsAndSurroundingPunctuation(string line)
        => Assert.Equal(Tick("09:42:16.37"), Assert.Single(TimingEvidenceMatching.ParseLine("labelled", line, s_date)).Ticks);

    [Theory]
    [InlineData("81 C0M 09:42:16.37 | 81 C0M 09:48:16.37")]
    [InlineData("81 C0M 09:42:16.37 | 81 C1M 09:42:16.37")]
    [InlineData("81 C0M 09 42 16.37 | 81 C1M 09 42 16.37")]
    public void ConflictedPhysicalOcrRowsRemainUnparsedEvenWhenOneReadingWouldMatch(string alternatives)
    {
        var persisted = TimingEvidenceMatching.OcrReviewRequiredPrefix + alternatives;
        Assert.Empty(TimingEvidenceMatching.ParseLine("conflict", persisted, s_date));
        Assert.Empty(TimingEvidenceMatching.ParseLine("conflict", persisted, s_date, fixedChannel: 0));
        var targets = new[] { new EvidenceTarget("start", 1, 0, Tick("09:42:16.37")), new EvidenceTarget("finish", 2, 1, Tick("09:42:16.37")) };
        Assert.All(TimingEvidenceMatching.Match(targets, TimingEvidenceMatching.ParseLine("conflict", persisted, s_date), TimeSpan.TicksPerSecond),
            x => { Assert.Null(x.Evidence); Assert.Equal("Not found", x.State); });
    }

    [Theory]
    [InlineData("81 C0M 09 42 16.37", 0, 2, "09:42:16.3700000")]
    [InlineData("82 C1M 09:42 18,3719", 1, 4, "09:42:18.3719000")]
    [InlineData("83 COM 09 42:21 . 1234567", 0, 7, "09:42:21.1234567")]
    [InlineData("84 C1M 09 42 24 56", 1, 2, "09:42:24.5600000")]
    [InlineData("  85\tC0M\t9 42 26.17  ", 0, 2, "09:42:26.1700000")]
    public void ParsesSpacesOnlyWithinCompleteNumberedAlgeReceiptRecords(string line, int channel, int precision, string expected)
    {
        ArgumentNullException.ThrowIfNull(line);
        var parsed = Assert.Single(TimingEvidenceMatching.ParseLine("synthetic-receipt", line, s_date));
        Assert.Equal(channel, parsed.Channel); Assert.Equal(precision, parsed.Precision);
        Assert.Equal(expected, new DateTime(parsed.Ticks).ToString("HH:mm:ss.fffffff", System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(line, parsed.Text);
        var hourOffset = line.IndexOf('9');
        if (hourOffset > 0 && line[hourOffset - 1] == '0') { hourOffset--; }
        Assert.Equal("synthetic-receipt:" + hourOffset.ToString(System.Globalization.CultureInfo.InvariantCulture), parsed.Key);
    }

    [Theory]
    [InlineData("09 42 16.37")]
    [InlineData("81 09 42 16 37")]
    [InlineData("81 C0M 09 42 16")]
    [InlineData("81 C0M 09 42 16.12345678")]
    [InlineData("81 C0M O9 42 16.37")]
    [InlineData("81 C0M 09 4Z 16.37")]
    [InlineData("81 C0M 09 42 61.37")]
    [InlineData("81 C0M 25 42 16.37")]
    [InlineData("81 C0M 09 42 16.37 99")]
    [InlineData("81 C0M 09 42\n16.37")]
    [InlineData("81 C0M 09 42 16.37 82 C1M 09 42 17.37")]
    public void ReceiptFallbackDoesNotGuessDigitsFractionsOrMergeAdjacentRecords(string line)
        => Assert.Empty(TimingEvidenceMatching.ParseLine("synthetic-rejected", line, s_date));

    [Fact]
    public void SeparateHandSourceProvidesChannelAndMixedBRetainsPrintedChannels()
    {
        var hand = Assert.Single(TimingEvidenceMatching.ParseLine("hand", "12:01:02.34", s_date, fixedChannel: 1));
        Assert.Equal(1, hand.Channel);
        var start = Assert.Single(TimingEvidenceMatching.ParseLine("b", "C0 12:01:02.3400", s_date));
        var matches = TimingEvidenceMatching.Match([new("finish", 7, 1, hand.Ticks)], [start, hand], TimeSpan.TicksPerSecond);
        Assert.Equal(hand.Key, Assert.Single(matches).Evidence!.Key);
    }

    [Fact]
    public void DuplicateOverlappingImagesProduceOneProposalAndRetainDeterministicSourceReference()
    {
        var first = TimingEvidenceMatching.ParseLine("image:first", "C0 12:00:00.0000001", s_date);
        var duplicate = TimingEvidenceMatching.ParseLine("image:second", "C0 12:00:00.0000001", s_date);
        var match = Assert.Single(TimingEvidenceMatching.Match([new("a", 1, 0, Tick("12:00:00.0000"))], [..first, ..duplicate], 2));
        Assert.Equal(first[0].Key, match.Evidence!.Key); Assert.Equal(1, match.DifferenceTicks);
    }

    [Fact]
    public void MultipleNearbyCandidatesStayUnassignedEvenWhenOneIsCloser()
    {
        var stamps = s_nearby.SelectMany((x, i) => TimingEvidenceMatching.ParseLine(i.ToString(System.Globalization.CultureInfo.InvariantCulture), x, s_date)).ToArray();
        var match = Assert.Single(TimingEvidenceMatching.Match([new("a", 1, 0, Tick("12:00:00.0"))], stamps, TimeSpan.TicksPerSecond));
        Assert.Null(match.Evidence); Assert.StartsWith("Ambiguous", match.State, StringComparison.Ordinal);
    }

    [Fact]
    public void OneStampCannotBeClaimedByTwoAthletesOrTimingPositions()
    {
        var stamp = TimingEvidenceMatching.ParseLine("receipt", "12:00:00.00", s_date);
        var matches = TimingEvidenceMatching.Match([new("a1", 1, 0, Tick("12:00:00.00")), new("a2", 2, 0, Tick("12:00:00.50"))], stamp, TimeSpan.TicksPerSecond);
        Assert.All(matches, x => { Assert.Null(x.Evidence); Assert.StartsWith("Ambiguous", x.State, StringComparison.Ordinal); });
    }

    [Fact]
    public void HandToleranceIsInclusiveAndMissingEvidenceRemainsEmpty()
    {
        var evidence = TimingEvidenceMatching.ParseLine("hand", "12:00:02.00", s_date, 0);
        var target = new EvidenceTarget("a", 1, 0, Tick("12:00:00.00"));
        Assert.NotNull(Assert.Single(TimingEvidenceMatching.Match([target], evidence, 2 * TimeSpan.TicksPerSecond)).Evidence);
        Assert.Null(Assert.Single(TimingEvidenceMatching.Match([target], evidence, 2 * TimeSpan.TicksPerSecond - 1)).Evidence);
        Assert.Equal("Not found", Assert.Single(TimingEvidenceMatching.Match([target], [], TimeSpan.TicksPerSecond)).State);
    }

    [Fact]
    public void AdjacentDayMatchingAttachesReceiptToActualObservationDateOnlyWhenRequested()
    {
        var evidence = TimingEvidenceMatching.ParseLine("hand", "00:00:00.20", s_date, 1);
        var target = new EvidenceTarget("finish", 1, 1, Tick("23:59:59.90"));
        Assert.Null(Assert.Single(TimingEvidenceMatching.Match([target], evidence, TimeSpan.TicksPerSecond)).Evidence);
        var match = Assert.Single(TimingEvidenceMatching.Match([target], evidence, TimeSpan.TicksPerSecond, allowAdjacentDay: true));
        Assert.Equal(3_000_000, match.DifferenceTicks);
        Assert.Equal(s_date.AddDays(1), DateOnly.FromDateTime(new(match.Evidence!.Ticks)));
        Assert.Equal(s_date, DateOnly.FromDateTime(new(evidence[0].Ticks)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(600000001)]
    public void InvalidToleranceRejected(long ticks) => Assert.Throws<DomainValidationException>(() => TimingEvidenceMatching.Match([], [], ticks));
}
