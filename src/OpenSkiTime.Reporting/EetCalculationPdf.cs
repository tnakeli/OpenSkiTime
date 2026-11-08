using System.Globalization;
using OpenSkiTime.Application;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace OpenSkiTime.Reporting;

/// <summary>
/// The printed record of one EET calculation (FIS Timing Booklet Alpine, Para Alpine 2.67, section 14), laid out like
/// the booklet's examples: the reference lines in start order, the sum, the correction before and after rounding and
/// the replacement time of day. For the jury, the technical delegate and the timing documentation of the race.
/// </summary>
public static class EetCalculationPdf
{
    public const string RuleReference = "FIS Timing Booklet Alpine, Para Alpine 2.67, section 14 (EET calculation)";
    private const string Ink = "#142F3A";
    private const string HeaderFill = "#EAF0F2";
    private const string RowLine = "#CCD7DC";
    private const string DraftRed = "#C01818";
    private static readonly string[] s_headers = ["Bib", "Competitor", "Backup time of day", "System A time of day", "Difference", "Explanation"];
    private static readonly string[] s_signatures = ["Chief of timing", "Technical delegate"];

    static EetCalculationPdf() { QuestPDF.Settings.License = LicenseType.Community; }

    public static byte[] Render(EetSheet sheet)
    {
        ArgumentNullException.ThrowIfNull(sheet);
        return Document.Create(document => document.Page(page =>
        {
            page.Size(PageSizes.A4);
            page.PageColor(Colors.White);
            page.Margin(16, Unit.Millimetre);
            page.DefaultTextStyle(x => x.FontSize(9).FontColor(Ink));
            page.Header().Column(column =>
            {
                column.Item().Text("EET calculation").FontSize(14).SemiBold();
                column.Item().Text(sheet.Event).FontSize(10).SemiBold();
                column.Item().Text(sheet.Race);
                column.Item().PaddingTop(4).LineHorizontal(0.7f).LineColor(Ink);
            });
            page.Content().PaddingTop(10).Column(column =>
            {
                column.Spacing(4);
                if (sheet.Draft)
                {
                    column.Item().Border(1).BorderColor(DraftRed).Padding(5)
                        .Text("DRAFT - NOT SAVED. This calculation has not been applied to the result.").Bold().FontColor(DraftRed);
                }
                column.Item().Table(table =>
                {
                    table.ColumnsDefinition(c => { c.ConstantColumn(150); c.RelativeColumn(); });
                    void Field(string label, Action<TextDescriptor> value)
                    {
                        table.Cell().PaddingVertical(1).Text(label).SemiBold();
                        table.Cell().PaddingVertical(1).Text(value);
                    }
                    Field("Competitor", text =>
                    {
                        text.Span("Bib " + sheet.Bib.ToString(CultureInfo.InvariantCulture) + "  ");
                        Person(text, sheet.Surname, sheet.FirstName);
                    });
                    Field("Missing System A time", text => text.Span(sheet.Position));
                    Field("Backup source", text => text.Span(sheet.BackupSource));
                    Field("Why System A missed", text => text.Span(sheet.ReplacementReason.Length > 0 ? sheet.ReplacementReason : "-"));
                    Field("Rule", text => text.Span(RuleReference));
                });
                column.Item().PaddingTop(6).Table(table =>
                {
                    table.ColumnsDefinition(c =>
                    {
                        c.ConstantColumn(34); c.RelativeColumn(2.2f); c.RelativeColumn(1.6f); c.RelativeColumn(1.7f); c.RelativeColumn(1.1f); c.RelativeColumn(1.9f);
                    });
                    table.Header(header =>
                    {
                        foreach (var text in s_headers)
                        { header.Cell().Background(HeaderFill).Padding(3).Text(text).SemiBold(); }
                    });
                    foreach (var row in sheet.Rows)
                    {
                        var target = row.Kind == EetSheetRowKind.Target;
                        IContainer Cell() => table.Cell().BorderBottom(target ? 0.7f : 0.25f).BorderColor(target ? Ink : RowLine).Padding(3);
                        void Value(string value, bool right = false)
                        {
                            var cell = Cell();
                            var text = (right ? cell.AlignRight() : cell).Text(value);
                            if (target) { text.SemiBold(); }
                        }
                        Value(row.Bib.ToString(CultureInfo.InvariantCulture));
                        Cell().Text(text => Person(text, row.Surname, row.FirstName));
                        Value(row.BackupTime, right: true);
                        Value(row.ATime, right: true);
                        Value(row.Difference, right: true);
                        Value(row.Explanation);
                    }
                });
                column.Item().PaddingTop(8).Table(table =>
                {
                    table.ColumnsDefinition(c => { c.RelativeColumn(); c.ConstantColumn(150); });
                    void Line(string label, string value, bool strong = false)
                    {
                        var left = table.Cell().PaddingVertical(1).Text(label);
                        var right = table.Cell().PaddingVertical(1).AlignRight().Text(value);
                        if (strong) { left.Bold(); right.Bold(); }
                    }
                    var count = sheet.PairCount.ToString(CultureInfo.InvariantCulture);
                    Line($"Sum of {count} time differences (backup - System A)", sheet.Sum);
                    Line($"Correction = sum / {count}", sheet.Average);
                    Line($"Correction after rounding to {sheet.CorrectionPrecision.ToString(CultureInfo.InvariantCulture)} decimals", sheet.Correction);
                    Line($"EET = backup time - correction = {sheet.BackupTime} - ({sheet.Correction})", sheet.Result, strong: true);
                    Line("Net time with the EET", sheet.NetTime);
                });
                if (sheet.PairCount < 10)
                {
                    column.Item().PaddingTop(4).Text($"Only {sheet.PairCount.ToString(CultureInfo.InvariantCulture)} reference pairs were available; the booklet uses 10.").Italic();
                }
                column.Item().PaddingTop(8).Text(sheet.Status);
                column.Item().PaddingTop(14).Row(row =>
                {
                    row.Spacing(24);
                    foreach (var role in s_signatures)
                    {
                        row.RelativeItem().Column(signature =>
                        {
                            signature.Item().Height(28).BorderBottom(0.5f).BorderColor(Ink);
                            signature.Item().PaddingTop(2).Text(role + " (name, signature)").FontSize(8);
                        });
                    }
                });
            });
            page.Footer().PaddingTop(6).Row(row =>
            {
                row.RelativeItem().Text(ProductInfo.DisplayName + " | " + sheet.GeneratedAt.ToString("yyyy-MM-dd HH:mm zzz", CultureInfo.InvariantCulture)).FontSize(7);
                row.AutoItem().Text(text => { text.Span("Page ").FontSize(7); text.CurrentPageNumber().FontSize(7); text.Span(" / ").FontSize(7); text.TotalPages().FontSize(7); });
            });
        })).GeneratePdf();
    }

    private static void Person(TextDescriptor text, string surname, string firstName)
        => ReportComponents.Person(text, new PersonName(surname, firstName));
}
