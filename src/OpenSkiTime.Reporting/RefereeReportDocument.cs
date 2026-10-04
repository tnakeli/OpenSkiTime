using System.Globalization;
using OpenSkiTime.Application;
using OpenSkiTime.Domain;
using OpenSkiTime.Timing;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace OpenSkiTime.Reporting;

internal static class RefereeReportDocument
{
    // FIS Brand Book, page 6; sources and asset attribution are recorded in docs/fis-report-branding.md.
    private const string Blue = "#002395";
    private const string Yellow = "#F0AB00";
    private const float VerticalMarginMm = 15;
    private const float HorizontalMarginMm = 12;
    private static readonly string[] s_disqualificationHeaders = ["No.", "Name - Surname", "Nat.", "Gate Number.", "Gate Judge", "Notes"];
    private static readonly Lazy<string> s_logo = new(() =>
    {
        using var stream = typeof(RefereeReportDocument).Assembly.GetManifestResourceStream("OpenSkiTime.Reporting.Assets.fis-logo.svg")
            ?? throw new InvalidOperationException("The embedded FIS logo is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });

    internal static string FisLogo => s_logo.Value;

    public static IDocument Create(PdfReportData data)
    {
        var run = data.Descriptor.RunNumber ?? throw new ArgumentException("A referee report requires a run number.", nameof(data));
        var results = data.Source.Runs.Where(x => x.List.Plan.RunNumber == run).SelectMany(x => x.Timing.Results).ToArray();
        return Document.Create(document => document.Page(page =>
        {
            page.Size(PageSizes.A4);
            page.PageColor(Colors.White);
            page.MarginVertical(VerticalMarginMm, Unit.Millimetre);
            page.MarginHorizontal(HorizontalMarginMm, Unit.Millimetre);
            page.DefaultTextStyle(x => x.FontSize(8).FontColor(Colors.Black));
            page.Header().PaddingBottom(12).Column(column =>
            {
                column.Item().Row(row =>
                {
                    row.ConstantItem(42).Height(46).Svg(s_logo.Value).FitArea();
                    row.RelativeItem().PaddingLeft(14).AlignMiddle().Element(c => Stripes(c, 4));
                });
                column.Item().PaddingTop(8).Row(row =>
                {
                    row.RelativeItem().Text("REPORT BY THE REFEREE").Bold().FontSize(12);
                    row.AutoItem().AlignBottom().Text("Run " + run).Bold();
                });
            });
            page.Content().Column(column =>
            {
                column.Item().Element(c => CompetitionFields(c, data, results));
                column.Item().Element(c => Disqualifications(c, results));
                column.Item().Element(c => StatusGrid(c, "Did not start (N°)", results, TimingStatus.DNS, 3));
                column.Item().Element(c => StatusGrid(c, "Not permitted to start (N°)", results, TimingStatus.NPS, 1));
                column.Item().Element(c => StatusGrid(c, "Did not finish (N°)", results, TimingStatus.DNF, 4));
                column.Item().Element(c => PublicationFields(c, data));
            });
            page.Footer().PaddingTop(12).Row(row =>
            {
                row.RelativeItem().AlignMiddle().Element(c => Stripes(c, 7));
                row.ConstantItem(130).PaddingLeft(18).Text("INTERNATIONAL\nSKI AND SNOWBOARD\nFEDERATION").Bold().FontSize(7);
            });
        }));
    }

    private static void Stripes(IContainer container, float thickness) => container.Column(column =>
    {
        column.Item().Height(thickness).Background(Yellow);
        column.Item().Height(3);
        column.Item().Height(thickness).Background(Blue);
    });

    private static IContainer Cell(IContainer container, float minimumHeight, float horizontalPadding = 3)
        => container.Border(0.4f).MinHeight(minimumHeight).PaddingVertical(3).PaddingHorizontal(horizontalPadding);

    private static void Field(IContainer container, string label, string value, float minimumHeight = 30)
        => Cell(container, minimumHeight).Column(column =>
        {
            column.Item().Text(label).Bold().FontSize(7);
            if (!string.IsNullOrWhiteSpace(value)) { column.Item().PaddingTop(2).Text(value); }
        });

    private static void CompetitionFields(IContainer container, PdfReportData data, IReadOnlyList<TimingResult> results)
    {
        var race = data.Source.Competition?.Values ?? throw new ArgumentException("A referee report requires a competition.", nameof(data));
        var calendar = race.Calendar;
        var gender = calendar?.Gender switch
        {
            "M" => "Men", "W" => "Women", "A" => "Mixed",
            _ => string.Join(" / ", results.Select(x => x.Entry.Entrant.Athlete.Gender).Distinct().Select(GenderLabels.Format))
        };
        var discipline = race.Discipline switch
        {
            Discipline.Slalom => "SL", Discipline.GiantSlalom => "GS", Discipline.SuperG => "SG",
            Discipline.Downhill => "DH", Discipline.AlpineCombined => "AC", _ => "Other"
        };
        container.Table(table =>
        {
            table.ColumnsDefinition(columns => { for (var i = 0; i < 12; i++) { columns.RelativeColumn(); } });
            Field(table.Cell().ColumnSpan(4), "Place", calendar?.Location ?? data.Source.Series.Values.Location, 36);
            Field(table.Cell().ColumnSpan(4), "Country", calendar?.Nation ?? data.Source.Series.Values.Nation, 36);
            Field(table.Cell().ColumnSpan(4), "Codex", race.FisCode ?? "", 36);
            Field(table.Cell().ColumnSpan(8), "Name of the Event", race.Name);
            Field(table.Cell().ColumnSpan(4), "Date", race.Date.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture));
            Field(table.Cell().ColumnSpan(4), "Category", data.Source.Information?.Category ?? calendar?.Category ?? "");
            Field(table.Cell().ColumnSpan(4), "Gender", gender);
            Field(table.Cell().ColumnSpan(4), "Event", discipline);
        });
    }

    private static void Disqualifications(IContainer container, IReadOnlyList<TimingResult> results)
    {
        var disqualified = results.Where(x => x.Status == TimingStatus.DSQ).ToArray();
        container.Table(table =>
        {
            table.ColumnsDefinition(columns =>
            { foreach (var width in new[] { 7.3f, 30, 5.8f, 12.7f, 22.6f, 21.6f }) { columns.RelativeColumn(width); } });
            table.Header(header =>
            {
                Cell(header.Cell().ColumnSpan(6), 22).Text("The following competitors have been disqualified according to ICR:").Bold();
                for (var column = 0; column < s_disqualificationHeaders.Length; column++)
                { Cell(header.Cell(), 30, column == 2 ? 1 : 3).Text(s_disqualificationHeaders[column]).Bold().FontSize(7); }
            });
            for (var index = 0; index < Math.Max(9, disqualified.Length); index++)
            {
                var result = index < disqualified.Length ? disqualified[index] : null;
                var values = result is null ? new string[6] : new[]
                {
                    ReportComponents.Number(result.Bib), result.Name, result.Entry.Entrant.Athlete.Nation ?? "",
                    ReportComponents.Number(result.Disqualification?.Gate), result.Disqualification?.Judge ?? "",
                    result.Disqualification?.Reason ?? ""
                };
                for (var column = 0; column < values.Length; column++)
                { Cell(table.Cell(), 27, column == 2 ? 1 : 3).Text(values[column] ?? "").FontSize(column is 0 or 2 ? 7 : 8); }
            }
        });
    }

    private static void StatusGrid(IContainer container, string label, IReadOnlyList<TimingResult> results, TimingStatus status, int minimumRows)
    {
        var bibs = results.Where(x => x.Status == status).Select(x => ReportComponents.Number(x.Bib)).ToArray();
        var rows = Math.Max(minimumRows, (bibs.Length + 4 + 13) / 14);
        container.Table(table =>
        {
            table.ColumnsDefinition(columns => { for (var i = 0; i < 14; i++) { columns.RelativeColumn(); } });
            Cell(table.Cell().ColumnSpan(4), 23).Text(label).Bold().FontSize(7);
            for (var cell = 0; cell < rows * 14 - 4; cell++)
            { Cell(table.Cell(), 23).AlignCenter().Text(cell < bibs.Length ? bibs[cell] : "").FontSize(7); }
        });
    }

    private static void PublicationFields(IContainer container, PdfReportData data)
    {
        var referee = data.Source.Information?.Jury.FirstOrDefault(x => x.Function == "Referee")?.Person;
        var name = referee is null ? "" : string.Join(" ", new[] { referee.LastName, referee.FirstName, referee.Nation }.Where(x => !string.IsNullOrWhiteSpace(x)));
        container.Table(table =>
        {
            table.ColumnsDefinition(columns =>
            { columns.RelativeColumn(27); columns.RelativeColumn(18.5f); columns.RelativeColumn(15); columns.RelativeColumn(39.5f); });
            Field(table.Cell(), "Time published", "", 34);
            Field(table.Cell(), "Deadline", "", 34);
            Field(table.Cell(), "Date", "", 34);
            Field(table.Cell(), "The Referee", name, 34);
        });
    }
}
