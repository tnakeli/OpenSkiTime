using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenSkiTime.Application;
using OpenSkiTime.Domain;

namespace OpenSkiTime.Desktop;

public sealed partial class MainViewModel
{
    private readonly HttpClient _informationHttp = informationHttp ?? new() { Timeout = TimeSpan.FromSeconds(20) };
    private readonly bool _ownsInformationHttp = informationHttp is null;
    private readonly Dictionary<(string?, Guid), (string Category, RacePersonEditor[] Jury, RaceRunEditor[] Runs, string Source)> _informationDrafts = [];
    private (string?, Guid)? _informationKey;
    public ObservableCollection<RacePersonEditor> ResultsJury { get; } = [];
    public ObservableCollection<RaceRunEditor> ResultsRuns { get; } = [];
    public ObservableCollection<WeatherPlace> WeatherPlaces { get; } = [];
    public ObservableCollection<WeatherHour> WeatherHours { get; } = [];
    [ObservableProperty] private string _raceInformationStatus = "Changes are saved automatically.";
    [ObservableProperty] private string _raceInformationSource = "";
    [ObservableProperty] private string _resultsPointsValidity = "";
    [ObservableProperty] private string _resultsCourseSummary = "";
    [ObservableProperty] private string _resultsStatistics = "";
    [ObservableProperty] private string _resultsTechnicalDelegateText = "";
    [ObservableProperty] private bool _isInformationBusy;
    [ObservableProperty] private string _weatherLocation = "";
    [ObservableProperty] private WeatherPlace? _selectedWeatherPlace;
    [ObservableProperty] private WeatherHour? _selectedWeatherHour;
    [ObservableProperty] private string _weatherBrowseStatus = "Optional forecast browser. Select a location and hour; enter measured temperatures and snow manually.";
    private void RememberInformationDraft()
    {
        if (_informationKey is { } key)
        { _informationDrafts[key] = (ResultsCategory, ResultsJury.ToArray(), ResultsRuns.ToArray(), RaceInformationSource); }
    }

    private void InvalidateCompetitionInformation(IEnumerable<Guid> ids)
    {
        foreach (var id in ids)
        {
            var key = (workspace.FilePath, id);
            _informationDrafts.Remove(key);
            if (_informationKey == key) { StopInformationTracking(); _informationKey = null; }
        }
    }

    private async Task LoadRaceInformationAsync(CompetitionDetails competition, int load)
    {
        var key = (workspace.FilePath, competition.Id);
        if (_informationKey != key)
        {
            RememberInformationDraft();
            StopInformationTracking();
            _informationKey = null;
            ResultsJury.Clear(); ResultsRuns.Clear();
            WeatherPlaces.Clear(); WeatherHours.Clear(); SelectedWeatherPlace = null; SelectedWeatherHour = null;
            ResultsCategory = ""; RaceInformationSource = "";
            if (_informationDrafts.TryGetValue(key, out var draft))
            {
                ResultsCategory = draft.Category; RaceInformationSource = draft.Source;
                foreach (var row in draft.Jury) { ResultsJury.Add(row); }
                foreach (var row in draft.Runs) { ResultsRuns.Add(row); }
            }
            else
            {
                var saved = await workspace.ReadRaceInformationAsync(competition.Id);
                if (load != _resultsLoad || workspace.FilePath != key.FilePath) { return; }
                var information = (saved?.Values ?? RaceInformation.Empty(competition.Values)).WithCompetitionCourse(competition.Values);
                ResultsCategory = competition.Values.Calendar?.Category ?? information.Category; RaceInformationSource = information.Source;
                _legacyDelegates[key] = information.Jury.FirstOrDefault(x => x.Function == "TechnicalDelegate")?.Person ?? new("", "", "");
                foreach (var function in RaceInformation.JuryFunctions.Where(x => x != "TechnicalDelegate"))
                { ResultsJury.Add(new(function, information.Jury.FirstOrDefault(x => x.Function == function)?.Person ?? new("", "", ""))); }
                foreach (var run in information.Runs) { ResultsRuns.Add(new(run)); }
                RaceInformationStatus = saved is null ? "Changes are saved automatically."
                    : "All changes saved.";
            }
            _informationKey = key;
            WeatherLocation = competition.Values.Calendar?.Location ?? _current?.Values.Location ?? "";
            TrackInformationEditors();
        }
        var c = competition.Values;
        var td = c.Calendar?.TechnicalDelegate;
        var person = td is null ? _legacyDelegates.GetValueOrDefault(key) ?? new("", "", "") : new FisPerson(td.FirstName, td.LastName, td.Nation, td.Number);
        ResultsTechnicalDelegateText = string.IsNullOrWhiteSpace(person.LastName)
            ? "Technical delegate: enter details in Competitions."
            : $"Technical delegate: {person.LastName} {person.FirstName} ({person.Nation}) · TD {person.Number}";
        ResultsCourseSummary = $"{c.Name} · {c.Date:yyyy-MM-dd} · {c.Discipline} · codex {c.FisCode} · {c.Calendar?.Location ?? _current?.Values.Location}";
        EnsureFisListLoaded();
        ResultsPointsList = FisListDisplay; ResultsPointsValidity = FisListValidityText;
    }

    private RaceInformation CurrentRaceInformation()
    {
        var competition = ResultsCompetition ?? throw new DomainValidationException("Choose a competition.");
        if (_informationKey is not { } key || key != (workspace.FilePath, competition.Id)) { throw new DomainValidationException("Wait for race information to load."); }
        var td = competition.Values.Calendar?.TechnicalDelegate;
        var delegatePerson = td is null ? _legacyDelegates.GetValueOrDefault(key) ?? new("", "", "") : new FisPerson(td.FirstName, td.LastName, td.Nation, td.Number);
        var information = new RaceInformation(competition.Values.Calendar?.Category ?? ResultsCategory.Trim().ToUpperInvariant(),
            new[] { new RaceOfficial("TechnicalDelegate", delegatePerson) }.Concat(ResultsJury.Select(x => new RaceOfficial(x.Function, x.Values))).ToArray(),
            ResultsRuns.Select(x => x.Values).ToArray(), RaceInformationSource, RaceCourseDefaults.From(competition.Values));
        information.Validate(competition.Values.RunCount);
        return information;
    }

    [RelayCommand]
    private async Task SearchWeatherPlacesAsync()
    {
        var key = _informationKey;
        await InformationRequestAsync(async () =>
        {
            var places = await new WeatherBrowseClient(_informationHttp).SearchAsync(WeatherLocation);
            if (key != _informationKey) { return; }
            WeatherPlaces.Clear(); WeatherHours.Clear(); SelectedWeatherHour = null;
            foreach (var place in places) { WeatherPlaces.Add(place); }
            SelectedWeatherPlace = null;
            WeatherBrowseStatus = places.Count == 0 ? "No locations found. Refine the location name." : "Select the correct location, then browse the race-day forecast.";
        });
    }

    partial void OnSelectedWeatherPlaceChanged(WeatherPlace? value) { WeatherHours.Clear(); SelectedWeatherHour = null; }

    [RelayCommand]
    private async Task BrowseWeatherHoursAsync()
    {
        if (SelectedWeatherPlace is not { } place || ResultsCompetition is not { } competition) { return; }
        var key = _informationKey;
        await InformationRequestAsync(async () =>
        {
            var hours = await new WeatherBrowseClient(_informationHttp).BrowseAsync(place, competition.Values.Date, DateTimeOffset.UtcNow);
            if (key != _informationKey || SelectedWeatherPlace != place) { return; }
            WeatherHours.Clear(); SelectedWeatherHour = null;
            foreach (var hour in hours) { WeatherHours.Add(hour); }
            WeatherBrowseStatus = "Select an hour and apply to a run. Forecast temperature remains separate from measured start/finish temperatures. Open-Meteo (CC BY 4.0).";
        });
    }

    [RelayCommand] private void ApplyWeatherToRun(RaceRunEditor? run)
    {
        if (SelectedWeatherHour is { } hour && run is not null && ResultsRuns.Contains(run)) { run.ApplyForecast(hour); }
    }

    private async Task InformationRequestAsync(Func<Task> action)
    {
        if (IsInformationBusy) { return; }
        IsInformationBusy = true;
        try { await action(); }
        catch (Exception ex) when (ex is DomainValidationException or HttpRequestException or IOException or JsonException
            or TaskCanceledException or PlatformNotSupportedException or ArgumentException)
        { SetStatus(ex is TaskCanceledException ? "Information lookup timed out. Retry or enter fields manually."
            : ex is HttpRequestException or JsonException ? "Information lookup failed. Retry or enter fields manually." : ex.Message, error: true); }
        finally { IsInformationBusy = false; }
    }
}
