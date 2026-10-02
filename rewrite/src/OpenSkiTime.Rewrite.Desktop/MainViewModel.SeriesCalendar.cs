using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenSkiTime.Rewrite.Application;
using OpenSkiTime.Rewrite.Domain;

namespace OpenSkiTime.Rewrite.Desktop;

public sealed partial class MainViewModel
{
    [ObservableProperty] private bool _isSeriesCalendarOpen;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UseSeriesCalendarEventCommand))]
    private bool _isSeriesCalendarBusy;
    [ObservableProperty] private string _seriesCalendarSeason = "";
    [ObservableProperty] private string _seriesCalendarStatus = "";
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UseSeriesCalendarEventCommand))]
    private FisCalendarEvent? _selectedSeriesCalendarEvent;
    public ObservableCollection<FisCalendarEvent> SeriesCalendarEvents { get; } = [];
    public ObservableCollection<CompetitionValues> SeriesCalendarCompetitions { get; } = [];
    private IReadOnlyList<FisCalendarEvent> _calendarEvents = [];
    private IReadOnlyList<CompetitionValues>? _pendingCalendarCompetitions;
    private int _calendarBrowseGeneration;
    private long? _calendarPreviewRevision;

    partial void OnSelectedSeriesCalendarEventChanged(FisCalendarEvent? value)
    {
        SeriesCalendarCompetitions.Clear();
        _calendarPreviewRevision = _current?.Revision;
        foreach (var race in value?.Competitions ?? [])
        {
            var matches = IsCreatingNew ? [] : Competitions.Where(x => x.Values.RaceType == RaceType.Fis
                && x.Values.FisCode?.PadLeft(4, '0') == race.FisCode
                && (x.Values.Calendar?.Season ?? FisSeason.FromDate(x.Values.Date)) == race.Calendar!.Season).ToArray();
            SeriesCalendarCompetitions.Add(matches.Length == 1 ? CalendarCompetitionMerge.Merge(matches[0].Values,
                value!.HasEventName ? race : race with { Name = matches[0].Values.Name }).Validated() : race);
        }
    }
    private void FilterSeriesCalendar()
    {
        var selectedId = SelectedSeriesCalendarEvent?.Id;
        SeriesCalendarEvents.Clear();
        foreach (var item in SortCalendarEvents(_calendarEvents.Where(x => MatchesCalendarTextFilters(x)
            && _calendarColumnFilters.All(f => f.Value.Contains(CalendarValue(x, f.Key))))))
        { SeriesCalendarEvents.Add(item); }
        SelectedSeriesCalendarEvent = SeriesCalendarEvents.FirstOrDefault(x => x.Id == selectedId);
        OnPropertyChanged(nameof(CalendarFiltersVersion));
    }

    [RelayCommand]
    private async Task BrowseSeriesCalendarAsync()
    {
        if (IsSeriesCalendarBusy) { return; }
        if (!IsSeriesCalendarOpen)
        {
            SeriesCalendarSeason = FisSeason.FromSeries(Season, DateOnly.FromDateTime(DateTime.Today)).ToString(CultureInfo.InvariantCulture);
            _calendarColumnFilters.Clear();
            _calendarTextFilters.Clear();
            if (!string.IsNullOrWhiteSpace(Nation)) { _calendarColumnFilters[CalendarColumn.Nation] = new(StringComparer.OrdinalIgnoreCase) { Nation.Trim() }; }
            IsSeriesCalendarOpen = true;
        }
        var generation = ++_calendarBrowseGeneration;
        var path = workspace.FilePath; var creating = IsCreatingNew; var seasonText = SeriesCalendarSeason;
        IsSeriesCalendarBusy = true; SeriesCalendarStatus = "Loading FIS alpine calendar…";
        try
        {
            if (!int.TryParse(seasonText, NumberStyles.None, CultureInfo.InvariantCulture, out var season))
            { throw new DomainValidationException("Enter the FIS season year."); }
            var events = await new FisRaceInformationClient(_informationHttp).GetCalendarAsync(season, _fisStore.ReadApiKey() ?? "");
            if (generation != _calendarBrowseGeneration || path != workspace.FilePath || creating != IsCreatingNew || seasonText != SeriesCalendarSeason) { return; }
            _calendarEvents = events; FilterSeriesCalendar();
            SeriesCalendarStatus = events.Count == 0 ? "The latest FIS export has no supported alpine events for this season. Check the season or enter the series manually."
                : $"{events.Count} alpine events loaded. Select an event to review its competitions.";
        }
        catch (Exception ex) when (ex is DomainValidationException or HttpRequestException or IOException or OperationCanceledException
            or ArgumentException or PlatformNotSupportedException)
        {
            if (generation != _calendarBrowseGeneration) { return; }
            _calendarEvents = []; FilterSeriesCalendar();
            SeriesCalendarStatus = ex is HttpRequestException or OperationCanceledException
                ? "FIS calendar could not be loaded. Retry or enter the series manually." : ex.Message;
        }
        finally { IsSeriesCalendarBusy = false; }
    }

    private bool CanUseSeriesCalendarEvent => !IsSeriesCalendarBusy && SelectedSeriesCalendarEvent is not null;

    [RelayCommand(CanExecute = nameof(CanUseSeriesCalendarEvent))]
    private async Task UseSeriesCalendarEventAsync()
    {
        if (IsSeriesCalendarBusy || SelectedSeriesCalendarEvent is not { } selected) { return; }
        if (_calendarPreviewRevision != _current?.Revision)
        { SetStatus("The series changed after the calendar preview. Select the event again and review the updated preview.", true); return; }
        var preview = SeriesCalendarCompetitions.ToArray();
        IsSeriesCalendarBusy = true;
        var generation = _calendarBrowseGeneration; var path = workspace.FilePath; var creating = IsCreatingNew;
        try
        {
            await GuardAsync(async () =>
            {
                EnsureDeskClean();
                if (IsDrawBusy || workspace.Timing?.IsActive == true)
                { throw new DomainValidationException("Finish drawing and disconnect timing before importing the calendar."); }
                if (!await FlushRaceInformationAsync()) { return; }
                var revision = _current?.Revision;
                SeriesCalendarStatus = "Checking selected competition codices…";
                await new FisRaceInformationClient(_informationHttp).ValidateCalendarEventAsync(selected, _fisStore.ReadApiKey() ?? "");
                if (generation != _calendarBrowseGeneration || path != workspace.FilePath || creating != IsCreatingNew
                    || revision != _current?.Revision || SelectedSeriesCalendarEvent != selected)
                { SeriesCalendarStatus = "The series or selection changed. Review the event again before importing."; return; }
                var values = new SeriesValues(!IsCreatingNew && !selected.HasEventName ? Name : selected.Name,
                    string.IsNullOrWhiteSpace(selected.Location) ? Location : selected.Location,
                    string.IsNullOrWhiteSpace(selected.Organizer) ? Organizer : selected.Organizer,
                    selected.StartDate, selected.EndDate, string.IsNullOrWhiteSpace(selected.Nation) ? Nation : selected.Nation,
                    $"{selected.Season - 1}/{selected.Season % 100:00}");
                if (IsCreatingNew)
                {
                    Name = values.Name; Location = values.Location; Organizer = values.Organizer;
                    StartDateText = FormatDate(values.StartDate); EndDateText = FormatDate(values.EndDate);
                    Nation = values.Nation; Season = values.Season;
                    _pendingCalendarCompetitions = preview;
                    IsSeriesCalendarOpen = false;
                    SetStatus($"FIS event selected with {selected.Competitions.Count} competitions. Review the series fields and create its file. Missing organizer details must be entered manually.");
                }
                else
                {
                    var details = await workspace.ApplyCalendarAsync(values, preview, _current!.Revision);
                    Apply(details);
                    InvalidateCompetitionInformation(details.Competitions.Select(x => x.Id));
                    if (_editingCompetitionId is { } id) { SelectedCompetition = Competitions.FirstOrDefault(x => x.Id == id); }
                    IsSeriesCalendarOpen = false;
                    SetStatus($"FIS event and {selected.Competitions.Count} competitions saved. Missing TD details remain available for manual review in Competitions.");
                }
            });
            if (IsError) { SeriesCalendarStatus = StatusMessage; }
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or PlatformNotSupportedException or ArgumentException)
        {
            SetStatus("FIS calendar validation failed. Retry or enter the series manually; existing data was preserved.", true);
            SeriesCalendarStatus = StatusMessage;
        }
        finally { IsSeriesCalendarBusy = false; }
    }

    [RelayCommand] private void CloseSeriesCalendar() { IsSeriesCalendarOpen = false; ++_calendarBrowseGeneration; }
    private void ResetSeriesCalendar()
    {
        ++_calendarBrowseGeneration; IsSeriesCalendarOpen = false;
        _pendingCalendarCompetitions = null; _calendarEvents = [];
        _calendarColumnFilters.Clear(); _calendarTextFilters.Clear(); _calendarSortColumn = null;
        SeriesCalendarEvents.Clear(); SelectedSeriesCalendarEvent = null;
    }
}
