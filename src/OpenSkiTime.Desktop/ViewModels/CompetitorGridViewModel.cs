using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenSkiTime.Application.Abstractions;
using OpenSkiTime.Application.Competitors;
using OpenSkiTime.Application.Participations;
using OpenSkiTime.Desktop.Models;
using OpenSkiTime.Desktop.Services;
using OpenSkiTime.Import;

namespace OpenSkiTime.Desktop.ViewModels;

public enum CompetitorViewMode { Flat, ByClub, ByNation }

/// <summary>
/// Backing VM for the competitor grid (feature 002).
/// Supports inline add/edit/delete with Change Log undo, inline paste, and clipboard copy.
/// </summary>
public sealed partial class CompetitorGridViewModel : ViewModelBase
{
    private const int ChangeLogCap = 200;

    private static readonly string[] s_copyHeaders =
        ["FIS Code", "Last Name", "First Name", "Year", "Gender", "Nation", "Club"];

    private readonly IEventSeriesRepository _repository;
    private readonly AddCompetitorUseCase _addCompetitor;
    private readonly EditCompetitorUseCase _editCompetitor;
    private readonly RemoveCompetitorUseCase _removeCompetitor;
    private readonly SetParticipationUseCase _setParticipation;
    private readonly IDialogService _dialogs;
    private readonly IClipboardService _clipboard;
    private readonly ImportPreviewService _preview;
    private readonly ImportApplyService _apply;

    private Guid _seriesId;
    private ImportPasteSession? _pasteSession;

    public CompetitorGridViewModel(
        IEventSeriesRepository repository,
        AddCompetitorUseCase addCompetitor,
        EditCompetitorUseCase editCompetitor,
        RemoveCompetitorUseCase removeCompetitor,
        SetParticipationUseCase setParticipation,
        IDialogService dialogs,
        IClipboardService clipboard,
        ImportPreviewService preview,
        ImportApplyService apply)
    {
        _repository = repository;
        _addCompetitor = addCompetitor;
        _editCompetitor = editCompetitor;
        _removeCompetitor = removeCompetitor;
        _setParticipation = setParticipation;
        _dialogs = dialogs;
        _clipboard = clipboard;
        _preview = preview;
        _apply = apply;
    }

    public ObservableCollection<CompetitorRowViewModel> Competitors { get; } = [];
    public ObservableCollection<ChangeLogEntry> ChangeLog { get; } = [];
    public ObservableCollection<object> SelectedItems { get; } = [];

    /// <summary>Ordered list of competitions — drives dynamic DataGrid columns.</summary>
    public ObservableCollection<(Guid Id, string ShortLabel)> CompetitionColumns { get; } = [];

    [ObservableProperty] private string _filterText = string.Empty;
    [ObservableProperty] private CompetitorViewMode _viewMode = CompetitorViewMode.Flat;
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private bool _hasActivePasteSession;
    [ObservableProperty] private string? _pasteWarningMessage;

    public bool HasStagedChanges =>
        Competitors.Any(r => r.RowState != RowState.Unchanged);

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
        ChangeLog.Clear();
        HasActivePasteSession = false;
        _pasteSession = null;

        CompetitionColumns.Clear();
        var competitions = series.Competitions.OrderBy(c => c.Date).ToList();
        foreach (var comp in competitions)
        {
            CompetitionColumns.Add((comp.Id, comp.ShortLabel));
        }

        foreach (var c in series.Competitors.OrderBy(c => c.LastName.Value).ThenBy(c => c.FirstName))
        {
            var row = new CompetitorRowViewModel(c);
            row.PropertyChanged += OnRowPropertyChanged;
            foreach (var comp in competitions)
            {
                bool participating = c.Participations.Any(p => p.CompetitionId == comp.Id && p.IsParticipating);
                row.Participations.Add(new ParticipationCellViewModel(comp.Id, comp.ShortLabel, participating));
            }

            Competitors.Add(row);
        }

        StatusMessage = $"{Competitors.Count} competitor(s)";
    }

    private void OnRowPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender is not CompetitorRowViewModel row)
        {
            return;
        }

        if (e.PropertyName is nameof(CompetitorRowViewModel.RowState)
                           or nameof(CompetitorRowViewModel.IsEditing))
        {
            return;
        }

        var editableFields = new[]
        {
            nameof(CompetitorRowViewModel.LastName), nameof(CompetitorRowViewModel.FirstName),
            nameof(CompetitorRowViewModel.YearOfBirth), nameof(CompetitorRowViewModel.Gender),
            nameof(CompetitorRowViewModel.NationCode), nameof(CompetitorRowViewModel.ClubName),
            nameof(CompetitorRowViewModel.FisCode),
        };

        if (!editableFields.Contains(e.PropertyName))
        {
            return;
        }

        if (row.RowState == RowState.Added || row.RowState == RowState.Deleted)
        {
            return;
        }

        if (row.RowState == RowState.Unchanged)
        {
            row.RowState = RowState.Edited;
        }
    }

    /// <summary>
    /// Called from code-behind on CellEditEnded — records the change log entry
    /// and auto-saves the row to the database.
    /// </summary>
    public async Task OnCellEditCommittedAsync(CompetitorRowViewModel row, string fieldName)
    {
        ArgumentNullException.ThrowIfNull(row);

        if (row.RowState == RowState.Deleted)
        {
            return;
        }

        var before = row.GetOriginalFieldValue(fieldName);
        var after = row.GetFieldValue(fieldName);

        if (before != after)
        {
            AppendToLog(new ChangeLogEntry(
                Guid.NewGuid(), DateTime.UtcNow, ChangeOp.Edit,
                row.Id, fieldName,
                before, after,
                $"{row.LastName} {row.FirstName} — {fieldName} {before}→{after}"));
        }

        await AutoSaveRowAsync(row);
    }

    /// <summary>Saves a single row immediately (used for auto-save on cell exit).</summary>
    public async Task AutoSaveRowAsync(CompetitorRowViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);

        if (row.RowState == RowState.Unchanged || row.RowState == RowState.Deleted
            || row.RowState == RowState.PasteHighlighted)
        {
            return;
        }

        if (row.RowState == RowState.Added)
        {
            if (string.IsNullOrWhiteSpace(row.LastName))
            {
                return;
            }

            var result = await _addCompetitor.ExecuteAsync(new AddCompetitorRequest
            {
                EventSeriesId = _seriesId,
                LastName = row.LastName.ToUpperInvariant(),
                FirstName = row.FirstName,
                YearOfBirth = row.YearOfBirth,
                Gender = string.IsNullOrWhiteSpace(row.Gender) ? null : Enum.TryParse<OpenSkiTime.Domain.Common.Gender>(row.Gender, out var ag) ? ag : null,
                NationCode = string.IsNullOrWhiteSpace(row.NationCode) ? null : row.NationCode,
                ClubName = string.IsNullOrWhiteSpace(row.ClubName) ? null : row.ClubName,
                FisCode = string.IsNullOrWhiteSpace(row.FisCode) ? null : row.FisCode,
            });

            if (result.Succeeded)
            {
                StatusMessage = $"Saved {row.LastName}";
                await RefreshAsync();
            }
            else
            {
                StatusMessage = $"Error: {result.ErrorMessage}";
            }

            return;
        }

        if (row.RowState == RowState.Edited)
        {
            var result = await _editCompetitor.ExecuteAsync(new EditCompetitorRequest
            {
                EventSeriesId = _seriesId,
                CompetitorId = row.Id,
                LastName = row.LastName.ToUpperInvariant(),
                FirstName = row.FirstName,
                YearOfBirth = row.YearOfBirth,
                Gender = string.IsNullOrWhiteSpace(row.Gender) ? null : Enum.TryParse<OpenSkiTime.Domain.Common.Gender>(row.Gender, out var g) ? g : null,
                NationCode = string.IsNullOrWhiteSpace(row.NationCode) ? null : row.NationCode,
                ClubName = string.IsNullOrWhiteSpace(row.ClubName) ? null : row.ClubName,
                FisCode = string.IsNullOrWhiteSpace(row.FisCode) ? null : row.FisCode,
            });

            if (result.Succeeded)
            {
                row.RowState = RowState.Unchanged;
                row.SnapshotOriginals();
                StatusMessage = $"Saved {row.LastName}";
            }
            else
            {
                StatusMessage = $"Error: {result.ErrorMessage}";
            }
        }
    }

    /// <summary>Save participation for one cell immediately.</summary>
    public async Task SaveParticipationAsync(CompetitorRowViewModel row, ParticipationCellViewModel cell)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(cell);

        if (row.RowState == RowState.Added || row.RowState == RowState.Deleted)
        {
            return;
        }

        var result = await _setParticipation.ExecuteAsync(new SetParticipationRequest
        {
            EventSeriesId = _seriesId,
            CompetitorId = row.Id,
            CompetitionId = cell.CompetitionId,
            IsParticipating = cell.IsParticipating,
        });

        if (!result.Succeeded)
        {
            StatusMessage = $"Error saving participation: {result.ErrorMessage}";
        }
        else
        {
            StatusMessage = $"Participation saved for {row.LastName}";
        }
    }

    private void AppendToLog(ChangeLogEntry entry)
    {
        if (ChangeLog.Count >= ChangeLogCap)
        {
            ChangeLog.RemoveAt(0);
        }

        ChangeLog.Add(entry);
    }

    [RelayCommand]
    private void AddCompetitor()
    {
        var row = new CompetitorRowViewModel();
        row.PropertyChanged += OnRowPropertyChanged;
        Competitors.Insert(0, row);

        AppendToLog(new ChangeLogEntry(
            Guid.NewGuid(), DateTime.UtcNow, ChangeOp.Add,
            row.Id, null, null, null,
            "New competitor (unsaved)"));
    }

    [RelayCommand]
    private void DeleteCompetitor(CompetitorRowViewModel row)
    {
        if (row.RowState == RowState.Added)
        {
            Competitors.Remove(row);
            var toRemove = ChangeLog.Where(e => e.CompetitorId == row.Id).ToList();
            foreach (var e in toRemove)
            {
                ChangeLog.Remove(e);
            }

            return;
        }

        row.RowState = RowState.Deleted;
        AppendToLog(new ChangeLogEntry(
            Guid.NewGuid(), DateTime.UtcNow, ChangeOp.Delete,
            row.Id, null,
            $"{row.LastName} {row.FirstName}", null,
            $"Deleted: {row.LastName} {row.FirstName}"));
    }

    [RelayCommand]
    private void RestoreEntry(ChangeLogEntry entry)
    {
        var row = Competitors.FirstOrDefault(r => r.Id == entry.CompetitorId);
        if (row is null)
        {
            return;
        }

        if (entry.OperationType == ChangeOp.Delete)
        {
            row.RowState = RowState.Unchanged;
        }
        else if (entry.OperationType == ChangeOp.Edit && entry.FieldName is not null)
        {
            row.RowState = RowState.Unchanged;
            row.SnapshotOriginals();
        }
        else if (entry.OperationType == ChangeOp.Add)
        {
            Competitors.Remove(row);
        }

        ChangeLog.Remove(entry);
    }

    [RelayCommand]
    private async Task DiscardAllAsync()
    {
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        var errors = new List<string>();

        foreach (var row in Competitors.ToList())
        {
            if (row.RowState == RowState.Added)
            {
                var result = await _addCompetitor.ExecuteAsync(new AddCompetitorRequest
                {
                    EventSeriesId = _seriesId,
                    LastName = row.LastName.ToUpperInvariant(),
                    FirstName = row.FirstName,
                    YearOfBirth = row.YearOfBirth,
                    Gender = string.IsNullOrWhiteSpace(row.Gender) ? null : Enum.TryParse<OpenSkiTime.Domain.Common.Gender>(row.Gender, out var ag) ? ag : null,
                    NationCode = string.IsNullOrWhiteSpace(row.NationCode) ? null : row.NationCode,
                    ClubName = string.IsNullOrWhiteSpace(row.ClubName) ? null : row.ClubName,
                    FisCode = string.IsNullOrWhiteSpace(row.FisCode) ? null : row.FisCode,
                });

                if (!result.Succeeded)
                {
                    errors.Add($"Add {row.LastName}: {result.ErrorMessage}");
                }
            }
            else if (row.RowState == RowState.Edited)
            {
                var result = await _editCompetitor.ExecuteAsync(new EditCompetitorRequest
                {
                    EventSeriesId = _seriesId,
                    CompetitorId = row.Id,
                    LastName = row.LastName.ToUpperInvariant(),
                    FirstName = row.FirstName,
                    YearOfBirth = row.YearOfBirth,
                    Gender = string.IsNullOrWhiteSpace(row.Gender) ? null : Enum.TryParse<OpenSkiTime.Domain.Common.Gender>(row.Gender, out var g) ? g : null,
                    NationCode = string.IsNullOrWhiteSpace(row.NationCode) ? null : row.NationCode,
                    ClubName = string.IsNullOrWhiteSpace(row.ClubName) ? null : row.ClubName,
                    FisCode = string.IsNullOrWhiteSpace(row.FisCode) ? null : row.FisCode,
                });

                if (!result.Succeeded)
                {
                    errors.Add($"Update {row.LastName}: {result.ErrorMessage}");
                }
            }
            else if (row.RowState == RowState.Deleted)
            {
                var result = await _removeCompetitor.ExecuteAsync(_seriesId, row.Id);
                if (!result.Succeeded)
                {
                    errors.Add($"Delete {row.LastName}: {result.ErrorMessage}");
                }
            }
        }

        if (errors.Count > 0)
        {
            await _dialogs.ShowErrorAsync("Save errors", string.Join("\n", errors));
            return;
        }

        await RefreshAsync();
        StatusMessage = "Saved.";
    }

    [RelayCommand]
    private async Task PasteAsync()
    {
        var text = await _clipboard.GetTextAsync();
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        PasteWarningMessage = null;
        var result = await _preview.PreviewAsync(_seriesId, text, CancellationToken.None);
        if (!result.Succeeded)
        {
            PasteWarningMessage = $"Paste failed: {result.ErrorMessage}";
            return;
        }

        var pr = result.Value!;
        if (!pr.Diff.HasChanges && pr.Diff.Warnings.Count == 0)
        {
            PasteWarningMessage = "No changes detected in pasted data.";
            return;
        }

        if (pr.Diff.Warnings.Count > 0 && !pr.Diff.HasChanges)
        {
            PasteWarningMessage = "Could not recognise column headers in pasted data. No rows were imported.";
            return;
        }

        _pasteSession = new ImportPasteSession(pr.SnapshotVersion);

        int newCount = 0;
        int changedCount = 0;

        foreach (var nc in pr.Diff.NewCompetitors)
        {
            var row = new CompetitorRowViewModel
            {
                LastName = nc.LastName,
                FirstName = nc.FirstName,
                YearOfBirth = nc.YearOfBirth,
                NationCode = nc.NationCode ?? string.Empty,
                ClubName = nc.ClubName ?? string.Empty,
                FisCode = nc.FisCode ?? string.Empty,
                RowState = RowState.PasteHighlighted,
            };
            foreach (var col in CompetitionColumns)
            {
                bool participating = nc.ParticipatingIn.Contains(col.ShortLabel, StringComparer.OrdinalIgnoreCase);
                row.Participations.Add(new ParticipationCellViewModel(col.Id, col.ShortLabel, participating));
            }

            row.PropertyChanged += OnRowPropertyChanged;
            Competitors.Insert(0, row);
            newCount++;
        }

        foreach (var pd in pr.Diff.Participations)
        {
            var existing = Competitors.FirstOrDefault(r => r.Id == pd.CompetitorId);
            if (existing is null)
            {
                continue;
            }

            var cell = existing.Participations.FirstOrDefault(p => p.CompetitionId == pd.CompetitionId);
            if (cell is not null)
            {
                cell.IsParticipating = pd.IsParticipating;
            }

            if (existing.RowState == RowState.Unchanged)
            {
                existing.RowState = RowState.Edited;
            }

            changedCount++;
        }

        foreach (var fc in pr.Diff.FieldChanges)
        {
            var existing = Competitors.FirstOrDefault(r => r.Id == fc.CompetitorId);
            if (existing is null)
            {
                continue;
            }

            switch (fc.FieldName)
            {
                case "LastName":   existing.LastName   = fc.NewValue; break;
                case "FirstName":  existing.FirstName  = fc.NewValue; break;
                case "YearOfBirth":
                    if (int.TryParse(fc.NewValue, out var newYob))
                    {
                        existing.YearOfBirth = newYob;
                    }

                    break;
                case "Gender":     existing.Gender     = fc.NewValue; break;
                case "FisCode":    existing.FisCode    = fc.NewValue; break;
                case "NationCode": existing.NationCode = fc.NewValue; break;
                case "ClubName":   existing.ClubName   = fc.NewValue; break;
            }

            existing.ModifiedFields.Add(fc.FieldName);

            if (existing.RowState == RowState.Unchanged)
            {
                existing.RowState = RowState.Edited;
            }

            changedCount++;
        }

        HasActivePasteSession = true;
        StatusMessage = $"Paste preview: {newCount} new, {changedCount} change(s). Apply or discard.";
    }

    [RelayCommand]
    private async Task ApplyImportAsync()
    {
        if (_pasteSession is null)
        {
            return;
        }

        var pasteRows = Competitors.Where(r => r.RowState == RowState.PasteHighlighted).ToList();
        var editedRows = Competitors.Where(r => r.RowState == RowState.Edited && r.ModifiedFields.Count > 0).ToList();
        var errors = new List<string>();

        foreach (var row in pasteRows)
        {
            var result = await _addCompetitor.ExecuteAsync(new AddCompetitorRequest
            {
                EventSeriesId = _seriesId,
                LastName = row.LastName.ToUpperInvariant(),
                FirstName = row.FirstName,
                YearOfBirth = row.YearOfBirth,
                Gender = string.IsNullOrWhiteSpace(row.Gender) ? null : Enum.TryParse<OpenSkiTime.Domain.Common.Gender>(row.Gender, out var pg) ? pg : null,
                NationCode = string.IsNullOrWhiteSpace(row.NationCode) ? null : row.NationCode,
                ClubName = string.IsNullOrWhiteSpace(row.ClubName) ? null : row.ClubName,
                FisCode = string.IsNullOrWhiteSpace(row.FisCode) ? null : row.FisCode,
            });

            if (!result.Succeeded)
            {
                errors.Add($"Add {row.LastName}: {result.ErrorMessage}");
            }
        }

        foreach (var row in editedRows)
        {
            var result = await _editCompetitor.ExecuteAsync(new EditCompetitorRequest
            {
                EventSeriesId = _seriesId,
                CompetitorId = row.Id,
                LastName = row.LastName.ToUpperInvariant(),
                FirstName = row.FirstName,
                YearOfBirth = row.YearOfBirth,
                Gender = string.IsNullOrWhiteSpace(row.Gender) ? null : Enum.TryParse<OpenSkiTime.Domain.Common.Gender>(row.Gender, out var g) ? g : null,
                NationCode = string.IsNullOrWhiteSpace(row.NationCode) ? null : row.NationCode,
                ClubName = string.IsNullOrWhiteSpace(row.ClubName) ? null : row.ClubName,
                FisCode = string.IsNullOrWhiteSpace(row.FisCode) ? null : row.FisCode,
            });

            if (!result.Succeeded)
            {
                errors.Add($"Update {row.LastName}: {result.ErrorMessage}");
            }
        }

        if (errors.Count > 0)
        {
            await _dialogs.ShowErrorAsync("Import errors", string.Join("\n", errors));
            return;
        }

        _pasteSession.Complete();
        _pasteSession = null;
        HasActivePasteSession = false;
        PasteWarningMessage = null;
        await RefreshAsync();
        StatusMessage = $"Import applied — {pasteRows.Count} new, {editedRows.Count} updated.";
    }

    [RelayCommand]
    private async Task DiscardImportAsync()
    {
        var pasteRows = Competitors.Where(r => r.RowState == RowState.PasteHighlighted).ToList();
        foreach (var row in pasteRows)
        {
            Competitors.Remove(row);
        }

        _pasteSession?.Complete();
        _pasteSession = null;
        HasActivePasteSession = false;
        PasteWarningMessage = null;
        StatusMessage = string.Empty;
        await Task.CompletedTask;
    }

    [RelayCommand]
    private async Task CopySelectedRowsAsync()
    {
        var rows = SelectedItems.OfType<CompetitorRowViewModel>().ToList();
        if (rows.Count == 0)
        {
            StatusMessage = "Select at least one row to copy.";
            return;
        }

        var series = await _repository.GetByIdAsync(_seriesId);
        var competitions = series?.Competitions.OrderBy(c => c.Date).ToList() ?? [];

        var sb = new StringBuilder();
        var staticHeaders = s_copyHeaders.Concat(competitions.Select(c => c.ShortLabel));
        sb.AppendLine(string.Join("\t", staticHeaders));

        foreach (var item in rows)
        {
            var cells = new List<string>
            {
                item.FisCode,
                item.LastName.ToUpperInvariant(),
                item.FirstName,
                item.YearOfBirth.ToString(CultureInfo.InvariantCulture),
                item.Gender,
                item.NationCode,
                item.ClubName,
            };

            if (series is not null)
            {
                var competitor = series.Competitors.FirstOrDefault(c => c.Id == item.Id);
                foreach (var comp in competitions)
                {
                    var participates = competitor?.Participations.Any(p => p.CompetitionId == comp.Id) ?? false;
                    cells.Add(participates ? "x" : string.Empty);
                }
            }

            sb.AppendLine(string.Join("\t", cells));
        }

        await _clipboard.SetTextAsync(sb.ToString());
        StatusMessage = $"Copied {rows.Count} row(s) to clipboard.";
    }

    [RelayCommand]
    private async Task RefreshGridAsync()
    {
        await RefreshAsync();
    }
}
