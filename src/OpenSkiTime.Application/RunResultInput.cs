using System.Globalization;
using OpenSkiTime.Domain;

namespace OpenSkiTime.Application;

// External/manual classified input for start-order preparation, not a timing engine.
public static class RunResultInput
{
    public static long ParseTime(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var parts = text.Trim().Replace(',', '.').Split(':');
        var minutes = 0;
        if (parts.Length > 2 || (parts.Length == 2 && !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out minutes))
            || !decimal.TryParse(parts[^1], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var seconds)
            || seconds < 0 || seconds > 86400 || minutes > 1440 || (parts.Length == 2 && seconds >= 60))
        { throw new DomainValidationException("Use SS.hh or M:SS.hh for a time, for example 52.34 or 1:12.34."); }
        var hundredths = (minutes * 60m + seconds) * 100m;
        if (hundredths <= 0 || hundredths > 8_640_000 || decimal.Truncate(hundredths) != hundredths)
        { throw new DomainValidationException("Times must be positive, at most 24 hours, with at most two decimal places."); }
        return (long)hundredths;
    }

    public static string FormatTime(long? hundredths) => hundredths is { } value
        ? string.Create(CultureInfo.InvariantCulture, $"{value / 6000}:{value % 6000 / 100:00}.{value % 100:00}") : string.Empty;

    public static IReadOnlyList<RunFinish> ParseTsv(string text, StartListRevision source)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(source);
        var lines = text.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var entries = source.Plan.Entries.ToDictionary(x => x.Bib);
        var results = new List<RunFinish>();
        foreach (var line in lines)
        {
            var fields = line.Split('\t').Select(x => x.Trim()).ToArray();
            if (results.Count == 0 && fields[0].Equals("Bib", StringComparison.OrdinalIgnoreCase)) { continue; }
            if (fields.Length is < 2 or > 3 || !int.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out var bib)
                || !entries.TryGetValue(bib, out var entry))
            { throw new DomainValidationException("Paste columns Bib, Time, optional Status. Every bib must exist in the Run 1 list."); }
            var statusText = fields.Length == 3 && fields[2].Length > 0 ? fields[2] : fields[1];
            var status = statusText.ToUpperInvariant() switch
            {
                "DNS" => FinishStatus.DNS, "DNF" => FinishStatus.DNF,
                "DSQ" => FinishStatus.DSQ, "NPS" => FinishStatus.NPS,
                _ => FinishStatus.Finished,
            };
            if (fields.Length == 3 && fields[2].Length > 0 && status == FinishStatus.Finished
                && !fields[2].Equals("Finished", StringComparison.OrdinalIgnoreCase))
            { throw new DomainValidationException("Status must be Finished, DNS, DNF, DSQ or NPS."); }
            if (status != FinishStatus.Finished && fields.Length == 3 && fields[1].Length > 0)
            { throw new DomainValidationException("A DNS/DNF/DSQ/NPS row must not contain a time."); }
            results.Add(new(entry.Entrant.CompetitorId, status, status == FinishStatus.Finished ? ParseTime(fields[1]) : null));
        }
        _ = FisStartOrder.SecondRun(source, results);
        return results;
    }
}
