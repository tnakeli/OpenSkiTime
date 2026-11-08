using OpenSkiTime.Application;
using OpenSkiTime.Reporting;
using OpenSkiTime.Timing;
using UglyToad.PdfPig;
using Xunit;

namespace OpenSkiTime.Tests;

// The printed EET calculation follows the Timing Booklet Alpine 2.67 example 14.1 (section 14, p. 33).
public sealed class EetSheetTests
{
    private static readonly DateTimeOffset s_at = new(2026, 9, 27, 13, 20, 0, TimeSpan.FromHours(3));

    [Fact]
    public void TheSheetOfExample141ListsTheDnfAndThePdfShowsTheCalculation()
    {
        var eet = Example141();
        var names = Enumerable.Range(11, 12).ToDictionary(x => x, x => new EetSheetName($"Racer{x}", "Test"));
        // Competitors outside the calculation (1–10, 23–30) are not printed; the DNF inside it is, with the reason.
        var sheet = EetSheetBuilder.Build(eet, Enumerable.Range(1, 30).ToArray(), names, new Dictionary<int, string> { [16] = "Did not finish", [5] = "No finish time" },
            "Synthetic race weekend", "Synthetic Giant Slalom | 2026-09-27 | Run 1", "1:12.34", draft: false, "Saved to the result by Operator.", s_at);

        Assert.Equal(Enumerable.Range(11, 12), sheet.Rows.Select(x => x.Bib));
        Assert.Equal(new EetSheetRow(EetSheetRowKind.Reference, 11, "Racer11", "Test", "13:00:00.483", "13:00:00.263", "+0.220", "Time difference"), sheet.Rows[0]);
        Assert.Equal(new EetSheetRow(EetSheetRowKind.NotUsed, 16, "Racer16", "Test", "", "", "", "Did not finish"), sheet.Rows[5]);
        Assert.Equal(new EetSheetRow(EetSheetRowKind.Target, 22, "Racer22", "Test", "13:04:12.158", "missed time", "-0.816", "Sum of the time differences"), sheet.Rows[^1]);
        Assert.Equal((10, "-0.816", "-0.0816", "-0.082", 3, "13:04:12.240"), (sheet.PairCount, sheet.Sum, sheet.Average, sheet.Correction, sheet.CorrectionPrecision, sheet.Result));
        Assert.Equal(("Finish", "System B", "Wire break"), (sheet.Position, sheet.BackupSource, sheet.ReplacementReason));

        var text = Text(EetCalculationPdf.Render(sheet));
        foreach (var expected in new[] { "EET calculation", "Synthetic race weekend", "RACER22", "13:04:12.158", "13:04:12.240", "-0.0816", "-0.082",
            "+0.220", "Did not finish", "Wire break", "System B", "Chief of timing", "Technical delegate", "1:12.34", "2.67" })
        { Assert.Contains(expected, text, StringComparison.Ordinal); }
        Assert.DoesNotContain("DRAFT", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Racer5 ", text, StringComparison.Ordinal);

        var draft = Text(EetCalculationPdf.Render(sheet with { Draft = true, Status = "Calculated by Operator; not saved to the result." }));
        Assert.Contains("DRAFT - NOT SAVED", draft, StringComparison.Ordinal);
    }

    [Fact]
    public void LeftOutCompetitorsAreListedWithTheReasonAndTheSheetNeedsPairs()
    {
        var eet = Example141() with { Exclusions = [new(16, "Synthetic: B cell blocked")] };
        var sheet = EetSheetBuilder.Build(eet, Enumerable.Range(11, 12).ToArray(), new Dictionary<int, EetSheetName>(), new Dictionary<int, string>(),
            "Event", "Race", "-", draft: true, "Draft", s_at);
        Assert.Equal(new EetSheetRow(EetSheetRowKind.LeftOut, 16, "", "", "", "", "", "Left out: Synthetic: B cell blocked"), sheet.Rows[5]);
        Assert.Throws<OpenSkiTime.Domain.DomainValidationException>(() => EetSheetBuilder.Build(eet with { Pairs = [] }, [], new Dictionary<int, EetSheetName>(),
            new Dictionary<int, string>(), "Event", "Race", "-", draft: true, "Draft", s_at));
    }

    private static EetCalculation Example141()
    {
        var rows = new (int Bib, string? Backup, string? A)[]
        {
            (11, "13:00:00.483", "13:00:00.263"), (12, "13:00:26.521", "13:00:26.880"), (13, "13:00:47.410", "13:00:47.368"),
            (14, "13:01:04.232", "13:01:04.368"), (15, "13:01:27.544", "13:01:27.775"), (16, null, null),
            (17, "13:02:12.993", "13:02:12.912"), (18, "13:02:42.501", "13:02:42.616"), (19, "13:03:00.211", "13:03:00.944"),
            (20, "13:03:20.694", "13:03:20.280"), (21, "13:03:48.560", "13:03:48.559"), (22, "13:04:12.158", null),
        };
        var selection = EetCalculator.Select(EetCalculationTests.Candidates(rows), 22);
        Assert.True(TimingTime.TryTimeOfDay("13:04:12.158", out var backup, out var precision));
        return EetCalculator.Calculate(22, 1, EetBackupSource.SystemB, backup, precision, null, selection.Pairs, [], "Wire break");
    }

    private static string Text(byte[] pdf)
    {
        Assert.True(pdf.AsSpan(0, 5).SequenceEqual("%PDF-"u8));
        using var document = PdfDocument.Open(pdf);
        return string.Join(" ", document.GetPages().SelectMany(x => x.GetWords()).Select(x => x.Text));
    }
}
