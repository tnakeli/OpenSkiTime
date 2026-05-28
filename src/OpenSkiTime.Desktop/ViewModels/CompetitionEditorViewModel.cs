using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenSkiTime.Application.Common;
using OpenSkiTime.Application.Competitions;
using OpenSkiTime.Desktop.Services;
using OpenSkiTime.Domain.Common;
using OpenSkiTime.Domain.Competitions;
using OpenSkiTime.Fis;

namespace OpenSkiTime.Desktop.ViewModels;

/// <summary>
/// Editor for a single Competition's basic data. Used both for "New
/// Competition" (no <see cref="CompetitionId"/>) and edit-existing
/// flows. The "Update from FIS API" command is wired to
/// <see cref="IFisCompetitionUpdater"/> which is the no-op placeholder
/// in feature 001 (FR-011, SC-007).
/// </summary>
public sealed partial class CompetitionEditorViewModel : ViewModelBase
{
    private readonly AddCompetitionUseCase _addUseCase;
    private readonly UpdateCompetitionUseCase _updateUseCase;
    private readonly IFisCompetitionUpdater _fisUpdater;
    private readonly IDialogService _dialogs;

    public CompetitionEditorViewModel(
        AddCompetitionUseCase addUseCase,
        UpdateCompetitionUseCase updateUseCase,
        IFisCompetitionUpdater fisUpdater,
        IDialogService dialogs)
    {
        _addUseCase = addUseCase;
        _updateUseCase = updateUseCase;
        _fisUpdater = fisUpdater;
        _dialogs = dialogs;
        Disciplines = Enum.GetValues<Discipline>();
        RaceTypes = Enum.GetValues<RaceType>();
        Genders = new Gender?[]
        {
            null,
            Domain.Common.Gender.Male,
            Domain.Common.Gender.Female,
            Domain.Common.Gender.Other,
        };
        Date = DateOnly.FromDateTime(DateTime.Today);
    }

    public Discipline[] Disciplines { get; }
    public RaceType[] RaceTypes { get; }
    public Gender?[] Genders { get; }

    public Guid EventSeriesId { get; private set; }
    public Guid? CompetitionId { get; private set; }

    [ObservableProperty]
    private string _title = "New Competition";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private string _name = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private string _shortLabel = string.Empty;

    [ObservableProperty]
    private DateOnly _date;

    [ObservableProperty]
    private Discipline _discipline = Discipline.SL;

    [ObservableProperty]
    private RaceType _raceType = RaceType.Club;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private int _numberOfRuns = 2;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private int _numberOfIntermediateTimes = 1;

    [ObservableProperty]
    private string? _fisCode;

    [ObservableProperty]
    private string? _localRaceCode;

    [ObservableProperty]
    private Gender? _gender;

    [ObservableProperty]
    private string? _courseName;

    [ObservableProperty]
    private int? _startAltitudeMeters;

    [ObservableProperty]
    private int? _finishAltitudeMeters;

    [ObservableProperty]
    private int? _verticalDropMeters;

    [ObservableProperty]
    private string? _homologationNumber;

    [ObservableProperty]
    private string? _statusMessage;

    /// <summary>Set when Save succeeded; the shell uses this to navigate back.</summary>
    public event EventHandler? Saved;

    public event EventHandler? Cancelled;

    public void InitializeForCreate(Guid eventSeriesId)
    {
        EventSeriesId = eventSeriesId;
        CompetitionId = null;
        Title = "New Competition";
    }

    public void InitializeForEdit(Guid eventSeriesId, Competition competition)
    {
        ArgumentNullException.ThrowIfNull(competition);

        EventSeriesId = eventSeriesId;
        CompetitionId = competition.Id;
        Title = $"Edit Competition — {competition.Name}";

        Name = competition.Name;
        ShortLabel = competition.ShortLabel;
        Date = competition.Date;
        Discipline = competition.Discipline;
        RaceType = competition.RaceType;
        NumberOfRuns = competition.NumberOfRuns;
        NumberOfIntermediateTimes = competition.NumberOfIntermediateTimes;
        FisCode = competition.FisCode;
        LocalRaceCode = competition.LocalRaceCode;
        Gender = competition.Gender;
        CourseName = competition.CourseName;
        StartAltitudeMeters = competition.StartAltitudeMeters;
        FinishAltitudeMeters = competition.FinishAltitudeMeters;
        VerticalDropMeters = competition.VerticalDropMeters;
        HomologationNumber = competition.HomologationNumber;
    }

    private bool CanSave() =>
        !string.IsNullOrWhiteSpace(Name) &&
        !string.IsNullOrWhiteSpace(ShortLabel) &&
        NumberOfRuns >= 1 &&
        NumberOfIntermediateTimes >= 0;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        Result result;
        if (CompetitionId is null)
        {
            var addResult = await _addUseCase.ExecuteAsync(new AddCompetitionCommand(
                EventSeriesId, Name, ShortLabel, Date, Discipline, RaceType,
                NumberOfRuns, NumberOfIntermediateTimes,
                FisCode, LocalRaceCode, Gender, CourseName,
                StartAltitudeMeters, FinishAltitudeMeters, VerticalDropMeters,
                HomologationNumber)).ConfigureAwait(true);
            result = addResult.Succeeded
                ? Result.Success()
                : Result.Failure(addResult.ErrorMessage!);
        }
        else
        {
            result = await _updateUseCase.ExecuteAsync(new UpdateCompetitionCommand(
                EventSeriesId, CompetitionId.Value, Name, ShortLabel, Date, Discipline,
                RaceType, NumberOfRuns, NumberOfIntermediateTimes,
                FisCode, LocalRaceCode, Gender, CourseName,
                StartAltitudeMeters, FinishAltitudeMeters, VerticalDropMeters,
                HomologationNumber)).ConfigureAwait(true);
        }

        if (!result.Succeeded)
        {
            await _dialogs.ShowErrorAsync("Could not save competition", result.ErrorMessage ?? "Unknown error.").ConfigureAwait(true);
            StatusMessage = result.ErrorMessage;
            return;
        }

        Saved?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void Cancel() => Cancelled?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private async Task UpdateFromFisAsync()
    {
        var result = await _fisUpdater.UpdateAsync(CompetitionId ?? Guid.Empty).ConfigureAwait(true);
        var message = result switch
        {
            FisUpdateResult.NotImplemented ni => ni.UserMessage,
            FisUpdateResult.Updated u => $"Updated {u.FieldsChanged} fields from FIS.",
            FisUpdateResult.Failed f => $"FIS update failed: {f.Reason}",
            _ => "Unknown FIS result.",
        };
        StatusMessage = message;
        await _dialogs.ShowMessageAsync("Update from FIS API", message).ConfigureAwait(true);
    }
}
