using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenSkiTime.Rewrite.Application;
using OpenSkiTime.Rewrite.Domain;

namespace OpenSkiTime.Rewrite.Desktop;

public sealed partial class MainViewModel(SeriesWorkspace workspace, IFileDialogs dialogs) : ObservableObject
{
    private static readonly string[] s_dateFormats = ["dd.MM.yyyy", "d.M.yyyy"];
    private SeriesDetails? _current;
    private Guid? _editingCompetitionId;

    public ObservableCollection<CompetitionDetails> Competitions { get; } = [];
    public IReadOnlyList<Discipline> Disciplines { get; } = Enum.GetValues<Discipline>();
    public IReadOnlyList<RaceType> RaceTypes { get; } = Enum.GetValues<RaceType>();

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

    [ObservableProperty] private string _fileLabel = "No series file open";
    [ObservableProperty] private string _statusMessage = "Create a series file or open an existing one.";
    [ObservableProperty] private bool _isError;
    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _location = string.Empty;
    [ObservableProperty] private string _organizer = string.Empty;
    [ObservableProperty] private string _startDateText = TodayText();
    [ObservableProperty] private string _endDateText = TodayText();
    [ObservableProperty] private string _nation = "FIN";
    [ObservableProperty] private string _season = string.Empty;
    [ObservableProperty] private CompetitionDetails? _selectedCompetition;
    [ObservableProperty] private bool _isCompetitionEditing;
    [ObservableProperty] private string _competitionEditorTitle = "Competition";
    [ObservableProperty] private string _competitionName = string.Empty;
    [ObservableProperty] private string _competitionShortLabel = string.Empty;
    [ObservableProperty] private string _competitionDateText = TodayText();
    [ObservableProperty] private Discipline _competitionDiscipline = Discipline.Slalom;
    [ObservableProperty] private RaceType _competitionRaceType = RaceType.Club;
    [ObservableProperty] private int _competitionRunCount = 2;
    [ObservableProperty] private int _competitionIntermediateCount;
    [ObservableProperty] private string _competitionFisCode = string.Empty;
    [ObservableProperty] private string _competitionLocalRaceCode = string.Empty;
    [ObservableProperty] private string _competitionCourseName = string.Empty;
    [ObservableProperty] private string _competitionStartAltitude = string.Empty;
    [ObservableProperty] private string _competitionFinishAltitude = string.Empty;
    [ObservableProperty] private string _competitionVerticalDrop = string.Empty;
    [ObservableProperty] private string _competitionHomologation = string.Empty;

    partial void OnSelectedCompetitionChanged(CompetitionDetails? value)
    {
        if (value is null) { return; }
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
        CompetitionLocalRaceCode = value.Values.LocalRaceCode ?? string.Empty;
        CompetitionCourseName = value.Values.CourseName ?? string.Empty;
        CompetitionStartAltitude = value.Values.StartAltitudeMeters?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
        CompetitionFinishAltitude = value.Values.FinishAltitudeMeters?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
        CompetitionVerticalDrop = value.Values.VerticalDropMeters?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
        CompetitionHomologation = value.Values.HomologationNumber ?? string.Empty;
        IsCompetitionEditing = true;
    }

    [RelayCommand]
    private void NewSeries()
    {
        IsCreatingNew = true;
        Name = Location = Organizer = Season = string.Empty;
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
            var values = DraftSeries().Validated();
            var path = await dialogs.ChooseNewAsync(SafeFileName(values.Name));
            if (path is null) { return; }
            var details = await workspace.CreateAsync(path, values);
            Apply(details);
            IsOpen = true;
            IsCreatingNew = false;
            SetStatus("Event series created and saved.");
        });
    }

    [RelayCommand]
    private async Task OpenSeriesAsync()
    {
        await GuardAsync(async () =>
        {
            var path = await dialogs.ChooseOpenAsync();
            if (path is null) { return; }
            var details = await workspace.OpenAsync(path);
            Apply(details);
            IsOpen = true;
            IsCreatingNew = false;
            SetStatus("Event series opened.");
        });
    }

    [RelayCommand]
    private async Task CloseSeriesAsync()
    {
        await GuardAsync(async () =>
        {
            await workspace.CloseAsync();
            _current = null;
            Competitions.Clear();
            SelectedCompetition = null;
            IsCompetitionEditing = false;
            IsOpen = false;
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
            if (_current is null) { throw new SeriesFileException("Open an event series first."); }
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
        CompetitionName = CompetitionShortLabel = CompetitionFisCode = CompetitionLocalRaceCode = string.Empty;
        CompetitionCourseName = CompetitionStartAltitude = CompetitionFinishAltitude = string.Empty;
        CompetitionVerticalDrop = CompetitionHomologation = string.Empty;
        CompetitionDateText = StartDateText;
        CompetitionDiscipline = Discipline.Slalom;
        CompetitionRaceType = RaceType.Club;
        CompetitionRunCount = 2;
        CompetitionIntermediateCount = 0;
        IsCompetitionEditing = true;
    }

    [RelayCommand]
    private async Task SaveCompetitionAsync()
    {
        await GuardAsync(async () =>
        {
            var revision = _current?.Revision ?? throw new SeriesFileException("Open an event series first.");
            var values = DraftCompetition().Validated();
            var details = await workspace.SaveCompetitionAsync(_editingCompetitionId, values, revision);
            var id = _editingCompetitionId;
            Apply(details);
            SelectedCompetition = id is null
                ? details.Competitions.Single(c => c.Values.ShortLabel.Equals(values.ShortLabel, StringComparison.OrdinalIgnoreCase))
                : details.Competitions.FirstOrDefault(c => c.Id == id);
            SetStatus("Competition saved.");
        });
    }

    [RelayCommand]
    private async Task RemoveCompetitionAsync()
    {
        await GuardAsync(async () =>
        {
            if (_current is null || _editingCompetitionId is not { } id)
            {
                throw new SeriesFileException("Select a saved competition first.");
            }
            if (!await dialogs.ConfirmRemoveAsync(CompetitionName)) { return; }
            var details = await workspace.RemoveCompetitionAsync(id, _current.Revision);
            Apply(details);
            SelectedCompetition = null;
            IsCompetitionEditing = false;
            SetStatus("Competition removed.");
        });
    }

    private SeriesValues DraftSeries() => new(Name, Location, Organizer,
        AsDate(StartDateText, "Start date"), AsDate(EndDateText, "End date"), Nation, Season);

    private CompetitionValues DraftCompetition() => new(
        CompetitionName, CompetitionShortLabel, AsDate(CompetitionDateText, "Competition date"),
        CompetitionDiscipline, CompetitionRaceType, CompetitionRunCount, CompetitionIntermediateCount,
        CompetitionFisCode, CompetitionLocalRaceCode, CompetitionCourseName,
        OptionalInt(CompetitionStartAltitude, "Start altitude"),
        OptionalInt(CompetitionFinishAltitude, "Finish altitude"),
        OptionalInt(CompetitionVerticalDrop, "Vertical drop"), CompetitionHomologation);

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
        _current = details;
        Name = details.Values.Name;
        Location = details.Values.Location;
        Organizer = details.Values.Organizer;
        StartDateText = FormatDate(details.Values.StartDate);
        EndDateText = FormatDate(details.Values.EndDate);
        Nation = details.Values.Nation;
        Season = details.Values.Season;
        Competitions.Clear();
        foreach (var competition in details.Competitions)
        {
            Competitions.Add(competition);
        }
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
