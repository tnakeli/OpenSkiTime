using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenSkiTime.Application.Abstractions;
using OpenSkiTime.Application.Competitors;
using OpenSkiTime.Desktop.Services;

namespace OpenSkiTime.Desktop.ViewModels;

public enum CompetitorViewMode { Flat, ByClub, ByNation }

/// <summary>
/// Backing VM for the competitor list screen (US2).
/// Shows competitors for the currently selected Event Series.
/// </summary>
public sealed partial class CompetitorGridViewModel : ViewModelBase
{
    private readonly IEventSeriesRepository _repository;
    private readonly AddCompetitorUseCase _addCompetitor;
    private readonly IDialogService _dialogs;

    private Guid _seriesId;

    public CompetitorGridViewModel(
        IEventSeriesRepository repository,
        AddCompetitorUseCase addCompetitor,
        IDialogService dialogs)
    {
        _repository = repository;
        _addCompetitor = addCompetitor;
        _dialogs = dialogs;
    }

    public ObservableCollection<CompetitorRowViewModel> Competitors { get; } = [];

    [ObservableProperty] private string _filterText = string.Empty;

    [ObservableProperty] private CompetitorViewMode _viewMode = CompetitorViewMode.Flat;

    [ObservableProperty] private string _statusMessage = string.Empty;

    public event EventHandler? ImportRequested;

    public async Task LoadAsync(Guid seriesId)
    {
        _seriesId = seriesId;
        await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        var series = await _repository.GetByIdAsync(_seriesId);
        if (series is null)
        {
            return;
        }

        Competitors.Clear();
        foreach (var c in series.Competitors.OrderBy(c => c.LastName.Value).ThenBy(c => c.FirstName))
        {
            Competitors.Add(new CompetitorRowViewModel(c));
        }

        StatusMessage = $"{Competitors.Count} competitor(s)";
    }

    [RelayCommand]
    private void RequestImport() => ImportRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private async Task RefreshAsync_()
    {
        await RefreshAsync();
    }
}

/// <summary>Read-only row VM for a single competitor in the grid.</summary>
public sealed class CompetitorRowViewModel
{
    public CompetitorRowViewModel(OpenSkiTime.Domain.Competitors.Competitor c)
    {
        ArgumentNullException.ThrowIfNull(c);
        Id = c.Id;
        LastName = c.LastName.Value;
        FirstName = c.FirstName;
        YearOfBirth = c.YearOfBirth;
        Gender = c.Gender?.ToString() ?? string.Empty;
        NationCode = c.NationCode ?? string.Empty;
        ClubName = c.ClubName ?? string.Empty;
        FisCode = c.FisCode ?? string.Empty;
        BibNumber = c.BibNumber?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
    }

    public Guid Id { get; }
    public string LastName { get; }
    public string FirstName { get; }
    public int YearOfBirth { get; }
    public string Gender { get; }
    public string NationCode { get; }
    public string ClubName { get; }
    public string FisCode { get; }
    public string BibNumber { get; }
}
