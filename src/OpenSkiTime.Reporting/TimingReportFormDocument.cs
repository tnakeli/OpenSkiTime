using System.Globalization;
using OpenSkiTime.Application;
using OpenSkiTime.Domain;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace OpenSkiTime.Reporting;

/// <summary>
/// FIS "Timing &amp; Data Technical Report Alpine" form. The layout reproduces the FIS Timing Report
/// application's A4 PDF (reviewed against a version 6.7.1 print); see docs/fis-report-branding.md.
/// The XML remains the document transmitted to FIS; this PDF is the human-readable copy.
/// </summary>
internal static class TimingReportFormDocument
{
    private const string Grey = "#E3E3E3";
    private const string SeasonHighlight = "#FFF79A";
    private const string NoticeRed = "#E00000";
    private const float Line = 0.6f;
    // Column boundaries of the reference form, in its own units, shared by every grid section.
    private static readonly float[] s_grid = [177, 67, 33, 49, 48, 99, 69, 30, 48, 50, 100];

    public static IDocument Create(PdfReportData data)
    {
        var draft = data.Source.TimingReport?.Values ?? Fallback(data);
        return Document.Create(document => document.Page(page =>
        {
            page.Size(PageSizes.A4);
            page.PageColor(Colors.White);
            page.MarginTop(10, Unit.Millimetre); page.MarginBottom(10, Unit.Millimetre);
            page.MarginHorizontal(17, Unit.Millimetre);
            page.DefaultTextStyle(x => x.FontSize(7.5f).FontColor(Colors.Black));
            page.Header().Column(column =>
            {
                column.Item().Row(row =>
                {
                    row.ConstantItem(70).Height(62).Svg(RefereeReportDocument.FisLogo).FitArea();
                    row.RelativeItem().AlignMiddle().AlignCenter().Text("Timing & Data Technical Report Alpine").Bold().FontSize(15);
                    row.ConstantItem(70);
                });
                column.Item().PaddingTop(10).AlignCenter().Text(text =>
                {
                    text.AlignCenter(); text.DefaultTextStyle(x => x.FontColor(NoticeRed).FontSize(7));
                    text.Line("To accompany the TD report please transmit immediately only as XML and NOT as PDF or NOT in paper format to FIS.");
                    text.Span("One timing report required for each codex.");
                });
            });
            page.Content().PaddingTop(10).Column(column =>
            {
                column.Spacing(6);
                column.Item().Element(c => RaceHeader(c, draft));
                column.Item().Element(c => Devices(c, draft));
                column.Item().Element(c => SupportDevices(c));
                column.Item().Element(c => Software(c));
                column.Item().Element(c => Connections(c, draft));
                column.Item().Element(c => Synchronization(c, draft));
                // The form holds two runs; a third slalom run continues in a second timing table.
                for (var index = 0; index < Math.Max(1, (draft.Runs.Length + 1) / 2); index++)
                {
                    var pair = draft.Runs.OrderBy(x => x.Run).Skip(index * 2).Take(2).ToArray();
                    column.Item().Element(c => Timing(c, pair, index * 2 + 1));
                }
                column.Item().Element(c => AllResults(c, draft));
                column.Item().Element(c => Comments(c, draft));
                column.Item().Element(c => Certification(c, draft));
            });
            page.Footer().Row(row =>
            {
                row.RelativeItem().Text("Created at " + data.GeneratedAt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " (" + ProductInfo.Version + ")").FontSize(7);
                row.RelativeItem().AlignCenter().Text(text => { text.DefaultTextStyle(x => x.FontSize(7)); text.Span("page "); text.CurrentPageNumber(); text.Span(" of "); text.TotalPages(); });
                row.RelativeItem().AlignRight().Text("Timing Report version used " + ProductInfo.DisplayName).FontSize(7);
            });
        }));
    }

    /// <summary>Unsaved report: the form still shows the race identity, leaving device and timing cells blank.</summary>
    private static TimingReportDraft Fallback(PdfReportData data)
    {
        var race = data.Source.Competition?.Values ?? throw new ArgumentException("A timing report requires a competition.", nameof(data));
        var calendar = race.Calendar;
        return new TimingReportDraft
        {
            Header = new(calendar?.Season ?? race.Date.Year, race.FisCode ?? "", calendar?.Nation ?? data.Source.Series.Values.Nation,
                Discipline(race.Discipline), data.Source.Information?.Category ?? calendar?.Category ?? "", calendar?.Gender ?? "",
                data.Source.Series.Values.Name, calendar?.Location ?? data.Source.Series.Values.Location, race.Date)
        };
    }

    private static string Discipline(Discipline value) => value switch
    {
        Domain.Discipline.Slalom => "SL", Domain.Discipline.GiantSlalom => "GS", Domain.Discipline.SuperG => "SG",
        Domain.Discipline.Downhill => "DH", Domain.Discipline.AlpineCombined => "AC", _ => ""
    };
    private static string DisciplineName(string code) => code switch
    { "SL" => "Slalom", "GS" => "Giant Slalom", "SG" => "Super G", "DH" => "Downhill", "AC" => "Alpine Combined", _ => code };
    private static string GenderName(string code) => code switch { "M" => "Men", "W" or "L" => "Women", "A" => "Mixed", _ => code };
    private static string Dash(string value) => string.IsNullOrWhiteSpace(value) ? "-" : value;
    private static string Stamp(TimingReportStamp? stamp) => stamp is null ? "" : TimingReportXml.FormatStamp(stamp);
    private static string Net(long? hundredths) => hundredths is { } value ? TimingReportXml.FormatNet(value) : "";
    private static string Person(TimingReportPerson p, bool firstNameFirst)
    {
        var name = firstNameFirst ? p.FirstName + " " + p.LastName : p.LastName + " " + p.FirstName;
        name = name.Trim();
        return name.Length == 0 ? "" : p.Nation.Length > 0 ? $"{name} ({p.Nation})" : name;
    }

    private static IContainer Box(IContainer container, string? background = null)
    {
        container = container.Border(Line);
        if (background is not null) { container = container.Background(background); }
        return container.PaddingHorizontal(2).PaddingVertical(1.5f).AlignMiddle();
    }
    private static void Label(IContainer container, string text) => Box(container).Text(text).Bold();
    private static void Value(IContainer container, string text, bool right = false)
    {
        var box = Box(container);
        if (right) { box = box.AlignRight(); }
        box.Text(text);
    }
    private static void Heading(IContainer container, string text) => Box(container).AlignCenter().Text(text).Bold();
    private static void Blank(IContainer container) => Box(container, Grey).Text("");

    private static void Grid(IContainer container, Action<TableDescriptor> content) => container.Table(table =>
    {
        table.ColumnsDefinition(columns => { foreach (var width in s_grid) { columns.RelativeColumn(width); } });
        content(table);
    });

    private static void RaceHeader(IContainer container, TimingReportDraft draft)
    {
        var h = draft.Header;
        container.Border(Line).Row(row =>
        {
            row.RelativeItem(617).Padding(2).Table(table =>
            {
                table.ColumnsDefinition(columns => { columns.RelativeColumn(77); columns.RelativeColumn(157); columns.RelativeColumn(120); columns.RelativeColumn(263); });
                void Pair(string label, string value, string label2, string value2)
                {
                    table.Cell().Text(label).Bold(); table.Cell().Text(value);
                    table.Cell().AlignCenter().Text(label2).Bold(); table.Cell().Text(value2);
                }
                Pair("Location", h.Place, "Category", Dash(h.Category));
                Pair("Nation", h.Nation, "Competition", DisciplineName(h.Discipline));
                Pair("Event name", h.EventName, "Gender", GenderName(h.Gender));
                table.Cell().Text("Competition\nDate").Bold();
                table.Cell().AlignBottom().Text(h.Date == default ? "" : h.Date.ToString("dd.MM.yy", CultureInfo.InvariantCulture));
                table.Cell().ColumnSpan(2);
            });
            row.RelativeItem(153).BorderLeft(Line).Padding(2).Column(column =>
            {
                column.Item().Row(r =>
                {
                    r.RelativeItem().AlignMiddle().Text("Season").Bold();
                    r.ConstantItem(42).Background(SeasonHighlight).AlignCenter().Text(h.Season > 0 ? h.Season.ToString(CultureInfo.InvariantCulture) : "").Bold().FontSize(12);
                });
                column.Item().PaddingTop(10).Row(r =>
                {
                    r.RelativeItem().AlignMiddle().Text("Competition\nCodex").Bold();
                    r.ConstantItem(42).AlignCenter().Text(h.Codex).Bold().FontSize(12);
                });
            });
        });
    }

    private static void Devices(IContainer container, TimingReportDraft draft)
    {
        var d = draft.Defaults;
        Grid(container, table =>
        {
            Label(table.Cell(), "TIMING DEVICES");
            Heading(table.Cell().ColumnSpan(3), "Brand/Company"); Heading(table.Cell().ColumnSpan(2), "Model");
            Heading(table.Cell().ColumnSpan(3), "Serial No"); Heading(table.Cell().ColumnSpan(2), "Homologation No");
            foreach (var (label, device) in new (string, TimingReportDevice?)[]
            {
                ("System A Timer (at finish)", d.TimerA), ("System B Timer (at finish)", d.TimerB),
                ("Timer A Start (if used)", d.TimerStartA), ("Timer B Start (if used)", d.TimerStartB),
                ("Start Device", d.StartDevice), ("Finish Cell A", d.FinishCellsA), ("Finish Cell B", d.FinishCellsB),
                ("Photo Finish A", null), ("Photo Finish B", null)
            })
            {
                Label(table.Cell(), label);
                Value(table.Cell().ColumnSpan(3), device is null ? "-" : Dash(device.Brand));
                Value(table.Cell().ColumnSpan(2), device?.Model ?? ""); Value(table.Cell().ColumnSpan(3), device?.Serial ?? "");
                Value(table.Cell().ColumnSpan(2), device?.Homologation ?? "");
            }
        });
    }

    private static void SupportDevices(IContainer container) => Grid(container, table =>
    {
        Label(table.Cell(), "TIMING SUPPORT DEVICES");
        Heading(table.Cell().ColumnSpan(3), "Brand/Company"); Heading(table.Cell().ColumnSpan(2), "Model");
        Heading(table.Cell().ColumnSpan(3), "Specifications"); Blank(table.Cell().ColumnSpan(2));
        Label(table.Cell(), "Video finish"); Value(table.Cell().ColumnSpan(3), "-"); Value(table.Cell().ColumnSpan(2), "");
        Value(table.Cell().ColumnSpan(2), ""); Value(table.Cell(), ""); Blank(table.Cell().ColumnSpan(2));
    });

    private static void Software(IContainer container) => Grid(container, table =>
    {
        Label(table.Cell().RowSpan(2), "Result software");
        Heading(table.Cell().ColumnSpan(4), "Software company"); Heading(table.Cell().ColumnSpan(3), "Software name/version"); Blank(table.Cell().ColumnSpan(3));
        Value(table.Cell().ColumnSpan(4), ProductInfo.Name); Value(table.Cell().ColumnSpan(3), ProductInfo.DisplayName); Blank(table.Cell().ColumnSpan(3));
    });

    private static void Connections(IContainer container, TimingReportDraft draft) => Grid(container, table =>
    {
        Label(table.Cell().RowSpan(2), "Connection to start");
        Heading(table.Cell().ColumnSpan(4), "System A"); Heading(table.Cell().ColumnSpan(3), "System B"); Heading(table.Cell().ColumnSpan(3), "Voice connection");
        Value(table.Cell().ColumnSpan(4), draft.Defaults.ConnectionA); Value(table.Cell().ColumnSpan(3), draft.Defaults.ConnectionB);
        Value(table.Cell().ColumnSpan(3), draft.Defaults.Voice);
    });

    private static void Synchronization(IContainer container, TimingReportDraft draft) => Grid(container, table =>
    {
        Label(table.Cell(), "SYNCHRONIZATION");
        Heading(table.Cell().ColumnSpan(2), "System A\n(at finish)"); Heading(table.Cell().ColumnSpan(2), "System B\n(at finish)");
        Heading(table.Cell(), "Hand"); Blank(table.Cell().ColumnSpan(2)); Blank(table.Cell().ColumnSpan(2)); Blank(table.Cell());
        // One electronic synchronization covers A and B, as in the FIS form and XML.
        Label(table.Cell(), "Syncronization time");
        Value(table.Cell().ColumnSpan(2), Stamp(draft.Sync), right: true); Value(table.Cell().ColumnSpan(2), "");
        Value(table.Cell(), Stamp(draft.HandSync), right: true); Blank(table.Cell().ColumnSpan(2)); Blank(table.Cell().ColumnSpan(2)); Blank(table.Cell());
        Label(table.Cell(), "Syncronization confirmation");
        Value(table.Cell().ColumnSpan(2), Stamp(draft.SyncCheckA), right: true); Value(table.Cell().ColumnSpan(2), Stamp(draft.SyncCheckB), right: true);
        Blank(table.Cell()); Blank(table.Cell().ColumnSpan(2)); Blank(table.Cell().ColumnSpan(2)); Blank(table.Cell());
    });

    private static void Timing(IContainer container, TimingReportRun[] runs, int firstRun) => Grid(container, table =>
    {
        Label(table.Cell(), "TIMING");
        Heading(table.Cell().ColumnSpan(5), Ordinal(firstRun) + " Run"); Heading(table.Cell().ColumnSpan(5), Ordinal(firstRun + 1) + " Run");
        Box(table.Cell()).Text("Time of day (TOD) expressed in precision used for net time calculations equal to the precision of the timing device.").FontSize(5.5f);
        foreach (var span in new[] { (2, 2, 1), (2, 2, 1) })
        {
            Heading(table.Cell().ColumnSpan((uint)span.Item1), "System A"); Heading(table.Cell().ColumnSpan((uint)span.Item2), "System B");
            Heading(table.Cell().ColumnSpan((uint)span.Item3), "Hand");
        }
        void Stamps(string label, Func<TimingReportRun, (TimingReportStamp? A, TimingReportStamp? B, TimingReportStamp? Hand)> select)
        {
            Label(table.Cell(), label);
            for (var i = 0; i < 2; i++)
            {
                var value = i < runs.Length ? select(runs[i]) : (null, null, null);
                Value(table.Cell().ColumnSpan(2), Stamp(value.A), right: true); Value(table.Cell().ColumnSpan(2), Stamp(value.B), right: true);
                Value(table.Cell(), Stamp(value.Hand), right: true);
            }
        }
        void NetRow(string label, Func<TimingReportRun, (long? Net, int? Bib)> select)
        {
            Label(table.Cell(), label);
            for (var i = 0; i < 2; i++)
            {
                var value = i < runs.Length ? select(runs[i]) : (null, null);
                Value(table.Cell(), Net(value.Net)); Value(table.Cell(), ReportComponents.Number(value.Bib), right: true);
                Blank(table.Cell().ColumnSpan(2)); Blank(table.Cell());
            }
        }
        Stamps("Start TOD First", r => (r.First.AStart, r.First.BStart, r.First.HandStart));
        Stamps("Finish TOD First", r => (r.First.AFinish, r.First.BFinish, r.First.HandFinish));
        NetRow("Net Time System A / BIB First", r => (r.First.NetHundredths, r.First.Bib));
        Stamps("Start TOD Last", r => (r.Last.AStart, r.Last.BStart, r.Last.HandStart));
        Stamps("Finish TOD Last", r => (r.Last.AFinish, r.Last.BFinish, r.Last.HandFinish));
        NetRow("Net Time System A / BIB Last", r => (r.Last.NetHundredths, r.Last.Bib));
        NetRow("Net Time System A / BIB Best", r => (r.BestHundredths, r.BestBib));
    });
    private static string Ordinal(int run) => run switch { 1 => "1st", 2 => "2nd", 3 => "3rd", _ => run.ToString(CultureInfo.InvariantCulture) + "th" };

    private static void YesNo(TableDescriptor table, bool? yes)
    {
        Label(table.Cell(), "Yes"); Box(table.Cell()).AlignCenter().Text(yes == true ? "X" : "");
        Label(table.Cell(), "No"); Box(table.Cell(), Grey).AlignCenter().Text(yes == false ? "X" : "");
    }

    private static void AllResults(IContainer container, TimingReportDraft draft) => container.Table(table =>
    {
        table.ColumnsDefinition(columns => { foreach (var width in new[] { 232f, 30, 30, 35, 25, 418 }) { columns.RelativeColumn(width); } });
        Label(table.Cell(), "Were all results from system A?");
        YesNo(table, draft.Runs.Length == 0 ? null : draft.Runs.All(x => x.AllResultsA));
        Blank(table.Cell());
    });

    private static void Comments(IContainer container, TimingReportDraft draft) => container.Table(table =>
    {
        table.ColumnsDefinition(columns => { columns.RelativeColumn(); columns.RelativeColumn(); });
        var runs = draft.Runs.OrderBy(x => x.Run).ToArray();
        for (var first = 0; first < Math.Max(2, runs.Length); first += 2)
        {
            for (var i = first; i < first + 2; i++) { Label(table.Cell(), "Comments run " + (i + 1).ToString(CultureInfo.InvariantCulture)); }
            for (var i = first; i < first + 2; i++)
            {
                var run = i < runs.Length ? runs[i] : null;
                // Backup-time replacements are part of the run declaration and stay visible on the paper copy.
                var lines = run is null ? [] : new[] { run.Comment }.Concat(run.MissedA.Select(m => $"Bib {m.Bib}: {m.Reason} (time from {m.TimeFrom})"))
                    .Where(x => !string.IsNullOrWhiteSpace(x)).ToArray();
                table.Cell().BorderHorizontal(Line).BorderLeft(i == first ? Line : 0).BorderRight(Line).MinHeight(6).PaddingHorizontal(2).Text(string.Join("\n", lines));
            }
        }
    });

    private static void Certification(IContainer container, TimingReportDraft draft)
    {
        var d = draft.Defaults;
        container.Column(column =>
        {
            column.Item().Table(table =>
            {
                table.ColumnsDefinition(columns => { foreach (var width in new[] { 497f, 30, 25, 25, 25, 168 }) { columns.RelativeColumn(width); } });
                Label(table.Cell(), "We certify that the timing and calculations of this event adhered to the FIS rules.");
                YesNo(table, draft.Runs.Length == 0 && !draft.CertifyFis ? null : draft.CertifyFis);
                Blank(table.Cell());
            });
            column.Item().Row(row =>
            {
                void Field(TableDescriptor table, string label, string value)
                { table.Cell().Text(label).Bold(); table.Cell().Text(value); }
                row.RelativeItem(255).Border(Line).Column(person =>
                {
                    person.Item().BorderBottom(Line).PaddingHorizontal(2).Text("Technical Delegate").Bold();
                    person.Item().MinHeight(52).PaddingHorizontal(2).Table(table =>
                    {
                        table.ColumnsDefinition(c => { c.ConstantColumn(52); c.RelativeColumn(); });
                        Field(table, "Name/Nation", Person(draft.TechnicalDelegate, firstNameFirst: true));
                        Field(table, "TD Number", draft.TechnicalDelegate.Number);
                    });
                    person.Item().BorderTop(Line).MinHeight(28).PaddingHorizontal(2).Text("Signature").Bold();
                });
                row.RelativeItem(257).BorderVertical(Line).BorderBottom(Line).BorderTop(Line).Column(person =>
                {
                    person.Item().BorderBottom(Line).PaddingHorizontal(2).Text("Chief of Timing and Calculation").Bold();
                    person.Item().MinHeight(52).PaddingHorizontal(2).Table(table =>
                    {
                        table.ColumnsDefinition(c => { c.ConstantColumn(52); c.RelativeColumn(); });
                        Field(table, "Name/Nation", Person(d.ChiefOfTiming, firstNameFirst: false));
                        Field(table, "Telephone", d.ChiefOfTiming.Phone); Field(table, "Email", d.ChiefOfTiming.Email);
                    });
                    person.Item().BorderTop(Line).MinHeight(28).PaddingHorizontal(2).Text("Signature").Bold();
                });
                // The timing company has no signature field on the FIS form.
                row.RelativeItem(258).AlignTop().BorderRight(Line).BorderBottom(Line).BorderTop(Line).Column(person =>
                {
                    person.Item().BorderBottom(Line).PaddingHorizontal(2).Text("Timing Company").Bold();
                    person.Item().MinHeight(52).PaddingHorizontal(2).Table(table =>
                    {
                        table.ColumnsDefinition(c => { c.ConstantColumn(58); c.RelativeColumn(); });
                        Field(table, "Company", d.Timekeeper.Company); Field(table, "Name/Nation", Person(d.Timekeeper, firstNameFirst: false));
                        Field(table, "Telephone", d.Timekeeper.Phone); Field(table, "Email", d.Timekeeper.Email);
                    });
                });
            });
        });
    }
}
