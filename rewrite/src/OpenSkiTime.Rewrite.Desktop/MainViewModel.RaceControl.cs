using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenSkiTime.Rewrite.Domain;
using OpenSkiTime.Rewrite.Timing;

namespace OpenSkiTime.Rewrite.Desktop;

public sealed partial class MainViewModel
{
    private TimingSnapshot? _queueSnapshot;
    private int _queueCheckpoint;
    public ObservableCollection<TimingGridRow> OnCourseRows { get; } = [];
    public ObservableCollection<TimingGridRow> FinishedTimingRows { get; } = [];
    public ObservableCollection<TimingGridRow> IntermediateTimingRows { get; } = [];
    public ObservableCollection<int> TimingCheckpoints { get; } = [];
    [ObservableProperty] private int _selectedTimingCheckpoint = 1;
    [ObservableProperty] private string _timingIntermediateChannels = "";
    [ObservableProperty] private bool _followTimingOrder = true;
    [ObservableProperty] private bool _showTimingReview;
    [ObservableProperty] private string _nextStartLabel = "No competitor waiting";
    [ObservableProperty] private string _expectedFinishLabel = "No competitor on course";
    [ObservableProperty] private string _expectedIntermediateLabel = "No competitor approaching";
    [ObservableProperty] private string _onCourseLabel = "ON COURSE · 0";
    [ObservableProperty] private string _finishListLabel = "FINISHED · 0";
    [ObservableProperty] private string _timingReviewLabel = "Impulses & corrections";
    [ObservableProperty] private string _startHoldLabel = "Hold start";
    [ObservableProperty] private string _finishHoldLabel = "Hold finish";
    [ObservableProperty] private string _intermediateHoldLabel = "Hold intermediate";
    public bool HasTimingIntermediates => TimingCheckpoints.Count > 0;
    public Avalonia.Controls.GridLength IntermediatePaneHeight => HasTimingIntermediates ? new(1.4, Avalonia.Controls.GridUnitType.Star) : new(0);
    public string SelectedTimingIdentity => SelectedTimingRow?.Label ?? "Select a competitor";
    private ObservationReview? LastFinishObservation => workspace.Timing?.Snapshot?.Observations.LastOrDefault(x =>
        x.Observation.Channel == 1 && x.State is "Assigned" or "Unassigned");
    public bool CanIgnoreLastFinish => LastFinishObservation is not null;
    public string LastFinishLabel => LastFinishObservation is { } finish
        ? $"Last finish: {(finish.Bib is { } bib ? "Bib " + bib : "unassigned")} · {TimingTime.FormatTimeOfDay(finish.Observation.DeviceTicks)}" : "No finish received";

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
        OnPropertyChanged(nameof(HasTimingIntermediates));
        OnPropertyChanged(nameof(IntermediatePaneHeight));
    }

    private void RefreshRaceQueues()
    {
        var timing = workspace.Timing;
        var snapshot = timing?.Snapshot;
        if (snapshot is null || !HasTimingRun)
        { OnCourseRows.Clear(); FinishedTimingRows.Clear(); IntermediateTimingRows.Clear(); _queueSnapshot = null; return; }
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
            OnCourseLabel = $"ON COURSE · {onCourse.Length}";
            FinishListLabel = $"FINISHED / REVIEW · {FinishedTimingRows.Count}";
            TimingReviewLabel = snapshot.Unresolved > 0 ? $"Impulses & corrections · {snapshot.Unresolved} to review" : "Impulses & corrections";
            OnPropertyChanged(nameof(LastFinishLabel));
            OnPropertyChanged(nameof(CanIgnoreLastFinish));
        }
        string Expected(int channel, string empty) => timing!.IsHeld(channel) ? "HOLD · impulses kept unassigned"
            : snapshot.Results.FirstOrDefault(x => x.Bib == timing.ExpectedBib(channel)) is { } row ? $"{row.Bib} · {row.Name}" : empty;
        NextStartLabel = Expected(0, "Choose a starter / connect to follow order");
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

    [RelayCommand] private void ReviewCompetitorFinish()
    {
        ShowTimingReview = true; ShowTimingHistory = false; ShowAllTimingObservations = true;
        SelectedTimingObservation = TimingObservations.FirstOrDefault(x => x.Key == SelectedTimingRow?.Result.FinishKey);
    }
}
