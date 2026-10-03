using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.Input;
using OpenSkiTime.Application;
using OpenSkiTime.Domain;

namespace OpenSkiTime.Desktop;

public sealed partial class MainViewModel
{
    private readonly List<CompetitorGridRow> _selectedExportRows = [];
    public ObservableCollection<string> ImportWarnings { get; } = [];

    public void SetSelectedExportRows(IEnumerable<CompetitorGridRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        _selectedExportRows.Clear();
        _selectedExportRows.AddRange(rows.Where(x => !x.IsPlaceholder && !x.IsPendingDelete));
    }

    [RelayCommand]
    private async Task PasteFromExcelAsync()
    {
        await GuardAsync(async () =>
        {
            if (entryExchange is null) { throw new SeriesFileException("Clipboard is unavailable."); }
            var text = await entryExchange.ReadClipboardAsync();
            if (string.IsNullOrWhiteSpace(text))
            {
                throw new DomainValidationException("Clipboard has no tab-separated competitor table.");
            }
            var series = _current ?? throw new SeriesFileException("Open an event series first.");
            var desk = _desk ?? await workspace.ReadCompetitorDeskAsync();
            var preview = TsvExchange.Preview(text, series, desk);
            if (preview.Rows.Any(x => x.Entries.Any(y => y.BibSpecified)))
            {
                throw new DomainValidationException("Bib values belong to Draw. Remove Bib columns before pasting competitors.");
            }
            ImportWarnings.Clear();
            foreach (var warning in preview.Warnings) { ImportWarnings.Add(warning); }
            foreach (var item in preview.Rows) { StagePastedRow(item); }
            RefreshVisibleCompetitors();
            RebuildChangeLog();
            SetStatus($"Pasted {preview.Rows.Count} rows. Review the highlighted cells, then choose Save changes.");
        });
    }

    private void StagePastedRow(ImportReviewItem item)
    {
        var row = item.CompetitorId is { } id
            ? _allCompetitorRows.FirstOrDefault(x => x.Id == id)
            : _allCompetitorRows.FirstOrDefault(x => x.Id is null && !x.IsPlaceholder
                && (item.Values.FederationCode is not null &&
                    string.Equals(x.FederationCode, item.Values.FederationCode, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(x.Surname, item.Values.Surname, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(x.FirstName, item.Values.FirstName, StringComparison.OrdinalIgnoreCase)
                    && x.BirthYearText == item.Values.BirthYear?.ToString(CultureInfo.InvariantCulture)));
        if (row is null)
        {
            row = new CompetitorGridRow();
            FillGridEntries(row);
            SubscribeRow(row);
            _allCompetitorRows.Add(row);
        }
        var values = item.Values;
        if (item.IsNew || item.ChangedFields.Contains("Surname")) { row.Surname = values.Surname; }
        if (item.IsNew || item.ChangedFields.Contains("First name")) { row.FirstName = values.FirstName; }
        if (item.IsNew || item.ChangedFields.Contains("Year"))
        {
            row.BirthYearText = values.BirthYear?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        }
        if (item.IsNew || item.ChangedFields.Contains("Gender")) { row.GenderText = GenderLabels.Format(values.Gender); }
        if (item.IsNew || item.ChangedFields.Contains("Nation")) { row.Nation = values.Nation ?? string.Empty; }
        if (item.IsNew || item.ChangedFields.Contains("Club")) { row.Club = values.Club ?? string.Empty; }
        if (item.IsNew || item.ChangedFields.Contains("Code")) { row.FederationCode = values.FederationCode ?? string.Empty; }
        foreach (var patch in item.Entries)
        {
            if (patch.Participates is { } state)
            {
                row.GridEntries.First(x => x.CompetitionId == patch.CompetitionId).IsParticipating = state;
            }
        }
        row.WarningText = string.Join(" ", item.Warnings);
        row.WarningApproved = false;
        UpdateCategory(row);
        row.NotifyDraftChanged();
    }

    [RelayCommand]
    private async Task CopySelectedCompetitorAsync()
    {
        await GuardAsync(async () =>
        {
            if (entryExchange is null) { throw new SeriesFileException("Clipboard is unavailable."); }
            var rows = _selectedExportRows.Count > 0 ? _selectedExportRows
                : SelectedCompetitorRow is { IsPlaceholder: false, IsPendingDelete: false } selected
                    ? [selected] : throw new DomainValidationException("Select a competitor to copy.");
            var table = new List<IReadOnlyList<string?>>();
            table.Add(["Code", "Surname", "First name", "Year", "Gender", "Nation", "Club",
                .. Competitions.Select(x => x.Values.ShortLabel)]);
            foreach (var row in rows)
            {
                table.Add([row.FederationCode, row.Surname, row.FirstName, row.BirthYearText,
                    row.GenderText, row.Nation, row.Club,
                    .. row.GridEntries.Select(x => x.IsParticipating ? "X" : "")]);
            }
            await entryExchange.WriteClipboardAsync(TsvExchange.Encode(table));
            SetStatus($"Copied {rows.Count} competitor rows as TSV.");
        });
    }
}
