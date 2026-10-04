using System.Collections.ObjectModel;
using System.Globalization;
using System.ComponentModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenSkiTime.Application;
using OpenSkiTime.Devices;
using OpenSkiTime.Domain;
using OpenSkiTime.Timing;

namespace OpenSkiTime.Desktop;

public sealed record TimingGridRow(TimingResult Result, long? Total, int? TotalRank)
{
    public string PreviousRunTime { get; init; } = "";
    public string Category { get; init; } = "";
    public int? DisplayRank { get; init; }
    public bool IsLatestFinish { get; init; }
    public string FinishMarker => IsLatestFinish ? "◆" : "";
    public IReadOnlyList<string> Intermediates => Result.Splits.Select(x => x.Time).ToArray();
    public RunningTimeDisplay Clock { get; } = new();
    public int Position => Result.Entry.Position;
    public int Bib => Result.Bib;
    public string Code => Result.Entry.Entrant.Athlete.FederationCode ?? "";
    public string Name => Result.Name;
    public string Status => Result.Status switch { TimingStatus.OnCourse => "On course", TimingStatus.Review => "No time", _ => Result.Status.ToString() };
    public string Time => Result.Time;
    public string DisplayTime => Result.Status == TimingStatus.Finished ? Time : Status;
    public string TotalTime => TimingTime.Format(Total);
    public int? Rank => Result.Rank;
    public string Detail => Result.Detail;
    public string Label => $"{Bib} · {Name}";
    public string SplitTimes => string.Join("  ·  ", Result.Splits.Where(x => x.ObservationKey is not null).Select(x => $"I{x.Number} {x.Time}"));
    public bool HasSplits => Result.Splits.Any(x => x.ObservationKey is not null);
}

public sealed class RunningTimeDisplay : INotifyPropertyChanged
{
    private string _marker = "";
    public string Marker { get => _marker; set { if (_marker != value) { _marker = value; PropertyChanged?.Invoke(this, new(nameof(Marker))); } } }
    private string _time = "—";
    public string Time { get => _time; set { if (_time != value) { _time = value; PropertyChanged?.Invoke(this, new(nameof(Time))); } } }
    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed record TimingObservationRow(ObservationReview Review)
{
    public string Key => Review.Observation.Key;
    public string Time => TimingTime.FormatTimeOfDay(Review.Observation.DeviceTicks);
    public string Channel => Review.Observation.Channel switch { 0 => "Start", 1 => "Finish", >= 2 and <= 21 => "I" + (Review.Observation.Channel - 1), _ => "?" };
    public string Bib => Review.Bib?.ToString(CultureInfo.InvariantCulture) ?? "—";
    public string State => Review.State;
    public string Detail => Review.Observation.Message;
}

public sealed record TimingHistoryRow(long Id, string Time, string Summary, string Operator, string Reason);

public sealed partial class MainViewModel
{
    private readonly HttpClient _timingHttp = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(20) };
    private DispatcherTimer? _timingTimer;
    private TimingSnapshot? _shownTiming;
    private TimingSnapshot? _previousTiming;
    private StartListRevision? _timingList;
    private SimulatorTimingSource? _simulator;
    public ObservableCollection<DrawMenuCompetition> TimingMenu { get; } = [];
    public ObservableCollection<TimingGridRow> TimingRows { get; } = [];
    public ObservableCollection<TimingObservationRow> TimingObservations { get; } = [];
    public ObservableCollection<TimingHistoryRow> TimingHistory { get; } = [];
    public ObservableCollection<string> TimingPorts { get; } = [];

    [ObservableProperty] private CompetitionDetails? _timingCompetition;
    [ObservableProperty] private int _timingRun = 1;
    [ObservableProperty] private bool _isTimingBusy;
    [ObservableProperty] private bool _isTimingConnected;
    [ObservableProperty] private bool _showAllTimingObservations;
    [ObservableProperty] private bool _showTimingHistory;
    [ObservableProperty] private string _timingDeviceDate = DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    [ObservableProperty] private string _timingOperator = Environment.UserName;
    [ObservableProperty] private string _timingConnection = "Disconnected";
    [ObservableProperty] private string _timingHoldSummary = "";
    [ObservableProperty] private string _timingAlarm = "";
    [ObservableProperty] private string _timingSummary = "Choose a saved start list.";
    [ObservableProperty] private string _startBibText = "";
    [ObservableProperty] private string _finishBibText = "";
    [ObservableProperty] private string _armedBibs = "Start —  /  Finish —";
    [ObservableProperty] private string _observationBibText = "";
    [ObservableProperty] private string _timingReason = "";
    [ObservableProperty] private string _correctedTimeText = "";
    [ObservableProperty] private bool _showTimingClassificationEditor;
    [ObservableProperty] private string _timingClassification = "DSQ";
    [ObservableProperty] private string _timingDsqGate = "";
    [ObservableProperty] private string _timingDsqReason = "";
    [ObservableProperty] private string _timingDsqJudge = "";
    private int? _classificationEditorBib;
    public IReadOnlyList<string> TimingClassifications { get; } = ["DSQ", "DNF", "DNS", "NPS", "Clear"];
    public bool IsTimingDsq => TimingClassification == "DSQ";
    partial void OnTimingClassificationChanged(string value) => OnPropertyChanged(nameof(IsTimingDsq));
    partial void OnShowTimingClassificationEditorChanged(bool value)
    { if (value) { LoadTimingClassificationEditor(SelectedTimingRow); } }

    private void LoadTimingClassificationEditor(TimingGridRow? row)
    {
        _classificationEditorBib = row?.Bib;
        TimingClassification = row?.Result.Status is TimingStatus.DSQ or TimingStatus.DNF or TimingStatus.DNS or TimingStatus.NPS
            ? row.Result.Status.ToString() : "DSQ";
        TimingDsqGate = row?.Result.Disqualification?.Gate?.ToString(CultureInfo.InvariantCulture) ?? "";
        TimingDsqReason = row?.Result.Disqualification?.Reason ?? "";
        TimingDsqJudge = row?.Result.Disqualification?.Judge ?? "";
    }
    [ObservableProperty] private string _simulationTime = "12:00:00.0000";
    [ObservableProperty] private TimingGridRow? _selectedTimingRow;
    public IReadOnlyList<int> SelectedTimingBibs { get; private set; } = [];
    public void SelectTimingBibs(IReadOnlyList<int> bibs, int? activeBib)
    {
        ArgumentNullException.ThrowIfNull(bibs);
        SelectedTimingBibs = bibs.Distinct().ToArray();
        OnPropertyChanged(nameof(SelectedTimingBibs));
        SelectedTimingRow = TimingRows.FirstOrDefault(x => x.Bib == activeBib)
            ?? TimingRows.FirstOrDefault(x => SelectedTimingBibs.Count > 0 && x.Bib == SelectedTimingBibs[0]);
        OnPropertyChanged(nameof(SelectedTimingIdentity));
    }
    [ObservableProperty] private TimingObservationRow? _selectedTimingObservation;
    [ObservableProperty] private TimingHistoryRow? _selectedTimingHistory;
    public bool HasTimingAlarm => TimingAlarm.Length > 0;
    public bool CanConnectTiming => _timingList is not null && !IsTimingConnected && !IsTimingBusy;
    public bool CanChangeTimingDevice => !IsTimingConnected && !IsTimingBusy;
    public bool HasTimingRun => _timingList is not null;
    public bool ShowTimingTotal => TimingRun > 1;
    public bool CanPrepareNextTimedRun => _shownTiming?.Complete == true && TimingRun == 1 && TimingCompetition?.Values.RunCount == 2;
    public string TimingContext => TimingCompetition is { } c
        ? $"{c.Values.ShortLabel}  /  Run {TimingRun} of {c.Values.RunCount}{CompetitionCodexLabel(c.Values)}"
        : "Timing · choose a competition and run";
    public string TimingCaptureLabel => IsTimingConnected ? TimingContext + " · " + TimingConnection : "";
    partial void OnIsTimingConnectedChanged(bool value) => NotifyTiming();
    partial void OnIsTimingBusyChanged(bool value) => NotifyTiming();
    partial void OnTimingAlarmChanged(string value) => OnPropertyChanged(nameof(HasTimingAlarm));
    partial void OnTimingCompetitionChanged(CompetitionDetails? value) => NotifyTiming();
    partial void OnTimingRunChanged(int value) => NotifyTiming();
    partial void OnShowAllTimingObservationsChanged(bool value) { _shownTiming = null; RefreshTiming(); }
    partial void OnSelectedTimingRowChanged(TimingGridRow? value)
    {
        if (value is not null && !SelectedTimingBibs.Contains(value.Bib))
        {
            SelectedTimingBibs = [value.Bib];
            OnPropertyChanged(nameof(SelectedTimingBibs));
        }
        OnPropertyChanged(nameof(SelectedTimingIdentity));
        OnPropertyChanged(nameof(SelectedTimingProblem));
        OnPropertyChanged(nameof(HasSelectedTimingProblem));
        ReturnToStartCommand.NotifyCanExecuteChanged();
        if (value is null) { return; }
        if (!ShowTimingClassificationEditor || value.Bib != _classificationEditorBib)
        { LoadTimingClassificationEditor(value); }
        ObservationBibText = value.Bib.ToString(CultureInfo.InvariantCulture);
        CorrectedTimeText = value.Result.Hundredths is null ? "" : value.Time;
    }
    partial void OnSelectedTimingObservationChanged(TimingObservationRow? value)
    { if (value?.Review.Bib is { } bib) { ObservationBibText = bib.ToString(CultureInfo.InvariantCulture); } }

    private void NotifyTiming()
    {
        foreach (var name in new[] { nameof(IsTimingSimulator), nameof(CanRetryBackupClock), nameof(CanConnectTiming), nameof(CanChangeTimingDevice), nameof(HasTimingRun), nameof(TimingContext),
            nameof(TimingCaptureLabel), nameof(TimingDeviceHelp), nameof(WindowTitle), nameof(CanPrepareNextTimedRun), nameof(ShowTimingTotal) }) { OnPropertyChanged(name); }
    }

    [RelayCommand]
    private async Task RefreshTimingMenuAsync()
    {
        TimingMenu.Clear();
        await GuardAsync(async () =>
        {
            foreach (var c in Competitions)
            {
                var lists = await workspace.ReadStartListsAsync(c.Id);
                var runs = lists.Revisions.Select(x => x.Plan.RunNumber).Distinct().Order().ToArray();
                if (runs.Length > 0) { TimingMenu.Add(new(c, runs)); }
            }
        });
    }

    [RelayCommand]
    private async Task OpenTimingRunAsync(DrawDestination destination)
    {
        await GuardAsync(async () =>
        {
            EnsureDeskClean();
            var timing = workspace.Timing ?? throw new SeriesFileException("Timing is unavailable in this workspace.");
            var entering = !IsTimingSection;
            SwitchSection(WorkspaceSection.Timing);
            if (!IsTimingSection) { return; }
            if (entering && timing.IsActive && timing.ListId is not null && _timingList is not null)
            { await HoldTimingChannelsAsync(timing); }
            var lists = await workspace.ReadStartListsAsync(destination.Competition.Id);
            var list = lists.Revisions.Where(x => x.Plan.RunNumber == destination.Run).MaxBy(x => x.Revision)
                ?? throw new DomainValidationException("Draw and save this run's start list first.");
            var changingRun = timing.ListId != list.Id;
            var currentCompetition = Competitions.Single(x => x.Id == destination.Competition.Id);
            if (changingRun || !timing.IsActive) { await timing.SelectRunAsync(list.Id); }
            else if (_timingList?.Plan.Competition.IntermediateCount != currentCompetition.Values.IntermediateCount)
            { await timing.RefreshIntermediateCountAsync(); }
            if (changingRun && workspace.Auxiliary is { } auxiliary
                && auxiliary.State(AuxiliaryTimingRole.B) is { IsActive: true, Live: true })
            { QueueLiveBackupRunSwitch(auxiliary, list.Id); }
            if (changingRun)
            {
                ShowTimingClassificationEditor = false;
                LoadTimingClassificationEditor(null);
                TimingReason = "";
            }
            TimingCompetition = currentCompetition;
            TimingRun = destination.Run;
            SetActiveRace(TimingCompetition, destination.Run, WorkspaceSection.Timing);
            _timingList = list with { Plan = list.Plan with { Competition = list.Plan.Competition with
                { IntermediateCount = TimingCompetition.Values.IntermediateCount } } };
            if (!timing.IsActive)
            {
                SelectedTimingRow = null; StartBibText = FinishBibText = "";
                if (timing.LastCaptureGroup is { Count: > 0 } last && timingPreferencesStore?.Load() is null)
                {
                    // No saved preferences: rebuild the primary roles from this run's last capture session.
                    // The replay path is not part of the saved session; keep the one already entered.
                    var replayPath = TimingStartRole.ReplayPath;
                    try { ApplyTimingConfiguration(TimingRoleConfiguration.FromCaptureOptions([.. last]), primary: true, backup: false); }
                    catch (DomainValidationException) { /* Unknown saved source: keep the current editors. */ }
                    if (TimingStartRole.ReplayPath.Length == 0) { TimingStartRole.ReplayPath = replayPath; }
                }
            }
            _previousTiming = null;
            if (list.Plan.SourceListId is { } previousId)
            {
                var previous = await workspace.ReadTimingAsync(previousId);
                _previousTiming = TimingReplay.Restore(previous, new AlgeDecoderFactory());
                if (previous.Sessions.Count == 0)
                {
                    // M4/external Run 1 inputs remain the authoritative snapshot behind this start list.
                    _previousTiming = _previousTiming with { Results = _previousTiming.Results.Select(row =>
                    {
                        var input = list.Plan.SourceResults.Single(x => x.CompetitorId == row.CompetitorId);
                        return row with { Status = Enum.Parse<TimingStatus>(input.Status.ToString()), Hundredths = input.Hundredths,
                            Detail = "External Run 1 result saved with starting order" };
                    }).ToArray() };
                }
            }
            TimingDeviceDate = (timing.LastCaptureOptions?.DeviceDate ?? TimingCompetition.Values.Date).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            await timing.FollowStartOrderAsync(FollowTimingOrder);
            _shownTiming = null;
            ConfigureTimingCheckpoints();
            if (entering || changingRun)
            { await HoldTimingChannelsAsync(timing); }
            RefreshTiming();
            RefreshTimingPorts();
            _timingTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Background, (_, _) => RefreshTiming());
            _timingTimer.Start();
            NotifyTiming();
            SetStatus("Timing changes save automatically. Original device data and correction history are retained.");
            if (!timing.IsActive && timingPreferencesStore?.Load() is not null)
            { await ConnectTimingAsync(); }
        });
    }

    [RelayCommand]
    private void RefreshTimingPorts()
    {
        TimingPorts.Clear();
        foreach (var port in SerialTimingSource.PortNames()) { TimingPorts.Add(port); }
    }

    [RelayCommand]
    private async Task SetupTimyUsbAsync()
    {
        IsTimingBusy = true;
        try
        {
            await TimyUsbSource.InstallSdkAsync(_timingHttp);
            SetStatus("Timy USB library installed locally. Install the ALGE USB driver as described in docs/timing.md, then connect.");
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or UnauthorizedAccessException or PlatformNotSupportedException)
        { SetStatus("Timy setup failed: " + ex.Message, true); }
        finally { IsTimingBusy = false; }
    }

    [RelayCommand]
    private async Task ConnectTimingAsync()
    {
        if (!CanConnectTiming || workspace.Timing is not { } timing) { return; }
        IsTimingBusy = true;
        await GuardAsync(async () =>
        {
            EnsureDeskClean();
            if (!DateOnly.TryParseExact(TimingDeviceDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            { throw new DomainValidationException("Device date must use YYYY-MM-DD."); }
            // Primary timing is validated on its own: an incomplete or invalid B Clock never prevents A from connecting.
            var primary = new TimingRoleConfiguration(PrimaryTimingRoles.Select(x => x.ToAssignment()).ToArray());
            primary.Validate();
            var since = AlgeReceiveFrom(timing);
            var capture = primary.PrimaryCapture(date, since);
            foreach (var device in capture) { ValidateAuthoritativeTimingEndpoint(device.Options); }
            var inputs = new List<TimingSourceInput>();
            try
            {
                foreach (var device in capture)
                { inputs.Add(new(TimingSourceFactory.Create(device.Connection, device.Options, _timingHttp, AlgePassword, () => _simulator = new()), device.Options)); }
                await timing.FollowStartOrderAsync(FollowTimingOrder);
                await HoldTimingChannelsAsync(timing);
                await timing.StartAsync(inputs, TimingOperator);
            }
            catch
            {
                // Sources created before a failure are not owned by capture; close them so no device stays open.
                if (!timing.IsActive) { foreach (var input in inputs) { await input.Source.DisposeAsync(); } }
                throw;
            }
            ConfigureTimingCheckpoints();
            if (_current is not null) { _current = await workspace.ReadAsync(); }
            RefreshTiming();
            var today = DateOnly.FromDateTime(DateTime.Today);
            SetStatus("Timing connected in HOLD. Check the race, then resume each timing position before assigning impulses."
                + (date == today ? "" : $" Device date {date:yyyy-MM-dd} is not today ({today:yyyy-MM-dd}): Timy/MT1 times use the device date, and ALGE Results impulses from another day are shown for review only. Change Device date in Settings if this is wrong."));
            // B Clock follows only after A is capturing. It runs detached and reports through B Clock status only.
            StartBackupClock(date, since);
        });
        IsTimingBusy = false;
    }

    // ALGE Results history is read from when this run's capture first started (so triggers during an interruption are
    // recovered; duplicates are recognized), otherwise from now. This is an absolute instant, not a clock setting.
    private static DateTimeOffset AlgeReceiveFrom(TimingWorkspace timing) =>
        timing.LastCaptureGroup.Select(x => x.FromUtc).OfType<DateTimeOffset>().DefaultIfEmpty(DateTimeOffset.UtcNow).Min();

    [RelayCommand]
    private async Task DisconnectTimingAsync()
    {
        IsTimingBusy = true;
        await GuardAsync(async () => { if (workspace.Timing is { } timing) { await timing.StopAsync(); } RefreshTiming(); });
        if (workspace.Timing?.IsActive != true) { StopBackupClock(); ForgetAlgeSessionPasswords(); }
        IsTimingBusy = false;
    }

    public async Task<bool> StopTimingForCloseAsync()
    {
        if (!await FlushTimingReportAsync()) { return false; }
        await EndBackupClockForCloseAsync();
        if (workspace.Auxiliary is { IsActive: true } auxiliary)
        {
            var stopped = false;
            await GuardAsync(async () => { await auxiliary.StopAllAsync(); stopped = true; });
            if (!stopped) { return false; }
        }
        if (workspace.Timing?.IsActive != true) { return true; }
        await DisconnectTimingAsync();
        return workspace.Timing?.IsActive != true;
    }

    [RelayCommand] private void RetryTimingStorage() { workspace.Timing?.RetryStorage(); RefreshTiming(); }
    [RelayCommand]
    private async Task ArmStartAsync()
    {
        await GuardAsync(async () =>
        {
            if (workspace.Timing is not { } timing) { return; }
            var text = string.IsNullOrWhiteSpace(StartBibText) ? SelectedTimingRow?.Bib.ToString(CultureInfo.InvariantCulture) ?? "" : StartBibText;
            await timing.ArmAsync(ReadBib(text), timing.ArmedFinish); StartBibText = ""; RefreshTiming();
        });
    }
    [RelayCommand]
    private async Task ArmFinishAsync()
    {
        await GuardAsync(async () =>
        {
            if (workspace.Timing is not { } timing) { return; }
            var text = string.IsNullOrWhiteSpace(FinishBibText) ? SelectedTimingRow?.Bib.ToString(CultureInfo.InvariantCulture) ?? "" : FinishBibText;
            await timing.ArmAsync(timing.ArmedStart, ReadBib(text)); FinishBibText = ""; RefreshTiming();
        });
    }
    [RelayCommand] private async Task ClearArmedBibsAsync()
    { if (workspace.Timing is { } timing) { await timing.ArmAsync(null, null); RefreshTiming(); } }

    [RelayCommand]
    private async Task SimulatePulseAsync(string channel)
    {
        await GuardAsync(async () =>
        {
            var options = workspace.Timing?.ActiveCaptureOptions.FirstOrDefault(x => x.Device == TimingSourceTypes.SimulatorLabel);
            if (_simulator is null || !IsTimingConnected || options is null)
            { throw new DomainValidationException("Connect the simulator first."); }
            if (!TimingTime.TryTimeOfDay(SimulationTime, out var ticks, out _)) { throw new DomainValidationException("Enter simulator time as HH:mm:ss with 1–7 decimal places (for example 12:00:00.1234567)."); }
            int position;
            if (channel == "start") { position = 0; }
            else if (channel == "finish") { position = 1; }
            else if (channel.StartsWith("intermediate:", StringComparison.Ordinal)
                && int.TryParse(channel.AsSpan("intermediate:".Length), out var checkpoint) && checkpoint >= 1 && checkpoint <= TimingCheckpoints.Count)
            { position = checkpoint + 1; }
            else { throw new DomainValidationException("Choose a valid simulator timing position."); }
            // Pulses use the physical channel the connected simulator session maps to this role.
            var physical = options.Channel(position)
                ?? throw new DomainValidationException("This timing role is not assigned to the simulator in Settings.");
            await _simulator.PulseAsync(physical, ticks);
        });
    }

    [RelayCommand]
    private async Task ChangeObservationAsync(string action)
    {
        await GuardAsync(async () =>
        {
            if (SelectedTimingObservation is not { } selected || workspace.Timing is not { } timing) { return; }
            var bib = action == "assign" ? ReadBib(ObservationBibText) : null;
            var reason = TimingReason.Trim();
            if (reason.Length == 0 && selected.State == "Unassigned" && action == "assign") { reason = "Bib identified by operator"; }
            await timing.CorrectAsync(new(DecisionKind.Assignment, selected.Key, Bib: bib, Ignored: action == "ignore"), TimingOperator, reason);
            RefreshTiming();
        });
    }

    [RelayCommand]
    private async Task ClassifyTimingAsync(string status)
    {
        await GuardAsync(async () =>
        {
            var bibs = SelectedTimingBibs.Count > 0 ? SelectedTimingBibs
                : SelectedTimingRow is { } row ? [row.Bib] : [];
            if (bibs.Count > 0) { await ApplyTimingStatusAsync(bibs, status); }
        });
    }

    private async Task ApplyTimingStatusAsync(IReadOnlyList<int> bibs, string status)
    {
        if (workspace.Timing is not { } timing) { return; }
        TimingStatus? classification = status switch
        {
            "DNS" => TimingStatus.DNS, "DNF" => TimingStatus.DNF,
            "DSQ" => TimingStatus.DSQ, "NPS" => TimingStatus.NPS,
            "Clear" => null,
            _ => throw new DomainValidationException("Choose DNS, DNF, DSQ, NPS or clear status.")
        };
        var reason = string.IsNullOrWhiteSpace(TimingReason)
            ? classification is null ? "Operator cleared classification" : "Operator marked " + status
            : TimingReason;
        await timing.CorrectStatusesAsync(bibs, classification, TimingOperator, reason);
        RefreshTiming();
    }

    [RelayCommand]
    private async Task SaveTimingClassificationAsync()
    {
        await GuardAsync(async () =>
        {
            if (SelectedTimingRow is not { } row || workspace.Timing is not { } timing)
            { throw new DomainValidationException("Select a competitor in this run."); }
            TimingStatus? status = TimingClassification == "Clear" ? null
                : TimingClassifications.Contains(TimingClassification) ? Enum.Parse<TimingStatus>(TimingClassification)
                : throw new DomainValidationException("Choose DSQ, DNF, DNS, NPS or Clear.");
            DisqualificationDetails? dsq = null;
            if (status == TimingStatus.DSQ)
            {
                int? gate = null;
                if (!string.IsNullOrWhiteSpace(TimingDsqGate))
                {
                    if (!int.TryParse(TimingDsqGate.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
                    { throw new DomainValidationException("Enter a whole gate number or leave it empty."); }
                    gate = parsed;
                }
                dsq = new(gate, TimingDsqReason.Trim(), TimingDsqJudge.Trim());
                dsq.Validate();
            }
            var reason = string.IsNullOrWhiteSpace(TimingReason)
                ? status is null ? "Operator cleared classification" : "Operator reviewed " + status
                : TimingReason.Trim();
            await timing.CorrectStatusesAsync([row.Bib], status, TimingOperator, reason, disqualification: dsq);
            RefreshTiming();
            SetStatus($"Bib {row.Bib}: classification saved with correction history.");
        });
    }

    [RelayCommand]
    private async Task CorrectTimingTimeAsync()
    {
        await GuardAsync(async () =>
        {
            if (SelectedTimingRow is not { } row || workspace.Timing is not { } timing) { return; }
            long? time = string.IsNullOrWhiteSpace(CorrectedTimeText) ? null : RunResultInput.ParseTime(CorrectedTimeText);
            await timing.CorrectAsync(new(DecisionKind.Time, CompetitorId: row.Result.CompetitorId, Hundredths: time), TimingOperator, TimingReason);
            RefreshTiming();
        });
    }

    [RelayCommand]
    private async Task UndoTimingChangeAsync()
    {
        await GuardAsync(async () =>
        {
            if (SelectedTimingHistory is not { } change || workspace.Timing is not { } timing) { return; }
            await timing.UndoAsync(change.Id, TimingOperator, string.IsNullOrWhiteSpace(TimingReason) ? "Undo selected change" : TimingReason);
            RefreshTiming();
        });
    }

    [RelayCommand]
    private async Task PrepareNextTimedRunAsync()
    {
        if (CanPrepareNextTimedRun && TimingCompetition is { } c) { await OpenDrawRunAsync(new(c, 2)); }
    }

    private static int? ReadBib(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) { return null; }
        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var bib) || bib is < 1 or > 99999)
        { throw new DomainValidationException("Enter a valid bib number."); }
        return bib;
    }

    internal bool IsRefreshingTimingUi { get; private set; }
    public void RefreshTiming()
    {
        var refreshing = IsRefreshingTimingUi;
        IsRefreshingTimingUi = true;
        try { RefreshTimingCore(); }
        finally { RefreshRunningTimes(); RefreshTimestamps(); RefreshBackupMonitor(); RefreshTimingRoleSignals(); IsRefreshingTimingUi = refreshing; if (!refreshing) { OnPropertyChanged(nameof(IsRefreshingTimingUi)); } }
    }

    private void RefreshTimingCore()
    {
        var timing = workspace.Timing;
        IsTimingConnected = timing?.IsActive == true;
        TimingConnection = timing?.Connection ?? "Disconnected";
        TimingDeviceClock = timing?.IsActive == true && timing.LiveDeviceTicks is { } ticks
            ? TimingTime.FormatTimeOfDay(ticks)[..8] : "--:--:--";
        StartInputOn = timing?.IsHeld(0) != true;
        FinishInputOn = timing?.IsHeld(1) != true;
        var heldCount = timing is { IsActive: true }
            ? Enumerable.Range(0, TimingCheckpoints.Count + 2).Count(timing.IsHeld) : 0;
        TimingHoldSummary = heldCount == 0 ? "" : heldCount == TimingCheckpoints.Count + 2
            ? "HOLD · all positions" : $"HOLD · {heldCount} position(s)";
        TimingAlarm = timing?.Fault ?? (timing?.Pending >= 512 ? "CAPTURE BACKLOG: input is waiting for storage. Do not close this file; check the local disk." : "");
        ArmedBibs = $"Start {timing?.ArmedStart?.ToString(CultureInfo.InvariantCulture) ?? "—"}  /  Finish {timing?.ArmedFinish?.ToString(CultureInfo.InvariantCulture) ?? "—"}";
        OnPropertyChanged(nameof(TimingCaptureLabel));
        RefreshRaceQueues();
        var snapshot = timing?.Snapshot;
        if (snapshot is null || ReferenceEquals(snapshot, _shownTiming)) { return; }
        _shownTiming = snapshot;
        var selectedBib = SelectedTimingRow?.Bib;
        var selectedObservation = SelectedTimingObservation?.Key;
        var selectedHistory = SelectedTimingHistory?.Id;
        SyncTimingRows(TimingRows, TimingEngine.Combined(snapshot, _previousTiming)
            .Select(row => new TimingGridRow(row.Result, row.Total, row.Rank)).ToArray());
        var editingTime = CorrectedTimeText;
        var editingBib = ObservationBibText;
        SelectedTimingRow = TimingRows.FirstOrDefault(x => x.Bib == selectedBib) ?? TimingRows.FirstOrDefault();
        if (SelectedTimingRow?.Bib == selectedBib) { CorrectedTimeText = editingTime; ObservationBibText = editingBib; }
        TimingObservations.Clear();
        foreach (var observation in snapshot.Observations.Where(x => ShowAllTimingObservations || x.State is "Unassigned" or "Review").Reverse().Take(500))
        { TimingObservations.Add(new(observation)); }
        SelectedTimingObservation = TimingObservations.FirstOrDefault(x => x.Key == selectedObservation);
        TimingHistory.Clear();
        foreach (var audit in snapshot.Audit.Reverse().Take(500))
        {
            var bib = audit.After.Bib ?? audit.Before.Bib;
            var athlete = snapshot.Results.FirstOrDefault(x => x.CompetitorId == audit.After.CompetitorId || x.Bib == bib);
            var identity = athlete is null ? "Observation" : $"{athlete.Entry.Entrant.Athlete.FederationCode} {athlete.Name} · Bib {athlete.Bib}";
            TimingHistory.Add(new(audit.Id, audit.At.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture),
                $"{identity} · {audit.After.Kind}: {Describe(audit.Before)} → {Describe(audit.After)}", audit.Operator, audit.Reason));
        }
        SelectedTimingHistory = TimingHistory.FirstOrDefault(x => x.Id == selectedHistory);
        var prefix = timing!.IsSimulation ? "TRAINING / REPLAY · " : "";
        TimingSummary = $"{prefix}{snapshot.Results.Count} starters · {snapshot.Results.Count(x => x.Status == TimingStatus.OnCourse)} on course · "
            + $"{snapshot.Results.Count(x => x.Status == TimingStatus.Finished)} finished"
            + (snapshot.Complete ? " · Run complete" : "");
        NotifyTiming();
        RefreshRaceQueues();
    }

    private static string Describe(TimingDecision value) => value.Kind switch
    {
        DecisionKind.Assignment => value.Ignored ? "Ignored" : value.Bib?.ToString(CultureInfo.InvariantCulture) ?? "Unassigned",
        DecisionKind.Status => value.Status is null ? "Use recorded times" : value.Status
            + (value.Disqualification is { } dsq ? string.Concat(
                dsq.Gate is { } gate ? $" · Gate {gate}" : "",
                dsq.Reason.Length > 0 ? " · " + dsq.Reason : "",
                dsq.Judge.Length > 0 ? " · Judge " + dsq.Judge : "") : ""),
        DecisionKind.StartOrder => value.StartOrder ?? "Draw order",
        _ => value.Hundredths is null ? "Use recorded times" : TimingTime.Format(value.Hundredths)
    };

    private static void SyncTimingRows(ObservableCollection<TimingGridRow> target, TimingGridRow[] rows)
    {
        // Replace only changed rows. Clearing the entire grid on every impulse loses scroll/focus state.
        while (target.Count > rows.Length) { target.RemoveAt(target.Count - 1); }
        for (var i = 0; i < rows.Length; i++)
        {
            if (i == target.Count) { target.Add(rows[i]); }
            else if (target[i].Total != rows[i].Total || target[i].TotalRank != rows[i].TotalRank
                || target[i].Category != rows[i].Category || target[i].DisplayRank != rows[i].DisplayRank || target[i].IsLatestFinish != rows[i].IsLatestFinish
                || !target[i].Result.Splits.SequenceEqual(rows[i].Result.Splits)
                || target[i].Result != (rows[i].Result with { Splits = target[i].Result.Splits }))
            { target[i] = rows[i]; }
        }
    }

    private void ResetTimingUi()
    {
        ResetBackupMonitor(); ResetBackupClock();
        ResetReportUi();
        _timingDragWorkspace = Guid.NewGuid(); _timestampSnapshot = null; TimestampRows.Clear();
        _timingTimer?.Stop(); _timingList = null; _shownTiming = null; _previousTiming = null;
        TimingCompetition = null; TimingRows.Clear(); TimingObservations.Clear(); TimingHistory.Clear();
        TimingCheckpoints.Clear(); ShowTimingCorrection = false;
        SelectedTimingRow = null; SelectedTimingObservation = null; SelectedTimingHistory = null;
        SelectedTimingBibs = [];
        ShowTimingClassificationEditor = false; LoadTimingClassificationEditor(null);
        StartBibText = FinishBibText = TimingReason = CorrectedTimeText = "";
        IsTimingConnected = false; RefreshRaceQueues(); NotifyTiming();
    }

    private void DisposeTimingUi() { ResetBackupMonitor(); DisposeLiveTiming(); _timingTimer?.Stop(); _timingHttp.Dispose(); }
}
