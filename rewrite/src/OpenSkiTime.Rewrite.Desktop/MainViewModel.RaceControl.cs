using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Collections;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenSkiTime.Rewrite.Domain;
using OpenSkiTime.Rewrite.Timing;

namespace OpenSkiTime.Rewrite.Desktop;

public sealed partial class MainViewModel
{
    private TimingSnapshot? _queueSnapshot;
    private int _queueCheckpoint;
    private DataGridCollectionView? _rankingView;
    public ObservableCollection<TimingGridRow> AtStartRows { get; } = [];
    public ObservableCollection<TimingGridRow> RunningRows { get; } = [];
    public ObservableCollection<TimingGridRow> RankingRows { get; } = [];
    public DataGridCollectionView RankingView => _rankingView ??= new(RankingRows);
    public bool HasTimingCategories => _desk?.Categories.Count > 0;
    public ObservableCollection<TimingGridRow> OnCourseRows { get; } = [];
    public ObservableCollection<TimingGridRow> FinishedTimingRows { get; } = [];
    public ObservableCollection<TimingGridRow> IntermediateTimingRows { get; } = [];
    public ObservableCollection<int> TimingCheckpoints { get; } = [];
    [ObservableProperty] private int _selectedTimingCheckpoint = 1;
    [ObservableProperty] private string _timingIntermediateChannels = "";
    [ObservableProperty] private bool _followTimingOrder = true;
    [ObservableProperty] private bool _showTimingCorrection;
    [ObservableProperty] private string _expectedFinishTime = "—";
    [ObservableProperty] private string _nextStartLabel = "No competitor waiting";
    [ObservableProperty] private string _expectedFinishLabel = "No competitor on course";
    [ObservableProperty] private string _expectedIntermediateLabel = "No competitor approaching";
    [ObservableProperty] private string _onCourseLabel = "ON COURSE · 0";
    [ObservableProperty] private string _finishListLabel = "FINISHED · 0";
    [ObservableProperty] private string _startHoldLabel = "Hold start";
    [ObservableProperty] private string _finishHoldLabel = "Hold finish";
    [ObservableProperty] private string _intermediateHoldLabel = "Hold intermediate";
    public bool HasTimingIntermediates => TimingCheckpoints.Count > 0;
    public string SelectedTimingIdentity => SelectedTimingRow?.Label ?? "Select a competitor";
    public string SelectedTimingProblem => SelectedTimingRow?.Result.Status == TimingStatus.Review ? SelectedTimingRow.Detail : "";
    public bool HasSelectedTimingProblem => SelectedTimingProblem.Length > 0;
    public bool CanReturnToStart => RaceFlow.CanReturnToStart(SelectedTimingRow?.Result);
    private ObservationReview? LastFinishObservation => workspace.Timing?.Snapshot?.Observations.LastOrDefault(x =>
        x.Observation.Channel == 1 && x.State is "Assigned" or "Unassigned");
    public bool CanIgnoreLastFinish => LastFinishObservation is not null;
    public bool HasUnassignedFinish => LastFinishObservation?.Bib is null && CanIgnoreLastFinish;
    public string LastFinishLabel => LastFinishObservation is { } finish
        ? finish.Bib is { } bib ? $"Last finish · Bib {bib} · {workspace.Timing?.Snapshot?.Results.FirstOrDefault(x => x.Bib == bib)?.Time}"
            : "Finish received · choose a competitor" : "No finish received";

    private void RefreshRunningTimes()
    {
        var timing = workspace.Timing;
        foreach (var row in TimingRows.Concat(OnCourseRows).Concat(IntermediateTimingRows).Concat(RunningRows))
        { row.Clock.Time = TimingTime.Format(row.Result.Status == TimingStatus.OnCourse ? timing?.RunningHundredths(row.Result.StartKey) : row.Result.Hundredths); }
        foreach (var row in AtStartRows) { row.Clock.Marker = row.Bib == timing?.ArmedStart ? "▶" : ""; }
        foreach (var row in RunningRows)
        {
            row.Clock.Marker = row.IsLatestFinish ? "◆" : row.Bib == timing?.ArmedFinish ? "▶" : "";
            if (row.Result.Status == TimingStatus.Finished) { row.Clock.Time = ""; }
        }
        ExpectedFinishTime = OnCourseRows.FirstOrDefault(x => x.Bib == timing?.ArmedFinish)?.Clock.Time ?? "—";
    }

    public void LoadTimingPreferences()
    {
        if (timingPreferencesStore?.Load() is not { } p) { return; }
        if (TimingSources.Contains(p.Source)) { TimingSource = p.Source; }
        TimingPort = p.Port; TimyDeviceId = p.UsbId; TimingBaud = p.Baud;
        TimingStartChannel = p.StartChannel; TimingFinishChannel = p.FinishChannel;
        TimingIntermediateChannels = p.IntermediateChannels; TimingFirmware = p.Firmware;
    }

    [RelayCommand] private async Task SaveTimingPreferencesAsync() => await GuardAsync(() =>
    {
        if (!CanChangeTimingDevice) { throw new DomainValidationException("Disconnect capture before changing settings."); }
        new Application.CaptureOptions(TimingSource, "Settings", DateOnly.FromDateTime(DateTime.Today), TimingStartChannel, TimingFinishChannel,
            StartDeviceId: IsAlgeResults ? Mt1StartDevice : null, FinishDeviceId: IsAlgeResults ? Mt1FinishDevice : null)
            { BaudRate = TimingBaud, IntermediateChannels = ReadIntermediateChannels() }.Validate();
        timingPreferencesStore?.Save(new(TimingSource, TimingPort, TimyDeviceId, TimingBaud,
            TimingStartChannel, TimingFinishChannel, TimingIntermediateChannels, TimingFirmware));
        SetStatus("Timing settings saved on this computer.");
        return Task.CompletedTask;
    });

    private int[] ReadIntermediateChannels()
    {
        if (string.IsNullOrWhiteSpace(TimingIntermediateChannels)) { return []; }
        var parts = TimingIntermediateChannels.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Any(x => !int.TryParse(x, NumberStyles.None, CultureInfo.InvariantCulture, out _)))
        { throw new DomainValidationException("Enter intermediate channels separated by commas, for example 2,3."); }
        return parts.Select(x => int.Parse(x, CultureInfo.InvariantCulture)).ToArray();
    }

    [RelayCommand] private void ReturnToTiming() { if (HasTimingRun) { SwitchSection(WorkspaceSection.Timing); } }
    [RelayCommand] private async Task ApplyFollowTimingOrderAsync()
    {
        if (workspace.Timing is { } timing) { await timing.FollowStartOrderAsync(FollowTimingOrder); RefreshTiming(); }
    }
    partial void OnSelectedTimingCheckpointChanged(int value) { if (value > 0) { RefreshTiming(); } }

    private void ConfigureTimingCheckpoints()
    {
        SelectedTimingCheckpoint = 0;
        TimingCheckpoints.Clear();
        for (var i = 1; i <= _timingList!.Plan.Competition.IntermediateCount; i++) { TimingCheckpoints.Add(i); }
        SelectedTimingCheckpoint = 1;
        _queueSnapshot = null;
        OnPropertyChanged(nameof(HasTimingIntermediates));
        OnPropertyChanged(nameof(HasTimingCategories));
    }

    private void RefreshRaceQueues()
    {
        var timing = workspace.Timing;
        var snapshot = timing?.Snapshot;
        if (snapshot is null || !HasTimingRun)
        { OnCourseRows.Clear(); FinishedTimingRows.Clear(); IntermediateTimingRows.Clear(); AtStartRows.Clear(); RunningRows.Clear(); RankingRows.Clear(); _queueSnapshot = null; return; }
        if (!ReferenceEquals(snapshot, _queueSnapshot) || _queueCheckpoint != SelectedTimingCheckpoint)
        {
            _queueSnapshot = snapshot; _queueCheckpoint = SelectedTimingCheckpoint;
            var currentRows = TimingEngine.Combined(snapshot, _previousTiming)
                .ToDictionary(x => x.Result.Bib, x => new TimingGridRow(x.Result, x.Total, x.Rank));
            TimingGridRow Map(TimingResult result) => currentRows[result.Bib];
            var onCourse = RaceFlow.OnCourse(snapshot).Select(Map).ToArray();
            SyncTimingRows(OnCourseRows, onCourse);
            SyncTimingRows(IntermediateTimingRows, HasTimingIntermediates
                ? RaceFlow.Expected(snapshot, SelectedTimingCheckpoint + 1).Select(Map).ToArray() : []);
            var finishOrder = snapshot.Observations.Select((x, i) => (x.Observation.Key, i)).ToDictionary(x => x.Key, x => x.i);
            SyncTimingRows(FinishedTimingRows, snapshot.Results.Where(x => x.FinishKey is not null || x.Status == TimingStatus.Finished)
                .OrderByDescending(x => x.FinishKey is null ? -1 : finishOrder.GetValueOrDefault(x.FinishKey)).Select(Map).ToArray());
            var latest = FinishedTimingRows.FirstOrDefault(x => x.Result.FinishKey is not null)?.Bib;
            TimingGridRow Present(TimingGridRow row) => row with
            {
                IsLatestFinish = row.Bib == latest,
                Category = HasTimingCategories ? CategoryResolver.Resolve(row.Result.Entry.Entrant.Athlete, _desk!.Categories) : ""
            };
            SyncTimingRows(AtStartRows, RaceFlow.Waiting(snapshot).Select(Map).ToArray());
            SyncTimingRows(RunningRows, onCourse.Concat(FinishedTimingRows.Take(3)).Select(Present).ToArray());
            var ranked = currentRows.Values.Where(x => x.Result.Status != TimingStatus.Ready && x.Result.Status != TimingStatus.OnCourse)
                .Where(x => x.Result.Status != TimingStatus.Review || x.Result.FinishKey is not null).Select(Present).ToArray();
            long? RankedTime(TimingGridRow row) => row.Result.Status == TimingStatus.Finished ? ShowTimingTotal ? row.Total : row.Result.Hundredths : null;
            var categoryOrder = _desk?.Categories.GroupBy(x => x.Values.Label).ToDictionary(x => x.Key, x => x.Min(r => r.Values.DisplayOrder)) ?? [];
            if (HasTimingCategories && RankingView.GroupDescriptions.Count == 0)
            { RankingView.GroupDescriptions.Add(new DataGridPathGroupDescription(nameof(TimingGridRow.Category))); }
            else if (!HasTimingCategories && RankingView.GroupDescriptions.Count > 0) { RankingView.GroupDescriptions.Clear(); }
            SyncTimingRows(RankingRows, ranked.OrderBy(x => categoryOrder.GetValueOrDefault(x.Category, int.MaxValue))
                .ThenBy(x => x.Category, StringComparer.Ordinal).ThenBy(x => RankedTime(x) ?? long.MaxValue).ThenBy(x => x.Position)
                .Select(x => x with { DisplayRank = RankedTime(x) is { } time
                    ? ranked.Count(y => y.Category == x.Category && RankedTime(y) is { } other && other < time) + 1 : null }).ToArray());
            OnCourseLabel = $"ON COURSE · {onCourse.Length}";
            FinishListLabel = $"FINISHED · {FinishedTimingRows.Count}";
            OnPropertyChanged(nameof(LastFinishLabel));
            OnPropertyChanged(nameof(CanIgnoreLastFinish));
            OnPropertyChanged(nameof(HasUnassignedFinish));
        }
        string Expected(int channel, string empty) => timing!.IsHeld(channel) ? "HOLD · impulses kept unassigned"
            : snapshot.Results.FirstOrDefault(x => x.Bib == timing.ExpectedBib(channel)) is { } row ? $"{row.Bib} · {row.Name}" : empty;
        NextStartLabel = Expected(0, "No starter selected");
        ExpectedFinishLabel = Expected(1, "No competitor expected");
        ExpectedIntermediateLabel = Expected(SelectedTimingCheckpoint + 1, "No competitor approaching");
        StartHoldLabel = timing!.IsHeld(0) ? "Resume start" : "Hold start";
        FinishHoldLabel = timing.IsHeld(1) ? "Resume finish" : "Hold finish";
        IntermediateHoldLabel = timing.IsHeld(SelectedTimingCheckpoint + 1) ? "Resume intermediate" : "Hold intermediate";
    }

    [RelayCommand] private async Task ExpectSelectedAsync(string position) => await GuardAsync(async () =>
    {
        if (SelectedTimingRow is not { } row || workspace.Timing is not { } timing) { return; }
        var channel = position == "start" ? 0 : position == "finish" ? 1 : SelectedTimingCheckpoint + 1;
        await timing.ExpectAsync(channel, row.Bib); RefreshTiming();
    });

    [RelayCommand] private async Task HoldTimingPositionAsync(string position) => await GuardAsync(async () =>
    {
        if (workspace.Timing is not { } timing) { return; }
        var channel = position == "start" ? 0 : position == "finish" ? 1 : SelectedTimingCheckpoint + 1;
        await timing.ExpectAsync(channel, null, !timing.IsHeld(channel)); RefreshTiming();
    });

    [RelayCommand] private async Task NextStartDnsAsync()
    {
        if (workspace.Timing?.ArmedStart is not { } bib) { return; }
        SelectedTimingRow = TimingRows.FirstOrDefault(x => x.Bib == bib);
        await ClassifyTimingAsync("DNS");
    }

    [RelayCommand(CanExecute = nameof(CanReturnToStart))]
    private async Task ReturnToStartAsync() => await GuardAsync(async () =>
    {
        if (SelectedTimingRow?.Result is not { StartKey: { } key } row || workspace.Timing is not { } timing) { return; }
        await timing.ReturnToStartAsync(row.Bib, key, TimingOperator);
        RefreshTiming();
        SetStatus($"Bib {row.Bib} returned to start. Original pulse retained."
            + (timing.IsHeld(0) ? " Start is still on hold." : " Ready for the next start impulse."));
    });

    [RelayCommand] private async Task IgnoreLastFinishAsync() => await GuardAsync(async () =>
    {
        if (workspace.Timing is not { } timing) { return; }
        var last = LastFinishObservation;
        if (last is null) { return; }
        await timing.CorrectAsync(new(DecisionKind.Assignment, last.Observation.Key, Ignored: true), TimingOperator,
            "False finish impulse — no competitor crossed the finish");
        RefreshTiming();
        SetStatus(last.Bib is { } bib ? $"False finish removed from Bib {bib}. Original pulse retained; competitor returns to the course queue."
            : "Unassigned false finish ignored. Original pulse retained; existing competitor results unchanged.");
    });

    [RelayCommand] private void CorrectCompetitorFinish()
    {
        ShowTimingCorrection = true; ShowAllTimingObservations = true;
        SelectedTimingObservation = TimingObservations.FirstOrDefault(x => x.Key == SelectedTimingRow?.Result.FinishKey);
        SelectedTimingHistory = TimingHistory.FirstOrDefault();
    }

    [RelayCommand] private void CorrectLastFinish()
    {
        ShowTimingCorrection = true; ShowAllTimingObservations = true;
        SelectedTimingObservation = TimingObservations.FirstOrDefault(x => x.Key == LastFinishObservation?.Observation.Key);
        if (LastFinishObservation?.Bib is { } bib) { SelectedTimingRow = TimingRows.FirstOrDefault(x => x.Bib == bib); }
    }

    [RelayCommand] private void CloseTimingCorrection() => ShowTimingCorrection = false;

    [RelayCommand] private async Task UndoLastTimingChangeAsync()
    {
        SelectedTimingHistory = TimingHistory.FirstOrDefault();
        await UndoTimingChangeAsync();
    }
}
