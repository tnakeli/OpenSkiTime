using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenSkiTime.Rewrite.Application;
using OpenSkiTime.Rewrite.Domain;

namespace OpenSkiTime.Rewrite.Desktop;

public sealed partial class MainViewModel
{
    [ObservableProperty] private string _competitionSeason = "";
    [ObservableProperty] private string _competitionLocation = "";
    [ObservableProperty] private string _competitionNation = "";
    [ObservableProperty] private string _competitionCalendarCategory = "";
    [ObservableProperty] private string _competitionGender = "";
    [ObservableProperty] private string _competitionTdLastName = "";
    [ObservableProperty] private string _competitionTdFirstName = "";
    [ObservableProperty] private string _competitionTdNation = "";
    [ObservableProperty] private string _competitionTdNumber = "";
    [ObservableProperty] private string _competitionTdOriginalName = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCompetitionCalendarStatus))]
    private string _competitionCalendarStatus = "";
    public bool HasCompetitionCalendarStatus => !string.IsNullOrWhiteSpace(CompetitionCalendarStatus);
    [ObservableProperty] private bool _isCompetitionDataBusy;
    private string _competitionCalendarSource = "";
    public IReadOnlyList<string> CompetitionGenders { get; } = ["", "W", "M", "A"];

    private void LoadCompetitionCalendar(CompetitionValues? values)
    {
        var c = values?.Calendar;
        SelectedCompetitionHomologation = null; CompetitionHomologations.Clear(); _homologationContext = null;
        OnPropertyChanged(nameof(HasCompetitionHomologations));
        CompetitionHomologationStatus = "";
        var date = values?.Date ?? AsDate(CompetitionDateText, "Competition date");
        CompetitionSeason = (c?.Season ?? FisSeason.FromSeries(_current?.Values.Season ?? Season, date)).ToString(CultureInfo.InvariantCulture);
        CompetitionLocation = c?.Location ?? _current?.Values.Location ?? "";
        CompetitionNation = c?.Nation ?? _current?.Values.Nation ?? "";
        CompetitionCalendarCategory = c?.Category ?? ""; CompetitionGender = c?.Gender ?? "";
        CompetitionTdLastName = c?.TechnicalDelegate?.LastName ?? "";
        CompetitionTdFirstName = c?.TechnicalDelegate?.FirstName ?? "";
        CompetitionTdNation = c?.TechnicalDelegate?.Nation ?? "";
        CompetitionTdNumber = c?.TechnicalDelegate?.Number ?? "";
        CompetitionTdOriginalName = c?.TechnicalDelegate?.OriginalName ?? "";
        _competitionCalendarSource = c?.Source ?? "";
        CompetitionCalendarStatus = "";
    }

    private CompetitionCalendarData DraftCompetitionCalendar()
    {
        if (!int.TryParse(CompetitionSeason, NumberStyles.None, CultureInfo.InvariantCulture, out var season))
        { throw new DomainValidationException("Enter the FIS season as a year, for example 2027."); }
        return new CompetitionCalendarData(season, CompetitionLocation, CompetitionNation, CompetitionCalendarCategory,
            CompetitionGender, new(CompetitionTdLastName, CompetitionTdFirstName, CompetitionTdNation, CompetitionTdNumber,
                string.IsNullOrWhiteSpace(CompetitionTdFirstName) || string.IsNullOrWhiteSpace(CompetitionTdLastName) ? CompetitionTdOriginalName : ""),
            _competitionCalendarSource).Validated();
    }

    [RelayCommand]
    private async Task GetCompetitionDataAsync()
    {
        if (IsCompetitionDataBusy || !IsCompetitionFis || !IsCompetitionEditing) { return; }
        var path = workspace.FilePath; var id = _editingCompetitionId;
        var codex = CompetitionFisCode.Trim(); var seasonText = CompetitionSeason;
        IsCompetitionDataBusy = true;
        CompetitionCalendarStatus = "Fetching competition data…";
        try
        {
            if (!int.TryParse(seasonText, NumberStyles.None, CultureInfo.InvariantCulture, out var season))
            { throw new DomainValidationException("Enter the FIS season year first."); }
            var data = await new FisRaceInformationClient(_informationHttp).GetCompetitionAsync(season, codex,
                _fisStore.ReadApiKey() ?? "", DateOnly.FromDateTime(DateTime.UtcNow));
            if (workspace.FilePath != path || _editingCompetitionId != id || CompetitionFisCode.Trim() != codex || CompetitionSeason != seasonText)
            { return; }
            var race = data.Competition;
            var discipline = race.EventCode.ToUpperInvariant() switch
            { "SL" => Discipline.Slalom, "GS" => Discipline.GiantSlalom, "SG" => Discipline.SuperG, "DH" => Discipline.Downhill,
                "AC" => Discipline.AlpineCombined, _ => throw new DomainValidationException("This FIS event is not supported. No fields were changed.") };
            var previousTd = new CompetitionTechnicalDelegateInfo(CompetitionTdLastName, CompetitionTdFirstName,
                CompetitionTdNation, CompetitionTdNumber, CompetitionTdOriginalName);
            var importedTd = data.TechnicalDelegate;
            if (importedTd is { Number.Length: 0 } && string.Equals(importedTd.LastName, previousTd.LastName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(importedTd.FirstName, previousTd.FirstName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(importedTd.Nation, previousTd.Nation, StringComparison.OrdinalIgnoreCase))
            { importedTd = importedTd with { Number = previousTd.Number }; }
            var calendar = new CompetitionCalendarData(season, race.PlaceName ?? CompetitionLocation, race.PlaceNationCode ?? CompetitionNation,
                race.CategoryCode, race.GenderCode ?? "", importedTd ?? previousTd, data.Source).Validated();
            var values = new CompetitionValues(data.EventName ?? (string.IsNullOrWhiteSpace(CompetitionName)
                    ? $"{calendar.Location} {race.EventCode}" : CompetitionName),
                string.IsNullOrWhiteSpace(CompetitionShortLabel) ? $"{race.EventCode} {calendar.Gender} {codex}" : CompetitionShortLabel,
                race.Date, discipline, RaceType.Fis, id is null && discipline is Discipline.Downhill or Discipline.SuperG ? 1 : CompetitionRunCount,
                CompetitionIntermediateCount, codex, CompetitionCourseName,
                OptionalInt(CompetitionStartAltitude, "Start altitude"), OptionalInt(CompetitionFinishAltitude, "Finish altitude"),
                OptionalInt(CompetitionVerticalDrop, "Drop"), CompetitionHomologation, calendar,
                OptionalInt(CompetitionCourseLength, "Course length")).Validated();
            // Stage the complete validated response, then use the existing transactional competition save.
            CompetitionName = values.Name; CompetitionShortLabel = values.ShortLabel; CompetitionDateText = FormatDate(values.Date);
            CompetitionDiscipline = values.Discipline; CompetitionRunCount = values.RunCount;
            LoadCompetitionCalendar(values);
            await SaveCompetitionCoreAsync(false);
            CompetitionCalendarStatus = IsError ? "Competition data could not be saved. " + StatusMessage : data.Note + " Saved in the event file.";
        }
        catch (Exception ex) when (ex is DomainValidationException or HttpRequestException or IOException or OperationCanceledException
            or PlatformNotSupportedException or ArgumentException)
        {
            CompetitionCalendarStatus = ex is HttpRequestException or OperationCanceledException
                ? "FIS lookup failed or timed out. Existing fields were preserved; retry or enter data manually." : ex.Message;
            SetStatus(CompetitionCalendarStatus, error: true);
        }
        finally { IsCompetitionDataBusy = false; }
    }
}
