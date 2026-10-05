using System.Globalization;
using OpenSkiTime.Application;
using OpenSkiTime.Domain;
using OpenSkiTime.Timing;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace OpenSkiTime.Reporting;

public sealed class QuestPdfRenderer : IReportPdfRenderer
{
    static QuestPdfRenderer() { QuestPDF.Settings.License = LicenseType.Community; }
    public void Render(PdfReportData data, string destination)
    {
        ArgumentNullException.ThrowIfNull(data);
        var profile = data.Source.Settings.Profile;
        if (data.Descriptor.Type == PdfReportType.RefereeReport)
        {
            // The FIS form must not include organizer or sponsor text from the series background.
            RefereeReportDocument.Create(data).GeneratePdf(destination);
            return;
        }
        if (data.Descriptor.Type == PdfReportType.TimingReport)
        {
            // The FIS form has its own fixed layout, without organizer background or report header.
            TimingReportFormDocument.Create(data).GeneratePdf(destination);
            return;
        }
        profile.Validate();
        var document = Document.Create(container => container.Page(page =>
        {
            page.Size(PageSizes.A4);
            // Transparent page lets a PDF underlay remain visible.
            page.PageColor(Colors.Transparent);
            ApplyMargins(page, profile);
            page.DefaultTextStyle(x => x.FontSize(8).FontColor("#142F3A"));
            page.Header().Element(c => ReportComponents.Header(c, data));
            page.Content().PaddingTop(8).Column(column =>
            {
                switch (data.Descriptor.Type)
                {
                    case PdfReportType.PenaltyCalculation: PenaltyCalculationDocument.Compose(column, data); break;
                    case PdfReportType.OfficialResults: OfficialResultsDocument.Compose(column, data); break;
                    default: EntriesDocument.Compose(column, data); break;
                }
            });
            page.Footer().PaddingTop(6).Row(row =>
            {
                row.RelativeItem().Text(ProductInfo.DisplayName + " | " + data.GeneratedAt.ToString("yyyy-MM-dd HH:mm zzz", CultureInfo.InvariantCulture)).FontSize(7);
                row.AutoItem().Text(text => { text.Span("Page "); text.CurrentPageNumber(); text.Span(" / "); text.TotalPages(); });
            });
        }));
        document.GeneratePdf(destination);
        ApplyBackground(profile, destination);
    }
    public static void RenderPrintProfilePreview(EventPrintProfile profile, string destination)
    {
        ArgumentNullException.ThrowIfNull(profile);
        profile.Validate();
        Document.Create(container => container.Page(page =>
        {
            page.Size(PageSizes.A4);
            page.PageColor(Colors.Transparent);
            ApplyMargins(page, profile);
            page.DefaultTextStyle(x => x.FontSize(9).FontColor("#D02020"));
            page.Content().Extend().Border(1).BorderColor("#D02020").Padding(6).Layers(layers =>
            {
                layers.PrimaryLayer().Extend();
                layers.Layer().AlignTop().AlignCenter().Background(Colors.White).Padding(3)
                    .Text(string.Create(CultureInfo.InvariantCulture, $"TOP={profile.TopMm:0.##} mm"));
                layers.Layer().AlignBottom().AlignCenter().Background(Colors.White).Padding(3)
                    .Text(string.Create(CultureInfo.InvariantCulture, $"BOTTOM={profile.BottomMm:0.##} mm"));
                layers.Layer().AlignMiddle().AlignLeft().Background(Colors.White).Padding(3)
                    .Text(string.Create(CultureInfo.InvariantCulture, $"LEFT={profile.LeftMm:0.##} mm"));
                layers.Layer().AlignMiddle().AlignRight().Background(Colors.White).Padding(3)
                    .Text(string.Create(CultureInfo.InvariantCulture, $"RIGHT={profile.RightMm:0.##} mm"));
            });
        })).GeneratePdf(destination);
        ApplyBackground(profile, destination);
    }
    private static void ApplyMargins(PageDescriptor page, EventPrintProfile profile)
    {
        page.MarginTop((float)profile.TopMm, Unit.Millimetre);
        page.MarginBottom((float)profile.BottomMm, Unit.Millimetre);
        page.MarginLeft((float)profile.LeftMm, Unit.Millimetre);
        page.MarginRight((float)profile.RightMm, Unit.Millimetre);
    }
    private static void ApplyBackground(EventPrintProfile profile, string destination)
    {
        if (profile.TemplatePdf is { Length: > 0 } background)
        {
            var template = destination + ".background.tmp";
            var combined = destination + ".combined.tmp";
            try
            {
                File.WriteAllBytes(template, background);
                DocumentOperation.LoadFile(destination)
                    .UnderlayFile(new DocumentOperation.LayerConfiguration
                    { FilePath = template, TargetPages = "1-z", SourcePages = "1", RepeatSourcePages = "1" }).Save(combined);
                File.Move(combined, destination, overwrite: true);
            }
            finally { if (File.Exists(template)) { File.Delete(template); } if (File.Exists(combined)) { File.Delete(combined); } }
        }
    }
    public static void ValidateTemplate(byte[] bytes)
    {
        var directory = Path.Combine(Path.GetTempPath(), "OpenSkiTime-PdfValidation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var input = Path.Combine(directory, "input.pdf");
            File.WriteAllBytes(input, bytes);
            ArgumentNullException.ThrowIfNull(bytes);
            DocumentOperation.LoadFile(input).TakePages("1").Save(Path.Combine(directory, "checked.pdf"));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}

internal static class ReportComponents
{
    public static string Number(object? value) => value is IFormattable f ? f.ToString(null, CultureInfo.InvariantCulture) : value?.ToString() ?? "";
    public static void Header(IContainer container, PdfReportData data)
    {
        var source = data.Source;
        container.Column(column =>
        {
            column.Item().Text(source.Series.Values.Name).FontSize(13).SemiBold();
            column.Item().Text(data.Descriptor.DisplayName).FontSize(11).SemiBold();
            if (data.Descriptor.CompetitionId is not null && source.Competition is { } race)
            {
                var gender = race.Values.Calendar?.Gender switch { "M" => "Men", "W" => "Women", "A" => "All", _ => "" };
                column.Item().Text($"{race.Values.Name} | {race.Values.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)} | {race.Values.Discipline} | {gender} | Codex {race.Values.FisCode ?? "-"}");
            }
            else { column.Item().Text(source.Series.Values.StartDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + " - " + source.Series.Values.EndDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)); }
            column.Item().Text(source.Series.Values.Location + " | " + source.Series.Values.Nation + " | " + source.Series.Values.Organizer).FontSize(8);
            column.Item().PaddingTop(4).LineHorizontal(0.7f).LineColor("#142F3A");
        });
    }
    public static void Heading(ColumnDescriptor column, string text) => column.Item().PaddingTop(6).PaddingBottom(4).Text(text).SemiBold().FontSize(9);
    public static void Table(IContainer container, string[] headers, IEnumerable<string[]> rows, float[]? widths = null)
    {
        container.Table(table =>
        {
            table.ColumnsDefinition(columns => { for (var i = 0; i < headers.Length; i++) { columns.RelativeColumn(widths?[i] ?? 1); } });
            table.Header(header => { foreach (var text in headers) { header.Cell().Background("#EAF0F2").Padding(3).Text(text).SemiBold(); } });
            foreach (var row in rows)
            { foreach (var value in row) { table.Cell().BorderBottom(0.25f).BorderColor("#CCD7DC").Padding(3).Text(value); } }
        });
    }
    public static void ManualLine(ColumnDescriptor column, string label, float height = 28)
        => column.Item().PaddingTop(8).Height(height).BorderBottom(0.5f).Text(label);
}

internal static class EntriesDocument
{
    public static void Compose(ColumnDescriptor column, PdfReportData data)
    {
        var start = data.Descriptor.Type == PdfReportType.StartList;
        if (data.Groups.Count == 0 || data.Groups.All(x => x.Entries.Count == 0)) { column.Item().Text("No entries."); }
        var columns = start ? StartColumns() : EntryColumns(data);
        foreach (var group in data.Groups)
        {
            if (group.Label.Length > 0) { ReportComponents.Heading(column, group.Label + " (" + group.Entries.Count + ")"); }
            ReportComponents.Table(column.Item(), columns.Select(x => x.Header).ToArray(),
                group.Entries.Select(row => columns.Select(x => x.Value(row)).ToArray()), columns.Select(x => x.Width).ToArray());
        }
        column.Item().PaddingTop(6).Text("Entries: " + data.Groups.Sum(x => x.Entries.Count));
    }
    private sealed record EntryColumn(string Header, float Width, Func<EntryReportRow, string> Value);
    private static string Name(EntryReportRow x) => x.Athlete.Surname + " " + x.Athlete.FirstName;
    private static EntryColumn[] StartColumns() =>
    [
        new("Pos", 0.5f, x => ReportComponents.Number(x.Position)), new("Bib", 0.5f, x => ReportComponents.Number(x.Bib)),
        new("Code", 0.9f, x => x.Athlete.FederationCode ?? ""), new("Name", 2.8f, Name), new("Year", 0.7f, x => ReportComponents.Number(x.Athlete.BirthYear)),
        new("Nation", 0.7f, x => x.Athlete.Nation ?? ""), new("Club", 1.5f, x => x.Athlete.Club ?? ""),
        new("Points", 0.8f, x => x.Points?.ToString("0.00", CultureInfo.InvariantCulture) ?? "")
    ];
    private static EntryColumn[] EntryColumns(PdfReportData data)
    {
        var rows = data.Groups.SelectMany(x => x.Entries).ToArray();
        var columns = new List<EntryColumn>();
        // A column that no entrant has a value for carries no information and is omitted.
        if (rows.Any(x => x.Bib is not null)) { columns.Add(new("Bib", 0.5f, x => ReportComponents.Number(x.Bib))); }
        columns.AddRange([new("Code", 0.9f, x => x.Athlete.FederationCode ?? ""), new("Name", 2.8f, Name),
            new("Year", 0.7f, x => ReportComponents.Number(x.Athlete.BirthYear)), new("Nation", 0.7f, x => x.Athlete.Nation ?? ""),
            new("Club", 1.5f, x => x.Athlete.Club ?? "")]);
        if (rows.Any(x => x.Category is not ("" or CategoryResolver.Unclassified))) { columns.Add(new("Category", 1.3f, x => x.Category)); }
        if (data.Descriptor.CompetitionId is null)
        {
            // Series entries mark each competition the entrant participates in, in race order.
            var participations = data.Source.Desk.Participations.Where(p => p.Participates).Select(p => (p.CompetitorId, p.CompetitionId)).ToHashSet();
            foreach (var race in data.Source.Series.Competitions.OrderBy(x => x.Values.Date).ThenBy(x => x.Values.ShortLabel, StringComparer.Ordinal).ThenBy(x => x.Id))
            { columns.Add(new(race.Values.ShortLabel, 0.8f, x => participations.Contains((x.Id, race.Id)) ? "X" : "")); }
        }
        return columns.ToArray();
    }
}

internal static class OfficialResultsDocument
{
    public static void Compose(ColumnDescriptor column, PdfReportData data)
    {
        if (data.Source.Information is { } information)
        {
            column.Item().Text("Category: " + information.Category);
            foreach (var official in information.Jury.Where(x => x.Person.LastName.Length > 0))
            { column.Item().Text(official.Function + ": " + official.Person.LastName + " " + official.Person.FirstName + " " + official.Person.Nation); }
            foreach (var run in information.Runs)
            { column.Item().Text($"Run {run.Number} | {run.Course} | Gates {run.Gates} / Turns {run.TurningGates} | Start {run.StartTime} | Homologation {run.Homologation}"); }
        }
        foreach (var final in data.Source.Finals)
        {
            ReportComponents.Heading(column, GenderLabels.Format(final.Race.FirstList.Plan.Gender));
            ReportComponents.Table(column.Item(), ["Rank", "Bib", "Code", "Name", "Year", "Nation", "Run 1", "Run 2", "Total", "Status", "Race pts"],
                final.Race.Rows.OrderByOfficialResult(x => x.Rank, x => x.Entry.Bib).ThenBy(x => x.StatusRun).ThenBy(x => x.Entry.Position).Select(x => new[]
                { ReportComponents.Number(x.Rank), ReportComponents.Number(x.Entry.Bib), x.Entry.Entrant.Athlete.FederationCode ?? "",
                    x.Entry.Entrant.Athlete.Surname + " " + x.Entry.Entrant.Athlete.FirstName, ReportComponents.Number(x.Entry.Entrant.Athlete.BirthYear),
                    x.Entry.Entrant.Athlete.Nation ?? "", TimingTime.Format(x.Run1Hundredths), TimingTime.Format(x.Run2Hundredths),
                    TimingTime.Format(x.TotalHundredths), x.Status == TimingStatus.Finished ? "" : x.Status + " R" + x.StatusRun,
                    final.Penalty is { } p && p.RacePoints.TryGetValue(x.Entry.Bib, out var rp) ? rp.ToString("0.00", CultureInfo.InvariantCulture) : "" }),
                [0.5f, 0.5f, 0.9f, 2.8f, 0.7f, 0.7f, 0.9f, 0.9f, 0.9f, 0.9f, 0.8f]);
            var approval = final.Approval ?? throw new InvalidOperationException("Current result approval is unavailable.");
            column.Item().Text("Applied penalty: " + approval.AppliedPenalty.ToString("0.00", CultureInfo.InvariantCulture));
            column.Item().Text("TD approval revision " + approval.Revision + " | " + approval.ApprovedAt.ToString("yyyy-MM-dd HH:mm zzz", CultureInfo.InvariantCulture));
        }
        ReportComponents.ManualLine(column, "Technical delegate signature (manual)");
    }
}

internal static class PenaltyCalculationDocument
{
    public static void Compose(ColumnDescriptor column, PdfReportData data)
    {
        foreach (var final in data.Source.Finals)
        {
            var penalty = final.Penalty ?? throw new InvalidOperationException("Penalty calculation is unavailable.");
            ReportComponents.Heading(column, GenderLabels.Format(final.Race.FirstList.Plan.Gender) + " | FIS list " + final.Race.FirstList.Plan.PointsList.Code);
            ReportComponents.Heading(column, "Top ten classified");
            ReportComponents.Table(column.Item(), ["Rank", "Bib", "Name", "Listed points", "Race points", "Selected for A / C"],
                final.Race.Rows.Where(x => x.Rank <= 10).OrderByOfficialResult(x => x.Rank, x => x.Entry.Bib).Select(x => new[]
                { ReportComponents.Number(x.Rank), ReportComponents.Number(x.Entry.Bib), x.Entry.Entrant.Athlete.Surname + " " + x.Entry.Entrant.Athlete.FirstName,
                    ReportComponents.Number(x.Entry.Entrant.Points), ReportComponents.Number(penalty.RacePoints.GetValueOrDefault(x.Entry.Bib)),
                    penalty.BestClassified.Any(p => p.Competitor.Bib == x.Entry.Bib) ? "Yes" : "" }));
            foreach (var section in new[] { ("Best five classified among the top ten", penalty.BestClassified), ("Best five started", penalty.BestStarted) })
            {
                ReportComponents.Heading(column, section.Item1);
                ReportComponents.Table(column.Item(), ["Bib", "Name", "Listed points", "Used points", "Race points", "Note"], section.Item2.Select(x => new[]
                { ReportComponents.Number(x.Competitor.Bib), x.Competitor.Entry.Entrant.Athlete.Surname + " " + x.Competitor.Entry.Entrant.Athlete.FirstName,
                    ReportComponents.Number(x.Competitor.ListedPoints), ReportComponents.Number(x.UsedPoints), ReportComponents.Number(x.RacePoints), x.SubstitutedMaximum ? "Points cap used" : "" }));
            }
            column.Item().PaddingTop(8).Text(string.Create(CultureInfo.InvariantCulture,
                $"A = {penalty.SumA:0.00}   B = {penalty.SumB:0.00}   C = {penalty.SumC:0.00}\nCalculated penalty = {penalty.Calculated:0.00}\nCorrection Z = {penalty.Rules.Correction:0.00}   Adder = {penalty.Rules.Adder:0.00}\nMinimum = {penalty.Rules.Minimum:0.00}   Maximum = {penalty.Rules.Maximum:0.00}\nApplied penalty = {penalty.Applied:0.00}   F = {penalty.FValue}   Points cap = {penalty.MaximumPoints:0.00}\nDouble minimum: {penalty.DoubleMinimum}"));
        }
        ReportComponents.ManualLine(column, "Technical delegate signature (manual)");
    }
}
