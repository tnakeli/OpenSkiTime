using OpenSkiTime.Rewrite.Recognition;
using OpenSkiTime.Rewrite.Timing;
using Xunit;

namespace OpenSkiTime.Rewrite.Tests;

public sealed class TimingReceiptOcrTests
{
    [Fact]
    public async Task FindsReceiptBehindLeadingNoiseWithoutTruncatingLongRecords()
    {
        var bytes = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "timing-receipt-noisy.png"));
        var original = bytes.ToArray();
        var recognized = await new TesseractTimingImageRecognizer().RecognizeAsync(bytes);
        Assert.Contains("localized contrast", recognized.Engine, StringComparison.Ordinal);
        var date = new DateOnly(2026, 10, 3);
        var parsed = recognized.Lines.SelectMany((line, i) => TimingEvidenceMatching.ParseLine("synthetic:" + i, line.Text, date)).ToArray();
        Assert.InRange(parsed.Length, 14, 16);
        var expected = Enumerable.Range(0, 16).Select(i =>
            (Ticks: new DateTime(2026, 10, 3, 12, 10 + i, 23).Ticks + 4567 * 1000L, Channel: (int?)(i % 2))).ToArray();
        Assert.All(parsed, stamp =>
        {
            Assert.Contains((stamp.Ticks, stamp.Channel), expected);
            Assert.Equal(4, stamp.Precision);
        });
        Assert.Equal(16, recognized.Lines.Count);
        for (var i = 0; i < 16; i++)
        {
            var line = recognized.Lines[i];
            // Bounds refer to the original fixture, including the removed contrast border.
            // The full six-digit sequence and four fractional digits must remain in view.
            Assert.InRange(line.X, 375, 390);
            Assert.InRange(line.Y, 120 + i * 60, 140 + i * 60);
            Assert.InRange(line.X + line.Width, 728, 740);
            if (line.Text.StartsWith(TimingEvidenceMatching.OcrReviewRequiredPrefix, StringComparison.Ordinal))
            {
                Assert.Contains($"12:{10 + i}:23.4567", line.Text, StringComparison.Ordinal);
                Assert.DoesNotContain(parsed, stamp => stamp.Ticks == expected[i].Ticks);
            }
            else
            {
                Assert.Contains(parsed, stamp => stamp.Ticks == expected[i].Ticks && stamp.Channel == i % 2);
            }
        }
        Assert.Equal(original, bytes);
    }

    [Fact]
    public async Task ExcludesReceiptRowsCutOffAtImageBoundary()
    {
        var bytes = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "timing-receipt-clipped.png"));
        var recognized = await new TesseractTimingImageRecognizer().RecognizeAsync(bytes);
        Assert.Contains("localized contrast", recognized.Engine, StringComparison.Ordinal);
        Assert.True(recognized.Lines.Count >= 14);
        Assert.All(recognized.Lines, line =>
        {
            Assert.StartsWith(TimingEvidenceMatching.OcrReviewRequiredPrefix, line.Text, StringComparison.Ordinal);
            Assert.Contains("boundary", line.Text, StringComparison.Ordinal);
            Assert.Empty(TimingEvidenceMatching.ParseLine("clipped", line.Text, new DateOnly(2026, 10, 3)));
        });
    }

    [Fact]
    public async Task DetectsSyntheticReceiptColumnAndKeepsConflictingPhysicalRowsUnmatched()
    {
        var bytes = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "timing-receipt-column.png"));
        var original = bytes.ToArray();
        var recognized = await new TesseractTimingImageRecognizer().RecognizeAsync(bytes);
        Assert.Contains("receipt row alternatives", recognized.Engine, StringComparison.Ordinal);
        var parsed = recognized.Lines.SelectMany((line, i) => TimingEvidenceMatching.ParseLine("synthetic:" + i,
            line.Text, new DateOnly(2026, 10, 3))).ToArray();
        for (var i = 0; i < 16; i++)
        {
            var ticks = new DateTime(2026, 10, 3, 12, 20 + i / 2, 0).Ticks + (i % 2 == 0 ? 12 : 34) * TimingTime.TicksPerHundredth;
            if (i == 1)
            {
                // The pinned engine's second row pass disagrees on seconds. Even though the
                // wrong alternative is outside matching tolerance, the whole row needs review.
                Assert.Contains(recognized.Lines, x => x.Text.StartsWith(TimingEvidenceMatching.OcrReviewRequiredPrefix,
                    StringComparison.Ordinal) && x.Text.Contains("12:20:00.34", StringComparison.Ordinal));
                Assert.DoesNotContain(parsed, x => x.Ticks == ticks);
                continue;
            }
            Assert.Contains(parsed, x => x.Ticks == ticks && x.Channel == i % 2 && x.Precision == 2);
        }
        Assert.All(recognized.Lines, x => Assert.InRange(x.X, 360, 740));
        Assert.Equal(original, bytes);
    }

    [Fact]
    public async Task BundledOfflineRecognizerReadsSyntheticPrintedReceiptAndProducesUniqueReviewProposals()
    {
        var image = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "timing-receipt.png"));
        var original = image.ToArray();
        var recognized = await new TesseractTimingImageRecognizer().RecognizeAsync(image);
        Assert.StartsWith("Tesseract", recognized.Engine, StringComparison.Ordinal);
        Assert.NotEmpty(recognized.Lines);
        var date = new DateOnly(2026, 10, 3);
        var evidence = recognized.Lines.SelectMany((line, i) => TimingEvidenceMatching.ParseLine("image:synthetic:" + i, line.Text, date)).ToArray();
        Assert.True(evidence.Length == 4, string.Join("\n", recognized.Lines.Select(x => x.Text)));
        Assert.Equal(new int?[] { 0, 1, 0, 1 }, evidence.Select(x => x.Channel));
        Assert.All(evidence, x => Assert.Equal(4, x.Precision));
        var expected = new[] { "12:00:00.1234", "12:01:00.1234", "12:02:00.5678", "12:03:00.5678" };
        Assert.Equal(expected, evidence.Select(x => new DateTime(x.Ticks).ToString("HH:mm:ss.ffff", System.Globalization.CultureInfo.InvariantCulture)));
        Assert.All(recognized.Lines, x => { Assert.True(x.Width > 0); Assert.True(x.Height > 0); });
        var targets = evidence.Select((x, i) => new EvidenceTarget("a:" + i, i / 2 + 1, i % 2, x.Ticks - 1000)).ToArray();
        var matches = TimingEvidenceMatching.Match(targets, evidence, TimeSpan.TicksPerSecond);
        Assert.All(matches, x => { Assert.NotNull(x.Evidence); Assert.Equal("Proposed - verify", x.State); Assert.Equal(1000, x.DifferenceTicks); });
        Assert.Equal(original, image);
    }
}
