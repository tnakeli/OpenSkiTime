using System.Collections.ObjectModel;
using System.Globalization;
using System.ComponentModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenSkiTime.Rewrite.Application;
using OpenSkiTime.Rewrite.Devices;
using OpenSkiTime.Rewrite.Domain;
using OpenSkiTime.Rewrite.Timing;

namespace OpenSkiTime.Rewrite.Desktop;

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
    private WindowsCredentialStore AlgeCredential => new("OpenSkiTime.ALGE.Results.Password:" + Mt1Username.Trim());
    public ObservableCollection<DrawMenuCompetition> TimingMenu { get; } = [];
    public ObservableCollection<TimingGridRow> TimingRows { get; } = [];
    public ObservableCollection<TimingObservationRow> TimingObservations { get; } = [];
    public ObservableCollection<TimingHistoryRow> TimingHistory { get; } = [];
    public ObservableCollection<string> TimingPorts { get; } = [];
    public IReadOnlyList<string> TimingSources { get; } = ["Timy 2/3 · USB", "MT1 · USB / serial", "MT1 · ALGE Results", "Simulator", "Replay file"];

    [ObservableProperty] private CompetitionDetails? _timingCompetition;
    [ObservableProperty] private int _timingRun = 1;
    [ObservableProperty] private bool _isTimingBusy;
    [ObservableProperty] private bool _isTimingConnected;
    [ObservableProperty] private bool _showAllTimingObservations;
    [ObservableProperty] private bool _showTimingHistory;
    [ObservableProperty] private string _timingSource = "Timy 2/3 · USB";
    [ObservableProperty] private string _timingPort = "";
    [ObservableProperty] private string _timyDeviceId = "";
    [ObservableProperty] private int _timingBaud = 38400;
    [ObservableProperty] private int _timingStartChannel;
    [ObservableProperty] private int _timingFinishChannel = 1;
    [ObservableProperty] private string _timingDeviceDate = DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    [ObservableProperty] private string _timingFirmware = "Not queried";
    [ObservableProperty] private string _mt1StartDevice = "";
    [ObservableProperty] private string _mt1FinishDevice = "";
    [ObservableProperty] private string _mt1Username = "";
    [ObservableProperty] private string _mt1Password = "";
    [ObservableProperty] private string _mt1FromUtc = "";
    [ObservableProperty] private bool _rememberAlgePassword;
    [ObservableProperty] private string _timingReplayPath = "";
    [ObservableProperty] private string _timingOperator = Environment.UserName;
    [ObservableProperty] private string _timingConnection = "Disconnected";
    [ObservableProperty] private string _timingAlarm = "";
    [ObservableProperty] private string _timingSummary = "Choose a saved start list.";
    [ObservableProperty] private string _startBibText = "";
    [ObservableProperty] private string _finishBibText = "";
    [ObservableProperty] private string _armedBibs = "Start —  /  Finish —";
    [ObservableProperty] private string _observationBibText = "";
    [ObservableProperty] private string _timingReason = "";
    [ObservableProperty] private string _correctedTimeText = "";
    [ObservableProperty] private string _simulationTime = "12:00:00.0000";
    [ObservableProperty] private TimingGridRow? _selectedTimingRow;
    [ObservableProperty] private TimingObservationRow? _selectedTimingObservation;
    [ObservableProperty] private TimingHistoryRow? _selectedTimingHistory;
    public bool IsTimyUsb => TimingSource == TimingSources[0];
    public bool IsMt1Serial => TimingSource == TimingSources[1];
    public bool IsAlgeResults => TimingSource == TimingSources[2];
    public bool IsTimingSimulator => TimingSource == TimingSources[3];
    public bool IsTimingReplay => TimingSource == TimingSources[4];
    public bool HasTimingAlarm => TimingAlarm.Length > 0;
    public bool CanConnectTiming => _timingList is not null && !IsTimingConnected && !IsTimingBusy;
    public bool CanChangeTimingDevice => !IsTimingConnected && !IsTimingBusy;
    public bool HasTimingRun => _timingList is not null;
    public bool ShowTimingTotal => TimingRun > 1;
    public bool CanPrepareNextTimedRun => _shownTiming?.Complete == true && TimingRun == 1 && TimingCompetition?.Values.RunCount == 2;
    public string TimingContext => TimingCompetition is { } c
        ? $"{c.Values.ShortLabel}  /  Run {TimingRun} of {c.Values.RunCount}  ·  Codex {c.Values.FisCode ?? c.Values.LocalRaceCode ?? "—"}"
        : "Timing · choose a competition and run";
    public string TimingNavigationLabel => IsTimingSection && TimingCompetition is { } c ? $"5  Timing · {c.Values.ShortLabel} / {TimingRun}  ▾" : "5  Timing  ▾";
    public string TimingCaptureLabel => IsTimingConnected ? TimingContext + " · " + TimingConnection : "";
    public string TimingDeviceHelp => IsTimyUsb ? "PC Timer mode · install the ALGE USB driver once. Native USB uses the vendor library."
        : IsMt1Serial ? "Choose the MT1 virtual COM port. Start and finish must use different channels on this device."
        : IsAlgeResults ? "Timekeeper account required. Device IDs may be the same. Receive-from uses UTC; empty means connect time."
        : "Training data only. Use a separate test event file; it cannot be mixed with real timing in one run.";

    partial void OnTimingSourceChanged(string value) => NotifyTiming();
    partial void OnIsTimingConnectedChanged(bool value) => NotifyTiming();
    partial void OnIsTimingBusyChanged(bool value) => NotifyTiming();
    partial void OnTimingAlarmChanged(string value) => OnPropertyChanged(nameof(HasTimingAlarm));
    partial void OnTimingCompetitionChanged(CompetitionDetails? value) => NotifyTiming();
    partial void OnTimingRunChanged(int value) => NotifyTiming();
    partial void OnShowAllTimingObservationsChanged(bool value) { _shownTiming = null; RefreshTiming(); }
    partial void OnSelectedTimingRowChanged(TimingGridRow? value)
    {
        OnPropertyChanged(nameof(SelectedTimingIdentity));
        OnPropertyChanged(nameof(SelectedTimingProblem));
        OnPropertyChanged(nameof(HasSelectedTimingProblem));
        ReturnToStartCommand.NotifyCanExecuteChanged();
        if (value is null) { return; }
        ObservationBibText = value.Bib.ToString(CultureInfo.InvariantCulture);
        CorrectedTimeText = value.Result.Hundredths is null ? "" : value.Time;
    }
    partial void OnSelectedTimingObservationChanged(TimingObservationRow? value)
    { if (value?.Review.Bib is { } bib) { ObservationBibText = bib.ToString(CultureInfo.InvariantCulture); } }

    private void NotifyTiming()
    {
        foreach (var name in new[] { nameof(IsTimyUsb), nameof(IsMt1Serial), nameof(IsAlgeResults), nameof(IsTimingSimulator), nameof(IsTimingReplay),
            nameof(CanConnectTiming), nameof(CanChangeTimingDevice), nameof(HasTimingRun), nameof(TimingContext), nameof(TimingNavigationLabel),
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
            SwitchSection(WorkspaceSection.Timing);
            if (!IsTimingSection) { return; }
            var lists = await workspace.ReadStartListsAsync(destination.Competition.Id);
            var list = lists.Revisions.Where(x => x.Plan.RunNumber == destination.Run).MaxBy(x => x.Revision)
                ?? throw new DomainValidationException("Draw and save this run's start list first.");
            if (timing.ListId != list.Id) { await timing.SelectRunAsync(list.Id); }
            TimingCompetition = Competitions.Single(x => x.Id == destination.Competition.Id);
            TimingRun = destination.Run;
            _timingList = list;
            if (!timing.IsActive)
            {
                SelectedTimingRow = null; StartBibText = FinishBibText = "";
                if (timing.LastCaptureOptions is { } last && timingPreferencesStore?.Load() is null)
                {
                    if (TimingSources.Contains(last.Device)) { TimingSource = last.Device; }
                    TimingStartChannel = last.StartChannel; TimingFinishChannel = last.FinishChannel;
                    TimingFirmware = last.Firmware;
                    TimingBaud = last.BaudRate;
                    TimingIntermediateChannels = string.Join(",", last.IntermediateChannels);
                    if (IsTimyUsb && last.Endpoint.StartsWith("Timy USB ", StringComparison.Ordinal)) { TimyDeviceId = last.Endpoint[9..].Trim(); }
                    if (IsMt1Serial) { TimingPort = last.Endpoint; }
                    Mt1StartDevice = last.StartDeviceId ?? ""; Mt1FinishDevice = last.FinishDeviceId ?? "";
                    Mt1FromUtc = last.FromUtc?.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? "";
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
            DateTimeOffset? since = null;
            if (IsAlgeResults)
            {
                if (string.IsNullOrWhiteSpace(Mt1FromUtc)) { since = DateTimeOffset.UtcNow; }
                else if (DateTimeOffset.TryParse(Mt1FromUtc, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)) { since = parsed; }
                else { throw new DomainValidationException("Enter a valid receive-from date/time in UTC, e.g. 2026-09-27 10:00:00."); }
            }
            var endpoint = IsTimyUsb ? "Timy USB " + TimyDeviceId.Trim() : IsMt1Serial ? TimingPort.Trim()
                : IsAlgeResults ? $"{Mt1StartDevice.Trim()}/{TimingStartChannel};{Mt1FinishDevice.Trim()}/{TimingFinishChannel}"
                : IsTimingSimulator ? "Simulator" : "Replay";
            var options = new CaptureOptions(TimingSource, endpoint, date, TimingStartChannel, TimingFinishChannel,
                IsTimingSimulator || IsTimingReplay, TimingFirmware, Mt1StartDevice.Trim(), Mt1FinishDevice.Trim(), since)
                { BaudRate = TimingBaud, IntermediateChannels = ReadIntermediateChannels() };
            options.Validate();
            if (IsAlgeResults && options.IntermediateChannels.Length > 0)
            { throw new DomainValidationException("ALGE Results supports start/finish only. Use USB/serial for intermediate capture."); }
            ITimingSource source;
            if (IsTimyUsb) { source = new TimyUsbSource(TimyDeviceId.Trim()); }
            else if (IsMt1Serial) { source = new SerialTimingSource(TimingPort.Trim(), TimingBaud); }
            else if (IsAlgeResults)
            {
                var password = Mt1Password;
                if (password.Length == 0 && OperatingSystem.IsWindows()) { password = AlgeCredential.Read() ?? ""; }
                if (Mt1Username.Length == 0 || password.Length == 0) { throw new DomainValidationException("Enter your ALGE Results username and password."); }
                if (OperatingSystem.IsWindows())
                {
                    if (RememberAlgePassword) { AlgeCredential.Save(password); }
                    else { AlgeCredential.Remove(); }
                }
                source = new AlgeResultsSource(_timingHttp, Mt1Username.Trim(), password, options);
                Mt1Password = "";
            }
            else if (IsTimingSimulator) { _simulator = new(); source = _simulator; }
            else
            {
                if (!File.Exists(TimingReplayPath)) { throw new DomainValidationException("Enter the path to an ALGE ASCII capture file."); }
                source = new ReplayFileTimingSource(TimingReplayPath);
            }
            await timing.FollowStartOrderAsync(FollowTimingOrder);
            await timing.StartAsync(source, options, TimingOperator);
            ConfigureTimingCheckpoints();
            if (_current is not null) { _current = await workspace.ReadAsync(); }
            RefreshTiming();
            SetStatus("Timing connected. Check Next start and Expected finish. Race queues advance with saved impulses.");
        });
        IsTimingBusy = false;
    }

    [RelayCommand]
    private async Task DisconnectTimingAsync()
    {
        IsTimingBusy = true;
        await GuardAsync(async () => { if (workspace.Timing is { } timing) { await timing.StopAsync(); } RefreshTiming(); });
        IsTimingBusy = false;
    }

    public async Task<bool> StopTimingForCloseAsync()
    {
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
            if (_simulator is null || !IsTimingConnected) { throw new DomainValidationException("Connect the simulator first."); }
            if (!TimingTime.TryTimeOfDay(SimulationTime, out var ticks, out _)) { throw new DomainValidationException("Enter simulator time as HH:mm:ss with 1–7 decimal places (for example 12:00:00.1234567)."); }
            int IntermediateChannel(string? position)
            {
                if (position is null || !position.StartsWith("intermediate:", StringComparison.Ordinal)
                    || !int.TryParse(position.AsSpan("intermediate:".Length), out var checkpoint)
                    || checkpoint < 1 || checkpoint > TimingCheckpoints.Count)
                { throw new DomainValidationException("Choose a valid simulator timing position."); }
                var configured = ReadIntermediateChannels();
                if (checkpoint > configured.Length)
                { throw new DomainValidationException($"Configure the channel for intermediate {checkpoint} in Settings."); }
                return configured[checkpoint - 1];
            }
            var physicalChannel = channel switch
            { "start" => TimingStartChannel, "finish" => TimingFinishChannel, _ => IntermediateChannel(channel) };
            await _simulator.PulseAsync(physicalChannel, ticks);
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
            if (SelectedTimingRow is not { } row || workspace.Timing is not { } timing) { return; }
            TimingStatus? classification = status == "Clear" ? null : Enum.Parse<TimingStatus>(status);
            var reason = string.IsNullOrWhiteSpace(TimingReason) ? (classification is null ? "Operator cleared classification" : "Operator marked " + status) : TimingReason;
            await timing.CorrectAsync(new(DecisionKind.Status, CompetitorId: row.Result.CompetitorId, Status: classification), TimingOperator, reason);
            RefreshTiming();
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
        finally { RefreshRunningTimes(); RefreshTimestamps(); IsRefreshingTimingUi = refreshing; if (!refreshing) { OnPropertyChanged(nameof(IsRefreshingTimingUi)); } }
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
        DecisionKind.Status => value.Status?.ToString() ?? "Use recorded times",
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
        _timingDragWorkspace = Guid.NewGuid(); _timestampSnapshot = null; TimestampRows.Clear();
        _timingTimer?.Stop(); _timingList = null; _shownTiming = null; _previousTiming = null;
        TimingCompetition = null; TimingRows.Clear(); TimingObservations.Clear(); TimingHistory.Clear();
        TimingCheckpoints.Clear(); ShowTimingCorrection = false;
        SelectedTimingRow = null; SelectedTimingObservation = null; SelectedTimingHistory = null;
        StartBibText = FinishBibText = TimingReason = CorrectedTimeText = "";
        IsTimingConnected = false; RefreshRaceQueues(); NotifyTiming();
    }

    private void DisposeTimingUi() { _timingTimer?.Stop(); _timingHttp.Dispose(); }
}
