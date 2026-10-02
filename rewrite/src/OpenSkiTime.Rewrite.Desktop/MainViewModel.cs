using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenSkiTime.Rewrite.Application;
using OpenSkiTime.Rewrite.Domain;

namespace OpenSkiTime.Rewrite.Desktop;

public enum WorkspaceSection { Series, Competitions, Competitors, Draw, Timing, Results, Settings }

public sealed partial class MainViewModel(SeriesWorkspace workspace, IFileDialogs dialogs,
    IEntryExchange? entryExchange = null, FisLocalStore? fisStore = null,
    CategoryRulePresetStore? categoryRulePresetStore = null,
    RecentSeriesStore? recentSeriesStore = null, TimingPreferencesStore? timingPreferencesStore = null,
    HttpClient? informationHttp = null) : ObservableObject, IDisposable
{
    private static readonly string[] s_dateFormats = ["dd.MM.yyyy", "d.M.yyyy"];
    private readonly RecentSeriesStore _recentSeriesStore = recentSeriesStore ?? new();
    private ObservableCollection<string>? _recentFiles;
    public ObservableCollection<string> RecentFiles => _recentFiles ??= new(_recentSeriesStore.Load());
    private SeriesDetails? _current;
    private Guid? _editingCompetitionId;
    private Guid? _activeRaceId;
    private int _activeRaceRun;
    private WorkspaceSection _activeRaceSection = WorkspaceSection.Draw;

    public ObservableCollection<CompetitionDetails> Competitions { get; } = [];
    public ObservableCollection<CompetitionDetails> FisCompetitions { get; } = [];
    public IReadOnlyList<Discipline> Disciplines { get; } = Enum.GetValues<Discipline>();
    public bool IsCompetitionFis
    {
        get => CompetitionRaceType == RaceType.Fis;
        set => CompetitionRaceType = value ? RaceType.Fis : RaceType.Club;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditingSeries))]
    [NotifyPropertyChangedFor(nameof(CanEditCompetitions))]
    private bool _isOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditingSeries))]
    [NotifyPropertyChangedFor(nameof(CanEditCompetitions))]
    private bool _isCreatingNew = true;

    public bool IsEditingSeries => IsOpen && !IsCreatingNew;
    public bool CanEditCompetitions => IsEditingSeries;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSeriesSection))]
    [NotifyPropertyChangedFor(nameof(IsCompetitionsSection))]
    [NotifyPropertyChangedFor(nameof(IsCompetitorsSection))]
    [NotifyPropertyChangedFor(nameof(IsSettingsSection))]
    [NotifyPropertyChangedFor(nameof(IsDrawSection))]
    [NotifyPropertyChangedFor(nameof(IsTimingSection))]
    [NotifyPropertyChangedFor(nameof(IsResultsSection))]
    [NotifyPropertyChangedFor(nameof(IsFormSection))]
    [NotifyPropertyChangedFor(nameof(WindowTitle))]
    private WorkspaceSection _activeSection = WorkspaceSection.Series;
    public bool IsSeriesSection => ActiveSection == WorkspaceSection.Series;
    public bool IsCompetitionsSection => ActiveSection == WorkspaceSection.Competitions;
    public bool IsCompetitorsSection => ActiveSection == WorkspaceSection.Competitors;
    public bool IsSettingsSection => ActiveSection == WorkspaceSection.Settings;
    public bool IsDrawSection => ActiveSection == WorkspaceSection.Draw;
    public bool IsTimingSection => ActiveSection == WorkspaceSection.Timing;
    public bool IsResultsSection => ActiveSection == WorkspaceSection.Results;
    public bool IsFormSection => !IsDrawSection && !IsTimingSection && !IsResultsSection;
    public string ActiveRaceLabel => _activeRaceId is { } id && Competitions.FirstOrDefault(c => c.Id == id) is { } race
        ? $"{race.Values.ShortLabel}  /  Run {_activeRaceRun}  ▾" : "Choose competition  ▾";
    public bool ActiveRaceUsesTiming => IsTimingSection || (!IsDrawSection && _activeRaceSection == WorkspaceSection.Timing);
    public DrawDestination? ActiveRaceDestination => _activeRaceId is { } id
        && Competitions.FirstOrDefault(c => c.Id == id) is { } race
            ? new(race, _activeRaceRun) : null;
    public bool IsActiveRaceDestination(Guid competitionId, int run, WorkspaceSection section)
        => _activeRaceId == competitionId && _activeRaceRun == run && _activeRaceSection == section;

    private void SetActiveRace(CompetitionDetails competition, int run, WorkspaceSection section)
    {
        _activeRaceId = competition.Id;
        _activeRaceRun = run;
        _activeRaceSection = section;
        OnPropertyChanged(nameof(ActiveRaceLabel));
    }

    private void ClearActiveRace()
    {
        _activeRaceId = null;
        _activeRaceRun = 0;
        _activeRaceSection = WorkspaceSection.Draw;
        OnPropertyChanged(nameof(ActiveRaceLabel));
    }

    [RelayCommand] private void ShowSeries() => SwitchSection(WorkspaceSection.Series);
    [RelayCommand] private void ShowCompetitions() => SwitchSection(WorkspaceSection.Competitions);
    [RelayCommand] private void ShowCompetitors() => SwitchSection(WorkspaceSection.Competitors);
    [RelayCommand] private async Task ShowResultsAsync()
    {
        SwitchSection(WorkspaceSection.Results);
        if (!IsResultsSection) { return; }
        var selected = FisCompetitions.FirstOrDefault(x => x.Id == _activeRaceId)
            ?? FisCompetitions.FirstOrDefault();
        if (Equals(ResultsCompetition, selected)) { await LoadResultsAsync(); }
        else { ResultsCompetition = selected; }
    }
    [RelayCommand] private void ShowSettings()
    {
        SwitchSection(WorkspaceSection.Settings);
        if (IsSettingsSection) { RefreshFisSettingsStatus(); }
    }

    private void SwitchSection(WorkspaceSection section)
    {
        if ((section is WorkspaceSection.Competitions or WorkspaceSection.Competitors or WorkspaceSection.Draw or WorkspaceSection.Timing or WorkspaceSection.Results) && !CanEditCompetitions) { return; }
        if (IsDrawBusy) { return; }
        if (section != ActiveSection && !CanLeaveDrawInput()) { return; }
        if (section != ActiveSection && HasDeskDrafts
            && section != WorkspaceSection.Settings
            && !(ActiveSection == WorkspaceSection.Settings && section == WorkspaceSection.Competitors))
        {
            SetStatus("Save or discard unsaved changes before leaving this view.", error: true);
            return;
        }
        ActiveSection = section;
        if (section == WorkspaceSection.Competitions && Competitions.Count == 0 && !IsCompetitionEditing) { NewCompetition(); }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WindowTitle))]
    private string _fileLabel = "No series file open";
    public string WindowTitle => FileLabel == "No series file open" ? "OpenSkiTime"
        : IsTimingSection ? $"{FileLabel} · {TimingContext}"
        : IsDrawSection && DrawCompetition is { } c
            ? $"{FileLabel} · {c.Values.ShortLabel}{CompetitionCodexLabel(c.Values)} · Run {DrawRun}"
            : IsResultsSection && ResultsCompetition is { } resultRace
                ? $"{FileLabel} · {resultRace.Values.ShortLabel} · Codex {resultRace.Values.FisCode ?? "—"} · Results"
            : FileLabel;
    private static string CompetitionCodexLabel(CompetitionValues competition)
        => competition.RaceType == RaceType.Fis ? $" · Codex {competition.FisCode ?? "—"}" : "";
    [ObservableProperty] private string _statusMessage = "Create a series file or open an existing one.";
    [ObservableProperty] private bool _isError;
    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _location = string.Empty;
    [ObservableProperty] private string _organizer = string.Empty;
    [ObservableProperty] private string _startDateText = TodayText();
    [ObservableProperty] private string _endDateText = TodayText();
    [ObservableProperty] private string _nation = "FIN";
    [ObservableProperty] private string _season = FisSeason.SeriesLabel(DateOnly.FromDateTime(DateTime.Today));
    [ObservableProperty] private CompetitionDetails? _selectedCompetition;
    [ObservableProperty] private bool _isCompetitionEditing;
    [ObservableProperty] private string _competitionEditorTitle = "Competition";
    [ObservableProperty] private string _competitionName = string.Empty;
    [ObservableProperty] private string _competitionShortLabel = string.Empty;
    [ObservableProperty] private string _competitionDateText = TodayText();
    [ObservableProperty] private Discipline _competitionDiscipline = Discipline.Slalom;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCompetitionFis))]
    [NotifyPropertyChangedFor(nameof(HasCompetitionHomologations))]
    private RaceType _competitionRaceType = RaceType.Fis;
    [ObservableProperty] private int _competitionRunCount = 2;
    [ObservableProperty] private int _competitionIntermediateCount;
    [ObservableProperty] private string _competitionFisCode = string.Empty;
    [ObservableProperty] private string _competitionCourseName = string.Empty;
    [ObservableProperty] private string _competitionCourseLength = string.Empty;
    [ObservableProperty] private string _competitionStartAltitude = string.Empty;
    [ObservableProperty] private string _competitionFinishAltitude = string.Empty;
    [ObservableProperty] private string _competitionVerticalDrop = string.Empty;
    [ObservableProperty] private string _competitionHomologation = string.Empty;

    partial void OnSelectedCompetitionChanged(CompetitionDetails? value)
    {
        if (value is null) { return; }
        DeskCompetition = value;
        _editingCompetitionId = value.Id;
        CompetitionEditorTitle = "Edit competition";
        CompetitionName = value.Values.Name;
        CompetitionShortLabel = value.Values.ShortLabel;
        CompetitionDateText = FormatDate(value.Values.Date);
        CompetitionDiscipline = value.Values.Discipline;
        CompetitionRaceType = value.Values.RaceType;
        CompetitionRunCount = value.Values.RunCount;
        CompetitionIntermediateCount = value.Values.IntermediateCount;
        CompetitionFisCode = value.Values.FisCode ?? string.Empty;
        CompetitionCourseName = value.Values.CourseName ?? string.Empty;
        CompetitionCourseLength = value.Values.CourseLengthMeters?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "";
        CompetitionStartAltitude = value.Values.StartAltitudeMeters?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
        CompetitionFinishAltitude = value.Values.FinishAltitudeMeters?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
        CompetitionVerticalDrop = value.Values.VerticalDropMeters?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
        CompetitionHomologation = value.Values.HomologationNumber ?? string.Empty;
        IsCompetitionEditing = true;
        LoadCompetitionCalendar(value.Values);
    }

    [RelayCommand]
    private void NewSeries()
    {
        if (workspace.Timing?.IsActive == true) { SetStatus("Disconnect timing before starting a new event file.", true); return; }
        if (!CanLeaveDrawInput()) { return; }
        if (HasDeskDrafts)
        {
            SetStatus("Save or discard unsaved changes before starting a new series.", error: true);
            return;
        }
        IsCreatingNew = true;
        ResetSeriesCalendar();
        ActiveSection = WorkspaceSection.Series;
        Name = Location = Organizer = string.Empty;
        Season = FisSeason.SeriesLabel(DateOnly.FromDateTime(DateTime.Today));
        StartDateText = EndDateText = TodayText();
        Nation = "FIN";
        IsCompetitionEditing = false;
        SetStatus("Enter the weekend details, then choose where to save its file.");
    }

    [RelayCommand]
    private void CancelNewSeries()
    {
        if (_current is not null)
        {
            Apply(_current);
            IsCreatingNew = false;
        }
        else
        {
            SetStatus("No series file is open.");
        }
    }

    [RelayCommand]
    private async Task CreateSeriesAsync()
    {
        await GuardAsync(async () =>
        {
            EnsureDeskClean();
            if (!await FlushRaceInformationAsync()) { return; }
            var values = DraftSeries().Validated();
            var path = await dialogs.ChooseNewAsync(SafeFileName(values.Name));
            if (path is null) { return; }
            var details = await workspace.CreateAsync(path, values, competitions: _pendingCalendarCompetitions);
            Apply(details);
            IsOpen = true;
            IsCreatingNew = false;
            await LoadCompetitorDeskAsync();
            SwitchSection(WorkspaceSection.Competitions);
            RememberCurrentFile();
            SetStatus("Event series created and saved.");
        });
    }

    [RelayCommand]
    private async Task OpenSeriesAsync()
    {
        await GuardAsync(async () =>
        {
            EnsureDeskClean();
            var path = await dialogs.ChooseOpenAsync();
            if (path is null) { return; }
            await OpenPathAsync(path);
        });
    }

    [RelayCommand]
    private async Task OpenRecentSeriesAsync(string path)
    {
        await GuardAsync(async () =>
        {
            EnsureDeskClean();
            if (!File.Exists(path))
            {
                RecentFiles.Remove(path);
                throw new SeriesFileException("That recent event file was not found. Use Open file to locate it.");
            }
            await OpenPathAsync(path);
        });
    }

    private async Task OpenPathAsync(string path)
    {
        if (!await FlushRaceInformationAsync()) { return; }
        var details = await workspace.OpenAsync(path);
        ClearActiveRace();
        ResetTimingUi();
        Apply(details);
        IsOpen = true;
        IsCreatingNew = false;
        await LoadCompetitorDeskAsync();
        ActiveSection = WorkspaceSection.Competitions;
        RememberCurrentFile();
        SetStatus("Event series opened.");
    }

    private void RememberCurrentFile()
    {
        if (workspace.FilePath is not { } path) { return; }
        for (var index = RecentFiles.Count - 1; index >= 0; index--)
        {
            if (string.Equals(RecentFiles[index], path, OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            {
                RecentFiles.RemoveAt(index);
            }
        }
        RecentFiles.Insert(0, path);
        while (RecentFiles.Count > 10) { RecentFiles.RemoveAt(RecentFiles.Count - 1); }
        try { _recentSeriesStore.Record(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A failed convenience-list write must not invalidate an opened event file.
        }
    }

    [RelayCommand]
    private async Task CloseSeriesAsync()
    {
        await GuardAsync(async () =>
        {
            EnsureDeskClean();
            if (!await FlushRaceInformationAsync()) { return; }
            await workspace.CloseAsync();
            StopInformationTracking(); _informationKey = null;
            ClearActiveRace();
            ResetTimingUi();
            _current = null;
            _settingDraw = true;
            DrawCompetition = null;
            DrawRevision = null;
            DrawEntries.Clear();
            DrawStartListRows.Clear();
            DrawResults.Clear();
            _drawDesk = null;
            _sourceRun = null;
            _drawLoad++;
            _settingDraw = false;
            ClearCompetitorDesk();
            Competitions.Clear();
            FisCompetitions.Clear();
            ResultsCompetition = null;
            SelectedCompetition = null;
            IsCompetitionEditing = false;
            IsOpen = false;
            ActiveSection = WorkspaceSection.Series;
            FileLabel = "No series file open";
            NewSeries();
            SetStatus("Event series closed. All completed changes are saved.");
        });
    }

    [RelayCommand]
    private async Task SaveSeriesAsync()
    {
        await GuardAsync(async () =>
        {
            EnsureDeskClean();
            if (!await FlushRaceInformationAsync()) { return; }
            var revision = _current?.Revision ?? throw new SeriesFileException("Open an event series first.");
            var details = await workspace.SaveSeriesAsync(DraftSeries(), revision);
            Apply(details);
            SetStatus("Event series saved.");
        });
    }

    [RelayCommand]
    private async Task BackupAsync()
    {
        await GuardAsync(async () =>
        {
            EnsureDeskClean();
            if (_current is null) { throw new SeriesFileException("Open an event series first."); }
            if (!await FlushRaceInformationAsync()) { return; }
            var path = await dialogs.ChooseBackupAsync(SafeFileName(_current.Values.Name));
            if (path is null) { return; }
            await workspace.BackupAsync(path);
            SetStatus($"Portable backup saved to {path}");
        });
    }

    [RelayCommand]
    private void NewCompetition()
    {
        if (!CanEditCompetitions) { return; }
        SelectedCompetition = null;
        _editingCompetitionId = null;
        CompetitionEditorTitle = "New competition";
        CompetitionName = CompetitionShortLabel = CompetitionFisCode = string.Empty;
        CompetitionCourseName = CompetitionStartAltitude = CompetitionFinishAltitude = string.Empty;
        CompetitionVerticalDrop = CompetitionHomologation = string.Empty;
        CompetitionCourseLength = string.Empty;
        CompetitionDateText = StartDateText;
        CompetitionDiscipline = Discipline.Slalom;
        CompetitionRaceType = RaceType.Fis;
        CompetitionRunCount = 2;
        CompetitionIntermediateCount = 0;
        IsCompetitionEditing = true;
        LoadCompetitionCalendar(null);
    }

    [RelayCommand]
    private async Task SaveCompetitionAsync()
    {
        await GuardAsync(async () =>
        {
            EnsureDeskClean();
            if (!await FlushRaceInformationAsync()) { return; }
            var revision = workspace.Timing?.IsActive == true
                ? (await workspace.ReadAsync()).Revision
                : _current?.Revision ?? throw new SeriesFileException("Open an event series first.");
            var values = DraftCompetition().Validated();
            var details = await workspace.SaveCompetitionAsync(_editingCompetitionId, values, revision);
            var id = _editingCompetitionId;
            if (id is { } savedId) { InvalidateCompetitionInformation([savedId]); }
            Apply(details);
            SelectedCompetition = id is null
                ? details.Competitions.Single(c => c.Values.ShortLabel.Equals(values.ShortLabel, StringComparison.OrdinalIgnoreCase))
                : details.Competitions.FirstOrDefault(c => c.Id == id);
            await LoadCompetitorDeskAsync();
            SetStatus("Competition saved.");
        });
    }

    [RelayCommand]
    private async Task RemoveCompetitionAsync()
    {
        await GuardAsync(async () =>
        {
            EnsureDeskClean();
            if (_current is null || _editingCompetitionId is not { } id)
            {
                throw new SeriesFileException("Select a saved competition first.");
            }
            if (!await dialogs.ConfirmRemoveAsync(CompetitionShortLabel)) { return; }
            var details = await workspace.RemoveCompetitionAsync(id, _current.Revision);
            Apply(details);
            SelectedCompetition = null;
            IsCompetitionEditing = false;
            await LoadCompetitorDeskAsync();
            SetStatus("Competition removed.");
        });
    }

    private SeriesValues DraftSeries() => new(Name, Location, Organizer,
        AsDate(StartDateText, "Start date"), AsDate(EndDateText, "End date"), Nation, Season);

    private CompetitionValues DraftCompetition() => new(
        CompetitionName, CompetitionShortLabel, AsDate(CompetitionDateText, "Competition date"),
        CompetitionDiscipline, CompetitionRaceType, CompetitionRunCount, CompetitionIntermediateCount,
        CompetitionFisCode, CompetitionCourseName,
        OptionalInt(CompetitionStartAltitude, "Start altitude"),
        OptionalInt(CompetitionFinishAltitude, "Finish altitude"),
        OptionalInt(CompetitionVerticalDrop, "Vertical drop"), CompetitionHomologation,
        DraftCompetitionCalendar(), OptionalInt(CompetitionCourseLength, "Course length"));

    private static int? OptionalInt(string value, string field)
    {
        if (string.IsNullOrWhiteSpace(value)) { return null; }
        if (!int.TryParse(value, System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out var result))
        {
            throw new DomainValidationException($"{field} must be a whole number.");
        }
        return result;
    }

    private static DateOnly AsDate(string text, string label)
    {
        if (!DateOnly.TryParseExact(text, s_dateFormats, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var date))
        {
            throw new DomainValidationException($"{label} must be a valid date in DD.MM.YYYY format.");
        }

        return date;
    }

    private static string FormatDate(DateOnly date)
        => date.ToString("dd.MM.yyyy", System.Globalization.CultureInfo.InvariantCulture);

    private static string TodayText()
        => FormatDate(DateOnly.FromDateTime(DateTime.Today));

    private static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Concat(name.Select(c => invalid.Contains(c) ? '_' : c)).Trim();
    }

    private void Apply(SeriesDetails details)
    {
        if (_current?.Id != details.Id)
        {
            ResetSeriesCalendar();
            ClearActiveRace();
            ResetTimingUi();
            _settingDraw = true;
            DrawCompetition = null;
            DrawRevision = null;
            _drawDesk = null;
            _sourceRun = null;
            _drawLoad++;
            _settingDraw = false;
            IsFisPanelOpen = false;
            FisEffectiveDateText = string.Empty;
        }
        var deskCompetitionId = DeskCompetition?.Id;
        _current = details;
        Name = details.Values.Name;
        Location = details.Values.Location;
        Organizer = details.Values.Organizer;
        StartDateText = FormatDate(details.Values.StartDate);
        EndDateText = FormatDate(details.Values.EndDate);
        Nation = details.Values.Nation;
        Season = details.Values.Season;
        Competitions.Clear();
            FisCompetitions.Clear();
        foreach (var competition in details.Competitions)
        {
            Competitions.Add(competition);
            if (competition.Values.RaceType == RaceType.Fis) { FisCompetitions.Add(competition); }
        }
        OnPropertyChanged(nameof(ActiveRaceLabel));
        DeskCompetition = Competitions.FirstOrDefault(x => x.Id == deskCompetitionId) ?? Competitions.FirstOrDefault();
        FileLabel = workspace.FilePath ?? "No series file open";
    }

    private async Task GuardAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex) when (ex is DomainValidationException or SeriesFileException or SeriesConflictException)
        {
            SetStatus(ex.Message, error: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            SetStatus("The operation failed. Your edits remain on screen; check the file and try again.", error: true);
        }
    }

    private void SetStatus(string text, bool error = false)
    {
        StatusMessage = text;
        IsError = error;
    }
}
