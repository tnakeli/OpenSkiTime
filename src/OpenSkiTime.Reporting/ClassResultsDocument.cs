using System.Globalization;
using OpenSkiTime.Application;
using OpenSkiTime.Timing;
using QuestPDF.Fluent;

namespace OpenSkiTime.Reporting;

/// <summary>
/// The results of a Club race in one document: the jury and the course of every run, then every class with its ranked
/// competitors and, below them, those who did not start, did not finish or were disqualified, by run.
/// </summary>
internal static class ClassResultsDocument
{
    // Races with many runs show the total only; the time of every run would not fit the page.
    private const int MaxRunColumns = 4;

    public static void Compose(ColumnDescriptor column, PdfReportData data)
    {
        var source = data.Source;
        var runCount = source.Competition?.Values.RunCount ?? 1;
        var showRuns = runCount <= MaxRunColumns;
        if (!ClassResultsBuilder.IsComplete(source))
        { column.Item().Text("PRELIMINARY - not every run is finished. Current timing results.").Bold(); }
        if (source.Information is { } information) { RaceInformationBlock(column, information, showRuns); }
        var groups = ClassResultsBuilder.Build(source);
        if (groups.Count == 0) { column.Item().Text("No results."); }
        var showCode = groups.SelectMany(x => x.Rows).Any(x => !string.IsNullOrWhiteSpace(x.Athlete.FederationCode));
        foreach (var group in groups)
        {
            ReportComponents.Heading(column, group.Class.Length > 0 ? group.Class : "Other");
            if (group.Classified.Any())
            {
                ReportComponents.Table(column.Item(), Headers(runCount, showRuns, showCode, ranked: true), group.Classified.Select(x => Row(x, showRuns, showCode, ranked: true)),
                    Widths(runCount, showRuns, showCode));
            }
            foreach (var other in group.Others)
            {
                column.Item().PaddingTop(4).Text(Title(other.Key.Outcome, other.Key.Run) + " (" + other.Count().ToString(CultureInfo.InvariantCulture) + ")").SemiBold();
                ReportComponents.Table(column.Item(), Headers(runCount, showRuns, showCode, ranked: false), other.Select(x => Row(x, showRuns, showCode, ranked: false)),
                    Widths(runCount, showRuns, showCode));
            }
        }
        var all = groups.SelectMany(x => x.Rows).ToArray();
        var nations = all.Select(x => (x.Athlete.Nation ?? "").Trim().ToUpperInvariant()).Where(x => x.Length > 0).Distinct().Count();
        column.Item().PaddingTop(8).Text($"Number of competitors {all.Length.ToString(CultureInfo.InvariantCulture)}, number of nations {nations.ToString(CultureInfo.InvariantCulture)}");
        var series = source.Series.Values;
        column.Item().Text($"{(source.Competition?.Values.Date ?? series.StartDate).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)} / {series.Location} ({series.Nation}) / {series.Organizer}").FontSize(7);
    }

    private static string Title(TimingStatus? outcome, int run) => outcome switch
    {
        TimingStatus.DNS => $"Did Not Start - Run {run}",
        TimingStatus.DNF => $"Did Not Finish - Run {run}",
        TimingStatus.DSQ => $"Disqualified - Run {run}",
        TimingStatus.NPS => $"Not Permitted to Start - Run {run}",
        _ => "No result",
    };

    private static string[] Headers(int runCount, bool showRuns, bool showCode, bool ranked)
    {
        var headers = new List<string> { "Rank", "Bib" };
        if (showCode) { headers.Add("ID"); }
        headers.AddRange(["Name", "Year", "Club"]);
        if (showRuns) { for (var run = 1; run <= runCount; run++) { headers.Add("Run " + run); } }
        headers.Add(ranked ? "Time" : "");
        return headers.ToArray();
    }

    private static float[] Widths(int runCount, bool showRuns, bool showCode)
    {
        var widths = new List<float> { 0.5f, 0.5f };
        if (showCode) { widths.Add(1f); }
        widths.AddRange([2.8f, 0.6f, 1.8f]);
        if (showRuns) { for (var run = 1; run <= runCount; run++) { widths.Add(0.9f); } }
        widths.Add(1f);
        return widths.ToArray();
    }

    private static object[] Row(ClassResultRow x, bool showRuns, bool showCode, bool ranked)
    {
        var cells = new List<object> { ranked ? ReportComponents.Number(x.Rank) : "", ReportComponents.Number(x.Bib) };
        if (showCode) { cells.Add(x.Athlete.FederationCode ?? ""); }
        cells.AddRange([new PersonName(x.Athlete.Surname, x.Athlete.FirstName), ReportComponents.Number(x.Athlete.BirthYear), x.Athlete.Club ?? ""]);
        if (showRuns)
        {
            for (var i = 0; i < x.RunTimes.Count; i++)
            { cells.Add(!ranked && i + 1 == x.OutcomeRun && x.Detail.Length > 0 ? x.Detail : x.RunTimes[i]); }
        }
        cells.Add(ranked ? TimingTime.Format(x.TotalHundredths) : "");
        return cells.ToArray();
    }

    // The jury, then one column per run with the course setter, forerunners, course data, start time and weather.
    private static void RaceInformationBlock(ColumnDescriptor column, RaceInformation information, bool showRuns)
    {
        var jury = information.Jury.Where(x => x.Person.LastName.Length > 0).ToArray();
        if (jury.Length > 0)
        {
            column.Item().PaddingTop(4).Text("Competition jury").SemiBold();
            foreach (var official in jury)
            {
                column.Item().Text(text =>
                {
                    text.Span(RaceInformation.JuryFunctionLabel(official.Function) + ": ");
                    ReportComponents.Person(text, new(official.Person.LastName, official.Person.FirstName, official.Person.Nation));
                });
            }
        }
        if (!showRuns || information.Runs.Count == 0) { return; }
        string Person(FisPerson p) => (p.FirstName + " " + p.LastName).Trim();
        string Meters(int? value) => value is { } v ? v.ToString(CultureInfo.InvariantCulture) + " m" : "";
        string Count(int? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "";
        string Degrees(decimal? value) => value is { } v ? v.ToString("0.0", CultureInfo.InvariantCulture) + " °C" : "";
        var lines = new (string Label, Func<RaceRunInformation, string> Value)[]
        {
            ("Course", r => r.Course), ("Course setter", r => Person(r.CourseSetter)),
            ("Forerunners", r => string.Join(", ", (r.Forerunners ?? []).Select(f => f.Letter + " " + Person(f.Person)))),
            ("Start", r => Meters(r.StartAltitude)), ("Finish", r => Meters(r.FinishAltitude)), ("Drop", r => Meters(r.Drop)), ("Length", r => Meters(r.Length)),
            ("Gates", r => Count(r.Gates)), ("Turns", r => Count(r.TurningGates)), ("Starting time", r => r.StartTime),
            ("Conditions", r => r.Weather?.Conditions ?? ""), ("Snow", r => r.Weather?.Snow ?? ""),
            ("Air temperature start", r => Degrees(r.Weather?.StartTemperature)), ("Air temperature finish", r => Degrees(r.Weather?.FinishTemperature)),
        };
        var rows = lines.Where(l => information.Runs.Any(r => l.Value(r).Length > 0))
            .Select(l => (object[])[l.Label, .. information.Runs.Select(r => (object)l.Value(r))]).ToArray();
        if (rows.Length == 0) { return; }
        column.Item().PaddingTop(6).Text("Courses").SemiBold();
        string[] runHeaders = ["", .. information.Runs.Select(r => "Run " + r.Number.ToString(CultureInfo.InvariantCulture))];
        float[] runWidths = [1.6f, .. information.Runs.Select(_ => 2f)];
        ReportComponents.Table(column.Item(), runHeaders, rows, runWidths);
    }
}
