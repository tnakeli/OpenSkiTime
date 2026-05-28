using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenSkiTime.Application.Abstractions;
using OpenSkiTime.Application.Competitions;
using OpenSkiTime.Application.Series;
using OpenSkiTime.Desktop.Services;
using OpenSkiTime.Domain.Competitions;
using OpenSkiTime.Domain.Series;

namespace OpenSkiTime.Desktop.ViewModels;

/// <summary>
/// Event Series overview: list of all series in the local database, plus
/// (when one is selected) its basic data form and competitions list.
/// Hosts the Delete-Series and Remove-Competition commands per US1
/// post-/speckit.analyze remediation (T135, T136).
/// </summary>
public sealed partial class EventSeriesOverviewViewModel : ViewModelBase
{
    private readonly IEventSeriesRepository _repository;
    private readonly CreateEventSeriesUseCase _createUseCase;
    private readonly UpdateEventSeriesUseCase _updateUseCase;
    private readonly DeleteEventSeriesUseCase _deleteUseCase;
    private readonly RemoveCompetitionUseCase _removeCompetitionUseCase;
    private readonly IDialogService _dialogs;

    public EventSeriesOverviewViewModel(
        IEventSeriesRepository repository,
        CreateEventSeriesUseCase createUseCase,
        UpdateEventSeriesUseCase updateUseCase,
        DeleteEventSeriesUseCase deleteUseCase,
        RemoveCompetitionUseCase removeCompetitionUseCase,
        IDialogService dialogs)
    {
        _repository = repository;
        _createUseCase = createUseCase;
        _updateUseCase = updateUseCase;
        _deleteUseCase = deleteUseCase;
        _removeCompetitionUseCase = removeCompetitionUseCase;
        _dialogs = dialogs;
    }

    public ObservableCollection<EventSeriesSummary> SeriesList { get; } = [];

    public ObservableCollection<Competition> Competitions { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveSeriesCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteSeriesCommand))]
    [NotifyCanExecuteChangedFor(nameof(NewCompetitionCommand))]
    private EventSeries? _currentSeries;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveSeriesCommand))]
    private string _name = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveSeriesCommand))]
    private string _location = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveSeriesCommand))]
    private string _organizer = string.Empty;

    [ObservableProperty]
    private DateOnly _startDate = DateOnly.FromDateTime(DateTime.Today);

    [ObservableProperty]
    private DateOnly _endDate = DateOnly.FromDateTime(DateTime.Today);

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveSeriesCommand))]
    private string _nation = "FIN";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveSeriesCommand))]
    private string _season = string.Empty;

    [ObservableProperty]
    private bool _isCreatingNew;

    [ObservableProperty]
    private string? _statusMessage;

    /// <summary>Raised when the user clicks "+ New Competition" or edits an existing one.</summary>
    public event EventHandler<CompetitionEditorRequestedEventArgs>? CompetitionEditorRequested;

    public async Task LoadAsync(CancellationToken ct = default)
    {
        SeriesList.Clear();
        foreach (var s in await _repository.ListAsync(ct).ConfigureAwait(true))
        {
            SeriesList.Add(s);
        }
    }

    public async Task SelectSeriesAsync(Guid id, CancellationToken ct = default)
    {
        var series = await _repository.GetByIdAsync(id, ct).ConfigureAwait(true);
        BindToSeries(series);
    }

    private void BindToSeries(EventSeries? series)
    {
        IsCreatingNew = false;
        CurrentSeries = series;
        if (series is null)
        {
            Name = Location = Organizer = Season = string.Empty;
            Nation = "FIN";
            StartDate = EndDate = DateOnly.FromDateTime(DateTime.Today);
            Competitions.Clear();
            return;
        }

        Name = series.Name;
        Location = series.Location;
        Organizer = series.Organizer;
        StartDate = series.StartDate;
        EndDate = series.EndDate;
        Nation = series.Nation;
        Season = series.Season;

        Competitions.Clear();
        foreach (var c in series.Competitions.OrderBy(c => c.Date))
        {
            Competitions.Add(c);
        }
    }

    [RelayCommand]
    private void NewSeries()
    {
        CurrentSeries = null;
        Competitions.Clear();
        Name = string.Empty;
        Location = string.Empty;
        Organizer = string.Empty;
        StartDate = DateOnly.FromDateTime(DateTime.Today);
        EndDate = DateOnly.FromDateTime(DateTime.Today);
        Nation = "FIN";
        Season = string.Empty;
        IsCreatingNew = true;
        StatusMessage = null;
    }

    private bool CanSaveSeries() =>
        !string.IsNullOrWhiteSpace(Name) &&
        !string.IsNullOrWhiteSpace(Location) &&
        !string.IsNullOrWhiteSpace(Organizer) &&
        !string.IsNullOrWhiteSpace(Nation) &&
        Nation.Trim().Length == 3 &&
        !string.IsNullOrWhiteSpace(Season) &&
        EndDate >= StartDate &&
        (CurrentSeries is not null || IsCreatingNew);

    [RelayCommand(CanExecute = nameof(CanSaveSeries))]
    private async Task SaveSeriesAsync()
    {
        if (IsCreatingNew)
        {
            var result = await _createUseCase.ExecuteAsync(new CreateEventSeriesCommand(
                Name, Location, Organizer, StartDate, EndDate, Nation, Season))
                .ConfigureAwait(true);

            if (!result.Succeeded)
            {
                await _dialogs.ShowErrorAsync("Could not create Event Series", result.ErrorMessage!).ConfigureAwait(true);
                return;
            }

            await LoadAsync().ConfigureAwait(true);
            await SelectSeriesAsync(result.Value).ConfigureAwait(true);
            StatusMessage = "Event Series created.";
        }
        else if (CurrentSeries is { } current)
        {
            var result = await _updateUseCase.ExecuteAsync(new UpdateEventSeriesCommand(
                current.Id, Name, Location, Organizer, StartDate, EndDate, Nation, Season))
                .ConfigureAwait(true);

            if (!result.Succeeded)
            {
                await _dialogs.ShowErrorAsync("Could not save Event Series", result.ErrorMessage!).ConfigureAwait(true);
                return;
            }

            await LoadAsync().ConfigureAwait(true);
            await SelectSeriesAsync(current.Id).ConfigureAwait(true);
            StatusMessage = "Saved.";
        }
    }

    private bool CanDeleteSeries() => CurrentSeries is not null;

    [RelayCommand(CanExecute = nameof(CanDeleteSeries))]
    private async Task DeleteSeriesAsync()
    {
        if (CurrentSeries is not { } current)
        {
            return;
        }

        var confirmed = await _dialogs.ConfirmAsync(
            "Delete Event Series",
            $"Delete '{current.Name}'? This will also remove all of its competitions and competitors. This cannot be undone.")
            .ConfigureAwait(true);
        if (!confirmed)
        {
            return;
        }

        var result = await _deleteUseCase.ExecuteAsync(current.Id).ConfigureAwait(true);
        if (!result.Succeeded)
        {
            await _dialogs.ShowErrorAsync("Could not delete Event Series", result.ErrorMessage!).ConfigureAwait(true);
            return;
        }

        await LoadAsync().ConfigureAwait(true);
        BindToSeries(null);
        StatusMessage = "Event Series deleted.";
    }

    private bool CanCreateCompetition() => CurrentSeries is not null;

    [RelayCommand(CanExecute = nameof(CanCreateCompetition))]
    private void NewCompetition()
    {
        if (CurrentSeries is { } current)
        {
            CompetitionEditorRequested?.Invoke(this,
                new CompetitionEditorRequestedEventArgs(current.Id, null));
        }
    }

    [RelayCommand]
    private void EditCompetition(Competition competition)
    {
        if (CurrentSeries is { } current && competition is not null)
        {
            CompetitionEditorRequested?.Invoke(this,
                new CompetitionEditorRequestedEventArgs(current.Id, competition));
        }
    }

    [RelayCommand]
    private async Task RemoveCompetitionAsync(Competition competition)
    {
        if (CurrentSeries is not { } current || competition is null)
        {
            return;
        }

        var confirmed = await _dialogs.ConfirmAsync(
            "Remove Competition",
            $"Remove competition '{competition.Name}' ({competition.ShortLabel})? This cannot be undone.")
            .ConfigureAwait(true);
        if (!confirmed)
        {
            return;
        }

        var result = await _removeCompetitionUseCase.ExecuteAsync(current.Id, competition.Id).ConfigureAwait(true);
        if (!result.Succeeded)
        {
            await _dialogs.ShowErrorAsync("Could not remove competition", result.ErrorMessage!).ConfigureAwait(true);
            return;
        }

        await SelectSeriesAsync(current.Id).ConfigureAwait(true);
        StatusMessage = $"Removed '{competition.ShortLabel}'.";
    }
}

public sealed class CompetitionEditorRequestedEventArgs : EventArgs
{
    public CompetitionEditorRequestedEventArgs(Guid eventSeriesId, Competition? competition)
    {
        EventSeriesId = eventSeriesId;
        Competition = competition;
    }

    public Guid EventSeriesId { get; }
    public Competition? Competition { get; }
}
