using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.DependencyInjection;

namespace OpenSkiTime.Desktop.ViewModels;

/// <summary>
/// Top-level VM. Owns the overview VM and swaps in a competition editor
/// when the user starts one. Keeps content swapping deliberately simple
/// for feature 001 — there are only two views to switch between.
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
