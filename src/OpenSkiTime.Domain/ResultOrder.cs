namespace OpenSkiTime.Domain;

// FIS ICR Book IV (Edition July 2026) 617.3.3: competitors with the same time or the same points are ex aequo, and
// the competitor with the higher start number is listed first. Sporting rank and listing position are different
// values: ex aequo competitors share one rank, and the start number only decides who is printed first.
public static class ResultOrder
{
    // Competition ranking (1, 2, 2, 4): one more than the number of strictly better values. An unclassified value
    // (null) has no rank and never occupies a place.
    public static int? Rank(long? value, IEnumerable<long?> field)
    {
        ArgumentNullException.ThrowIfNull(field);
        return value is { } own ? field.Count(x => x is { } other && other < own) + 1 : null;
    }

    // Official result-list order: classified rows by their rank value (time, total or rank), ex aequo rows by
    // descending start number. Rows without a rank value follow in their existing order; callers may refine that
    // order with ThenBy without affecting the classified rows.
    public static IOrderedEnumerable<T> OrderByOfficialResult<T>(this IEnumerable<T> rows, Func<T, long?> rankValue,
        Func<T, int> startNumber)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(rankValue);
        ArgumentNullException.ThrowIfNull(startNumber);
        return rows.OrderBy(x => rankValue(x) is null).ThenBy(x => rankValue(x) ?? 0)
            .ThenByDescending(x => rankValue(x) is null ? 0 : startNumber(x));
    }

    // The same order within an existing grouping, such as categories in the ranking view.
    public static IOrderedEnumerable<T> ThenByOfficialResult<T>(this IOrderedEnumerable<T> rows, Func<T, long?> rankValue,
        Func<T, int> startNumber)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(rankValue);
        ArgumentNullException.ThrowIfNull(startNumber);
        return rows.ThenBy(x => rankValue(x) is null).ThenBy(x => rankValue(x) ?? 0)
            .ThenByDescending(x => rankValue(x) is null ? 0 : startNumber(x));
    }
}
