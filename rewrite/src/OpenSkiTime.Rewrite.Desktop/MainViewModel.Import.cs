using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenSkiTime.Rewrite.Application;
using OpenSkiTime.Rewrite.Domain;

namespace OpenSkiTime.Rewrite.Desktop;

public sealed partial class MainViewModel
{
    private ImportPreview? _importPreview;
    private readonly HashSet<Guid> _selectedExportIds = [];
    public void SetSelectedExportRows(IEnumerable<CompetitorGridRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        _selectedExportIds.Clear();
        foreach (var row in rows)
        {
            if (row.Id is { } id) { _selectedExportIds.Add(id); }
        }
    }

    private IReadOnlyList<Guid> ExportIds()
    {
        if (_selectedExportIds.Count > 0) { return [.. _selectedExportIds]; }
        return SelectedCompetitorRow?.Id is { } id ? [id]
            : throw new DomainValidationException("Select one or more saved competitors first.");
    }
    public ObservableCollection<ImportReviewGridRow> ImportReviewRows { get; } = [];
    public ObservableCollection<string> ImportWarnings { get; } = [];
    [ObservableProperty] private string _importSourceText = string.Empty;
    [ObservableProperty] private string _importReviewSummary = string.Empty;
    [ObservableProperty] private ImportReviewGridRow? _selectedImportReviewRow;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CompetitorSectionTitle))]
    [NotifyPropertyChangedFor(nameof(IsDeskChangeLogVisible))]
    private bool _isImportReviewOpen;
    public string CompetitorSectionTitle => IsImportReviewOpen ? "Competitor desk · paste preview" : "Competitor desk";

    [RelayCommand]
    private async Task PasteFromExcelAsync()
    {
        await GuardAsync(async () =>
        {
            if (entryExchange is null) { throw new SeriesFileException("Clipboard is unavailable."); }
            var text = await entryExchange.ReadClipboardAsync();
            if (string.IsNullOrWhiteSpace(text)) { throw new DomainValidationException("Clipboard has no tab-separated competitor table."); }
            ImportSourceText = text;
            await BuildImportPreviewAsync();
        });
    }

    private async Task BuildImportPreviewAsync()
    {
        if (_allCompetitorRows.Any(x => x.HasDraftChanges || x.IsPendingDelete)
            || ParticipationChoices.Any(x => x.IsSaving))
        {
            throw new DomainValidationException("Save, restore or commit current competitor changes before importing.");
        }
        var series = _current ?? throw new SeriesFileException("Open an event series first.");
        var desk = await workspace.ReadCompetitorDeskAsync();
        var preview = TsvExchange.Preview(ImportSourceText, series, desk, DeskCompetition?.Id);
        _importPreview = preview;
        PopulateReview(preview, desk, series);
        IsImportReviewOpen = true;
        CompetitorFilterText = string.Empty;
        RefreshVisibleCompetitors();
        SelectedCompetitorRow = _allCompetitorRows.FirstOrDefault(x => x.PendingImport?.NeedsApproval == true)
            ?? _allCompetitorRows.FirstOrDefault(x => x.IsImportHighlighted);
        SetStatus($"Previewed {preview.Rows.Count} rows in the competitor grid. Review highlighted changes, then commit or discard.");
    }

    private void PopulateReview(ImportPreview preview, CompetitorDeskDetails desk, SeriesDetails series)
    {
        foreach (var staged in _allCompetitorRows.Where(x => x.IsImportHighlighted).ToArray())
        {
            staged.ClearImport();
            foreach (var choice in staged.GridEntries) { choice.Restore(); }
            if (staged.Id is null) { _allCompetitorRows.Remove(staged); }
            else { UpdateIndicators(staged); }
        }
        ImportReviewRows.Clear();
        foreach (var item in preview.Rows)
        {
            var before = desk.Competitors.FirstOrDefault(x => x.Id == item.CompetitorId)?.Values;
            var review = new ImportReviewGridRow(item, before, series.Competitions, desk.Participations);
            ImportReviewRows.Add(review);
            var row = item.CompetitorId is { } id
                ? _allCompetitorRows.FirstOrDefault(x => x.Id == id)
                : null;
            if (row is null)
            {
                row = new CompetitorGridRow();
                _allCompetitorRows.Add(row);
            }
            FillGridEntries(row);
            row.StageImport(review);
            foreach (var patch in review.Entries)
            {
                var choice = row.GridEntries.First(x => x.CompetitionId == patch.CompetitionId);
                if (patch.ParticipationText.Length > 0) { choice.IsParticipating = patch.ParticipationText == "X"; }
                var stagedRow = row;
                patch.PropertyChanged += (_, args) =>
                {
                    if (args.PropertyName == nameof(ImportReviewEntryRow.ParticipationText)
                        && patch.ParticipationText is "X" or "0")
                    {
                        choice.IsParticipating = patch.ParticipationText == "X";
                    }
                    if (DeskCompetition?.Id == patch.CompetitionId
                        && args.PropertyName == nameof(ImportReviewEntryRow.BibText))
                    {
                        stagedRow.ImportedBibText = patch.BibText == "~" ? string.Empty : patch.BibText;
                    }
                };
            }
            var selectedChoice = row.GridEntries.FirstOrDefault(x => x.CompetitionId == DeskCompetition?.Id);
            if (selectedChoice is not null) { row.IsParticipating = selectedChoice.IsParticipating; }
            var selectedPatch = review.Entries.FirstOrDefault(x => x.CompetitionId == DeskCompetition?.Id);
            if (selectedPatch?.BibText.Length > 0)
            {
                row.ImportedBibText = selectedPatch.BibText == "~" ? string.Empty : selectedPatch.BibText;
            }
            row.Category = CategoryResolver.Resolve(item.Values, CategoryRules);
            row.Readiness = item.Values.Readiness(selectedChoice?.IsParticipating ?? false);
        }
        ImportWarnings.Clear();
        foreach (var warning in preview.Warnings) { ImportWarnings.Add(warning); }
        SelectedImportReviewRow = ImportReviewRows.FirstOrDefault();
        ImportReviewSummary = $"{ImportReviewRows.Count} rows · {ImportReviewRows.Count(x => x.IsNew)} new · "
            + $"{ImportReviewRows.Count(x => x.Changes.Length > 0 && !x.IsNew)} changed · "
            + $"{ImportReviewRows.Count(x => x.NeedsApproval)} need confirmation";
    }

    [RelayCommand]
    private void CloseImportReview()
    {
        ClearImportReview();
        SetStatus("Pasted preview discarded. The event file was not changed.");
    }

    private void ClearImportReview()
    {
        foreach (var row in _allCompetitorRows.Where(x => x.IsImportHighlighted).ToArray())
        {
            row.ClearImport();
            foreach (var choice in row.GridEntries) { choice.Restore(); }
            if (row.Id is null) { _allCompetitorRows.Remove(row); }
            else { UpdateIndicators(row); }
        }
        _importPreview = null;
        ImportReviewRows.Clear();
        ImportWarnings.Clear();
        SelectedImportReviewRow = null;
        IsImportReviewOpen = false;
        ImportReviewSummary = string.Empty;
        RefreshVisibleCompetitors();
        OnSelectedCompetitorRowChanged(SelectedCompetitorRow);
    }

    [RelayCommand]
    private async Task CommitImportAsync()
    {
        await GuardAsync(async () =>
        {
            var preview = _importPreview ?? throw new DomainValidationException("Preview the pasted table before committing.");
            var series = _current ?? throw new SeriesFileException("Open an event series first.");
            if (series.Id != preview.SeriesId || series.Revision != preview.Revision
                || TsvExchange.Hash(ImportSourceText) != preview.SourceHash)
            {
                throw new SeriesConflictException();
            }
            foreach (var row in _allCompetitorRows.Where(x => x.PendingImport is not null))
            {
                var review = row.PendingImport!;
                review.Surname = row.Surname;
                review.FirstName = row.FirstName;
                review.BirthYearText = row.BirthYearText;
                review.GenderText = row.GenderText;
                review.Nation = row.Nation;
                review.Club = row.Club;
                review.FederationCode = row.FederationCode;
            }
            var unapproved = ImportReviewRows.FirstOrDefault(x => x.NeedsApproval && !x.Approved);
            if (unapproved is not null)
            {
                throw new DomainValidationException($"Confirm the warning on source row {unapproved.SourceRow} before Commit.");
            }
            var rows = ImportReviewRows.Select(x => x.ToCommitRow(series.Values.EndDate.Year)).ToArray();
            var result = await workspace.ApplyImportAsync(new ImportCommit(preview.SeriesId, preview.Revision,
                preview.SourceHash, rows));
            _current = series with { Revision = result.Revision };
            DeskChangeLog.Clear();
            OnPropertyChanged(nameof(HasDeskChangeLog));
            OnPropertyChanged(nameof(IsDeskChangeLogVisible));
            ClearImportReview();
            await LoadCompetitorDeskAsync();
            SetStatus(result.AlreadyApplied
                ? "This pasted source was already committed; no changes were written."
                : $"Committed {result.Created} new and {result.Updated} existing competitors. All rows were saved together.");
        });
    }

    [RelayCommand]
    private async Task CopySelectedCompetitorAsync()
    {
        await GuardAsync(async () =>
        {
            EnsureDeskClean();
            if (entryExchange is null) { throw new SeriesFileException("Clipboard is unavailable."); }
            var series = _current ?? throw new SeriesFileException("Open an event series first.");
            var desk = await workspace.ReadCompetitorDeskAsync();
            await entryExchange.WriteClipboardAsync(TsvExchange.Export(series, desk, ExportIds()));
            SetStatus("Selected competitors copied as TSV with competition entries.");
        });
    }

    [RelayCommand]
    private async Task ExportSelectedTsvAsync()
    {
        await GuardAsync(async () =>
        {
            EnsureDeskClean();
            if (entryExchange is null) { throw new SeriesFileException("TSV export is unavailable."); }
            var series = _current ?? throw new SeriesFileException("Open an event series first.");
            var desk = await workspace.ReadCompetitorDeskAsync();
            var saved = await entryExchange.SaveTsvAsync(series.Values.Name + "-entries", TsvExchange.Export(series, desk, ExportIds()));
            if (saved) { SetStatus("Selected competitors exported as TSV."); }
        });
    }
}
