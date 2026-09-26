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
    [NotifyPropertyChangedFor(nameof(IsCompetitorDeskVisible))]
    [NotifyPropertyChangedFor(nameof(CompetitorSectionTitle))]
    private bool _isImportReviewOpen;
    public bool IsCompetitorDeskVisible => !IsImportReviewOpen;
    public string CompetitorSectionTitle => IsImportReviewOpen ? "Import review" : "Competitor desk";

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

    [RelayCommand]
    private async Task PreviewImportAsync() => await GuardAsync(BuildImportPreviewAsync);

    private async Task BuildImportPreviewAsync()
    {
        if (_allCompetitorRows.Any(x => x.HasDraftChanges) || ParticipationChoices.Any(x => x.IsSaving))
        {
            throw new DomainValidationException("Save or discard current competitor edits before importing.");
        }
        var series = _current ?? throw new SeriesFileException("Open an event series first.");
        var desk = await workspace.ReadCompetitorDeskAsync();
        var preview = TsvExchange.Preview(ImportSourceText, series, desk, DeskCompetition?.Id);
        _importPreview = preview;
        PopulateReview(preview, desk, series);
        IsImportReviewOpen = true;
        SetStatus($"Previewed {preview.Rows.Count} rows. Review highlighted values, then Commit or close the review.");
    }

    private void PopulateReview(ImportPreview preview, CompetitorDeskDetails desk, SeriesDetails series)
    {
        ImportReviewRows.Clear();
        foreach (var item in preview.Rows)
        {
            var before = desk.Competitors.FirstOrDefault(x => x.Id == item.CompetitorId)?.Values;
            ImportReviewRows.Add(new ImportReviewGridRow(item, before, series.Competitions, desk.Participations));
        }
        ImportWarnings.Clear();
        foreach (var warning in preview.Warnings) { ImportWarnings.Add(warning); }
        SelectedImportReviewRow = ImportReviewRows.FirstOrDefault();
        ImportReviewSummary = $"{ImportReviewRows.Count} rows · {ImportReviewRows.Count(x => x.IsNew)} new · "
            + $"{ImportReviewRows.Count(x => x.Changes.Length > 0 && !x.IsNew)} changed · "
            + $"{ImportReviewRows.Count(x => x.NeedsApproval)} need confirmation";
    }

    [RelayCommand]
    private async Task ResetImportReviewAsync()
    {
        await GuardAsync(async () =>
        {
            var preview = _importPreview ?? throw new DomainValidationException("No import review is open.");
            var series = _current ?? throw new SeriesFileException("Open an event series first.");
            var desk = await workspace.ReadCompetitorDeskAsync();
            if (series.Id != preview.SeriesId || desk.Revision != preview.Revision
                || TsvExchange.Hash(ImportSourceText) != preview.SourceHash)
            {
                throw new SeriesConflictException();
            }
            PopulateReview(preview, desk, series);
            SetStatus("Review edits discarded; the original pasted preview is restored.");
        });
    }

    [RelayCommand]
    private void CloseImportReview()
    {
        ClearImportReview();
        SetStatus("Import review closed without changing the event file.");
    }

    private void ClearImportReview()
    {
        _importPreview = null;
        ImportReviewRows.Clear();
        ImportWarnings.Clear();
        SelectedImportReviewRow = null;
        IsImportReviewOpen = false;
        ImportReviewSummary = string.Empty;
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
            var unapproved = ImportReviewRows.FirstOrDefault(x => x.NeedsApproval && !x.Approved);
            if (unapproved is not null)
            {
                throw new DomainValidationException($"Confirm the warning on source row {unapproved.SourceRow} before Commit.");
            }
            var rows = ImportReviewRows.Select(x => x.ToCommitRow(series.Values.EndDate.Year)).ToArray();
            var result = await workspace.ApplyImportAsync(new ImportCommit(preview.SeriesId, preview.Revision,
                preview.SourceHash, rows));
            _current = series with { Revision = result.Revision };
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
