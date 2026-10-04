using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenSkiTime.Application;
using OpenSkiTime.Devices;
using OpenSkiTime.Domain;
using OpenSkiTime.Timing;

namespace OpenSkiTime.Desktop;

public sealed partial class MainViewModel
{
    private TimingReportDraft? _reportDraft;
    private SavedTimingReport? _reportSaved;
    private string? _reportFile;
    private bool _reportLoading;
    private int _reportLoadGeneration;
    private readonly List<TimingReplayData> _reportSources = [];
    public ObservableCollection<TimingReportEvidenceRow> ReportEvidence { get; } = [];
    public ObservableCollection<TimingReportRunEditor> ReportRuns { get; } = [];
    public ObservableCollection<TimingReportMissedEditor> ReportMissed { get; } = [];
    public ObservableCollection<ApprovedTimingReport> ReportApprovals { get; } = [];
    [ObservableProperty] private CompetitionDetails? _reportCompetition;
    [ObservableProperty] private ApprovedTimingReport? _selectedReportApproval;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AcceptReportImageMatchesCommand))]
    private bool _isReportBusy;
    [ObservableProperty] private bool _hasTimingReportEdits;
    [ObservableProperty] private string _reportStatus = "Select a competition to prepare its timing report.";
    [ObservableProperty] private string _reportValidation = "";
    [ObservableProperty] private string _reportSync = "";
    [ObservableProperty] private string _reportHandSync = "";
    [ObservableProperty] private string _reportCheckA = "";
    [ObservableProperty] private string _reportCheckB = "";
    [ObservableProperty] private string _reportCheckAStart = "";
    [ObservableProperty] private string _reportCheckBStart = "";
    [ObservableProperty] private int? _reportLevel;
    public IReadOnlyList<int> ReportLevels { get; } = [0, 1, 2, 3, 4];
    public bool ReportHasStartTimerA => _reportDraft?.Defaults.TimerStartA is not null;
    public bool ReportHasStartTimerB => _reportDraft?.Defaults.TimerStartB is not null;
    [ObservableProperty] private bool _reportReviewed;
    [ObservableProperty] private bool _reportCertified;
    [ObservableProperty] private string _reportChangeReason = "Report preparation";
    public string ReportEquipmentSummary => _reportDraft is { } draft
        ? $"A: {draft.Defaults.TimerA.Brand} {draft.Defaults.TimerA.Model}  /  B: {draft.Defaults.TimerB.Brand} {draft.Defaults.TimerB.Model}\nTimekeeper: {draft.Defaults.Timekeeper.LastName} {draft.Defaults.Timekeeper.FirstName}  /  Chief: {draft.Defaults.ChiefOfTiming.LastName} {draft.Defaults.ChiefOfTiming.FirstName}" : "";
    public string ReportIdentity => _reportDraft is { } d
        ? $"{d.Header.Nation}{d.Header.Codex} / {d.Header.Discipline} / {d.Header.Date:dd.MM.yyyy} / TD {d.TechnicalDelegate.LastName} {d.TechnicalDelegate.FirstName}" : "";
    public string ReportSoftware { get; } = ProductInfo.DisplayName;
    private static string ReportSoftwareVersion => ProductInfo.Version;

    partial void OnReportSyncChanged(string value) => ReportChanged();
    partial void OnReportHandSyncChanged(string value) => ReportChanged();
    partial void OnReportCheckAChanged(string value) => ReportChanged();
    partial void OnReportCheckBChanged(string value) => ReportChanged();
    partial void OnReportCheckAStartChanged(string value) => ReportChanged();
    partial void OnReportCheckBStartChanged(string value) => ReportChanged();
    partial void OnReportLevelChanged(int? value) => ReportChanged();
    partial void OnReportReviewedChanged(bool value) => ReportChanged(false);
    partial void OnReportCertifiedChanged(bool value) => ReportChanged(false);
    private void ReportChanged(bool invalidateReview = true)
    {
        if (_reportLoading || _reportDraft is null) { return; }
        HasTimingReportEdits = true;
        if (invalidateReview) { ReportReviewed = false; ReportCertified = false; }
        QueueReportSave();
    }
    public IReadOnlyList<int> ReportReplacementRuns => ReportRuns.Where(x => !x.AllResultsA).Select(x => x.Run).ToArray();
    public bool CanAddReportReplacement => ReportReplacementRuns.Count > 0;
    private void ReportRowChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (sender is TimingReportRunEditor && args.PropertyName == nameof(TimingReportRunEditor.AllResultsA))
        { UpdateReportReplacementRuns(); }
        ReportChanged();
    }
    private void UpdateReportReplacementRuns()
    {
        var runs = ReportReplacementRuns;
        foreach (var row in ReportMissed.ToArray())
        {
            if (!runs.Contains(row.Run)) { row.PropertyChanged -= ReportRowChanged; ReportMissed.Remove(row); }
            else { row.Runs = runs; }
        }
        OnPropertyChanged(nameof(ReportReplacementRuns)); OnPropertyChanged(nameof(CanAddReportReplacement));
    }
    [RelayCommand] private void AddReportReplacement()
    {
        var runs = ReportReplacementRuns;
        if (runs.Count == 0) { return; }
        var row = new TimingReportMissedEditor { Runs = runs, Run = runs[0] };
        row.PropertyChanged += ReportRowChanged; ReportMissed.Add(row); ReportChanged();
    }
    [RelayCommand] private void RemoveReportReplacement(TimingReportMissedEditor row)
    { row.PropertyChanged -= ReportRowChanged; ReportMissed.Remove(row); ReportChanged(); }

    [RelayCommand]
    private async Task ShowTimingReportAsync()
    {
        SwitchSection(WorkspaceSection.TimingReport);
        if (!IsTimingReportSection) { return; }
        var race = FisCompetitions.FirstOrDefault(x => x.Id == _activeRaceId) ?? FisCompetitions.FirstOrDefault();
        if (race is null) { ReportStatus = "Select a FIS competition in Competitions first."; return; }
        if (!await FlushTimingReportAsync()) { return; }
        ReportCompetition = race;
        await LoadTimingReportAsync();
        SetActiveRace(race, _activeRaceId == race.Id && _activeRaceRun > 0 ? _activeRaceRun : 1, _activeRaceSection);
    }

    public async Task SelectReportCompetitionAsync(CompetitionDetails competition)
    {
        ArgumentNullException.ThrowIfNull(competition);
        if (!await FlushTimingReportAsync()) { return; }
        ReportCompetition = competition;
        SetActiveRace(competition, _activeRaceId == competition.Id ? _activeRaceRun : 1, _activeRaceSection);
        await LoadTimingReportAsync();
    }

    [RelayCommand]
    private async Task LoadTimingReportAsync()
    {
        if (IsReportBusy || ReportCompetition is not { } competition) { return; }
        if (!await FlushTimingReportAsync()) { return; }
        var generation = ++_reportLoadGeneration;
        IsReportBusy = true;
        await GuardAsync(async () =>
        {
            var file = workspace.FilePath;
            var sameReport = _reportDraft?.CompetitionId == competition.Id && _reportFile == file;
            var previousImportRun = ReportImportRun;
            var previousRuns = ReportImportRuns.ToArray();
            if (!sameReport) { ClearReportPreview(); }
            var series = await workspace.ReadAsync();
            var saved = await workspace.ReadTimingReportAsync(competition.Id);
            var desk = await workspace.ReadStartListsAsync(competition.Id);
            var sources = new List<TimingReplayData>();
            foreach (var list in desk.Revisions.GroupBy(x => x.Plan.RunNumber).Select(g => g.MaxBy(x => x.Revision)!))
            { sources.Add(await workspace.ReadTimingAsync(list.Id)); }
            if (generation != _reportLoadGeneration || workspace.FilePath != file) { return; }
            _reportSources.Clear(); _reportSources.AddRange(sources);
            var c = competition.Values; var calendar = c.Calendar; var td = calendar?.TechnicalDelegate;
            var header = new TimingReportHeader(calendar?.Season ?? (c.Date.Month >= 7 ? c.Date.Year + 1 : c.Date.Year),
                c.FisCode ?? "", calendar?.Nation is { Length: > 0 } nation ? nation : series.Values.Nation,
                c.Discipline switch { Discipline.Slalom => "SL", Discipline.GiantSlalom => "GS", Discipline.SuperG => "SG", Discipline.Downhill => "DH", _ => "" },
                calendar?.Category ?? "", calendar?.Gender ?? "", c.Name,
                calendar?.Location is { Length: > 0 } place ? place : series.Values.Location, c.Date);
            var draft = saved?.Values ?? new TimingReportDraft { CompetitionId = competition.Id, Defaults = ReportDefaults };
            var runs = sources.OrderBy(s => s.List.Plan.RunNumber).Select(s => TimingReportProjection.FromTiming(s, TimingReplay.Restore(s, new AlgeDecoderFactory())))
                .Select(run => run with { Comment = draft.Runs.FirstOrDefault(r => r.Run == run.Run)?.Comment ?? "",
                    AllResultsA = draft.Runs.FirstOrDefault(r => r.Run == run.Run)?.AllResultsA ?? run.AllResultsA,
                    MissedA = draft.Runs.FirstOrDefault(r => r.Run == run.Run) is { } savedRun
                        ? savedRun.AllResultsA ? [] : savedRun.MissedA.GroupBy(m => m.Bib).Select(g => g.Last()).ToArray()
                        : run.MissedA }).ToArray();
            var fingerprint = TimingReportProjection.Fingerprint(sources);
            header = header with { TimingLevel = draft.Header.TimingLevel };
            var stale = draft.SourceFingerprint != fingerprint || draft.Header != header || draft.Defaults != ReportDefaults;
            draft = draft with { Defaults = ReportDefaults, Header = header, TechnicalDelegate = td is null ? new() : new(td.FirstName, td.LastName, td.Nation, Number: td.Number),
                Runs = runs, SourceFingerprint = fingerprint, Reviewed = !stale && draft.Reviewed, CertifyFis = !stale && draft.CertifyFis };
            _reportFile = file; _reportSaved = saved; _reportDraft = draft;
            await PopulateReportAsync(draft, autoFill: true);
            var importRun = sameReport && (previousImportRun is null || ReportRuns.Any(x => x.Run == previousImportRun)) ? previousImportRun
                : sources.Any(x => x.List.Plan.RunNumber == _activeRaceRun) ? _activeRaceRun : sources.FirstOrDefault()?.List.Plan.RunNumber;
            _reportRefreshingImportChoices = true;
            try
            {
                if (!previousRuns.SequenceEqual(ReportImportRuns)) { OnPropertyChanged(nameof(ReportImportRuns)); }
                ReportImportRun = importRun;
            }
            finally { _reportRefreshingImportChoices = false; }
            ReportApprovals.Clear();
            foreach (var approval in (await workspace.ReadApprovedTimingReportsAsync(competition.Id)).Reverse()) { ReportApprovals.Add(approval); }
            SelectedReportApproval = ReportApprovals.FirstOrDefault();
            await LoadReportHistoryAsync();

            // A and connected auxiliary evidence are projections of the current database,
            // not operator edits that require importing or saving before changing races.
            HasTimingReportEdits = false;
            ReportStatus = sources.Count == 0 ? "No saved start list yet. Equipment and synchronization can be prepared now."
                : stale && saved is not null ? "" : "Current competition timing data. Changes are saved automatically.";
            _current = await workspace.ReadAsync();
            UpdateReportPreviewValidity();
            NotifyReportDefaults(); OnPropertyChanged(nameof(ReportIdentity));
        });
        IsReportBusy = false;
    }

    private async Task PopulateReportAsync(TimingReportDraft draft, bool autoFill = false)
    {
        _reportLoading = true;
        try
        {
            foreach (var row in ReportEvidence) { row.PropertyChanged -= ReportRowChanged; }
            foreach (var row in ReportRuns) { row.PropertyChanged -= ReportRowChanged; }
            foreach (var row in ReportMissed) { row.PropertyChanged -= ReportRowChanged; }
            ReportEvidence.Clear(); ReportRuns.Clear(); ReportMissed.Clear();
            ReportSync = TimingReportEvidenceRow.Format(draft.Sync); ReportHandSync = TimingReportEvidenceRow.Format(draft.HandSync);
            ReportCheckA = TimingReportEvidenceRow.Format(draft.SyncCheckA); ReportCheckB = TimingReportEvidenceRow.Format(draft.SyncCheckB);
            ReportCheckAStart = TimingReportEvidenceRow.Format(draft.SyncCheckAStart); ReportCheckBStart = TimingReportEvidenceRow.Format(draft.SyncCheckBStart);
            ReportLevel = draft.Header.TimingLevel;
            ReportReviewed = draft.Reviewed; ReportCertified = draft.CertifyFis;
            foreach (var source in _reportSources.OrderBy(x => x.List.Plan.RunNumber))
            {
                var snapshot = TimingReplay.Restore(source, new AlgeDecoderFactory());
                var run = draft.Runs.FirstOrDefault(x => x.Run == source.List.Plan.RunNumber);
                if (run is null) { continue; }
                var editor = TimingReportRunEditor.FromTiming(run, snapshot);
                editor.PropertyChanged += ReportRowChanged; ReportRuns.Add(editor);
                foreach (var missed in run.AllResultsA ? [] : run.MissedA)
                {
                    var row = new TimingReportMissedEditor { Runs = draft.Runs.Select(x => x.Run).ToArray(), Run = run.Run, Bib = missed.Bib, Reason = missed.Reason, TimeFrom = missed.TimeFrom };
                    row.PropertyChanged += ReportRowChanged; ReportMissed.Add(row);
                }
                var auxiliary = autoFill && workspace.Auxiliary is { } aux ? (await aux.ReadAsync(source.List.Id)).Decode(new AlgeDecoderFactory()) : [];
                foreach (var result in snapshot.Results.OrderBy(x => x.Entry.Position))
                {
                    TimingReportStamp? A(string? key) => snapshot.Observations.FirstOrDefault(x => x.Observation.Key == key)?.Observation is { DeviceTicks: { } ticks } o
                        ? new(ticks, o.Precision, o.Key) : null;
                    var row = new TimingReportEvidenceRow { Run = run.Run, Bib = result.Bib, Name = result.Name,
                        Sample = (run.First.Bib == result.Bib ? "First " : "") + (run.Last.Bib == result.Bib ? "Last" : ""),
                        AStartStamp = A(result.StartKey), AFinishStamp = A(result.FinishKey),
                        Net = run.First.Bib == result.Bib ? TimingTime.Format(run.First.NetHundredths)
                            : run.Last.Bib == result.Bib ? TimingTime.Format(run.Last.NetHundredths) : result.Time };
                    TimingReportStamp? Saved(TimingReportImageRole role, int channel) => draft.Associations.FirstOrDefault(x => x.Run == run.Run && x.Bib == row.Bib && x.Role == role && x.Channel == channel)?.Stamp;
                    row.OriginalBStart = Saved(TimingReportImageRole.B, 0); row.OriginalBFinish = Saved(TimingReportImageRole.B, 1);
                    row.OriginalHandStart = Saved(TimingReportImageRole.HandStart, 0); row.OriginalHandFinish = Saved(TimingReportImageRole.HandFinish, 1);
                    row.BStart = TimingReportEvidenceRow.Format(row.OriginalBStart); row.BFinish = TimingReportEvidenceRow.Format(row.OriginalBFinish);
                    row.HandStart = TimingReportEvidenceRow.Format(row.OriginalHandStart); row.HandFinish = TimingReportEvidenceRow.Format(row.OriginalHandFinish);
                    ReportEvidence.Add(row);
                }
                editor.First = ReportEvidence.FirstOrDefault(x => x.Run == run.Run && x.Bib == run.First.Bib);
                editor.Last = ReportEvidence.FirstOrDefault(x => x.Run == run.Run && x.Bib == run.Last.Bib);
                if (autoFill) { ApplyAutomaticAuxiliary(run.Run, auxiliary); }
            }
            foreach (var row in ReportEvidence) { row.PropertyChanged += ReportRowChanged; }
            UpdateReportReplacementRuns();
            if (autoFill && JsonSerializer.Serialize(ReadReportDraft().Associations) != JsonSerializer.Serialize(draft.Associations))
            { ReportReviewed = false; ReportCertified = false; }
        }
        finally { _reportLoading = false; }
    }

    private void ApplyAutomaticAuxiliary(int run, IReadOnlyList<AuxiliaryTimingObservation> observations)
    {
        if (run == _reportSources.Min(x => x.List.Plan.RunNumber)) { ReportAuxiliaryClockWarning = ""; }
        var source = _reportSources.Single(x => x.List.Plan.RunNumber == run);
        var a = TimingReplay.Restore(source, new AlgeDecoderFactory());
        var byKey = a.Observations.ToDictionary(x => x.Observation.Key, x => x.Observation, StringComparer.Ordinal);
        var rows = ReportEvidence.Where(x => x.Run == run).ToDictionary(x => x.Bib);
        var warnings = new HashSet<string>(StringComparer.Ordinal);
        foreach (var role in Enum.GetValues<TimingReportImageRole>())
        {
            var targets = ReportTargets(run, role);
            var auxiliaryRole = role switch { TimingReportImageRole.B => AuxiliaryTimingRole.B,
                TimingReportImageRole.HandStart => AuxiliaryTimingRole.HandStart, _ => AuxiliaryTimingRole.HandFinish };
            var roleEvidence = observations.Where(x => x.Role == auxiliaryRole).ToArray();
            var proposals = new List<EvidenceMatch>();
            bool TargetUsesUtc(EvidenceTarget target)
            {
                var row = rows[target.Bib];
                var stamp = target.Channel == 0 ? row.AStartStamp : row.AFinishStamp;
                return stamp is not null && byKey.TryGetValue(stamp.SourceReference, out var value) && AuxiliaryClockComparison.UsesUtc(value);
            }
            foreach (var group in targets.GroupBy(TargetUsesUtc))
            {
                var normalized = AuxiliaryClockComparison.Normalize(group.Key, roleEvidence);
                foreach (var warning in normalized.Warnings) { warnings.Add($"Run {run}, {role}: {warning}"); }
                var evidence = normalized.Observations.Select(x => new EvidenceTimestamp(x.Key, x.DeviceTicks!.Value,
                    x.Precision, x.Channel, "Captured device evidence; explicit clock context applied")).ToArray();
                proposals.AddRange(TimingEvidenceMatching.Match(group.ToArray(), evidence,
                    role == TimingReportImageRole.B ? TimeSpan.TicksPerSecond : 2 * TimeSpan.TicksPerSecond));
            }
            foreach (var proposal in proposals.Where(x => x.Evidence is not null).GroupBy(x => x.Evidence!.Key).Where(g => g.Count() == 1))
            {
                var match = proposal.Single(); var found = match.Evidence!;
                SetReportStamp(rows[match.Target.Bib], role, match.Target.Channel, new(found.Ticks, found.Precision, found.Key), false);
            }
        }
        if (warnings.Count != 0)
        { ReportAuxiliaryClockWarning = string.Join("\n", new[] { ReportAuxiliaryClockWarning }.Where(x => x.Length != 0).Concat(warnings)); }
    }

    private EvidenceTarget[] ReportTargets(int run, TimingReportImageRole role) => ReportEvidence.Where(x => x.Run == run && x.Sample.Length > 0)
        .SelectMany(row => new[] { (Channel: 0, Stamp: row.AStartStamp), (Channel: 1, Stamp: row.AFinishStamp) }
            .Where(p => p.Stamp is not null && (role == TimingReportImageRole.B || p.Channel == (role == TimingReportImageRole.HandStart ? 0 : 1)))
            .Select(p => new EvidenceTarget($"{run}:{row.Bib}:{p.Channel}", row.Bib, p.Channel, p.Stamp!.Ticks))).ToArray();

    private static void SetReportStamp(TimingReportEvidenceRow row, TimingReportImageRole role, int channel, TimingReportStamp stamp, bool replace)
    {
        var text = TimingReportEvidenceRow.Format(stamp);
        if (role == TimingReportImageRole.B && channel == 0 && (replace || row.BStart.Length == 0)) { row.OriginalBStart = stamp; row.BStart = text; }
        if (role == TimingReportImageRole.B && channel == 1 && (replace || row.BFinish.Length == 0)) { row.OriginalBFinish = stamp; row.BFinish = text; }
        if (role == TimingReportImageRole.HandStart && (replace || row.HandStart.Length == 0)) { row.OriginalHandStart = stamp; row.HandStart = text; }
        if (role == TimingReportImageRole.HandFinish && (replace || row.HandFinish.Length == 0)) { row.OriginalHandFinish = stamp; row.HandFinish = text; }
    }

    private TimingReportDraft ReadReportDraft()
    {
        var draft = _reportDraft ?? throw new DomainValidationException("Open a timing report first.");
        var date = draft.Header.Date;
        var associations = new List<TimingReportAssociation>();
        foreach (var row in ReportEvidence)
        {
            void Add(TimingReportImageRole role, int channel, string text, TimingReportStamp? original, TimingReportStamp? reference)
            {
                if (row.Read(text, original, reference is null ? date : DateOnly.FromDateTime(new(reference.Ticks))) is { } stamp)
                { associations.Add(new(row.Run, row.Bib, channel, role, stamp)); }
            }
            Add(TimingReportImageRole.B, 0, row.BStart, row.OriginalBStart, row.AStartStamp);
            Add(TimingReportImageRole.B, 1, row.BFinish, row.OriginalBFinish, row.AFinishStamp);
            Add(TimingReportImageRole.HandStart, 0, row.HandStart, row.OriginalHandStart, row.AStartStamp);
            Add(TimingReportImageRole.HandFinish, 1, row.HandFinish, row.OriginalHandFinish, row.AFinishStamp);
        }
        TimingReportBib Sample(int run, TimingReportBib sample)
        {
            TimingReportStamp? Get(TimingReportImageRole role, int channel) => associations.FirstOrDefault(x => x.Run == run && x.Bib == sample.Bib && x.Role == role && x.Channel == channel)?.Stamp;
            return sample with { BStart = Get(TimingReportImageRole.B, 0), BFinish = Get(TimingReportImageRole.B, 1),
                HandStart = Get(TimingReportImageRole.HandStart, 0), HandFinish = Get(TimingReportImageRole.HandFinish, 1) };
        }
        var parser = new TimingReportEvidenceRow();
        return draft with { Sync = parser.Read(ReportSync, draft.Sync, date), HandSync = parser.Read(ReportHandSync, draft.HandSync, date),
            SyncCheckA = parser.Read(ReportCheckA, draft.SyncCheckA, date), SyncCheckB = parser.Read(ReportCheckB, draft.SyncCheckB, date),
            SyncCheckAStart = parser.Read(ReportCheckAStart, draft.SyncCheckAStart, date), SyncCheckBStart = parser.Read(ReportCheckBStart, draft.SyncCheckBStart, date),
            Defaults = ReportDefaults, Header = draft.Header with { TimingLevel = ReportLevel },
            Reviewed = ReportReviewed, CertifyFis = ReportCertified, Associations = associations.ToArray(),
            Runs = ReportRuns.Select(r => r.Source with { First = Sample(r.Run, r.Source.First), Last = Sample(r.Run, r.Source.Last), Comment = r.Comment, AllResultsA = r.AllResultsA,
                MissedA = r.AllResultsA ? [] : ReportMissed.Where(m => m.Run == r.Run).Select(m => new TimingReportMissed(m.Bib, m.Reason, m.TimeFrom)).ToArray() }).ToArray() };
    }

    private void NotifyReportDefaults() { OnPropertyChanged(nameof(ReportEquipmentSummary)); OnPropertyChanged(nameof(ReportHasStartTimerA)); OnPropertyChanged(nameof(ReportHasStartTimerB)); }

    private async Task SaveReportCoreAsync()
    {
        await _reportSaveGate.WaitAsync();
        try
        {
        if (_reportFile != workspace.FilePath) { throw new DomainValidationException("Reopen this report in its original series."); }
        var draft = ReadReportDraft();
        var generation = _reportEditGeneration;
        for (var attempt = 0; ; attempt++)
        {
            var current = await workspace.ReadAsync();
            if (_reportSaved?.Revision != (await workspace.ReadTimingReportAsync(draft.CompetitionId))?.Revision)
            { throw new DomainValidationException("The saved report changed. Reopen the report before editing it further."); }
            try
            {
                await workspace.SaveTimingReportAsync(draft, current.Revision, TimingOperator, ReportChangeReason, DateTimeOffset.UtcNow);
                break;
            }
            // Another local editor can commit unrelated series metadata while we read.
            // Retry its revision only; never overwrite a report revision we did not load.
            catch (SeriesConflictException) when (attempt < 2) { }
        }
        _reportSaved = await workspace.ReadTimingReportAsync(draft.CompetitionId);
        if (generation == _reportEditGeneration) { _reportDraft = draft; HasTimingReportEdits = false; }
        _current = await workspace.ReadAsync();
        await LoadReportHistoryAsync();
        ReportValidation = string.Join("\n", TimingReportXml.Validate(draft));
        if (!HasTimingReportEdits) { ReportStatus = "All changes saved."; SetStatus(ReportStatus); }
        }
        finally { _reportSaveGate.Release(); }
    }

    [RelayCommand]
    private async Task ApproveTimingReportAsync()
    {
        if (IsReportBusy) { return; }
        if (!await FlushTimingReportAsync()) { return; }
        IsReportBusy = true;
        await GuardAsync(async () =>
        {
            await SaveReportCoreAsync();
            if (ReportValidation.Length > 0) { ReportStatus = "Complete the listed fields before creating XML."; return; }
            var approved = await workspace.ApproveTimingReportAsync(new(_reportDraft!.CompetitionId, _reportSaved!.Revision,
                _current!.Revision, TimingOperator, DateTimeOffset.UtcNow, ReportSoftwareVersion));
            ReportApprovals.Insert(0, approved); SelectedReportApproval = approved;
            _current = await workspace.ReadAsync(); ReportStatus = "Approved XML saved in the series. Export or send in test mode.";
        });
        IsReportBusy = false;
    }

    [RelayCommand]
    private async Task ExportTimingReportAsync()
    {
        await GuardAsync(async () =>
        {
            if (SelectedReportApproval is not { } approval) { throw new DomainValidationException("Create an approved report XML first."); }
            var path = await dialogs.ChooseResultXmlExportAsync(Path.GetFileNameWithoutExtension(approval.XmlFileName) + "-timing-report.xml");
            if (path is null) { return; }
            await File.WriteAllBytesAsync(path, approval.Xml);
            ReportStatus = "Exported the exact approved timing report XML.";
        });
    }

    private void ResetReportUi()
    {
        _reportSaveDelay?.Cancel();
        _reportLoadGeneration++; _reportDraft = null; _reportSaved = null; _reportFile = null;
        ReportEvidence.Clear(); ReportRuns.Clear(); ReportMissed.Clear(); ReportApprovals.Clear();
        ReportCompetition = null; SelectedReportApproval = null; HasTimingReportEdits = false;
        ReportHistory.Clear(); SelectedReportHistory = null;
        ReportAuxiliaryClockWarning = "";
        ResetReportImport();
    }
}
