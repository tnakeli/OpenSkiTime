using System.Globalization;
using CommunityToolkit.Mvvm.Input;
using OpenSkiTime.Application;

namespace OpenSkiTime.Desktop;

public enum CalendarColumn { Start, End, Location, Nation, Event, Races }
public sealed record CalendarFilterValue(string Value, string Label, bool Selected);

public sealed partial class MainViewModel
{
    private readonly Dictionary<CalendarColumn, HashSet<string>> _calendarColumnFilters = [];
    private readonly Dictionary<CalendarColumn, string> _calendarTextFilters = [];
    public string CalendarTextFilter(CalendarColumn column) => _calendarTextFilters.TryGetValue(column, out var text) ? text
        : _calendarColumnFilters.TryGetValue(column, out var accepted) ? string.Join(", ", accepted.Order(StringComparer.OrdinalIgnoreCase)) : "";
    public bool? CalendarSortDirection(CalendarColumn column) => _calendarSortColumn == column ? _calendarSortDescending : null;
    public void SetCalendarTextFilter(CalendarColumn column, string text)
    {
        _calendarColumnFilters.Remove(column);
        if (string.IsNullOrWhiteSpace(text)) { _calendarTextFilters.Remove(column); }
        else { _calendarTextFilters[column] = text; }
        FilterSeriesCalendar();
    }
    private bool MatchesCalendarTextFilters(FisCalendarEvent item) => _calendarTextFilters.All(filter =>
        ColumnGridController<FisCalendarEvent>.Matches(filter.Key switch
        {
            CalendarColumn.Start => item.StartDate.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture),
            CalendarColumn.End => item.EndDate.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture),
            _ => CalendarValue(item, filter.Key)
        }, filter.Value));
    private CalendarColumn? _calendarSortColumn;
    private bool _calendarSortDescending;
    // Notification for header controls; no mutable row or persistence state participates in filtering.
    public int CalendarFiltersVersion => _calendarColumnFilters.Count;

    public IReadOnlyList<CalendarFilterValue> CalendarFilterValues(CalendarColumn column)
        => _calendarEvents.Select(x => CalendarValue(x, column)).Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase).Select(value => new CalendarFilterValue(value,
                column is CalendarColumn.Start or CalendarColumn.End
                    ? DateOnly.ParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture).ToString("dd.MM.yyyy", CultureInfo.InvariantCulture)
                    : string.IsNullOrEmpty(value) ? "(Blank)" : value,
                !_calendarColumnFilters.TryGetValue(column, out var accepted) || accepted.Contains(value))).ToArray();

    public string CalendarHeader(CalendarColumn column, string label)
        => label + (_calendarColumnFilters.ContainsKey(column) ? " ●" : "")
            + (_calendarSortColumn == column ? _calendarSortDescending ? " ↓" : " ↑" : " ▾");

    public void ApplyCalendarFilter(CalendarColumn column, IEnumerable<string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var accepted = values.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var all = _calendarEvents.Select(x => CalendarValue(x, column)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!accepted.IsSubsetOf(all)) { throw new ArgumentException("Unknown calendar filter value.", nameof(values)); }
        if (accepted.SetEquals(all)) { _calendarColumnFilters.Remove(column); }
        else { _calendarColumnFilters[column] = accepted; }
        FilterSeriesCalendar();
    }
    public void ClearCalendarFilter(CalendarColumn column)
    { _calendarColumnFilters.Remove(column); _calendarTextFilters.Remove(column); FilterSeriesCalendar(); }
    [RelayCommand]
    private void ClearCalendarFilters() { _calendarColumnFilters.Clear(); _calendarTextFilters.Clear(); FilterSeriesCalendar(); }
    public void SortCalendar(CalendarColumn column, bool descending)
    { _calendarSortColumn = column; _calendarSortDescending = descending; FilterSeriesCalendar(); }

    private IEnumerable<FisCalendarEvent> SortCalendarEvents(IEnumerable<FisCalendarEvent> events)
    {
        // ISO date keys order chronologically; counts must be sorted numerically.
        if (_calendarSortColumn is not { } column) { return events; }
        return column == CalendarColumn.Races
            ? (_calendarSortDescending ? events.OrderByDescending(x => x.CompetitionCount) : events.OrderBy(x => x.CompetitionCount)).ThenBy(x => x.Id)
            : (_calendarSortDescending ? events.OrderByDescending(x => CalendarValue(x, column), StringComparer.OrdinalIgnoreCase)
                : events.OrderBy(x => CalendarValue(x, column), StringComparer.OrdinalIgnoreCase)).ThenBy(x => x.Id);
    }
    private static string CalendarValue(FisCalendarEvent item, CalendarColumn column) => column switch
    {
        CalendarColumn.Start => item.StartDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        CalendarColumn.End => item.EndDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        CalendarColumn.Location => item.Location, CalendarColumn.Nation => item.Nation,
        CalendarColumn.Event => item.Name, CalendarColumn.Races => item.CompetitionCount.ToString(CultureInfo.InvariantCulture),
        _ => throw new ArgumentOutOfRangeException(nameof(column))
    };
}
