using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenSkiTime.Application;
using OpenSkiTime.Devices;
using OpenSkiTime.Domain;
using OpenSkiTime.Timing;

namespace OpenSkiTime.Desktop;

/// <summary>
/// One reference competitor in the EET table of the Edit result dialog. The operator confirms or types the backup time
/// and may leave a competitor out with a reason; the view model recalculates on every change.
/// </summary>
public sealed partial class EetEntryRow : ObservableObject
{
    public int Bib { get; init; }
    public string Surname { get; init; } = "";
    public string FirstName { get; init; } = "";
    public bool IsTarget { get; init; }
    // The usable System A time of day; empty when this competitor cannot be a reference (Note says why).
    public bool HasA { get; init; }
    public string ATime { get; init; } = "";
    // The captured backup paired with A, as shown when loaded, and its journal key.
    public string PairedText { get; init; } = "";
    public string? PairedKey { get; init; }
    public string Name => Surname + " " + FirstName;
    public bool CanEdit => HasA && !IsTarget;
    [ObservableProperty] private string _backupText = "";
    [ObservableProperty] private bool _excluded;
    [ObservableProperty] private string _excludeReason = "";
    [ObservableProperty] private bool _used;
    [ObservableProperty] private string _difference = "";
    [ObservableProperty] private string _note = "";
    public string UsedMark => IsTarget ? "EET" : Used ? "✓" : "";
    internal Action? Changed { get; set; }
    partial void OnBackupTextChanged(string value) => Changed?.Invoke();
    partial void OnExcludedChanged(bool value) => Changed?.Invoke();
    partial void OnExcludeReasonChanged(string value) => Changed?.Invoke();
    partial void OnUsedChanged(bool value) => OnPropertyChanged(nameof(UsedMark));
}

// The Edit result dialog beyond classification: a start/finish time of day typed from the device record or a hand/B
// reading (Time of day tab), and the FIS EET calculation for a missing System A time (EET tab, Timing Booklet
// section 14). Both save one audited batch that also assigns the time to the competitor.
public sealed partial class MainViewModel
{
    public const string DeviceRecordSourceLabel = "A device tape / memory";
    public const string HandSourceLabel = "Hand / B (not EET)";
    public const string DefaultDeviceRecordReason = "System A impulse missing: entered from the device record";
    public IReadOnlyList<string> TimeEntryPositions { get; } = ["Start", "Finish"];
    public IReadOnlyList<string> TimeEntrySources { get; } = [DeviceRecordSourceLabel, HandSourceLabel];
    public IReadOnlyList<string> EetSources { get; } = ["System B", "Photo finish", "Hand time"];
    // The reasons the timing report offers for a missed System A time.
    public IReadOnlyList<string> EetReplacementReasons { get; } = ["Batteries", "Snow obscuration", "Wire break", "Photocell alignment", "Other"];

    // 0 Status, 1 Time of day, 2 EET.
    [ObservableProperty] private int _timingResultTab;
    [ObservableProperty] private string _timeEntryPosition = "Finish";
    [ObservableProperty] private string _timeEntrySource = DeviceRecordSourceLabel;
    [ObservableProperty] private string _timeEntryText = "";
    [ObservableProperty] private string _timeEntryReference = "";
    [ObservableProperty] private string _timeEntryReason = DefaultDeviceRecordReason;
    [ObservableProperty] private string _eetPosition = "Finish";
    [ObservableProperty] private string _eetSource = "System B";
    [ObservableProperty] private string _eetBackupText = "";
    [ObservableProperty] private string _eetReplacementReason = "";
    [ObservableProperty] private string _eetMessage = "";
    [ObservableProperty] private string _eetCandidates = "";
    [ObservableProperty] private string _eetPairCount = "";
    [ObservableProperty] private string _eetSum = "—";
    [ObservableProperty] private string _eetAverage = "—";
    [ObservableProperty] private string _eetCorrection = "—";
    [ObservableProperty] private string _eetResult = "—";
    [ObservableProperty] private string _eetNetTime = "—";
    [ObservableProperty] private string _eetCorrectionLabel = "Correction";
    public ObservableCollection<EetEntryRow> EetRows { get; } = [];
    private EetPreparationResult? _eetPreparation;
    private EetCalculation? _eetCalculation;
    private int _eetLoad;
    private (int Bib, int Channel, string Source)? _eetLoaded;
    // The competitor's impulses at the EET position when the table was loaded; the EET replaces exactly these.
    private IReadOnlyList<string> _eetReplacing = [];

    public bool IsStatusTab => TimingResultTab == 0;
    public bool IsTimeEntryTab => TimingResultTab == 1;
    public bool IsEetTab => TimingResultTab == 2;
    public bool HasEetMessage => EetMessage.Length > 0;
    public bool HasEetCandidates => EetCandidates.Length > 0;
    // The timing report needs the reason System A missed the time, so an EET is saved only with one.
    public bool CanSaveEet => _eetCalculation is { ReplacementReason.Length: > 0 };
    private int TimeEntryChannel => TimeEntryPosition == "Start" ? 0 : 1;
    private int EetChannel => EetPosition == "Start" ? 0 : 1;
    private ManualTimeSource TimeEntryKind => TimeEntrySource == HandSourceLabel ? ManualTimeSource.Hand : ManualTimeSource.DeviceRecord;
    private EetBackupSource EetKind => EetSource switch
    {
        "Photo finish" => EetBackupSource.PhotoFinish,
        "Hand time" => EetBackupSource.HandTime,
        _ => EetBackupSource.SystemB
    };

    // The competitor's current start and finish time of day with their source, as the Time of day tab shows them.
    public string TimeEntryStart => CurrentTimeOfDay(0);
    public string TimeEntryFinish => CurrentTimeOfDay(1);
    public string TimeEntryPreview => PreviewNetTime(TimeEntryChannel, TimeEntryText, TimeEntryKind != ManualTimeSource.DeviceRecord);

    partial void OnTimeEntryTextChanged(string value) => OnPropertyChanged(nameof(TimeEntryPreview));
    partial void OnTimeEntryPositionChanged(string value) => OnPropertyChanged(nameof(TimeEntryPreview));
    partial void OnTimeEntrySourceChanged(string value)
    {
        // A default reason follows the source; a typed one is kept.
        if (TimeEntryReason is DefaultDeviceRecordReason or DefaultManualTimestampReason || string.IsNullOrWhiteSpace(TimeEntryReason))
        { TimeEntryReason = value == HandSourceLabel ? DefaultManualTimestampReason : DefaultDeviceRecordReason; }
        OnPropertyChanged(nameof(TimeEntryPreview));
    }
    partial void OnTimingResultTabChanged(int value)
    {
        OnPropertyChanged(nameof(IsStatusTab)); OnPropertyChanged(nameof(IsTimeEntryTab)); OnPropertyChanged(nameof(IsEetTab));
        // Back on the EET tab, the table of the same competitor, position and source keeps what the operator typed.
        if (value == 2 && (_eetLoaded is not { } loaded || loaded != (TimingClassificationTarget?.Bib ?? -1, EetChannel, EetSource)))
        { _ = LoadEetAsync(); }
    }
    partial void OnEetPositionChanged(string value) => _ = LoadEetAsync();
    partial void OnEetSourceChanged(string value) => _ = LoadEetAsync();
    partial void OnEetBackupTextChanged(string value) => RecalculateEet();
    partial void OnEetReplacementReasonChanged(string value) => RecalculateEet();
    partial void OnEetMessageChanged(string value) => OnPropertyChanged(nameof(HasEetMessage));
    partial void OnEetCandidatesChanged(string value) => OnPropertyChanged(nameof(HasEetCandidates));

    // The dialog's competitor changed (or timing refreshed): update the time views; the EET table is rebuilt only for
    // another competitor, so a refresh never discards typed backup times.
    private void OnResultEditorTargetChanged()
    {
        OnPropertyChanged(nameof(TimeEntryStart));
        OnPropertyChanged(nameof(TimeEntryFinish));
        OnPropertyChanged(nameof(TimeEntryPreview));
        if (ShowTimingClassificationEditor && TimingResultTab == 2 && TimingClassificationTarget?.Bib != _eetLoaded?.Bib)
        { _ = LoadEetAsync(); }
        // The same competitor's other time may have changed: the kept table recalculates its net time.
        else if (_eetLoaded?.Bib == TimingClassificationTarget?.Bib) { RecalculateEet(); }
    }

    private TimingObservation? TargetObservation(int channel)
    {
        if (TimingClassificationTarget is not { } row || workspace.Timing?.Snapshot is not { } snapshot) { return null; }
        var key = channel == 0 ? row.Result.StartKey : row.Result.FinishKey;
        return key is null ? null : snapshot.Observations.FirstOrDefault(x => x.Observation.Key == key)?.Observation;
    }

    private string CurrentTimeOfDay(int channel)
    {
        if (TimingClassificationTarget is null) { return "—"; }
        if (TargetObservation(channel) is not { DeviceTicks: { } ticks } observation) { return "missing"; }
        var source = observation.EntrySource switch
        {
            ManualTimeSource.DeviceRecord => "A · device record",
            ManualTimeSource.Hand => "hand time",
            ManualTimeSource.Eet => "EET",
            _ => observation.Manual ? "device keyboard" : "A · device"
        };
        return $"{TimingTime.FormatTimeOfDay(ticks, observation.Precision)} · {source}";
    }

    // The net time the typed time of day would give with the competitor's other time, by the same rules as results.
    private string PreviewNetTime(int channel, string text, bool manual)
    {
        if (!TimingTime.TryTimeOfDay(text.Trim(), out var timeOfDay, out var precision)) { return ""; }
        if (TargetObservation(1 - channel) is not { DeviceTicks: { } otherTicks } other)
        { return channel == 1 ? "No start time" : "No finish yet"; }
        var typed = other with { Key = "preview", DeviceTicks = TimingTime.PlaceOnClock(otherTicks, timeOfDay), Precision = precision,
            Manual = manual, ManualEntry = true, Channel = channel };
        var elapsed = channel == 1 ? TimingEngine.Elapsed(other, typed, finish: true) : TimingEngine.Elapsed(typed, other, finish: true);
        return elapsed.Problem switch
        {
            ElapsedProblem.None => "Net " + TimingTime.Format(elapsed.Hundredths),
            ElapsedProblem.NotAfterStart => "Invalid: finish is not after start",
            ElapsedProblem.OverTwoHours => "Invalid: over two hours",
            ElapsedProblem.LowPrecision => "Invalid: too few decimals",
            _ => "Invalid: different device clock"
        };
    }

    [RelayCommand]
    private async Task SaveTimeEntryAsync()
    {
        await GuardAsync(async () =>
        {
            if (TimingClassificationTarget is not { IsForerunner: false } row || workspace.Timing is not { } timing || timing.ListId is not { } listId)
            { throw new DomainValidationException("Choose a competitor of this run."); }
            // The submitted values: the operator may start the next entry while this one is being saved.
            var (position, text, reference) = (TimeEntryPosition, TimeEntryText, TimeEntryReference);
            await timing.EnterCompetitorTimeAsync(listId,
                new(row.Bib, TimeEntryChannel, TimeEntryKind, text, reference,
                    Replacing: _shownTiming is { } shown ? CompetitorTimeEntry.AssignedKeys(shown, row.Bib, TimeEntryChannel) : []),
                TimingOperator, TimeEntryReason);
            RefreshTiming();
            OnResultEditorTargetChanged();
            SetStatus($"Bib {row.Bib}: {position.ToLowerInvariant()} {text.Trim()} saved with correction history.");
            if (TimeEntryText == text && TimeEntryReference == reference && TimingClassificationTarget?.Bib == row.Bib)
            { TimeEntryText = ""; TimeEntryReference = ""; }
        });
    }

    private async Task LoadEetAsync()
    {
        var version = ++_eetLoad;
        _eetPreparation = null; _eetCalculation = null; _eetLoaded = null;
        foreach (var row in EetRows) { row.Changed = null; }
        EetRows.Clear(); EetCandidates = ""; EetBackupText = "";
        // Backup timing is read only while the EET tab is open.
        if (!ShowTimingClassificationEditor || TimingResultTab != 2) { RecalculateEet(); return; }
        if (TimingClassificationTarget is not { IsForerunner: false } target || workspace.Timing?.Snapshot is not { } snapshot)
        { EetMessage = "Choose a competitor of this run."; RecalculateEet(); return; }
        var channel = EetChannel;
        var source = EetKind;
        try
        {
            IReadOnlyList<TimingObservation> backup = [];
            if (source != EetBackupSource.PhotoFinish && workspace.Auxiliary is { } auxiliary)
            {
                var role = source == EetBackupSource.SystemB ? AuxiliaryTimingRole.B
                    : channel == 0 ? AuxiliaryTimingRole.HandStart : AuxiliaryTimingRole.HandFinish;
                var data = await auxiliary.ReadAsync(snapshot.ListId);
                var decoded = await Task.Run(() => data.Decode(new AlgeDecoderFactory()));
                backup = decoded.Where(x => x.Role == role).Select(x => x.Observation).ToArray();
            }
            if (version != _eetLoad) { return; }
            var preparation = EetPreparation.Prepare(snapshot, target.Bib, channel, backup, new BackupComparisonPolicy().PairingToleranceTicks);
            var names = TimingRows.ToDictionary(x => x.Bib);
            var rows = preparation.StartOrder.Select(x =>
            {
                var paired = x.Backup is { DeviceTicks: { } ticks } b ? TimingTime.FormatTimeOfDay(ticks, b.Precision) : "";
                var row = new EetEntryRow
                {
                    Bib = x.Bib, Surname = names.GetValueOrDefault(x.Bib)?.Surname ?? "", FirstName = names.GetValueOrDefault(x.Bib)?.FirstName ?? "",
                    HasA = x.A is not null, ATime = x.A is { DeviceTicks: { } a } ? TimingTime.FormatTimeOfDay(a, x.A.Precision) : "",
                    PairedText = paired, PairedKey = x.Backup?.Key, BackupText = paired,
                    Note = x.Unavailable ?? (x.Backup is null && x.BackupState != "No backup captured" ? x.BackupState : "")
                };
                return row;
            }).ToList();
            var current = TargetObservation(channel);
            rows.Insert(Math.Clamp(preparation.TargetIndex, 0, rows.Count), new EetEntryRow
            {
                Bib = target.Bib, Surname = target.Surname, FirstName = target.FirstName, IsTarget = true,
                ATime = current is { DeviceTicks: { } ticks } ? TimingTime.FormatTimeOfDay(ticks, current.Precision) : "missing",
                Note = "EET target"
            });
            _eetPreparation = preparation;
            _eetReplacing = CompetitorTimeEntry.AssignedKeys(snapshot, target.Bib, channel);
            _eetLoaded = (target.Bib, channel, EetSource);
            foreach (var row in rows) { row.Changed = RecalculateEet; EetRows.Add(row); }
            var proposed = preparation.ProposedTarget;
            EetCandidates = string.Join(" ", new[]
            {
                preparation.BackupNote ?? "",
                proposed is null && preparation.TargetCandidates.Count > 0
                    ? "Unpaired backup times: " + string.Join(", ", preparation.TargetCandidates.Take(6)
                        .Select(x => TimingTime.FormatTimeOfDay(x.DeviceTicks!.Value, x.Precision)))
                    : ""
            }.Where(x => x.Length > 0));
            EetBackupText = proposed is { DeviceTicks: { } proposedTicks } ? TimingTime.FormatTimeOfDay(proposedTicks, proposed.Precision) : "";
            RecalculateEet();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Started without awaiting: report every failure in the tab instead of losing it.
            if (version == _eetLoad) { EetMessage = ex is DomainValidationException ? ex.Message : "Backup timing could not be read: " + ex.Message; }
        }
    }

    private async Task ReloadEetKeepingInputAsync()
    {
        var typed = EetRows.Where(x => x.CanEdit).ToDictionary(x => x.Bib, x => (x.BackupText, x.PairedText, x.Excluded, x.ExcludeReason));
        var backupText = EetBackupText;
        await LoadEetAsync();
        foreach (var row in EetRows.Where(x => x.CanEdit))
        {
            if (!typed.TryGetValue(row.Bib, out var before)) { continue; }
            // A typed value is kept; a value that was only captured follows the new capture.
            if (before.BackupText != before.PairedText) { row.BackupText = before.BackupText; }
            row.ExcludeReason = before.ExcludeReason;
            row.Excluded = before.Excluded;
        }
        if (backupText.Length > 0) { EetBackupText = backupText; }
        RecalculateEet();
    }

    // Recalculates the selection and the EET from the table; never writes anything.
    private void RecalculateEet()
    {
        _eetCalculation = null;
        EetSum = EetAverage = EetCorrection = EetResult = EetNetTime = "—";
        EetCorrectionLabel = "Correction";
        try
        {
            if (_eetPreparation is not { } preparation) { EetPairCount = ""; return; }
            var values = new Dictionary<int, (long TimeOfDay, int Precision, string? Key)>();
            var problems = new List<string>();
            // A left-out reference is not used, so its backup text need not be valid.
            foreach (var row in EetRows.Where(x => x.CanEdit && !x.Excluded))
            {
                var text = row.BackupText.Trim();
                // A captured backup time is removed only by leaving the reference out with a reason.
                if (text.Length == 0 && row.PairedText.Length > 0)
                { problems.Add($"Bib {row.Bib}: to remove its backup time, leave it out with a reason."); continue; }
                if (text.Length == 0) { continue; }
                if (TimingTime.TryTimeOfDay(text, out var ticks, out var precision) && precision >= 2)
                { values[row.Bib] = (ticks, precision, text == row.PairedText ? row.PairedKey : null); }
                else { problems.Add($"Bib {row.Bib}: enter the backup time as HH:mm:ss.ff."); }
            }
            var excluded = EetRows.Where(x => x.CanEdit && x.Excluded).Select(x => x.Bib).ToHashSet();
            EetSelection SelectAt(EetPreparationResult place) => EetCalculator.Select(EetPreparation.Candidates(place, values, excluded), preparation.Bib);
            var selection = SelectAt(preparation);
            // A missing start takes its place in the actual start order from its time on the System A scale: the backup
            // start minus the mean difference of the references, repeated until the place is stable.
            if (preparation.Channel == 0 && TimingTime.TryTimeOfDay(EetBackupText.Trim(), out var typedBackup, out var typedPrecision) && typedPrecision >= 2)
            {
                for (var i = 0; i < 4 && selection.Pairs.Count > 0; i++)
                {
                    var estimate = typedBackup - selection.Pairs.Sum(x => x.DifferenceTicks) / selection.Pairs.Count;
                    var next = SelectAt(EetPreparation.Placed(preparation, (estimate % TimeSpan.TicksPerDay + TimeSpan.TicksPerDay) % TimeSpan.TicksPerDay));
                    if (next.Pairs.Select(x => x.Bib).SequenceEqual(selection.Pairs.Select(x => x.Bib))) { break; }
                    selection = next;
                }
            }
            var used = selection.Pairs.ToDictionary(x => x.Bib);
            foreach (var row in EetRows.Where(x => !x.IsTarget))
            {
                row.Used = used.ContainsKey(row.Bib);
                var a = preparation.StartOrder.FirstOrDefault(x => x.Bib == row.Bib)?.A;
                row.Difference = a?.DeviceTicks is { } aTicks && values.TryGetValue(row.Bib, out var value)
                    ? TimingTime.FormatSignedSeconds(EetCalculator.TimeOfDayDifference(value.TimeOfDay, aTicks), Math.Max(a.Precision, value.Precision)) : "";
            }
            EetPairCount = $"{selection.Pairs.Count} / {EetCalculator.RequiredPairs}";
            if (problems.Count > 0) { EetMessage = problems[0]; return; }
            if (EetRows.FirstOrDefault(x => x.CanEdit && x.Excluded && string.IsNullOrWhiteSpace(x.ExcludeReason)) is { } missing)
            { EetMessage = $"Bib {missing.Bib}: give the reason it is left out."; return; }
            if (selection.Pairs.Count == 0) { EetMessage = "No competitor has both a System A and a backup time at this position."; return; }
            if (!TimingTime.TryTimeOfDay(EetBackupText.Trim(), out var backup, out var backupPrecision) || backupPrecision < 2)
            { EetMessage = $"Enter the backup time of day for bib {preparation.Bib} (HH:mm:ss.ff)."; return; }
            var proposed = preparation.ProposedTarget;
            var backupKey = proposed is { DeviceTicks: { } p } && TimingTime.FormatTimeOfDay(p, proposed.Precision) == EetBackupText.Trim() ? proposed.Key : null;
            // The calculation records every reference it passed over: left out by the operator, or without a backup time.
            var exclusions = EetRows.Where(x => x.CanEdit && x.Excluded).Select(x => new EetExclusion(x.Bib, x.ExcludeReason.Trim()))
                .Concat(selection.Skipped.Where(x => x.Unavailable == EetPreparation.NoBackupTime).Select(x => new EetExclusion(x.Bib, EetPreparation.NoBackupTime)))
                .ToArray();
            var calculation = EetCalculator.Calculate(preparation.Bib, preparation.Channel, EetKind, backup, backupPrecision, backupKey,
                selection.Pairs, exclusions, EetReplacementReason.Trim());
            var digits = selection.Pairs.Max(x => Math.Max(x.APrecision, x.BackupPrecision));
            EetSum = TimingTime.FormatSignedSeconds(calculation.SumTicks, digits);
            EetAverage = TimingTime.FormatSignedSeconds((decimal)calculation.SumTicks / calculation.Pairs.Count / TimeSpan.TicksPerSecond, Math.Min(7, digits + 1));
            EetCorrectionLabel = $"Correction (rounded to {calculation.CorrectionPrecision} decimals)";
            EetCorrection = TimingTime.FormatSignedSeconds(calculation.CorrectionTicks, calculation.CorrectionPrecision);
            EetResult = TimingTime.FormatTimeOfDay(calculation.ResultTimeOfDay, calculation.ResultPrecision);
            var net = PreviewNetTime(preparation.Channel, EetResult, manual: true);
            EetNetTime = net.StartsWith("Net ", StringComparison.Ordinal) ? net[4..] : net.Length == 0 ? "—" : net;
            EetMessage = string.Join(" ", new[]
            {
                selection.Pairs.Count < EetCalculator.RequiredPairs
                    ? $"Only {selection.Pairs.Count} reference pairs exist in this run; the booklet uses {EetCalculator.RequiredPairs}." : "",
                calculation.ReplacementReason.Length == 0 ? "Choose why System A missed the time (timing report) to save." : ""
            }.Where(x => x.Length > 0));
            _eetCalculation = calculation;
        }
        catch (DomainValidationException ex) { EetMessage = ex.Message; }
        finally
        {
            OnPropertyChanged(nameof(CanSaveEet));
            SaveEetCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(CanSaveEetPdf));
            SaveEetPdfCommand.NotifyCanExecuteChanged();
        }
    }

    [RelayCommand(CanExecute = nameof(CanSaveEet))]
    private async Task SaveEetAsync()
    {
        await GuardAsync(async () =>
        {
            if (_eetCalculation is not { } calculation || workspace.Timing is not { } timing || timing.ListId is not { } listId)
            { throw new DomainValidationException("Calculate the EET first."); }
            if (calculation.ReplacementReason.Length == 0)
            { throw new DomainValidationException("Choose why System A missed the time; the timing report needs the reason."); }
            var position = calculation.Channel == 0 ? "start" : "finish";
            try
            {
                await timing.EnterCompetitorTimeAsync(listId, new(calculation.Bib, calculation.Channel, ManualTimeSource.Eet, Eet: calculation, Replacing: _eetReplacing,
                        ReferenceKeys: _eetPreparation is { } prepared ? CompetitorTimeEntry.EligibleKeys(EetPreparation.Placed(prepared, calculation.ResultTimeOfDay)) : null),
                    TimingOperator, $"EET for missing {position} from {(calculation.Source == EetBackupSource.SystemB ? "System B" : EetSheetBuilder.SourceLabel(calculation.Source).ToLowerInvariant())} · {calculation.ReplacementReason}");
            }
            catch (DomainValidationException)
            {
                // The run changed since the table was prepared: prepare it again on the current run, keeping what the
                // operator typed, so the recalculated EET can be saved.
                await ReloadEetKeepingInputAsync();
                throw;
            }
            RefreshTiming();
            SetStatus($"Bib {calculation.Bib}: EET {position} {TimingTime.FormatTimeOfDay(calculation.ResultTimeOfDay, calculation.ResultPrecision)} saved with correction history and the calculation.");
            // Reload only the table that was saved; another competitor chosen meanwhile keeps its own table.
            if (TimingClassificationTarget?.Bib == calculation.Bib && EetChannel == calculation.Channel) { await LoadEetAsync(); }
        });
    }

    // The EET of the chosen position as saved in the history, with when and by whom; null without one.
    private (EetCalculation Calculation, TimingAudit Audit)? SavedEet(int channel)
    {
        if (TargetObservation(channel) is not { EntrySource: ManualTimeSource.Eet } observation || workspace.Timing?.Snapshot is not { } snapshot) { return null; }
        return snapshot.Audit.LastOrDefault(x => x.After.Kind == DecisionKind.ManualTime && x.After.ObservationKey == observation.Key
            && x.After.Timestamp?.Eet is not null) is { } audit ? (audit.After.Timestamp!.Eet!, audit) : null;
    }

    public bool CanSaveEetPdf => _eetCalculation is not null || SavedEet(EetChannel) is not null;

    /// <summary>
    /// Saves the EET calculation as a PDF for the timing documentation: the saved EET of the chosen position, or, before
    /// saving, the calculation shown, marked as a draft.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSaveEetPdf))]
    private async Task SaveEetPdfAsync()
    {
        await GuardAsync(async () =>
        {
            if (TimingClassificationTarget is not { IsForerunner: false } target || workspace.Timing?.Snapshot is not { } snapshot || TimingCompetition is not { } competition)
            { throw new DomainValidationException("Choose a competitor of this run."); }
            var saved = SavedEet(EetChannel);
            var calculation = saved?.Calculation ?? _eetCalculation ?? throw new DomainValidationException("Calculate the EET first.");
            var now = DateTimeOffset.Now;
            var status = saved is { Audit: var audit }
                ? $"Saved to the result {audit.At.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture)} by {audit.Operator}. History: {audit.Reason}"
                : $"Calculated by {TimingOperator}; not saved to the result.";
            var prepared = _eetPreparation is { } preparation && preparation.Bib == calculation.Bib && preparation.Channel == calculation.Channel
                ? EetPreparation.Placed(preparation, calculation.ResultTimeOfDay) : null;
            var notUsable = prepared?.StartOrder.Where(x => x.Unavailable is not null || x.Backup is null)
                .ToDictionary(x => x.Bib, x => x.Unavailable ?? x.BackupState) ?? new Dictionary<int, string>();
            // The lines follow the order the competitors actually started in, as the table does.
            var startOrder = snapshot.StartOrder.ToList();
            if (prepared is not null)
            {
                startOrder = prepared.StartOrder.Select(x => x.Bib).ToList();
                startOrder.Insert(Math.Clamp(prepared.TargetIndex, 0, startOrder.Count), prepared.Bib);
            }
            var values = competition.Values;
            var race = string.Join(" | ", new[]
            {
                values.Name, values.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), values.Discipline.ToString(),
                _timingList?.Plan.Gender is { } gender ? GenderLabels.Format(gender) : "",
                values.FisCode is { Length: > 0 } codex ? "Codex " + codex : "", $"Run {TimingRun}"
            }.Where(x => x.Length > 0));
            var result = TimingTime.FormatTimeOfDay(calculation.ResultTimeOfDay, calculation.ResultPrecision);
            var net = PreviewNetTime(calculation.Channel, result, manual: true);
            var sheet = EetSheetBuilder.Build(calculation, startOrder,
                TimingRows.ToDictionary(x => x.Bib, x => new EetSheetName(x.Surname, x.FirstName)), notUsable,
                _current?.Values.Name ?? "", race, net.StartsWith("Net ", StringComparison.Ordinal) ? net[4..] : net.Length == 0 ? "-" : net,
                draft: saved is null, status, now);
            var position = calculation.Channel == 0 ? "Start" : "Finish";
            var name = string.Concat($"EET-{values.ShortLabel}-Run{TimingRun}-Bib{calculation.Bib}-{position}{(saved is null ? "-draft" : "")}.pdf"
                .Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '-' : c));
            var path = await dialogs.ChoosePdfExportAsync("Save EET calculation", name);
            if (path is null) { return; }
            var bytes = await Task.Run(() => Reporting.EetCalculationPdf.Render(sheet));
            await ExportFile.WriteAsync(path, bytes);
            SetStatus($"Bib {calculation.Bib}: EET calculation{(saved is null ? " (draft)" : "")} saved to {path}.");
        });
    }
}
