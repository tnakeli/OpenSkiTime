using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;

namespace OpenSkiTime.Desktop.ViewModels;

/// <summary>
/// Top-level VM. Owns the overview VM and swaps in a competition editor
/// or the import view when requested.
/// </summary>
public sealed partial class ShellViewModel : ViewModelBase
{
    private readonly IServiceProvider _services;
    private readonly EventSeriesOverviewViewModel _overview;

    public ShellViewModel(IServiceProvider services, EventSeriesOverviewViewModel overview)
    {
        _services = services;
        _overview = overview;
        _overview.CompetitionEditorRequested += OnCompetitionEditorRequested;
        CurrentContent = _overview;
    }

    [ObservableProperty]
    private object _currentContent;

    public EventSeriesOverviewViewModel Overview => _overview;

    public async Task LoadAsync()
    {
        await _overview.LoadAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task NavigateToImportAsync()
    {
        var vm = _services.GetRequiredService<ImportViewModel>();
        vm.Cancelled += (_, _) => CurrentContent = _overview;
        CurrentContent = vm;
        await vm.LoadAsync().ConfigureAwait(true);
    }

    private void OnCompetitionEditorRequested(object? sender, CompetitionEditorRequestedEventArgs e)
    {
        var editor = _services.GetRequiredService<CompetitionEditorViewModel>();
        if (e.Competition is null)
        {
            editor.InitializeForCreate(e.EventSeriesId);
        }
        else
        {
            editor.InitializeForEdit(e.EventSeriesId, e.Competition);
        }

        editor.Saved += async (_, _) =>
        {
            CurrentContent = _overview;
            await _overview.SelectSeriesAsync(e.EventSeriesId).ConfigureAwait(true);
        };
        editor.Cancelled += (_, _) => CurrentContent = _overview;

        CurrentContent = editor;
    }
}
