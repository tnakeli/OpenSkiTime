using System.Collections.ObjectModel;
using System.ComponentModel;
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
    private readonly SemaphoreSlim _deskCommitGate = new(1, 1);
    private readonly CategoryRulePresetStore _categoryRulePresetStore = categoryRulePresetStore ?? new();
    private bool _suspendDeskChangeLog;
    private string? _competitorSortKey;
    private bool _competitorSortDescending;

    public ObservableCollection<CompetitorGridRow> VisibleCompetitors { get; } = [];
    public ObservableCollection<DeskChangeLogEntry> DeskChangeLog { get; } = [];
    public ObservableCollection<CategoryRuleDetails> CategoryRules { get; } = [];
    public IReadOnlyList<string> CategoryGenderOptions { get; } = ["Any", "Women", "Men"];
    public bool HasDeskChangeLog => DeskChangeLog.Count > 0;
    public string UnsavedChangesText => DeskChangeLog.Count is 0 ? "All changes saved"
        : $"{DeskChangeLog.Count} unsaved change{(DeskChangeLog.Count == 1 ? "" : "s")}";
    public string ChangeReviewButtonText => IsChangeReviewOpen ? "Hide changes" : "Review changes";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ChangeReviewButtonText))]
    private bool _isChangeReviewOpen;

    [RelayCommand]
    private void ToggleChangeReview() => IsChangeReviewOpen = !IsChangeReviewOpen;

    private CompetitionDetails? _deskCompetition;
    public CompetitionDetails? DeskCompetition
    {
        get => _deskCompetition;
        set
        {
            if (_deskCompetition?.Id != value?.Id && HasDeskDrafts)
            {
                SetStatus("Save or discard unsaved changes before switching competitions.", error: true);
                OnPropertyChanged(nameof(DeskCompetition));
                return;
            }
            SetProperty(ref _deskCompetition, value);
        }
    }

    [ObservableProperty] private CompetitorGridRow? _selectedCompetitorRow;
    [ObservableProperty] private CategoryRuleDetails? _selectedCategoryRule;
    [ObservableProperty] private string _categoryLabel = string.Empty;
    [ObservableProperty] private string _categoryMinYearText = string.Empty;
    [ObservableProperty] private string _categoryMaxYearText = string.Empty;
    [ObservableProperty] private string _categoryGenderText = "Any";
    [ObservableProperty] private string _categoryOrderText = "0";

    partial void OnSelectedCategoryRuleChanged(CategoryRuleDetails? value)
    {
        if (value is null) { return; }
        CategoryLabel = value.Values.Label;
        CategoryMinYearText = value.Values.BirthYearMin.ToString(CultureInfo.InvariantCulture);
        CategoryMaxYearText = value.Values.BirthYearMax.ToString(CultureInfo.InvariantCulture);
        CategoryGenderText = value.Values.Gender is null ? "Any" : GenderLabels.Format(value.Values.Gender);
        CategoryOrderText = value.Values.DisplayOrder.ToString(CultureInfo.InvariantCulture);
    }

    private bool HasDeskDrafts => _allCompetitorRows.Any(x => x.HasPendingChanges);

    private void EnsureDeskClean(bool includeDrawInput = true)
    {
        if (includeDrawInput && HasUnsavedRunInput)
        { throw new DomainValidationException("Create the start list to save the Run 1 input, or discard changes before changing the event file."); }
        if (HasDeskDrafts)
        {
            throw new DomainValidationException("Save or discard unsaved changes before changing the event file or competitions.");
        }
    }

    private async Task LoadCompetitorDeskAsync()
    {
        EnsureFisListLoaded();
        var selectedId = SelectedCompetitorRow?.Id;
        _desk = await workspace.ReadCompetitorDeskAsync();
        _selectedExportRows.Clear();
        CategoryRules.Clear();
        foreach (var rule in _desk.Categories) { CategoryRules.Add(rule); }
        _allCompetitorRows.Clear();
        foreach (var competitor in _desk.Competitors)
        {
            var row = CompetitorGridRow.From(competitor);
            UpdateFisPoints(row);
            FillGridEntries(row);
            SubscribeRow(row);
            UpdateCategory(row);
            _allCompetitorRows.Add(row);
        }
        AddPlaceholder();
        RefreshVisibleCompetitors();
        SelectedCompetitorRow = VisibleCompetitors.FirstOrDefault(x => x.Id == selectedId);
        RebuildChangeLog();
    }

    private void ClearCompetitorDesk()
    {
        IsFisPanelOpen = false;
        FisEffectiveDateText = string.Empty;
        _desk = null;
        _selectedExportRows.Clear();
        _allCompetitorRows.Clear();
        VisibleCompetitors.Clear();
        _competitorColumnFilters.Clear(); _competitorSortKey = null;
        SelectedCompetitorRow = null;
        DeskCompetition = null;
        CategoryRules.Clear();
        ImportWarnings.Clear();
        RebuildChangeLog();
    }

    private void FillGridEntries(CompetitorGridRow row)
    {
        row.GridEntries.Clear();
        foreach (var competition in Competitions)
        {
            var entry = _desk?.Participations.FirstOrDefault(x => x.CompetitorId == row.Id
                && x.CompetitionId == competition.Id);
            var choice = new CompetitionEntryChoice(competition, entry?.Participates ?? false, entry?.ImportedBib);
            row.GridEntries.Add(choice);
            choice.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName != nameof(CompetitionEntryChoice.IsParticipating)) { return; }
                row.NotifyDraftChanged();
                if (row.IsPlaceholder && row.HasTypedContent)
                {
                    row.IsPlaceholder = false;
                    AddPlaceholder();
                    VisibleCompetitors.Add(_allCompetitorRows[^1]);
                }
                UpdateCategory(row);
                RebuildChangeLog();
            };
        }
    }

    private void SubscribeRow(CompetitorGridRow row)
    {
        row.PropertyChanged += (_, args) => OnGridRowChanged(row, args);
    }

    private void OnGridRowChanged(CompetitorGridRow row, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is not (nameof(CompetitorGridRow.Surname) or nameof(CompetitorGridRow.FirstName)
            or nameof(CompetitorGridRow.BirthYearText) or nameof(CompetitorGridRow.GenderText)
            or nameof(CompetitorGridRow.Nation) or nameof(CompetitorGridRow.Club)
            or nameof(CompetitorGridRow.FederationCode) or nameof(CompetitorGridRow.IsPendingDelete))) { return; }
        if (row.IsPlaceholder && row.HasTypedContent)
        {
            row.IsPlaceholder = false;
            AddPlaceholder();
            VisibleCompetitors.Add(_allCompetitorRows[^1]);
        }
        UpdateCategory(row);
        if (args.PropertyName == nameof(CompetitorGridRow.FederationCode)) { UpdateFisPoints(row); }
        if (!_suspendDeskChangeLog) { RebuildChangeLog(); }
    }

    private void AddPlaceholder()
    {
        var row = new CompetitorGridRow { IsPlaceholder = true };
        FillGridEntries(row);
        SubscribeRow(row);
        _allCompetitorRows.Add(row);
    }

    private void UpdateCategory(CompetitorGridRow row)
    {
        if (row.IsPlaceholder) { return; }
        try { row.Category = CategoryResolver.Resolve(row.Draft(), CategoryRules); }
        catch (DomainValidationException) { row.Category = "Review fields"; }
    }

    [RelayCommand]
    private void UpdateCategories()
    {
        foreach (var row in _allCompetitorRows) { UpdateCategory(row); }
        RefreshVisibleCompetitors();
        var count = _allCompetitorRows.Count(x => !x.IsPlaceholder);
        var unresolved = _allCompetitorRows.Count(x => !x.IsPlaceholder
            && x.Category is "Unclassified" or "Ambiguous" or "Review fields");
        SetStatus($"Categories updated for {count} competitor{(count == 1 ? "" : "s")}. {unresolved} need review.");
    }

    private void RefreshVisibleCompetitors()
    {
        var source = _allCompetitorRows.Where(row => row.IsPlaceholder
            || MatchesCompetitorColumnFilters(row));
        source = _competitorSortKey is { } sortKey
            ? _competitorSortDescending
                ? source.OrderBy(x => x.IsPlaceholder).ThenByDescending(x => SortValue(x, sortKey), CompetitorSortComparer.Instance)
                : source.OrderBy(x => x.IsPlaceholder).ThenBy(x => SortValue(x, sortKey), CompetitorSortComparer.Instance)
            : source.OrderBy(x => x.IsPlaceholder).ThenBy(x => x.Surname).ThenBy(x => x.FirstName);
        var selected = SelectedCompetitorRow;
        VisibleCompetitors.Clear();
        foreach (var row in source) { VisibleCompetitors.Add(row); }
        SelectedCompetitorRow = selected is not null && VisibleCompetitors.Contains(selected) ? selected : null;
    }

    public void SortCompetitors(string key, bool descending)
    {
        _competitorSortKey = key;
        _competitorSortDescending = descending;
        RefreshVisibleCompetitors();
    }

    private static object? SortValue(CompetitorGridRow row, string key) => key switch
    {
        nameof(CompetitorGridRow.FederationCode) => row.FederationCode,
        nameof(CompetitorGridRow.Surname) => row.Surname,
        nameof(CompetitorGridRow.FirstName) => row.FirstName,
        nameof(CompetitorGridRow.BirthYearText) => int.TryParse(row.BirthYearText, out var year) ? year : null,
        nameof(CompetitorGridRow.GenderText) => row.GenderText,
        nameof(CompetitorGridRow.Nation) => row.Nation,
        nameof(CompetitorGridRow.Club) => row.Club,
        nameof(CompetitorGridRow.Category) => row.Category,
        nameof(CompetitorGridRow.FisDh) => row.FisDh,
        nameof(CompetitorGridRow.FisSg) => row.FisSg,
        nameof(CompetitorGridRow.FisSl) => row.FisSl,
        nameof(CompetitorGridRow.FisGs) => row.FisGs,
        nameof(CompetitorGridRow.FisAc) => row.FisAc,
        _ when key.StartsWith("entry:", StringComparison.Ordinal) && Guid.TryParse(key[6..], out var id)
            => row.GridEntries.FirstOrDefault(x => x.CompetitionId == id)?.IsParticipating,
        _ => null,
    };

    private sealed class CompetitorSortComparer : IComparer<object?>
    {
        public static CompetitorSortComparer Instance { get; } = new();
        public int Compare(object? x, object? y)
        {
            if (x is null) { return y is null ? 0 : 1; }
            if (y is null) { return -1; }
            return x is string left && y is string right
                ? StringComparer.CurrentCultureIgnoreCase.Compare(left, right)
                : Comparer<object>.Default.Compare(x, y);
        }
    }

    private void RebuildChangeLog()
    {
        DeskChangeLog.Clear();
        foreach (var row in _allCompetitorRows.Where(x => x.HasPendingChanges))
        {
            var label = ((row.FederationCode.Length > 0 ? row.FederationCode : "—") + " "
                + (row.Surname.Length > 0 ? row.Surname : "—")).Trim();
            if (row.IsPendingDelete)
            {
                DeskChangeLog.Add(new DeskChangeLogEntry(row.LocalId, DeskChangeKind.Delete, null, null,
                    $"{label} · Delete: present → removed"));
                continue;
            }
            if (row.Id is null)
            {
                DeskChangeLog.Add(new DeskChangeLogEntry(row.LocalId, DeskChangeKind.Add, null, null,
                    $"{label} · Add: — → new competitor"));
                continue;
            }
            AddFieldChange(row, nameof(CompetitorGridRow.Surname), row.IsSurnameChanged, "Surname", label);
            AddFieldChange(row, nameof(CompetitorGridRow.FirstName), row.IsFirstNameChanged, "First name", label);
            AddFieldChange(row, nameof(CompetitorGridRow.BirthYearText), row.IsYearChanged, "Year", label);
            AddFieldChange(row, nameof(CompetitorGridRow.GenderText), row.IsGenderChanged, "Gender", label);
            AddFieldChange(row, nameof(CompetitorGridRow.Nation), row.IsNationChanged, "Nation", label);
            AddFieldChange(row, nameof(CompetitorGridRow.Club), row.IsClubChanged, "Club", label);
            AddFieldChange(row, nameof(CompetitorGridRow.FederationCode), row.IsCodeChanged, "Code", label);
            foreach (var choice in row.GridEntries.Where(x => x.IsChanged))
            {
                DeskChangeLog.Add(new DeskChangeLogEntry(row.LocalId, DeskChangeKind.Entry,
                    null, choice.CompetitionId, $"{label} · {choice.Label}: "
                    + $"{(choice.SavedParticipation ? "entered" : "not entered")} → "
                    + (choice.IsParticipating ? "entered" : "not entered")));
            }
        }
        if (!HasDeskChangeLog) { IsChangeReviewOpen = false; }
        OnPropertyChanged(nameof(HasDeskChangeLog));
        OnPropertyChanged(nameof(UnsavedChangesText));
    }

    private void AddFieldChange(CompetitorGridRow row, string field, bool changed, string title, string label)
    {
        if (changed)
        {
            var saved = row.SavedValues;
            var (oldValue, newValue) = field switch
            {
                nameof(CompetitorGridRow.Surname) => (saved?.Surname, row.Surname),
                nameof(CompetitorGridRow.FirstName) => (saved?.FirstName, row.FirstName),
                nameof(CompetitorGridRow.BirthYearText) => (saved?.BirthYear?.ToString(CultureInfo.InvariantCulture), row.BirthYearText),
                nameof(CompetitorGridRow.GenderText) => (GenderLabels.Format(saved?.Gender), row.GenderText),
                nameof(CompetitorGridRow.Nation) => (saved?.Nation, row.Nation),
                nameof(CompetitorGridRow.Club) => (saved?.Club, row.Club),
                nameof(CompetitorGridRow.FederationCode) => (saved?.FederationCode, row.FederationCode),
                _ => (null, null),
            };
            DeskChangeLog.Add(new DeskChangeLogEntry(row.LocalId, DeskChangeKind.Edit, field, null,
                $"{label} · {title}: {DisplayChangeValue(oldValue)} → {DisplayChangeValue(newValue)}"));
        }
    }

    private static string DisplayChangeValue(string? value) => string.IsNullOrWhiteSpace(value) ? "—" : value;

    [RelayCommand]
    private void RestoreDeskChange(DeskChangeLogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var row = _allCompetitorRows.FirstOrDefault(x => x.LocalId == entry.LocalRowId);
        if (row is null) { return; }
        switch (entry.Kind)
        {
            case DeskChangeKind.Add:
                _allCompetitorRows.Remove(row);
                RefreshVisibleCompetitors();
                break;
            case DeskChangeKind.Delete:
                row.IsPendingDelete = false;
                break;
            case DeskChangeKind.Edit:
                if (entry.Field is not null) { row.RestoreField(entry.Field); }
                break;
            case DeskChangeKind.Entry:
                row.GridEntries.First(x => x.CompetitionId == entry.CompetitionId).Restore();
                break;
        }
        RebuildChangeLog();
        if (!HasDeskChangeLog) { ImportWarnings.Clear(); }
        SetStatus("Change undone. Saved data was not changed.");
    }

    [RelayCommand]
    private async Task DiscardDeskChangesAsync()
    {
        if (!HasDeskChangeLog) { return; }
        var count = DeskChangeLog.Count;
        if (!await dialogs.ConfirmDiscardChangesAsync(count)) { return; }
        foreach (var row in _allCompetitorRows.ToArray())
        {
            if (row.Id is null && !row.IsPlaceholder) { _allCompetitorRows.Remove(row); }
            else { row.RestoreAll(); UpdateCategory(row); }
        }
        ImportWarnings.Clear();
        RefreshVisibleCompetitors();
        RebuildChangeLog();
        SetStatus($"Discarded {count} unsaved change{(count == 1 ? "" : "s")}.");
    }

    [RelayCommand]
    private void RemoveCompetitor()
    {
        RemoveCompetitors(SelectedCompetitorRow is { } row ? [row] : []);
    }

    public void RemoveCompetitors(IEnumerable<CompetitorGridRow> selected)
    {
        ArgumentNullException.ThrowIfNull(selected);
        var rows = selected.Where(x => _allCompetitorRows.Contains(x) && !x.IsPlaceholder && !x.IsPendingDelete)
            .Distinct().ToArray();
        if (rows.Length == 0) { return; }
        _suspendDeskChangeLog = true;
        try
        {
            foreach (var row in rows)
            {
                if (row.Id is null) { _allCompetitorRows.Remove(row); }
                else { row.IsPendingDelete = true; }
            }
        }
        finally { _suspendDeskChangeLog = false; }
        RefreshVisibleCompetitors();
        RebuildChangeLog();
        SetStatus($"{rows.Length} competitor row(s) marked for deletion. Save changes or undo the deletion.");
    }

    [RelayCommand]
    private void RestoreSelectedRow()
    {
        var row = SelectedCompetitorRow;
        if (row is null || row.IsPlaceholder) { return; }
        if (row.Id is null)
        {
            _allCompetitorRows.Remove(row);
            RefreshVisibleCompetitors();
        }
        else { row.RestoreAll(); UpdateCategory(row); }
        RebuildChangeLog();
        if (!HasDeskChangeLog) { ImportWarnings.Clear(); }
    }

    public Task StageGridEntryAsync(CompetitorGridRow row, CompetitionEntryChoice choice,
        IEnumerable<CompetitorGridRow>? selectedRows = null)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(choice);
        var selection = selectedRows?.Where(x => !x.IsPlaceholder && !x.IsPendingDelete).Distinct().ToArray() ?? [];
        if (selection.Length > 1 && selection.Contains(row))
        {
            _suspendDeskChangeLog = true;
            try
            {
                foreach (var target in selection)
                {
                    var entry = target.GridEntries.FirstOrDefault(x => x.CompetitionId == choice.CompetitionId);
                    if (entry is not null) { entry.IsParticipating = choice.IsParticipating; }
                }
            }
            finally { _suspendDeskChangeLog = false; }
        }
        if (selection.Length <= 1) { SelectedCompetitorRow = row; }
        RebuildChangeLog();
        return Task.CompletedTask;
    }

    [RelayCommand]
    private async Task CommitDeskChangesAsync()
    {
        if (!HasDeskChangeLog) { return; }
        await _deskCommitGate.WaitAsync();
        try
        {
            await GuardAsync(async () =>
            {
                var series = _current ?? throw new SeriesFileException("Open an event series first.");
                var unapproved = _allCompetitorRows.FirstOrDefault(x => x.NeedsApproval && !x.WarningApproved);
                if (unapproved is not null)
                {
                    throw new DomainValidationException($"Confirm the matching warning for {unapproved.Surname} before saving.");
                }
                var changedRows = _allCompetitorRows.Where(x => x.HasPendingChanges && !x.IsPendingDelete).ToArray();
                var rows = changedRows.Select(row => new DeskBatchRow(row.Id, row.Draft(),
                    row.GridEntries.Where(x => x.IsChanged).Select(x =>
                        new ImportEntryPatch(x.CompetitionId, x.IsParticipating, false, null)).ToArray())).ToArray();
                var deletes = _allCompetitorRows.Where(x => x.IsPendingDelete && x.Id is not null)
                    .Select(x => x.Id!.Value).ToArray();
                var result = await workspace.ApplyDeskBatchAsync(new DeskBatch(series.Id, series.Revision, rows, deletes));
                _current = series with { Revision = result.Revision };
                await LoadCompetitorDeskAsync();
                ImportWarnings.Clear();
                SetStatus($"Saved {result.Created} new, {result.Updated} changed and {result.Deleted} deleted competitors.");
            });
        }
        finally { _deskCommitGate.Release(); }
    }

    [RelayCommand]
    private void NewCategoryRule()
    {
        SelectedCategoryRule = null;
        CategoryLabel = CategoryMinYearText = CategoryMaxYearText = string.Empty;
        CategoryGenderText = "Any";
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
            SetStatus("Category rule saved.");
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

    [RelayCommand]
    private async Task SaveCategoryRulesPresetAsync()
    {
        await GuardAsync(async () =>
        {
            await _categoryRulePresetStore.SaveAsync(CategoryRules.Select(x => x.Values).ToArray());
            SetStatus($"Saved {CategoryRules.Count} category rules for reuse on this computer.");
        });
    }

    [RelayCommand]
    private async Task LoadCategoryRulesPresetAsync()
    {
        await GuardAsync(async () =>
        {
            EnsureDeskClean();
            var series = _current ?? throw new SeriesFileException("Open an event series first.");
            var rules = await _categoryRulePresetStore.LoadAsync();
            var revision = await workspace.ReplaceCategoryRulesAsync(rules, series.Revision);
            _current = series with { Revision = revision };
            await LoadCompetitorDeskAsync();
            NewCategoryRule();
            SetStatus($"Loaded {rules.Count} saved category rules into this event series. Categories updated.");
        });
    }

    private static Gender? ParseCategoryGender(string text) => text.Trim().ToUpperInvariant() switch
    {
        "" or "ANY" => null,
        "F" or "FEMALE" or "WOMAN" or "WOMEN" => Gender.Female,
        "M" or "MALE" or "MAN" or "MEN" => Gender.Male,
        _ => throw new DomainValidationException("Category gender must be Women or Men."),
    };

    public void Dispose() { DisposeTimingUi(); DisposeInformationAutosave(); DisposeResultSubmission(); if (_ownsInformationHttp) { _informationHttp.Dispose(); } _deskCommitGate.Dispose(); }
}
