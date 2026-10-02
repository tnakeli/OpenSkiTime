using System.Globalization;

namespace OpenSkiTime.Rewrite.Domain;

public static class FisSeason
{
    public static int FromDate(DateOnly date) => date.Month >= 6 ? date.Year + 1 : date.Year;
    public static string SeriesLabel(DateOnly date)
    {
        var end = FromDate(date);
        return $"{end - 1}/{end % 100:00}";
    }

    public static int FromSeries(string value, DateOnly fallbackDate)
    {
        ArgumentNullException.ThrowIfNull(value);
        var parts = value.Trim().Split(['/', '-', '–', '—'], StringSplitOptions.TrimEntries);
        if (parts.Length is 1 or 2 && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var start)
            && start is >= 1900 and <= 2999)
        {
            if (parts.Length == 1) { return start; }
            if (int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var end))
            {
                if (parts[1].Length == 2) { end += start / 100 * 100; if (end < start) { end += 100; } }
                if (end == start + 1 && end <= 2999) { return end; }
            }
        }
        return FromDate(fallbackDate);
    }
}
