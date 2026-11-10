using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenSkiTime.Application;
using OpenSkiTime.Persistence;

namespace OpenSkiTime.Desktop;

public sealed record AuditCompetitionChoice(Guid? Id, string Label)
{
    public override string ToString() => Label;
}
public sealed record DebugLogWindow(int Hours, string Label)
{
    public override string ToString() => Label;
}

public sealed partial class MainViewModel
{
    private static readonly JsonSerializerOptions s_auditJson = new() { WriteIndented = true };
    private readonly UiPreferencesStore _uiPreferences = uiPreferencesStore ?? new();
    private readonly IAuditTrailReader _auditReader = auditReader ?? new SqliteAuditTrailReader(new Devices.AlgeDecoderFactory());
    private DebugBundleExporter? _bundleExporter;
    private CancellationTokenSource? _auditRead;
    private CancellationTokenSource? _auditDetailRead;
    private CancellationTokenSource? _bundleWrite;
    private readonly Stack<AuditCursor?> _auditBack = new();
    private AuditCursor? _auditCursor;
    private AuditCursor? _auditNext;
    private int _auditGeneration;
    private int _auditDetailGeneration;
    private AuditQuery? _auditPageQuery;
    private string? _auditFile;
    private bool _loadingUiPreferences;
    public bool IsAuditTrailSection => ActiveSection == WorkspaceSection.AuditTrail;
    [ObservableProperty] private bool _advancedMode;
    [ObservableProperty] private int _selectedAuditTab;
    [ObservableProperty] private bool _isAuditBusy;
    [ObservableProperty] private string _auditStatus = "Refresh to inspect saved history.";
    [ObservableProperty] private string _auditDetails = "Select a record to inspect its stored fields and source references.";
    [ObservableProperty] private string _auditEnvironment = "";
    [ObservableProperty] private string _auditSearch = "";
    [ObservableProperty] private string _auditOperator = "";
    [ObservableProperty] private string _auditBib = "";
    [ObservableProperty] private string _auditRun = "";
    [ObservableProperty] private string _auditFrom = "";
    [ObservableProperty] private string _auditUntil = "";
    [ObservableProperty] private string _auditSource = "All sources";
    [ObservableProperty] private AuditCompetitionChoice? _auditCompetition;
    [ObservableProperty] private AuditRecord? _selectedAuditRecord;
    [ObservableProperty] private bool _isDebugBundleReviewOpen;
    [ObservableProperty] private bool _includeDebugSeries;
    [ObservableProperty] private string _debugIssueDescription = "";
    [ObservableProperty] private bool _includeDebugDescription;
    [ObservableProperty] private bool _isDebugBundleBusy;
    [ObservableProperty] private string _debugBundleStatus = "";
    [ObservableProperty] private string? _savedDebugBundlePath;
    [ObservableProperty] private SupportLogFile? _selectedLocalAuditLog;
    [ObservableProperty] private DebugLogWindow _debugLogWindow = new(24, "Last 24 hours");
    private string? _reviewedBundleFile;
    private Guid? _reviewedBundleId;
    public ObservableCollection<AuditRecord> AuditRecords { get; } = [];
    public ObservableCollection<AuditLink> AuditLinks { get; } = [];
    public ObservableCollection<string> AuditSources { get; } = ["All sources"];
    public ObservableCollection<AuditCompetitionChoice> AuditCompetitions { get; } = [];
    public ObservableCollection<SupportLogEvent> AuditLogEvents { get; } = [];
    public ObservableCollection<SupportLogFile> LocalAuditLogs { get; } = [];
    public IReadOnlyList<DebugLogWindow> DebugLogWindows { get; } = [new(1, "Last hour"), new(24, "Last 24 hours"), new(168, "Last 7 days"), new(744, "Last 31 days")];
    public bool HasSavedDebugBundle => SavedDebugBundlePath is not null;
    public bool IsAuditDiagnostics => SelectedAuditTab == 2;
    public bool IsAuditRecords => !IsAuditDiagnostics;
    public bool CanAuditNext => _auditNext is not null && !IsAuditBusy;
    public bool CanAuditPrevious => _auditBack.Count > 0 && !IsAuditBusy;
    public bool CanIncludeDebugSeries => workspace.IsOpen && workspace.Timing?.IsActive != true && workspace.Auxiliary?.IsActive != true;
    public string DebugBundlePreview => "environment.json, configuration-summary.json (source types and channels only), diagnostics/worker-events.json, manifest.json, README.txt" +
        (_reviewedBundleFile is null ? "" : ", series-summary.json (counts only)") +
        (IncludeDebugSeries ? ", series/event-series.ost (complete saved history, raw input and personal race data; share privately)" : "") +
        (IncludeDebugDescription ? ", attachments/issue-description.txt (your text)" : "") +
        ". Log window: " + DebugLogWindow.Label + ". Unknown log text, wire payloads, credentials and downloaded FIS caches are excluded. ZIP is not encrypted.";

    public void LoadUiPreferences()
    {
        _loadingUiPreferences = true;
        try { AdvancedMode = _uiPreferences.Load(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        { AdvancedMode = false; SetStatus("UI preferences could not be read. Advanced mode is off; check local application data permissions.", error: true); }
        finally { _loadingUiPreferences = false; }
    }

    partial void OnAdvancedModeChanged(bool value)
    {
        if (!_loadingUiPreferences)
        {
            try { _uiPreferences.Save(value); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { _advancedMode = !value; OnPropertyChanged(nameof(AdvancedMode)); SetStatus("Advanced mode could not be saved. Check local application data permissions and retry.", error: true); return; }
        }
        if (!value && IsAuditTrailSection) { SelectedSettingsTab = 4; SwitchSection(WorkspaceSection.Settings); }
    }

    partial void OnSelectedAuditTabChanged(int value)
    {
        OnPropertyChanged(nameof(IsAuditDiagnostics)); OnPropertyChanged(nameof(IsAuditRecords));
        ResetAuditSources();
        if (IsAuditTrailSection) { RefreshAuditCommand.Execute(null); }
    }
    private void ResetAuditSources()
    {
        AuditSources.Clear(); AuditSources.Add("All sources");
        foreach (var source in _auditReader.Sources(AuditAreaForTab())) { AuditSources.Add(source); }
        AuditSource = "All sources";
    }
    partial void OnIsAuditBusyChanged(bool value) => NotifyAuditPaging();
    partial void OnIncludeDebugSeriesChanged(bool value) => OnPropertyChanged(nameof(DebugBundlePreview));
    partial void OnIncludeDebugDescriptionChanged(bool value) => OnPropertyChanged(nameof(DebugBundlePreview));
    partial void OnDebugLogWindowChanged(DebugLogWindow value) => OnPropertyChanged(nameof(DebugBundlePreview));
    partial void OnSavedDebugBundlePathChanged(string? value) => OnPropertyChanged(nameof(HasSavedDebugBundle));
    partial void OnSelectedAuditRecordChanged(AuditRecord? value)
    {
        _auditDetailRead?.Cancel(); _auditDetailGeneration++;
        AuditLinks.Clear(); AuditDetails = "";
        if (value is not null) { LoadAuditDetailCommand.Execute(value.Reference); }
    }

    private AuditArea AuditAreaForTab() => SelectedAuditTab switch { 1 => AuditArea.RawTiming, 3 => AuditArea.CurrentState, _ => AuditArea.Events };
    private void NotifyAuditPaging() { OnPropertyChanged(nameof(CanAuditNext)); OnPropertyChanged(nameof(CanAuditPrevious)); }

    [RelayCommand] private async Task ShowAuditTrailAsync()
    {
        if (!AdvancedMode || !await FlushRaceInformationAsync() || !await FlushTimingReportAsync()) { return; }
        SwitchSection(WorkspaceSection.AuditTrail);
        if (!IsAuditTrailSection) { return; }
        var competitionId = AuditCompetition?.Id;
        AuditCompetitions.Clear(); AuditCompetitions.Add(new(null, "All competitions"));
        foreach (var competition in Competitions) { AuditCompetitions.Add(new(competition.Id, competition.Values.ShortLabel)); }
        AuditCompetition = AuditCompetitions.FirstOrDefault(c => c.Id == competitionId) ?? AuditCompetitions[0];
        ResetAuditSources();
        await RefreshAuditAsync();
    }

    [RelayCommand(AllowConcurrentExecutions = true)] private async Task RefreshAuditAsync()
    {
        _auditCursor = null; _auditBack.Clear();
        await ReadAuditPageAsync();
    }
    [RelayCommand] private void ClearAuditFilters()
    {
        AuditSource = "All sources"; AuditCompetition = AuditCompetitions.FirstOrDefault();
        AuditSearch = AuditOperator = AuditBib = AuditRun = AuditFrom = AuditUntil = "";
        RefreshAuditCommand.Execute(null);
    }
    [RelayCommand] private async Task NextAuditPageAsync()
    {
        if (!CanAuditNext) { return; }
        _auditBack.Push(_auditCursor); _auditCursor = _auditNext;
        await ReadAuditPageAsync();
    }
    [RelayCommand] private async Task PreviousAuditPageAsync()
    {
        if (!CanAuditPrevious) { return; }
        _auditCursor = _auditBack.Pop(); await ReadAuditPageAsync();
    }

    private async Task ReadAuditPageAsync()
    {
        _auditRead?.Cancel();
        using var cancel = new CancellationTokenSource(); _auditRead = cancel;
        var generation = ++_auditGeneration;
        var file = workspace.FilePath; _auditFile = file;
        var selected = SelectedAuditRecord?.Reference;
        _auditNext = null;
        _auditDetailRead?.Cancel(); _auditDetailGeneration++;
        IsAuditBusy = true;
        try
        {
            var query = new AuditQuery(AuditAreaForTab(), AuditSource == "All sources" ? null : AuditSource,
                AuditCompetition?.Id, Number(AuditRun), AuditSearch, AuditOperator, Number(AuditBib),
                Timestamp(AuditFrom), Timestamp(AuditUntil), _auditCursor);
            if (_auditPageQuery != (query with { After = null }))
            { _auditCursor = null; _auditBack.Clear(); query = query with { After = null }; }
            _auditPageQuery = query with { After = null };
            if (query.From > query.Until) { throw new ArgumentException("From must be before Until."); }
            if (IsAuditDiagnostics)
            {
                var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
                var logs = await Task.Run(() => SupportDiagnostics.ReadAsync(SupportDiagnostics.WorkerLogDirectory, query.From ?? now.AddHours(-24), query.Until ?? now, cancel.Token));
                if (generation != _auditGeneration || workspace.FilePath != file) { return; }
                AuditLogEvents.Clear(); foreach (var item in logs.Events.Where(e => (e.Event + " " + e.Component).Contains(query.Search, StringComparison.OrdinalIgnoreCase))) { AuditLogEvents.Add(item); }
                AuditEnvironment = SupportDiagnostics.EnvironmentJson() + "\nSession-only state:\n" + JsonSerializer.Serialize(new
                {
                    PrimaryCaptureActive = workspace.Timing?.IsActive ?? false,
                    PendingPrimaryPackets = workspace.Timing?.Pending ?? 0,
                    AuxiliaryCaptureActive = workspace.Auxiliary?.IsActive ?? false,
                    Publishing = _liveChannels?.Select(c => new { c.Label, c.Status, c.WorkerProcessId }).ToArray()
                }, s_auditJson);
                LocalAuditLogs.Clear();
                foreach (var log in SupportDiagnostics.ListLocalFiles(SupportDiagnostics.WorkerLogDirectory)) { LocalAuditLogs.Add(log); }
                AuditStatus = $"{logs.Events.Count} safe events from {logs.FilesRead} files; {logs.OmittedLines} lines omitted. " + string.Join(" ", logs.Notices);
            }
            else if (file is null) { AuditRecords.Clear(); AuditStatus = "Open an event series to inspect saved history. Diagnostics and debug bundles work without a series."; }
            else
            {
                var page = await Task.Run(() => _auditReader.ReadAsync(file, query, cancel.Token));
                if (generation != _auditGeneration || workspace.FilePath != file) { return; }
                AuditRecords.Clear(); foreach (var row in page.Records) { AuditRecords.Add(row); }
                _auditNext = page.Next;
                SelectedAuditRecord = AuditRecords.FirstOrDefault(r => r.Reference == selected);
                AuditStatus = $"{page.Records.Count} records · page {_auditBack.Count + 1}" + (page.Next is null ? " · end" : " · more available");
                if (SelectedAuditTab == 3)
                {
                    var summary = await Task.Run(() => _auditReader.ReadSummaryAsync(file, cancel.Token));
                    if (generation == _auditGeneration && workspace.FilePath == file)
                    { AuditDetails = SupportDiagnostics.EnvironmentJson() + "\n" + JsonSerializer.Serialize(summary, s_auditJson); }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SeriesFileException or System.Data.Common.DbException or ArgumentException)
        { if (generation == _auditGeneration) { AuditRecords.Clear(); AuditStatus = ex is ArgumentException ? ex.Message : "Inspection failed. Check the series file and permissions, then refresh. Existing race data is unchanged."; } }
        finally
        {
            if (generation == _auditGeneration) { IsAuditBusy = false; }
            if (ReferenceEquals(_auditRead, cancel)) { _auditRead = null; }
        }
    }

    private static int? Number(string value) => string.IsNullOrWhiteSpace(value) ? null :
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number > 0 ? number : throw new ArgumentException("Run and bib must be positive whole numbers.");
    private static DateTimeOffset? Timestamp(string value) => string.IsNullOrWhiteSpace(value) ? null :
        (value.TrimEnd().EndsWith('Z') || value.Contains('+') || value.LastIndexOf('-') > 9)
        && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time) ? time : throw new ArgumentException("Enter a valid From / Until timestamp with its UTC offset.");

    [RelayCommand(AllowConcurrentExecutions = true)] private async Task LoadAuditDetailAsync(AuditReference? reference)
    {
        var file = _auditFile; var generation = _auditGeneration;
        var detailGeneration = ++_auditDetailGeneration;
        if (reference is null || file is null || workspace.FilePath != file) { return; }
        _auditDetailRead?.Cancel();
        using var cancel = new CancellationTokenSource(); _auditDetailRead = cancel;
        try
        {
            var detail = await Task.Run(() => _auditReader.ReadDetailAsync(file, reference, cancel.Token));
            if (generation != _auditGeneration || detailGeneration != _auditDetailGeneration || workspace.FilePath != file) { return; }
            AuditDetails = detail.Text; AuditLinks.Clear(); foreach (var link in detail.Links) { AuditLinks.Add(link); }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SeriesFileException or System.Data.Common.DbException)
        { if (generation == _auditGeneration && detailGeneration == _auditDetailGeneration && workspace.FilePath == file) { AuditDetails = "Record could not be read. Refresh and try again."; } }
        finally { if (ReferenceEquals(_auditDetailRead, cancel)) { _auditDetailRead = null; } }
    }

    [RelayCommand] private void ReviewDebugBundle()
    {
        _reviewedBundleFile = workspace.FilePath; _reviewedBundleId = _current?.Id; IncludeDebugSeries = false; IncludeDebugDescription = false;
        DebugBundleStatus = ""; SavedDebugBundlePath = null; IsDebugBundleReviewOpen = true;
        OnPropertyChanged(nameof(CanIncludeDebugSeries)); OnPropertyChanged(nameof(DebugBundlePreview));
    }
    [RelayCommand] private void CloseDebugBundleReview() { if (!IsDebugBundleBusy) { IsDebugBundleReviewOpen = false; } }
    [RelayCommand] private void CancelDebugBundle() => _bundleWrite?.Cancel();
    [RelayCommand] private async Task ShowLocalAuditLogAsync()
    {
        if (SelectedLocalAuditLog is not { } file) { return; }
        var generation = _auditGeneration;
        try
        {
            var content = await Task.Run(() => SupportDiagnostics.ReadLocalFileAsync(SupportDiagnostics.WorkerLogDirectory, file));
            if (generation == _auditGeneration && IsAuditDiagnostics) { AuditEnvironment = content; }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        { if (generation == _auditGeneration) { AuditEnvironment = "Original log unavailable. Refresh the log list and try again."; } }
    }
    [RelayCommand] private void OpenDebugBundleFolder()
    {
        if (SavedDebugBundlePath is null) { return; }
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Path.GetDirectoryName(SavedDebugBundlePath)!) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        { DebugBundleStatus = "The folder could not be opened. Use the saved path shown above."; }
    }
    [RelayCommand] private async Task SaveDebugBundleAsync(string? chosenPath)
    {
        if (IsDebugBundleBusy) { return; }
        if (!IsDebugBundleReviewOpen) { DebugBundleStatus = "Open the bundle preview before saving."; return; }
        if (_reviewedBundleFile != workspace.FilePath) { DebugBundleStatus = "The event series changed. Reopen the bundle preview."; return; }
        if (IncludeDebugSeries && !CanIncludeDebugSeries) { DebugBundleStatus = "Disconnect and drain all timing before including the complete series."; return; }
        if (DebugLogWindow is null) { DebugBundleStatus = "Choose a log window before saving."; return; }
        var until = (timeProvider ?? TimeProvider.System).GetUtcNow();
        var options = new DebugBundleOptions(_reviewedBundleFile, IncludeDebugSeries, until.AddHours(-DebugLogWindow.Hours),
            until, IncludeDebugDescription ? DebugIssueDescription : null, _reviewedBundleId,
            PrimaryTimingRoles.Concat(BackupTimingRoles).Select(r => new SupportTimingRole(r.Role.Kind, r.Role.Index, r.EffectiveSourceType, r.Channel, r.UsesLeaderConnection)).ToArray());
        using var cancel = new CancellationTokenSource(); _bundleWrite = cancel; IsDebugBundleBusy = true;
        SavedDebugBundlePath = null;
        try
        {
            var path = chosenPath ?? await dialogs.ChooseDebugBundleAsync();
            if (path is null) { DebugBundleStatus = "Export cancelled. No ZIP was created."; return; }
            DebugBundleStatus = "Creating ZIP…";
            _bundleExporter ??= debugBundleExporter ?? new(_auditReader);
            var result = await Task.Run(() => _bundleExporter.ExportAsync(path, options, cancel.Token));
            SavedDebugBundlePath = result.Path;
            DebugBundleStatus = $"{(result.Notices.Count > 0 ? "Saved with omissions" : "Saved")} · {result.Bytes:N0} bytes · {result.Path}. " + string.Join(" ", result.Notices);
        }
        catch (OperationCanceledException) { DebugBundleStatus = "Export cancelled. No completed bundle was published."; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SeriesFileException or System.Data.Common.DbException or ArgumentException or InvalidOperationException)
        { DebugBundleStatus = ex is DebugBundleCleanupException or SeriesFileException or InvalidOperationException or ArgumentException ? ex.Message : "Bundle export failed. Check destination permissions and free space, then retry."; }
        finally { IsDebugBundleBusy = false; _bundleWrite = null; }
    }

    partial void OnFileLabelChanged(string value)
    {
        _auditRead?.Cancel(); _auditDetailRead?.Cancel(); _auditGeneration++; _auditFile = null;
        IsAuditBusy = false;
        _auditCursor = null; _auditNext = null; _auditBack.Clear();
        AuditRecords.Clear(); AuditLinks.Clear(); AuditDetails = ""; NotifyAuditPaging();
    }
    private void DisposeAuditUi() { _auditRead?.Cancel(); _auditDetailRead?.Cancel(); _bundleWrite?.Cancel(); _auditGeneration++; }
}
