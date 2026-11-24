using OpenSkiTime.Application;
using QuestPDF.Elements;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace OpenSkiTime.Reporting;

/// <summary>A column of a <see cref="ReportTable"/>. Values are text, a <see cref="PersonName"/> or a <see cref="NationCell"/>.</summary>
/// <param name="Header">The heading.</param>
/// <param name="Value">The value of a row.</param>
/// <param name="Grow">The column takes part of the width left over when every value fits on one line.</param>
/// <param name="Mark">A narrow column of marks: the header is printed upwards, the values centred between thin lines.</param>
/// <param name="Numeric">Values are aligned to the right.</param>
internal sealed record ReportColumn<TRow>(string Header, Func<TRow, object> Value, bool Grow = false, bool Mark = false, bool Numeric = false);
internal sealed record ReportSection<TRow>(string Heading, IReadOnlyList<TRow> Rows);

/// <summary>
/// A table whose column widths follow its content: an ordinary value is never broken inside a word. Columns wrap between words
/// when the values do not fit on one line, and the whole table is scaled down when even the words do not fit.
/// All sections share one header, repeated on every page.
/// </summary>
internal static class ReportTable
{
    private const float CellPadding = 3;
    // Room for rounding, so that text measured to fit is never wrapped.
    private const float Slack = 0.5f;
    // One exceptionally long word is broken rather than shrinking the whole table.
    private const float LongestWord = 130;
    private const string Line = "#CCD7DC";

    public static void Compose<TRow>(IContainer container, IReadOnlyList<ReportColumn<TRow>> columns,
        IReadOnlyList<ReportSection<TRow>> sections, float availableWidth, float fontSize)
    {
        var cells = sections.SelectMany(x => x.Rows).Select(row => columns.Select(x => x.Value(row)).ToArray()).ToArray();
        var measured = Measure(columns, cells, fontSize);
        var layout = Layout(measured, columns.Select(x => x.Grow).ToArray(), availableWidth - Slack);
        // The header is repeated on every page; a row of several lines is not split between pages.
        (layout.Scale < 1 ? container.Scale(layout.Scale) : container).Decoration(table =>
        {
            table.Before().Background("#EAF0F2").Row(row =>
            {
                for (var i = 0; i < columns.Count; i++)
                {
                    var column = columns[i];
                    var cell = row.ConstantItem(layout.Widths[i]).Element(x => MarkLine(x, column.Mark)).AlignBottom();
                    if (column.Mark) { cell.AlignCenter().RotateLayoutCounterclockwise().Padding(CellPadding).Text(column.Header).SemiBold(); }
                    else { Align(cell.Padding(CellPadding), column).Text(column.Header).SemiBold(); }
                }
            });
            table.Content().Column(content =>
            {
                var index = 0;
                foreach (var section in sections)
                {
                    for (var i = 0; i < section.Rows.Count; i++)
                    {
                        var row = cells[index++];
                        if (i > 0 || section.Heading.Length == 0) { content.Item().PreventPageBreak().Element(x => Row(x, columns, layout.Widths, row)); continue; }
                        // A heading stays with its first row.
                        content.Item().PreventPageBreak().Column(first =>
                        {
                            first.Item().BorderBottom(0.5f).BorderColor(Line).PaddingTop(8).PaddingBottom(3).PaddingHorizontal(CellPadding)
                                .Text(section.Heading).SemiBold().FontSize(9);
                            first.Item().Element(x => Row(x, columns, layout.Widths, row));
                        });
                    }
                }
            });
        });
    }

    private static void Row<TRow>(IContainer container, IReadOnlyList<ReportColumn<TRow>> columns, float[] widths, object[] values)
        => container.BorderBottom(0.25f).BorderColor(Line).Row(row =>
        {
            for (var i = 0; i < columns.Count; i++)
            {
                var column = columns[i];
                Value(Align(row.ConstantItem(widths[i]).Element(x => MarkLine(x, column.Mark))
                    .PaddingVertical(CellPadding).PaddingHorizontal(column.Mark ? 1 : CellPadding), column), values[i]);
            }
        });

    /// <summary>
    /// Column widths for the available width. <paramref name="columns"/> holds each column's widest word and widest value.
    /// Widths add up to the available width divided by the scale.
    /// </summary>
    internal static (float[] Widths, float Scale) Layout(IReadOnlyList<(float Word, float Value)> columns, IReadOnlyList<bool> grow, float available)
    {
        var words = columns.Sum(x => x.Word);
        if (words > available) { return (columns.Select(x => x.Word).ToArray(), available / words); }
        // Growing columns (names) wrap before a short value or its header does.
        var kept = columns.Select((x, i) => grow[i] ? x.Word : x.Value).ToArray();
        var from = kept.Sum() > available ? columns.Select(x => x.Word).ToArray() : kept;
        var to = kept.Sum() > available ? kept : columns.Select(x => x.Value).ToArray();
        var (least, most) = (from.Sum(), to.Sum());
        if (most > available) { return (from.Select((x, i) => x + (to[i] - x) * (available - least) / (most - least)).ToArray(), 1); }
        var growing = to.Where((_, i) => grow[i]).Sum();
        return (to.Select((x, i) => growing > 0 && grow[i] ? x + (available - most) * x / growing : x).ToArray(), 1);
    }

    private static IContainer MarkLine(IContainer container, bool mark) => mark ? container.BorderLeft(0.25f).BorderColor(Line) : container;
    private static IContainer Align<TRow>(IContainer container, ReportColumn<TRow> column)
        => column.Mark ? container.AlignCenter() : column.Numeric ? container.AlignRight() : container;

    private static void Value(IContainer container, object? value)
    {
        if (value is PersonName person) { container.Text(text => ReportComponents.Person(text, person)); }
        else if (value is NationCell nation) { ReportComponents.Nation(container, nation); }
        else { container.Text(value?.ToString() ?? ""); }
    }

    private static (float Word, float Value)[] Measure<TRow>(IReadOnlyList<ReportColumn<TRow>> columns, object[][] cells, float fontSize)
    {
        var needed = new HashSet<MeasuredText>();
        // The first pass collects the texts, the second one uses their widths.
        (float Word, float Value)[] Widths(Func<MeasuredText, float> width) => columns.Select((column, i) =>
        {
            var values = cells.Select(row => Parts(row[i])).ToArray();
            if (column.Mark)
            {
                // The header is printed upwards: its line height is the width it takes.
                var content = values.SelectMany(x => x.Words).Select(width).Append(fontSize * 1.2f).Max() + 2 * CellPadding + Slack;
                return (content, content);
            }
            var header = new MeasuredText(column.Header, "");
            var words = values.SelectMany(x => x.Words).Concat(Words(column.Header, bold: true)).Distinct().Select(width).Max();
            var full = values.Select(x => x.Full).Append(header).Distinct().Select(width).Max();
            var extra = 2 * CellPadding + Slack + (cells.Any(row => row[i] is NationCell) ? ReportComponents.NationFlagWidth + ReportComponents.NationSpacing : 0);
            return (Math.Min(words, LongestWord) + extra, Math.Max(words, full) + extra);
        }).ToArray();
        Widths(text => { needed.Add(text); return 0; });
        var measurement = new TextMeasurement(needed);
        // Layout needs the widths of the fonts the report is printed with; only a layout pass of the engine knows them.
        Document.Create(document => document.Page(page =>
        {
            page.Size(PageSizes.A4);
            page.DefaultTextStyle(x => x.FontSize(fontSize));
            page.Content().Dynamic(measurement);
        })).GeneratePdf();
        return Widths(text => measurement.Widths[text]);
    }

    private static (MeasuredText Full, IEnumerable<MeasuredText> Words) Parts(object? value)
    {
        if (value is PersonName person)
        {
            var rest = string.Concat(new[] { person.FirstName, person.Suffix }.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => " " + x));
            var surname = person.Surname.ToUpperInvariant();
            return (new(surname, rest), Words(surname, bold: true).Concat(Words(rest, bold: false)));
        }
        var text = value is NationCell nation ? NationFlags.Normalize(nation.Code) : value?.ToString() ?? "";
        return (new("", text), Words(text, bold: false));
    }

    // Lines may break at white space and after a hyphen.
    private static IEnumerable<MeasuredText> Words(string text, bool bold)
        => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .SelectMany(word => word.Replace("-", "-\n", StringComparison.Ordinal).Split('\n', StringSplitOptions.RemoveEmptyEntries))
            .Select(word => bold ? new MeasuredText(word, "") : new MeasuredText("", word));

    /// <summary>A single line: an optional bold part, then a regular part.</summary>
    private sealed record MeasuredText(string BoldPart, string RegularPart);

    private sealed class TextMeasurement(IReadOnlyCollection<MeasuredText> texts) : IDynamicComponent
    {
        public Dictionary<MeasuredText, float> Widths { get; } = [];
        public DynamicComponentComposeResult Compose(DynamicContext context)
        {
            foreach (var text in texts)
            {
                if (Widths.ContainsKey(text)) { continue; }
                Widths[text] = text.BoldPart.Length + text.RegularPart.Length == 0 ? 0 : context.CreateElement(x => x.Text(block =>
                {
                    if (text.BoldPart.Length > 0) { block.Span(text.BoldPart).Bold(); }
                    if (text.RegularPart.Length > 0) { block.Span(text.RegularPart); }
                })).Size.Width;
            }
            return new() { Content = context.CreateElement(_ => { }), HasMoreContent = false };
        }
    }
}
