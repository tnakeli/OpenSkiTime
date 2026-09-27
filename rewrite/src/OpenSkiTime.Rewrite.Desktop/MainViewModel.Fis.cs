using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenSkiTime.Rewrite.Application;
using OpenSkiTime.Rewrite.Domain;

namespace OpenSkiTime.Rewrite.Desktop;

public sealed record FisSearchResult(FisAthlete Athlete)
{
    public string Display => $"{Athlete.Code}   {Athlete.Surname}, {Athlete.FirstName}   {Athlete.BirthYear}   {Athlete.Nation}   "
        + $"SL {Point("SL")}  GS {Point("GS")}";

    private string Point(string discipline) => Athlete.Points is not null
        && Athlete.Points.TryGetValue(discipline, out var value)
        ? value.ToString("0.00", CultureInfo.InvariantCulture) : "—";
}

public sealed partial class MainViewModel
{
    private readonly FisLocalStore _fisStore = fisStore ?? new();
    private readonly FisPointsDownloader _fisDownloader = new(new HttpClient { Timeout = TimeSpan.FromMinutes(2) });
    private FisPointsList? _fisList;
    private bool _fisCacheLoaded;

    [ObservableProperty] private bool _isFisPanelOpen;
    [ObservableProperty] private bool _isFisBusy;
    [ObservableProperty] private string _fisApiKeyInput = string.Empty;
    [ObservableProperty] private string _fisApiKeyStatus = "Not configured";
    [ObservableProperty] private string _fisApiKeyWatermark = "FIS API key";
    [ObservableProperty] private string _fisListDisplay = "No FIS points list downloaded";
    [ObservableProperty] private string _fisListValidityText = string.Empty;
    [ObservableProperty] private string _fisEffectiveDateText = string.Empty;
    [ObservableProperty] private string _fisSearchText = string.Empty;
    [ObservableProperty] private FisSearchResult? _selectedFisSearchResult;
    public ObservableCollection<FisSearchResult> FisSearchResults { get; } = [];
    private readonly List<FisSearchResult> _selectedFisSearchResults = [];

    public void SetSelectedFisSearchResults(IEnumerable<FisSearchResult> results)
    {
        _selectedFisSearchResults.Clear();
        _selectedFisSearchResults.AddRange(results);
        SelectedFisSearchResult = _selectedFisSearchResults.FirstOrDefault();
    }

    private void RefreshFisSettingsStatus()
    {
        try
        {
            var configured = _fisStore.HasApiKey();
            FisApiKeyStatus = configured ? "Saved for this Windows user" : "Not configured";
            FisApiKeyWatermark = configured ? "••••••••••••" : "FIS API key";
        }
        catch (Exception ex) when (ex is IOException or PlatformNotSupportedException)
        {
            FisApiKeyStatus = "Credential store unavailable";
            FisApiKeyWatermark = "FIS API key";
            SetStatus(ex.Message, error: true);
        }
    }

    [RelayCommand]
    private void SaveFisApiKey()
    {
        try
        {
            _fisStore.SaveApiKey(FisApiKeyInput);
            FisApiKeyInput = string.Empty;
            RefreshFisSettingsStatus();
            SetStatus("FIS API key saved in Windows Credential Manager.");
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or PlatformNotSupportedException)
        {
            SetStatus(ex.Message, error: true);
        }
    }

    [RelayCommand]
    private void RemoveFisApiKey()
    {
        try
        {
            _fisStore.RemoveApiKey();
            FisApiKeyInput = string.Empty;
            RefreshFisSettingsStatus();
            SetStatus("FIS API key removed from Windows Credential Manager.");
        }
        catch (Exception ex) when (ex is IOException or PlatformNotSupportedException)
        {
            SetStatus(ex.Message, error: true);
        }
    }

    partial void OnIsFisPanelOpenChanged(bool value)
    {
        if (!value) { return; }
        if (FisEffectiveDateText.Length == 0)
        {
            var date = Competitions.Count > 0
                ? Competitions.Min(x => x.Values.Date) : DateOnly.FromDateTime(DateTime.Today);
            FisEffectiveDateText = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }
        EnsureFisListLoaded();
    }

    private void EnsureFisListLoaded()
    {
        if (_fisCacheLoaded) { return; }
        _fisCacheLoaded = true;
        try
        {
            var bytes = _fisStore.LoadList();
            _fisList = bytes is null ? null : FisPointsListReader.Read(bytes);
            ShowFisListInfo();
            RefreshFisSearch();
        }
        catch (Exception ex) when (ex is IOException or DomainValidationException)
        {
            _fisList = null;
            FisListDisplay = "Cached FIS list cannot be read";
            FisListValidityText = string.Empty;
            SetStatus(ex.Message, error: true);
        }
    }

    private void UpdateFisPoints(CompetitorGridRow row)
    {
        var code = row.FederationCode.Trim();
        row.SetFisPoints(code.Length > 0 && _fisList?.Athletes.TryGetValue(code, out var athlete) == true
            ? athlete.Points : null);
    }

    private void RefreshAllCompetitorFisPoints()
    {
        foreach (var row in _allCompetitorRows) { UpdateFisPoints(row); }
    }

    [RelayCommand]
    private async Task DownloadFisListAsync()
    {
        if (IsFisBusy) { return; }
        IsFisBusy = true;
        try
        {
            if (!DateOnly.TryParseExact(FisEffectiveDateText.Trim(), "yyyy-MM-dd",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            {
                throw new DomainValidationException("Use YYYY-MM-DD for the effective date.");
            }
            var key = _fisStore.ReadApiKey();
            if (string.IsNullOrEmpty(key))
            {
                throw new DomainValidationException("Set the FIS API key in Settings first.");
            }
            SetStatus("Downloading FIS points list…");
            var bytes = await _fisDownloader.DownloadAsync(date, key);
            var parsed = FisPointsListReader.Read(bytes);
            if (date < parsed.ValidFrom || date > parsed.ValidTo)
            {
                throw new DomainValidationException("Downloaded FIS list is not effective on the selected date.");
            }
            await _fisStore.SaveListAsync(bytes);
            _fisList = parsed;
            _fisCacheLoaded = true;
            ShowFisListInfo();
            RefreshAllCompetitorFisPoints();
            RefreshFisSearch();
            SetStatus($"Downloaded {parsed.DisplayName}. {parsed.Athletes.Count} athletes available offline.");
        }
        catch (Exception ex) when (ex is DomainValidationException or IOException or HttpRequestException
            or TaskCanceledException or PlatformNotSupportedException)
        {
            SetStatus(ex is TaskCanceledException ? "FIS download timed out." : ex.Message, error: true);
        }
        finally { IsFisBusy = false; }
    }

    partial void OnFisSearchTextChanged(string value)
    {
        _selectedFisSearchResults.Clear();
        SelectedFisSearchResult = null;
        RefreshFisSearch();
    }

    [RelayCommand]
    private void ClearFisSearch() => FisSearchText = string.Empty;

    private void ShowFisListInfo()
    {
        FisListDisplay = _fisList?.DisplayName ?? "No FIS points list downloaded";
        FisListValidityText = _fisList is null ? string.Empty
            : $"Effective {_fisList.ValidFrom:yyyy-MM-dd}–{_fisList.ValidTo:yyyy-MM-dd}";
    }

    private void RefreshFisSearch()
    {
        FisSearchResults.Clear();
        if (_fisList is null || FisSearchText.Trim().Length < 2) { return; }
        foreach (var athlete in _fisList.Search(FisSearchText.Trim()))
        {
            FisSearchResults.Add(new FisSearchResult(athlete));
        }
    }

    [RelayCommand]
    private void StageFisUpdates()
    {
        if (_fisList is null) { SetStatus("Download a FIS points list first.", error: true); return; }
        var groups = _allCompetitorRows.Where(x => !x.IsPlaceholder && !x.IsPendingDelete
            && !string.IsNullOrWhiteSpace(x.FederationCode))
            .GroupBy(x => x.FederationCode.Trim(), StringComparer.OrdinalIgnoreCase).ToArray();
        var staged = 0;
        var skipped = 0;
        var ambiguous = 0;
        _suspendDeskChangeLog = true;
        try
        {
            foreach (var group in groups)
            {
                if (!_fisList.Athletes.TryGetValue(group.Key, out var athlete)) { continue; }
                if (group.Count() != 1) { ambiguous++; continue; }
                var row = group.Single();
                if (row.HasPendingChanges && !string.IsNullOrWhiteSpace(row.Surname)) { skipped++; continue; }
                if (ApplyFisValues(row, athlete)) { staged++; }
            }
        }
        finally { _suspendDeskChangeLog = false; }
        RefreshVisibleCompetitors();
        RebuildChangeLog();
        SetStatus($"FIS: {staged} updates ready to save; {skipped} unsaved edits and {ambiguous} duplicate Codes skipped.");
    }

    [RelayCommand]
    private void StageSelectedFisAthlete()
    {
        var athletes = (_selectedFisSearchResults.Count > 0 ? _selectedFisSearchResults
            : SelectedFisSearchResult is { } selected ? [selected] : [])
            .Select(x => x.Athlete).DistinctBy(x => x.Code).ToArray();
        if (athletes.Length == 0) { return; }
        var added = 0;
        var skipped = 0;
        _suspendDeskChangeLog = true;
        try
        {
            foreach (var athlete in athletes)
            {
                if (_allCompetitorRows.Any(x => !x.IsPlaceholder && !x.IsPendingDelete
                    && string.Equals(x.FederationCode.Trim(), athlete.Code, StringComparison.OrdinalIgnoreCase)))
                {
                    skipped++;
                    continue;
                }
                var row = new CompetitorGridRow();
                FillGridEntries(row);
                SubscribeRow(row);
                _allCompetitorRows.Add(row);
                row.FederationCode = athlete.Code;
                ApplyFisValues(row, athlete);
                added++;
            }
        }
        finally { _suspendDeskChangeLog = false; }
        RefreshVisibleCompetitors();
        RebuildChangeLog();
        SetStatus($"FIS: {added} competitor(s) ready to save; {skipped} existing Codes skipped.");
    }

    private bool ApplyFisValues(CompetitorGridRow row, FisAthlete athlete)
    {
        if (string.IsNullOrWhiteSpace(athlete.Surname)) { return false; }
        var wasChanged = row.HasPendingChanges;
        var wasCodeOnly = string.IsNullOrWhiteSpace(row.Surname);
        row.FederationCode = athlete.Code;
        row.Surname = athlete.Surname;
        if (athlete.FirstName.Length > 0) { row.FirstName = athlete.FirstName; }
        if (athlete.BirthYear is { } year) { row.BirthYearText = year.ToString(CultureInfo.InvariantCulture); }
        if (athlete.Gender is { } gender) { row.GenderText = GenderLabels.Format(gender); }
        if (athlete.Nation is { Length: 3 } nation) { row.Nation = nation; }
        if (!string.IsNullOrWhiteSpace(athlete.Club)) { row.Club = athlete.Club; }
        UpdateCategory(row);
        return row.HasPendingChanges && (!wasChanged || wasCodeOnly);
    }
}
