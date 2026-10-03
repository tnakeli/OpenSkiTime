using Avalonia.Controls;
using Avalonia.Controls.Templates;
using OpenSkiTime.Rewrite.Application;

namespace OpenSkiTime.Rewrite.Desktop;

public partial class MainWindow
{
    internal Flyout? CalendarFilterMenu { get; private set; }
    private readonly List<ColumnHeaderControls> _calendarHeaders = [];
    private static CalendarColumn? CalendarColumnFor(string? member) => member switch
    {
        nameof(FisCalendarEvent.StartDate) => CalendarColumn.Start,
        nameof(FisCalendarEvent.EndDate) => CalendarColumn.End,
        nameof(FisCalendarEvent.Location) => CalendarColumn.Location,
        nameof(FisCalendarEvent.Nation) => CalendarColumn.Nation,
        nameof(FisCalendarEvent.Name) => CalendarColumn.Event,
        nameof(FisCalendarEvent.CompetitionCount) => CalendarColumn.Races,
        _ => null
    };

    private void InstallCalendarHeaders()
    {
        _calendarHeaders.Clear();
        if (this.FindControl<DataGrid>("SeriesCalendarEventsGrid") is not { } grid) { return; }
        grid.Sorting -= CalendarGridSorting; grid.Sorting += CalendarGridSorting;
        foreach (var column in grid.Columns)
        {
            if (CalendarColumnFor(column.SortMemberPath) is not { } key) { continue; }
            var label = column.Header?.ToString() ?? "";
            column.HeaderTemplate = new FuncDataTemplate<string>((_, _) =>
            {
                var header = new ColumnHeaderControls("Calendar", key.ToString(), label,
                    () => _gridViewModel?.CalendarTextFilter(key) ?? "",
                    value => _gridViewModel?.SetCalendarTextFilter(key, value),
                    () => _gridViewModel?.CalendarSortDirection(key),
                    () => _gridViewModel?.SortCalendar(key, _gridViewModel.CalendarSortDirection(key) == false),
                    menu => { CalendarFilterMenu?.Hide(); CalendarFilterMenu = menu; },
                    () => _gridViewModel?.IsSeriesCalendarBusy == false);
                _calendarHeaders.Add(header); return header.Content;
            });
        }
    }

    private void CalendarGridSorting(object? sender, DataGridColumnEventArgs e)
    {
        e.Handled = true;
        if (_gridViewModel is { IsSeriesCalendarBusy: false } vm
            && CalendarColumnFor(e.Column.SortMemberPath) is { } key)
        { vm.SortCalendar(key, vm.CalendarSortDirection(key) == false); }
    }

    private void UpdateCalendarHeaders()
    { foreach (var header in _calendarHeaders) { header.Refresh(); } }
}
