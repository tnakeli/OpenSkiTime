using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenSkiTime.Rewrite.Application;
using OpenSkiTime.Rewrite.Domain;

namespace OpenSkiTime.Rewrite.Desktop;

public sealed partial class MainViewModel
{
    private CompetitorDeskDetails? _desk;
    private readonly List<CompetitorGridRow> _allCompetitorRows = [];
    private readonly SemaphoreSlim _deskRowSaveGate = new(1, 1);
    private UndoDeskEdit? _lastDeskEdit;

    public ObservableCollection<CompetitorGridRow> VisibleCompetitors { get; } = [];
    public ObservableCollection<DeskChangeLogEntry> DeskChangeLog { get; } = [];
    public bool HasDeskChangeLog => DeskChangeLog.Count > 0;
    public bool IsDeskChangeLogVisible => HasDeskChangeLog && !IsImportReviewOpen;
    private void AppendDeskChange(DeskChangeLogEntry entry)
    {
        if (DeskChangeLog.Count >= 200) { DeskChangeLog.RemoveAt(0); }
        DeskChangeLog.Add(entry);
        OnPropertyChanged(nameof(HasDeskChangeLog));
        OnPropertyChanged(nameof(IsDeskChangeLogVisible));
    }
    public ObservableCollection<CompetitionEntryChoice> ParticipationChoices { get; } = [];
    public ObservableCollection<CategoryRuleDetails> CategoryRules { get; } = [];
    public ObservableCollection<LegacySeriesPreview> LegacySeriesPreviews { get; } = [];
    public ObservableCollection<string> LegacySourceWarnings { get; } = [];
    public IReadOnlyList<string> CompetitorGroupings { get; } = ["Surname", "Category", "Imported bib"];

    private CompetitionDetails? _deskCompetition;
    public CompetitionDetails? DeskCompetition
    {
        get => _deskCompetition;
        set
        {
            if (_deskCompetition?.Id != value?.Id && HasDeskDrafts)
            {
                SetStatus("Save or discard the current competitor row before switching competitions.", error: true);
                OnPropertyChanged(nameof(DeskCompetition));
                return;
            }
            if (SetProperty(ref _deskCompetition, value)) { OnDeskCompetitionChanged(value); }
        }
    }
    [ObservableProperty] private CompetitorGridRow? _selectedCompetitorRow;

    partial void OnSelectedCompetitorRowChanged(CompetitorGridRow? value)
    {
        SelectedImportReviewRow = value?.PendingImport;
        ParticipationChoices.Clear();
        if (value?.Id is not { } competitorId) { return; }
        foreach (var choice in value.GridEntries) { ParticipationChoices.Add(choice); }
    }
    [ObservableProperty] private string _competitorFilterText = string.Empty;
    [ObservableProperty] private string _competitorGrouping = "Surname";
    [ObservableProperty] private CategoryRuleDetails? _selectedCategoryRule;
    [ObservableProperty] private string _categoryLabel = string.Empty;
    [ObservableProperty] private string _categoryMinYearText = string.Empty;
    [ObservableProperty] private string _categoryMaxYearText = string.Empty;
    [ObservableProperty] private string _categoryGenderText = string.Empty;
    [ObservableProperty] private string _categoryOrderText = "0";
    [ObservableProperty] private bool _canUndoDeskEdit;
    [ObservableProperty] private LegacySeriesPreview? _selectedLegacySeries;
    [ObservableProperty] private string _legacySnapshotLabel = string.Empty;
    [ObservableProperty] private string _legacyPreviewSummary = string.Empty;

    partial void OnSelectedLegacySeriesChanged(LegacySeriesPreview? value)
    {
        LegacyPreviewSummary = value is null ? string.Empty
            : $"{value.Competitions.Count} competitions · {value.Competitors.Count} competitors · "
              + $"{value.Entries.Count} entries · {value.Categories.Count} categories · {value.Warnings.Count} warnings";
    }

    private bool HasDeskDrafts => _allCompetitorRows.Any(x => x.HasDraftChanges || x.IsPendingDelete)
        || ParticipationChoices.Any(x => x.IsSaving || x.IsParticipating != x.SavedParticipation)
        || IsImportReviewOpen;

    private void EnsureDeskClean()
    {
        if (IsImportReviewOpen)
        {
            throw new DomainValidationException("Commit or discard the pasted preview before changing the event file or competitions.");
        }
        if (HasDeskDrafts)
        {
            throw new DomainValidationException("Save or discard the current competitor row before changing the event file or races.");
        }
    }

    private void OnDeskCompetitionChanged(CompetitionDetails? value)
    {
        foreach (var row in _allCompetitorRows)
        {
            row.SetEntry(_desk?.Participations.FirstOrDefault(x => x.CompetitorId == row.Id
                && x.CompetitionId == value?.Id));
            UpdateIndicators(row);
        }
        RefreshVisibleCompetitors();
    }

    partial void OnCompetitorFilterTextChanged(string value) => RefreshVisibleCompetitors();
    partial void OnCompetitorGroupingChanged(string value) => RefreshVisibleCompetitors();

    partial void OnSelectedCategoryRuleChanged(CategoryRuleDetails? value)
    {
        if (value is null) { return; }
        CategoryLabel = value.Values.Label;
        CategoryMinYearText = value.Values.BirthYearMin.ToString(CultureInfo.InvariantCulture);
        CategoryMaxYearText = value.Values.BirthYearMax.ToString(CultureInfo.InvariantCulture);
        CategoryGenderText = GenderLabels.Format(value.Values.Gender);
        CategoryOrderText = value.Values.DisplayOrder.ToString(CultureInfo.InvariantCulture);
    }

    private async Task LoadCompetitorDeskAsync()
    {
        var selectedId = SelectedCompetitorRow?.Id;
        _desk = await workspace.ReadCompetitorDeskAsync();
        _selectedExportIds.Clear();
        CategoryRules.Clear();
        foreach (var rule in _desk.Categories) { CategoryRules.Add(rule); }
        _allCompetitorRows.Clear();
        foreach (var competitor in _desk.Competitors)
        {
            var entry = _desk.Participations.FirstOrDefault(x => x.CompetitorId == competitor.Id
                && x.CompetitionId == DeskCompetition?.Id);
            var row = CompetitorGridRow.From(competitor, entry);
            FillGridEntries(row);
            UpdateIndicators(row);
            _allCompetitorRows.Add(row);
        }
        _lastDeskEdit = null;
        CanUndoDeskEdit = false;
        RefreshVisibleCompetitors();
        SelectedCompetitorRow = VisibleCompetitors.FirstOrDefault(x => x.Id == selectedId);
    }

    private void ClearCompetitorDesk()
    {
        _desk = null;
        _selectedExportIds.Clear();
        _allCompetitorRows.Clear();
        DeskChangeLog.Clear();
        OnPropertyChanged(nameof(HasDeskChangeLog));
        OnPropertyChanged(nameof(IsDeskChangeLogVisible));
        VisibleCompetitors.Clear();
        ParticipationChoices.Clear();
        CategoryRules.Clear();
        SelectedCompetitorRow = null;
        DeskCompetition = null;
        _lastDeskEdit = null;
        CanUndoDeskEdit = false;
        LegacySeriesPreviews.Clear();
        LegacySourceWarnings.Clear();
        SelectedLegacySeries = null;
        LegacySnapshotLabel = string.Empty;
        ClearImportReview();
    }

    private void UpdateIndicators(CompetitorGridRow row)
    {
        if (row.SavedValues is not { } values) { return; }
        row.Category = CategoryResolver.Resolve(values, CategoryRules);
        row.Readiness = values.Readiness(row.SavedParticipation);
    }

    private void FillGridEntries(CompetitorGridRow row)
    {
        row.GridEntries.Clear();
        foreach (var competition in Competitions)
        {
            var entry = _desk?.Participations.FirstOrDefault(x => x.CompetitorId == row.Id
                && x.CompetitionId == competition.Id);
            row.GridEntries.Add(new CompetitionEntryChoice(competition,
                entry?.Participates ?? false, entry?.ImportedBib));
        }
    }

    private void RefreshVisibleCompetitors()
    {
        var filter = CompetitorFilterText.Trim();
        var source = _allCompetitorRows.Where(row => filter.Length == 0
            || row.Surname.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || row.FirstName.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || row.FederationCode.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || row.Club.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || row.Category.Contains(filter, StringComparison.OrdinalIgnoreCase));
        source = CompetitorGrouping switch
        {
            "Category" => source.OrderBy(x => x.Category).ThenBy(x => x.Surname).ThenBy(x => x.FirstName),
            "Imported bib" => source.OrderBy(x => int.TryParse(x.ImportedBibText, out var bib) ? bib : int.MaxValue)
                .ThenBy(x => x.Surname),
            _ => source.OrderBy(x => x.Surname).ThenBy(x => x.FirstName),
        };
        var selected = SelectedCompetitorRow;
        VisibleCompetitors.Clear();
        foreach (var row in source) { VisibleCompetitors.Add(row); }
        SelectedCompetitorRow = selected is not null && VisibleCompetitors.Contains(selected) ? selected : null;
    }

    [RelayCommand]
    private void AddCompetitor()
    {
        if (!IsEditingSeries) { return; }
        if (IsImportReviewOpen)
        {
            SetStatus("Commit or discard the pasted preview before adding competitors.", error: true);
            return;
        }
        var row = new CompetitorGridRow();
        FillGridEntries(row);
        _allCompetitorRows.Add(row);
        RefreshVisibleCompetitors();
        SelectedCompetitorRow = row;
        SetStatus("Enter a surname, then leave the row to save the competitor.");
    }

    public async Task SaveCompetitorRowAsync(CompetitorGridRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (IsImportReviewOpen)
        {
            SetStatus("Commit or discard the pasted preview before saving a competitor row.", error: true);
            return;
        }
        await _deskRowSaveGate.WaitAsync();
        try
        {
            await GuardAsync(async () =>
            {
                var series = _current ?? throw new SeriesFileException("Open an event series first.");
                if (row.Id is null && string.IsNullOrWhiteSpace(row.Surname)) { return; }
                if (row.IsPendingDelete) { throw new DomainValidationException("Restore the pending deletion before editing this competitor."); }
                var draft = row.Draft();
                var bib = row.DraftImportedBib();
                var raceId = DeskCompetition?.Id;
                if (row.SavedValues == draft && row.SavedParticipation == row.IsParticipating
                    && row.SavedImportedBib == bib) { return; }

                var previous = row.SavedValues;
                var previousParticipation = row.SavedParticipation;
                var previousBib = row.SavedImportedBib;
                var result = await workspace.SaveDeskRowAsync(row.Id, draft, raceId, row.IsParticipating, bib, series.Revision);
                _current = series with { Revision = result.Revision };
                var entry = raceId is { } id
                    ? new ParticipationDetails(result.Value.Id, id, row.IsParticipating, bib, null) : null;
                row.MarkSaved(result.Value, entry);
                UpdateDeskSnapshot(result.Value, entry, result.Revision);
                if (previous is null)
                {
                    FillGridEntries(row);
                    OnSelectedCompetitorRowChanged(row);
                }
                UpdateIndicators(row);
                _lastDeskEdit = new UndoDeskEdit(result.Value.Id, previous, raceId, previousParticipation, previousBib);
                CanUndoDeskEdit = true;
                AppendDeskChange(new DeskChangeLogEntry(Guid.NewGuid(), DateTime.UtcNow,
                    previous is null ? DeskChangeKind.Add : DeskChangeKind.Edit, result.Value.Id,
                    previous, raceId, previousParticipation, previousBib,
                    previous is null ? $"Added: {result.Value.Values.Surname} {result.Value.Values.FirstName}"
                        : $"Edited: {result.Value.Values.Surname} {result.Value.Values.FirstName}"));
                SetStatus("Competitor row saved.");
            });
        }
        finally { _deskRowSaveGate.Release(); }
    }

    private void UpdateDeskSnapshot(CompetitorDetails competitor, ParticipationDetails? entry, long revision)
    {
        if (_desk is null) { return; }
        var competitors = _desk.Competitors.Where(x => x.Id != competitor.Id).Append(competitor).ToArray();
        var entries = entry is null ? _desk.Participations : _desk.Participations
            .Where(x => x.CompetitorId != entry.CompetitorId || x.CompetitionId != entry.CompetitionId)
            .Append(entry).ToArray();
        _desk = _desk with { Revision = revision, Competitors = competitors, Participations = entries };
        if (SelectedCompetitorRow?.Id == competitor.Id && entry is not null)
        {
            var choice = ParticipationChoices.FirstOrDefault(x => x.CompetitionId == entry.CompetitionId);
            if (choice is not null) { choice.IsParticipating = entry.Participates; choice.MarkSaved(); }
        }
    }

    public async Task SaveParticipationChoiceAsync(CompetitionEntryChoice choice)
    {
        ArgumentNullException.ThrowIfNull(choice);
        if (IsImportReviewOpen)
        {
            choice.Restore();
            SetStatus("Commit or discard the pasted preview before changing participation.", error: true);
            return;
        }
        await _deskRowSaveGate.WaitAsync();
        try
        {
            if (choice.IsParticipating == choice.SavedParticipation) { return; }
            if (SelectedCompetitorRow is not { Id: { } competitorId, SavedValues: { } values } row
                || _current is not { } series || row.HasDraftChanges || row.IsPendingDelete)
            {
                choice.Restore();
                SetStatus("Save the selected competitor row before changing participation.", error: true);
                return;
            }
            choice.IsSaving = true;
            await GuardAsync(async () =>
            {
                var result = await workspace.SaveDeskRowAsync(competitorId, values, choice.CompetitionId,
                    choice.IsParticipating, choice.ImportedBib, series.Revision);
                _current = series with { Revision = result.Revision };
                var entry = new ParticipationDetails(competitorId, choice.CompetitionId,
                    choice.IsParticipating, choice.ImportedBib, null);
                UpdateDeskSnapshot(result.Value, entry, result.Revision);
                if (DeskCompetition?.Id == choice.CompetitionId)
                {
                    row.SetEntry(entry);
                    UpdateIndicators(row);
                }
                choice.MarkSaved();
                _lastDeskEdit = null;
                CanUndoDeskEdit = false;
                AppendDeskChange(new DeskChangeLogEntry(Guid.NewGuid(), DateTime.UtcNow,
                    DeskChangeKind.Edit, competitorId, values, choice.CompetitionId,
                    !choice.IsParticipating, choice.ImportedBib,
                    $"Entry: {row.Surname} {row.FirstName} · {choice.Label}"));
                SetStatus($"Participation saved for {choice.Label}.");
            });
            if (IsError) { choice.Restore(); }
        }
        finally { choice.IsSaving = false; _deskRowSaveGate.Release(); }
    }

    public async Task SaveGridParticipationAsync(CompetitorGridRow row, CompetitionEntryChoice choice)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(choice);
        SelectedCompetitorRow = row;
        if (IsImportReviewOpen)
        {
            if (row.PendingImport is not { } review)
            {
                choice.Restore();
                SetStatus("Commit or discard the pasted preview before changing other competitors.", error: true);
                return;
            }
            review.Entries.First(x => x.CompetitionId == choice.CompetitionId).ParticipationText =
                choice.IsParticipating ? "X" : "0";
            return;
        }
        await SaveParticipationChoiceAsync(choice);
    }

    [RelayCommand]
    private async Task SaveSelectedCompetitorAsync()
    {
        if (SelectedCompetitorRow is { } row) { await SaveCompetitorRowAsync(row); }
    }

    [RelayCommand]
    private void DiscardCompetitorDraft()
    {
        if (IsImportReviewOpen)
        {
            SetStatus("Use Discard import to reject the entire pasted preview.", error: true);
            return;
        }
        if (SelectedCompetitorRow is not { } row) { return; }
        if (row.Id is null) { _allCompetitorRows.Remove(row); }
        else { row.RestoreDraft(); }
        RefreshVisibleCompetitors();
        SetStatus("Unsaved row edits discarded.");
    }

    [RelayCommand]
    private async Task UndoDeskEditAsync()
    {
        if (IsImportReviewOpen)
        {
            SetStatus("Commit or discard the pasted preview before undoing a saved edit.", error: true);
            return;
        }
        if (_lastDeskEdit is not { } undo || _current is not { } series) { return; }
        await GuardAsync(async () =>
        {
            if (undo.Previous is null)
            {
                var revision = await workspace.RemoveCompetitorAsync(undo.CompetitorId, series.Revision);
                _current = series with { Revision = revision };
                var row = _allCompetitorRows.FirstOrDefault(x => x.Id == undo.CompetitorId);
                if (row is not null) { _allCompetitorRows.Remove(row); }
                if (_desk is { } desk)
                {
                    _desk = desk with { Revision = revision,
                        Competitors = desk.Competitors.Where(x => x.Id != undo.CompetitorId).ToArray(),
                        Participations = desk.Participations.Where(x => x.CompetitorId != undo.CompetitorId).ToArray() };
                }
            }
            else
            {
                var result = await workspace.SaveDeskRowAsync(undo.CompetitorId, undo.Previous,
                    undo.CompetitionId, undo.Participates, undo.ImportedBib, series.Revision);
                _current = series with { Revision = result.Revision };
                var entry = undo.CompetitionId is { } id
                    ? new ParticipationDetails(undo.CompetitorId, id, undo.Participates, undo.ImportedBib, null) : null;
                UpdateDeskSnapshot(result.Value, entry, result.Revision);
                var row = _allCompetitorRows.FirstOrDefault(x => x.Id == undo.CompetitorId);
                if (row is not null)
                {
                    row.MarkSaved(result.Value, DeskCompetition?.Id == undo.CompetitionId ? entry
                        : _desk?.Participations.FirstOrDefault(x => x.CompetitorId == row.Id
                            && x.CompetitionId == DeskCompetition?.Id));
                    UpdateIndicators(row);
                }
            }
            _lastDeskEdit = null;
            CanUndoDeskEdit = false;
            var log = DeskChangeLog.LastOrDefault(x => x.CompetitorId == undo.CompetitorId
                && x.Kind is DeskChangeKind.Add or DeskChangeKind.Edit);
            if (log is not null)
            {
                DeskChangeLog.Remove(log);
                OnPropertyChanged(nameof(HasDeskChangeLog));
                OnPropertyChanged(nameof(IsDeskChangeLogVisible));
            }
            RefreshVisibleCompetitors();
            SetStatus("Last saved competitor edit restored.");
        });
    }

    [RelayCommand]
    private async Task RestoreDeskChangeAsync(DeskChangeLogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (IsImportReviewOpen)
        {
            SetStatus("Commit or discard the pasted preview before restoring changes.", error: true);
            return;
        }
        if (entry.Kind == DeskChangeKind.Delete)
        {
            var deleted = _allCompetitorRows.FirstOrDefault(x => x.Id == entry.CompetitorId);
            if (deleted is not null) { deleted.IsPendingDelete = false; }
            DeskChangeLog.Remove(entry);
            OnPropertyChanged(nameof(HasDeskChangeLog));
            OnPropertyChanged(nameof(IsDeskChangeLogVisible));
            SetStatus("Pending deletion restored.");
            return;
        }
        _lastDeskEdit = new UndoDeskEdit(entry.CompetitorId, entry.PreviousValues,
            entry.CompetitionId, entry.PreviousParticipation, entry.PreviousImportedBib);
        CanUndoDeskEdit = true;
        await UndoDeskEditAsync();
        if (!IsError && entry.Kind == DeskChangeKind.Add)
        {
            foreach (var later in DeskChangeLog.Where(x => x.CompetitorId == entry.CompetitorId).ToArray())
            {
                DeskChangeLog.Remove(later);
            }
            OnPropertyChanged(nameof(HasDeskChangeLog));
            OnPropertyChanged(nameof(IsDeskChangeLogVisible));
        }
    }

    [RelayCommand]
    private async Task CommitDeskChangesAsync()
    {
        if (IsImportReviewOpen)
        {
            SetStatus("Commit or discard the pasted preview first.", error: true);
            return;
        }
        if (_allCompetitorRows.Any(x => x.HasDraftChanges))
        {
            SetStatus("Save or discard unfinished row edits before committing the Change Log.", error: true);
            return;
        }
        await GuardAsync(async () =>
        {
            foreach (var row in _allCompetitorRows.Where(x => x.IsPendingDelete).ToArray())
            {
                var series = _current ?? throw new SeriesFileException("Open an event series first.");
                var id = row.Id ?? throw new DomainValidationException("An unsaved row cannot be deleted.");
                var revision = await workspace.RemoveCompetitorAsync(id, series.Revision);
                _current = series with { Revision = revision };
                _allCompetitorRows.Remove(row);
                if (_desk is { } desk)
                {
                    _desk = desk with { Revision = revision,
                        Competitors = desk.Competitors.Where(x => x.Id != id).ToArray(),
                        Participations = desk.Participations.Where(x => x.CompetitorId != id).ToArray() };
                }
            }
            DeskChangeLog.Clear();
            OnPropertyChanged(nameof(HasDeskChangeLog));
            OnPropertyChanged(nameof(IsDeskChangeLogVisible));
            _lastDeskEdit = null;
            CanUndoDeskEdit = false;
            RefreshVisibleCompetitors();
            SetStatus("Changes committed. Pending deletions were saved.");
        });
    }

    [RelayCommand]
    private async Task RemoveCompetitorAsync()
    {
        if (IsImportReviewOpen)
        {
            SetStatus("Commit or discard the pasted preview before removing competitors.", error: true);
            return;
        }
        if (SelectedCompetitorRow?.Id is not { } id || _current is null) { return; }
        if (SelectedCompetitorRow.HasDraftChanges)
        {
            SetStatus("Save or discard this row's edits before marking it for deletion.", error: true);
            return;
        }
        if (!await dialogs.ConfirmRemoveCompetitorAsync(SelectedCompetitorRow.Surname)) { return; }
        if (SelectedCompetitorRow.IsPendingDelete) { return; }
        SelectedCompetitorRow.IsPendingDelete = true;
        AppendDeskChange(new DeskChangeLogEntry(Guid.NewGuid(), DateTime.UtcNow,
            DeskChangeKind.Delete, id, SelectedCompetitorRow.SavedValues, null, false, null,
            $"Deleted: {SelectedCompetitorRow.Surname} {SelectedCompetitorRow.FirstName}"));
        SetStatus("Competitor marked for deletion. Restore it in Change Log or Commit Changes.");
    }

    [RelayCommand]
    private void NewCategoryRule()
    {
        SelectedCategoryRule = null;
        CategoryLabel = CategoryMinYearText = CategoryMaxYearText = CategoryGenderText = string.Empty;
        CategoryOrderText = CategoryRules.Count.ToString(CultureInfo.InvariantCulture);
    }

    [RelayCommand]
    private async Task SaveCategoryRuleAsync()
    {
        await GuardAsync(async () =>
        {
            EnsureDeskClean();
            var series = _current ?? throw new SeriesFileException("Open an event series first.");
            if (!int.TryParse(CategoryMinYearText, NumberStyles.None, CultureInfo.InvariantCulture, out var min)
                || !int.TryParse(CategoryMaxYearText, NumberStyles.None, CultureInfo.InvariantCulture, out var max)
                || !int.TryParse(CategoryOrderText, NumberStyles.None, CultureInfo.InvariantCulture, out var order))
            {
                throw new DomainValidationException("Category years and order must be whole numbers.");
            }
            var values = new CategoryRuleValues(CategoryLabel, min, max, ParseCategoryGender(CategoryGenderText), order);
            var result = await workspace.SaveCategoryRuleAsync(SelectedCategoryRule?.Id, values, series.Revision);
            _current = series with { Revision = result.Revision };
            await LoadCompetitorDeskAsync();
            SelectedCategoryRule = CategoryRules.First(x => x.Id == result.Value.Id);
            SetStatus("Category rule saved. Ambiguous matches remain visible for review.");
        });
    }

    [RelayCommand]
    private async Task RemoveCategoryRuleAsync()
    {
        if (SelectedCategoryRule is not { } selected || _current is not { } series) { return; }
        await GuardAsync(async () =>
        {
            EnsureDeskClean();
            var revision = await workspace.RemoveCategoryRuleAsync(selected.Id, series.Revision);
            _current = series with { Revision = revision };
            await LoadCompetitorDeskAsync();
            NewCategoryRule();
            SetStatus("Category rule removed.");
        });
    }

    private static Gender? ParseCategoryGender(string text) => text.Trim().ToUpperInvariant() switch
    {
        "" => null,
        "F" or "FEMALE" or "WOMAN" or "WOMEN" => Gender.Female,
        "M" or "MALE" or "MAN" or "MEN" => Gender.Male,
        "O" or "OTHER" => Gender.Other,
        _ => throw new DomainValidationException("Category gender must be Women, Men or Other."),
    };

    [RelayCommand]
    private async Task PreviewLegacyAsync()
    {
        await GuardAsync(async () =>
        {
            if (legacyPreviewer is null)
            {
                throw new SeriesFileException("Legacy preview is not available in this build.");
            }
            var path = await dialogs.ChooseLegacyDatabaseAsync();
            if (path is null) { return; }
            var preview = await legacyPreviewer.PreviewAsync(path);
            LegacySeriesPreviews.Clear();
            foreach (var series in preview.Series) { LegacySeriesPreviews.Add(series); }
            LegacySourceWarnings.Clear();
            foreach (var warning in preview.Warnings) { LegacySourceWarnings.Add(warning); }
            SelectedLegacySeries = LegacySeriesPreviews.FirstOrDefault();
            LegacySnapshotLabel = $"Read-only snapshot {preview.SnapshotSha256[..12]} · {preview.SourcePath}";
            SetStatus("Legacy data previewed from a consistent read-only snapshot. No data was converted.");
        });
    }

    private sealed record UndoDeskEdit(Guid CompetitorId, CompetitorValues? Previous,
        Guid? CompetitionId, bool Participates, int? ImportedBib);

    public void Dispose() => _deskRowSaveGate.Dispose();
}
