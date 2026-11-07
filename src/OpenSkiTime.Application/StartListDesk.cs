using System.Globalization;
using System.Net;
using System.Text;
using OpenSkiTime.Domain;

namespace OpenSkiTime.Application;

public sealed record StartListDesk(long SeriesRevision, IReadOnlyList<StartListRevision> Revisions);
public sealed record SaveStartList(StartListPlan Plan, long ExpectedRevision, string Operator, string Reason, DateTimeOffset At,
    string? ExpectedTimingVersion = null);

public static class StartListExchange
{
    public static string ToTsv(StartListRevision revision)
    {
        ArgumentNullException.ThrowIfNull(revision);
        var laterRun = revision.Plan.RunNumber > 1;
        var sourceTimes = revision.Plan.SourceResults.ToDictionary(x => x.CompetitorId, x => x.Hundredths);
        var withClass = revision.Plan.Entries.Any(x => x.Entrant.Class.Length > 0);
        var output = new StringBuilder("Position\tBib\tCode\tSurname\tFirst name\tYear\tNation");
        if (withClass) { output.Append("\tClass"); }
        if (laterRun) { output.Append("\tRun 1 time"); }
        output.Append("\tFIS points\n");
        foreach (var entry in revision.Plan.Entries)
        {
            var a = entry.Entrant.Athlete;
            var values = new List<string?> { entry.Position.ToString(CultureInfo.InvariantCulture),
                entry.Bib.ToString(CultureInfo.InvariantCulture), a.FederationCode, a.Surname, a.FirstName,
                a.BirthYear?.ToString(CultureInfo.InvariantCulture), a.Nation };
            if (withClass) { values.Add(entry.Entrant.Class); }
            if (laterRun) { values.Add(sourceTimes.TryGetValue(entry.Entrant.CompetitorId, out var time) ? RunResultInput.FormatTime(time) : ""); }
            values.Add(entry.Entrant.Points?.ToString("0.00", CultureInfo.InvariantCulture));
            output.AppendLine(string.Join('\t', values.Select(TsvCell)));
        }
        return output.ToString();
    }

    public static string ToPrintHtml(StartListRevision revision)
    {
        ArgumentNullException.ThrowIfNull(revision);
        var p = revision.Plan;
        var laterRun = p.RunNumber > 1;
        var sourceTimes = p.SourceResults.ToDictionary(x => x.CompetitorId, x => x.Hundredths);
        var withClass = p.Entries.Any(x => x.Entrant.Class.Length > 0);
        var heading = $"{p.Competition.ShortLabel} · " + (p.Gender is null ? "" : GenderLabels.Format(p.Gender) + " · ") + $"Run {p.RunNumber}";
        var codexLabel = p.Competition.RaceType == RaceType.Fis ? $" · Codex {p.Competition.FisCode ?? "—"}" : "";
        var output = new StringBuilder("<!doctype html><html lang=\"en\"><meta charset=\"utf-8\"><title>")
            .Append(WebUtility.HtmlEncode(heading)).Append("</title><style>body{font:12px system-ui;color:#142f3a;margin:24px}h1{font-size:20px}table{width:100%;border-collapse:collapse}td,th{text-align:left;padding:5px 8px;border-bottom:1px solid #ccd7dc}th{background:#eef3f5}small{color:#526572}@media print{thead{display:table-header-group}tr{break-inside:avoid}}</style><h1>")
            .Append(WebUtility.HtmlEncode(heading)).Append(" — Start list</h1><p>")
            .Append(WebUtility.HtmlEncode($"{p.Competition.Date:yyyy-MM-dd}{codexLabel}"))
            .Append("</p><table><thead><tr><th>Pos</th><th>Bib</th><th>Code</th><th>Surname</th><th>First name</th><th>Year</th><th>Nation</th>");
        if (withClass) { output.Append("<th>Class</th>"); }
        if (laterRun) { output.Append("<th>Run 1 time</th>"); }
        output.Append("<th>Points</th></tr></thead><tbody>");
        foreach (var e in p.Entries)
        {
            var a = e.Entrant.Athlete;
            output.Append("<tr>");
            var values = new List<string?> { e.Position.ToString(CultureInfo.InvariantCulture), e.Bib.ToString(CultureInfo.InvariantCulture),
                a.FederationCode, a.Surname, a.FirstName, a.BirthYear?.ToString(CultureInfo.InvariantCulture), a.Nation };
            if (withClass) { values.Add(e.Entrant.Class); }
            if (laterRun) { values.Add(sourceTimes.TryGetValue(e.Entrant.CompetitorId, out var time) ? RunResultInput.FormatTime(time) : ""); }
            values.Add(e.Entrant.Points?.ToString("0.00", CultureInfo.InvariantCulture));
            foreach (var value in values)
            { output.Append("<td>").Append(WebUtility.HtmlEncode(value)).Append("</td>"); }
            output.Append("</tr>");
        }
        return output.Append("</tbody></table><p><small>").Append(WebUtility.HtmlEncode(p.PointsList.IsNone ? "" : $"{(p.Setup is null ? "FIS list" : "Points list")} {p.PointsList.Code}"))
            .Append("</small></p></html>").ToString();
    }

    private static string TsvCell(string? value) => "\"" + (value ?? string.Empty).Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}
