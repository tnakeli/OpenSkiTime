using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenSkiTime.Application.Abstractions;
using OpenSkiTime.Desktop.Services;
using OpenSkiTime.Import;

namespace OpenSkiTime.Desktop.ViewModels;

/// <summary>
/// Backing VM for the TSV-paste import screen (US3).
/// Flow: paste → Preview → review diff → Apply.
/// </summary>
public sealed partial class ImportViewModel : ViewModelBase
{
    private readonly ImportPreviewService _preview;
    private readonly ImportApplyService _apply;
    private readonly IEventSeriesRepository _repository;
    private readonly IDialogService _dialogs;

    private long _snapshotVersion;

    public ImportViewModel(
        ImportPreviewService preview,
        ImportApplyService apply,
        IEventSeriesRepository repository,
        IDialogService dialogs)
    {
        _preview = preview;
        _apply = apply;
        _repository = repository;
        _dialogs = dialogs;
    }

    // ── Series selection ─────────────────────────────────────────────────────

    public ObservableCollection<EventSeriesSummary> SeriesList { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PreviewCommand))]
    private EventSeriesSummary? _selectedSeries;

    // ── Paste area ────────────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PreviewCommand))]
    private string _pasteText = string.Empty;

    // ── Preview state ─────────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    private bool _hasPreview;

    [ObservableProperty]
    private string? _statusMessage;

    [ObservableProperty]
    private bool _isBusy;

    public ObservableCollection<NewCompetitorRow> NewCompetitors { get; } = [];
    public ObservableCollection<BibRow> BibAssignments { get; } = [];
    public ObservableCollection<ParticipationRow> Participations { get; } = [];
    public ObservableCollection<WarningRow> Warnings { get; } = [];

    // ── Back-navigation ───────────────────────────────────────────────────────

    /// <summary>Raised when the user clicks Cancel / Back.</summary>
    public event EventHandler? Cancelled;

    // ── Commands ──────────────────────────────────────────────────────────────

    public async Task LoadAsync(CancellationToken ct = default)
    {
        SeriesList.Clear();
        foreach (var s in await _repository.ListAsync(ct).ConfigureAwait(true))
        {
            SeriesList.Add(s);
        }

        if (SeriesList.Count == 1)
        {
            SelectedSeries = SeriesList[0];
        }
    }

    private bool CanPreview() =>
        SelectedSeries is not null && !string.IsNullOrWhiteSpace(PasteText);

    [RelayCommand(CanExecute = nameof(CanPreview))]
    private async Task PreviewAsync(CancellationToken ct)
    {
        if (SelectedSeries is null)
        {
            return;
        }

        IsBusy = true;
        StatusMessage = null;
        ClearPreview();

        try
        {
            var result = await _preview.PreviewAsync(
                SelectedSeries.Id, PasteText, ct).ConfigureAwait(true);

            if (!result.Succeeded)
            {
                StatusMessage = $"Preview failed: {result.ErrorMessage}";
                return;
            }

            var pr = result.Value!;
            _snapshotVersion = pr.SnapshotVersion;

            foreach (var nc in pr.Diff.NewCompetitors)
            {
                NewCompetitors.Add(new NewCompetitorRow(
                    nc.LastName, nc.FirstName, nc.YearOfBirth,
                    nc.NationCode, nc.FisCode, nc.ClubName,
                    nc.BibNumber,
                    string.Join(", ", nc.ParticipatingIn)));
            }

            foreach (var ba in pr.Diff.BibAssignments)
            {
                BibAssignments.Add(new BibRow(
                    ba.LastName, ba.FirstName, ba.OldBib, ba.NewBib));
            }

            foreach (var pd in pr.Diff.Participations)
            {
                Participations.Add(new ParticipationRow(
                    pd.LastName, pd.FirstName,
                    pd.CompetitionShortLabel, pd.IsParticipating));
            }

            foreach (var w in pr.Diff.Warnings)
            {
                Warnings.Add(new WarningRow(w.RowIndex, w.Message));
            }

            HasPreview = pr.Diff.HasChanges || pr.Diff.Warnings.Count > 0;

            StatusMessage = pr.Diff.HasChanges
                ? $"Preview ready — {pr.Diff.NewCompetitors.Count} new, " +
                  $"{pr.Diff.BibAssignments.Count} bibs, " +
                  $"{pr.Diff.Participations.Count} participation changes."
                : "No changes detected.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanApply() => HasPreview && SelectedSeries is not null;

    [RelayCommand(CanExecute = nameof(CanApply))]
    private async Task ApplyAsync(CancellationToken ct)
    {
        if (SelectedSeries is null)
        {
            return;
        }

        IsBusy = true;
        StatusMessage = null;

        try
        {
            // Re-build the diff from the current paste to pass to ApplyAsync.
            var previewResult = await _preview.PreviewAsync(
                SelectedSeries.Id, PasteText, ct).ConfigureAwait(true);

            if (!previewResult.Succeeded)
            {
                StatusMessage = $"Apply failed: {previewResult.ErrorMessage}";
                return;
            }

            var applyResult = await _apply.ApplyAsync(
                SelectedSeries.Id,
                _snapshotVersion,
                previewResult.Value!.Diff,
                ct).ConfigureAwait(true);

            if (!applyResult.Succeeded)
            {
                await _dialogs.ShowErrorAsync(
                    "Import failed", applyResult.ErrorMessage!).ConfigureAwait(true);
                return;
            }

            var r = applyResult.Value!;

            if (r.HasErrors)
            {
                var errList = string.Join("\n• ", r.Errors);
                await _dialogs.ShowErrorAsync(
                    "Import completed with warnings",
                    $"Some rows could not be applied:\n• {errList}").ConfigureAwait(true);
            }

            StatusMessage =
                $"Applied — {r.CompetitorsAdded} added, " +
                $"{r.BibsAssigned} bibs, " +
                $"{r.ParticipationsSet} participations.";

            ClearPreview();
            PasteText = string.Empty;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Cancel() => Cancelled?.Invoke(this, EventArgs.Empty);

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void ClearPreview()
    {
        HasPreview = false;
        NewCompetitors.Clear();
        BibAssignments.Clear();
        Participations.Clear();
        Warnings.Clear();
    }
}

// ── Display row records ───────────────────────────────────────────────────────

public sealed record NewCompetitorRow(
    string LastName,
    string FirstName,
    int YearOfBirth,
    string? Nation,
    string? FisCode,
    string? Club,
    int? Bib,
    string ParticipatingIn);

public sealed record BibRow(
    string LastName,
    string FirstName,
    int? OldBib,
    int NewBib);

public sealed record ParticipationRow(
    string LastName,
    string FirstName,
    string Competition,
    bool IsParticipating);

public sealed record WarningRow(int RowIndex, string Message);
