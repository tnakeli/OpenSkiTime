using System.Globalization;
using System.Net;
using System.Text;
using OpenSkiTime.Rewrite.Domain;

namespace OpenSkiTime.Rewrite.Application;

public sealed record StartListDesk(long SeriesRevision, IReadOnlyList<StartListRevision> Revisions);
public sealed record SaveStartList(StartListPlan Plan, long ExpectedRevision, string Operator, string Reason, DateTimeOffset At);

public static class StartListExchange
{
    public static string ToTsv(StartListRevision revision)
    {
        ArgumentNullException.ThrowIfNull(revision);
        var output = new StringBuilder("Position\tBib\tCode\tSurname\tFirst name\tYear\tNation\tFIS points\n");
        foreach (var entry in revision.Plan.Entries)
        {
            var a = entry.Entrant.Athlete;
            output.AppendLine(string.Join('\t', new[] { entry.Position.ToString(CultureInfo.InvariantCulture),
                entry.Bib.ToString(CultureInfo.InvariantCulture), a.FederationCode, a.Surname, a.FirstName,
                a.BirthYear?.ToString(CultureInfo.InvariantCulture), a.Nation,
                entry.Entrant.Points?.ToString("0.00", CultureInfo.InvariantCulture) }.Select(TsvCell)));
        }
        return output.ToString();
    }

    public static string ToPrintHtml(StartListRevision revision)
    {
        ArgumentNullException.ThrowIfNull(revision);
        var p = revision.Plan;
        var heading = $"{p.Competition.ShortLabel} · {GenderLabels.Format(p.Gender)} · Run {p.RunNumber}";
        var output = new StringBuilder("<!doctype html><html lang=\"en\"><meta charset=\"utf-8\"><title>")
            .Append(WebUtility.HtmlEncode(heading)).Append("</title><style>body{font:12px system-ui;color:#142f3a;margin:24px}h1{font-size:20px}table{width:100%;border-collapse:collapse}td,th{text-align:left;padding:5px 8px;border-bottom:1px solid #ccd7dc}th{background:#eef3f5}small{color:#526572}@media print{thead{display:table-header-group}tr{break-inside:avoid}}</style><h1>")
            .Append(WebUtility.HtmlEncode(heading)).Append(" — Start list</h1><p>")
            .Append(WebUtility.HtmlEncode($"{p.Competition.Date:yyyy-MM-dd} · Codex {p.Competition.FisCode ?? p.Competition.LocalRaceCode ?? "—"}"))
            .Append("</p><table><thead><tr><th>Pos</th><th>Bib</th><th>Code</th><th>Surname</th><th>First name</th><th>Year</th><th>Nation</th><th>Points</th></tr></thead><tbody>");
        foreach (var e in p.Entries)
        {
            var a = e.Entrant.Athlete;
            output.Append("<tr>");
            foreach (var value in new[] { e.Position.ToString(CultureInfo.InvariantCulture), e.Bib.ToString(CultureInfo.InvariantCulture),
                a.FederationCode, a.Surname, a.FirstName, a.BirthYear?.ToString(CultureInfo.InvariantCulture), a.Nation,
                e.Entrant.Points?.ToString("0.00", CultureInfo.InvariantCulture) })
            { output.Append("<td>").Append(WebUtility.HtmlEncode(value)).Append("</td>"); }
            output.Append("</tr>");
        }
        return output.Append("</tbody></table><p><small>").Append(WebUtility.HtmlEncode($"FIS list {p.PointsList.Code}"))
            .Append("</small></p></html>").ToString();
    }

    private static string TsvCell(string? value) => "\"" + (value ?? string.Empty).Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}
