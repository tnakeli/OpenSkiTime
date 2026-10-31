using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenSkiTime.Application;
using OpenSkiTime.Domain;

namespace OpenSkiTime.Desktop;

public sealed record DrawDestination(CompetitionDetails Competition, int Run);
public sealed record DrawMenuCompetition(CompetitionDetails Competition, IReadOnlyList<int> Runs);
public sealed record DrawStartListRow(StartListEntry Entry, string RunOneTime, bool SharesPoints = false)
{
    // Competitors with identical FIS points; the draw decides their order. Bold shows each complete group.
    public FontWeight Weight => SharesPoints ? FontWeight.Bold : FontWeight.Normal;
    // FIS points use the FIS list notation (decimal point) everywhere, independent of the Windows number format.
    public string Points => Entry.Entrant.Points?.ToString("0.00", CultureInfo.InvariantCulture) ?? string.Empty;
}

// One entered competitor before the Run 1 draw: saved points for the race discipline and, when a FIS list is loaded,
// that list's points. A difference means the saved points are not the loaded list's (Update from FIS list, then Save).
public sealed record DrawPreviewRow(int Order, CompetitorDetails Competitor, decimal? Points, decimal? ListPoints, bool HasListPoints)
{
    public string Code => Competitor.Values.FederationCode ?? string.Empty;
    public string Surname => Competitor.Values.Surname;
    public string FirstName => Competitor.Values.FirstName;
    public string Year => Competitor.Values.BirthYear?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
    public string Nation => Competitor.Values.Nation ?? string.Empty;
    public string Club => Competitor.Values.Club ?? string.Empty;
    public string PointsText => Points?.ToString("0.00", CultureInfo.InvariantCulture) ?? string.Empty;
    public string ListPointsText => HasListPoints ? ListPoints?.ToString("0.00", CultureInfo.InvariantCulture) ?? "—" : string.Empty;
    public bool DiffersFromList => HasListPoints && Points != ListPoints;
}

public sealed partial class ResultInputRow(StartListEntry entry) : ObservableObject
{
    public Guid CompetitorId => entry.Entrant.CompetitorId;
    public int Bib => entry.Bib;
    public string Code => entry.Entrant.Athlete.FederationCode ?? string.Empty;
    public string Name => entry.Entrant.Athlete.Surname + " " + entry.Entrant.Athlete.FirstName;
    public string Surname => entry.Entrant.Athlete.Surname;
    public string FirstName => entry.Entrant.Athlete.FirstName;
    [ObservableProperty] private string _time = string.Empty;
    [ObservableProperty] private string _status = "Finished";
    public RunFinish Read()
    {
        // Names only: Enum.TryParse would also read "1" as DNS and silently change Run 2 eligibility.
        var text = Status.Trim();
        if (text.Length == 0 || !text.All(char.IsAsciiLetter) || !Enum.TryParse<FinishStatus>(text, ignoreCase: true, out var status))
        { throw new DomainValidationException($"Check status for bib {Bib}: use Finished, DNS, DNF, DSQ or NPS."); }
        if (status != FinishStatus.Finished && !string.IsNullOrWhiteSpace(Time))
        { throw new DomainValidationException($"Remove the time for bib {Bib}, or set its status to Finished."); }
        return new(CompetitorId, status, status == FinishStatus.Finished ? RunResultInput.ParseTime(Time) : null);
    }
}

public sealed partial class MainViewModel
{
    private StartListDesk? _drawDesk;
    private StartListRevision? _sourceRun;
    private int _drawLoad;
    private bool _settingDraw;
    public ObservableCollection<DrawMenuCompetition> DrawMenu { get; } = [];
    public ObservableCollection<StartListEntry> DrawEntries { get; } = [];
    public ObservableCollection<DrawStartListRow> DrawStartListRows { get; } = [];
    public ObservableCollection<ResultInputRow> DrawResults { get; } = [];
    public IReadOnlyList<int> ReverseChoices { get; } = [30, 15];
    private CompetitionDetails? _drawCompetition;
    private Gender? _drawGender;
    private Timing.TimingSnapshot? _sourceTiming;
    private string? _sourceTimingVersion;
    public bool HasCapturedRunInput => _sourceTiming is not null;
    public bool IsDrawFis => DrawCompetition?.Values.RaceType == RaceType.Fis;
    // Local races are also drawn by FIS points; show them whenever the list has any, so equal-points emphasis is explained.
    public bool ShowDrawPoints => IsDrawFis || DrawEntries.Any(x => x.Entrant.Points is not null);
    public bool CanPasteDrawResults => CanEditDrawResults && !HasCapturedRunInput;
    public string RunInputHelp => HasCapturedRunInput
        ? "Results come from Timing. Resolve observations and classify every starter there; then choose the reversal and create the start list."
        : "Enter every starter's time (1:12.34) or DNS / DNF / DSQ / NPS. Create the start list to save.";
    private int _drawRun = 1;
    public CompetitionDetails? DrawCompetition
    {
        get => _drawCompetition;
        set
        {
            if (Equals(value, _drawCompetition)) { return; }
            if (!_settingDraw && !CanLeaveDrawInput()) { OnPropertyChanged(); return; }
            SetProperty(ref _drawCompetition, value);
            if (_settingDraw) { return; }
            _settingDraw = true;
            DrawRun = 1;
            _settingDraw = false;
            _ = LoadDrawAsync();
        }
    }
    public int DrawRun
    {
        get => _drawRun;
        set
        {
            if (value == _drawRun) { return; }
            if (!_settingDraw && !CanLeaveDrawInput()) { OnPropertyChanged(); return; }
            SetProperty(ref _drawRun, value);
            if (!_settingDraw) { _ = LoadDrawAsync(); }
        }
    }
    [ObservableProperty] private bool _hasUnsavedRunInput;
    [ObservableProperty] private StartListRevision? _drawRevision;
    [ObservableProperty] private bool _isDrawBusy;
    [ObservableProperty] private bool _isResultInputOpen;
    [ObservableProperty] private int _firstDrawGroup = 15;
    [ObservableProperty] private int _drawReverseCount = 30;
    [ObservableProperty] private int _drawFirstBib = 1;
    [ObservableProperty] private string _drawState = "Choose a competition";
    [ObservableProperty] private string _drawHelp = "Choose a competition and run from the Draw / Start lists menu.";
    [ObservableProperty] private string _drawListInfo = string.Empty;
    [ObservableProperty] private string _drawContext = string.Empty;
    [ObservableProperty] private bool _drawHasChangedEntries;
    [ObservableProperty] private bool _drawRunStarted;
    [ObservableProperty] private string _drawEntryIssue = string.Empty;
    // The FIS points list the Run 1 draw uses (before the draw) or used (after it); a warning when it is not valid on the race date.
    [ObservableProperty] private string _drawPointsListText = string.Empty;
    public ObservableCollection<DrawPreviewRow> DrawPreviewRows { get; } = [];
    [ObservableProperty] private string _drawPreviewInfo = string.Empty;
    public bool ShowDrawPreview => DrawRun == 1 && DrawRevision is null && DrawPreviewRows.Count > 0;
    // Bib numbers that are not handed out, e.g. "13, 27, 40-42"; used by the draw and by Assign bibs.
    [ObservableProperty] private string _drawSkippedBibsText = string.Empty;
    [ObservableProperty] private DrawStartListRow? _selectedDrawRow;
    [ObservableProperty] private string _drawMoveToPosition = string.Empty;
    [ObservableProperty] private string _drawMoveReason = string.Empty;
    // The saved start list can still change (moves, bibs) until timing begins or Run 2 refers to it.
    public bool CanEditStartList => DrawRevision is { HasCapture: false } && !DrawRunStarted && !IsDrawBusy;
    public bool CanAssignBibs => CanEditStartList && DrawRun == 1;
    [ObservableProperty] private bool _drawPointsListWarning;
    public bool IsFirstDrawRun => DrawRun == 1;
    public bool IsLaterDrawRun => DrawRun > 1;
    public bool CanPrepareDraw => DrawBlockReason.Length == 0;
    // Why the draw / start list cannot be created now; empty when it can. The button stays active and reports this reason.
    public string DrawBlockReason
        => DrawCompetition is null ? "Choose a competition and run from the Start lists menu."
            : IsDrawBusy ? "The start list is being prepared. Wait until it is ready."
            : DrawRunStarted ? $"Run {DrawRun} has started. Its starting order is locked."
            : DrawRevision?.HasCapture == true ? $"Timing has been recorded with this Run {DrawRun} start list. Its starting order is locked."
            : DrawEntryIssue.Length > 0 ? DrawEntryIssue
            : DrawRun == 1 ? _drawPointsIssue
            : DrawRun != 2 ? $"Start lists for Run {DrawRun} are not supported."
            : _sourceRun is null ? "Draw Run 1 before preparing Run 2."
            : _sourceTiming is { Complete: false } ? "Run 1 is incomplete. Finish or classify every starter in Timing."
            : string.Empty;
    // A list that has been raced can always be exported. Before that, only changed Run 1 results block it: other audited
    // Run 1 changes (an ignored stray impulse, a queue reorder) leave the starting order valid.
    public bool CanExportDraw => DrawRevision is not null && !IsDrawBusy && !HasUnsavedRunInput
        && (_sourceTiming is null || DrawRunStarted || DrawRevision.HasCapture
            || (_sourceTiming.Complete && DrawRevision.Plan.SourceResults.SequenceEqual(_sourceTiming.ToRunFinishes())));
    public bool HasDrawSource => _sourceRun is not null;
    public bool CanEditDrawResults => HasDrawSource && !DrawRunStarted && !IsDrawBusy;
    public string DrawActionLabel => IsFirstDrawRun ? DrawRevision is null ? "Draw" : "Draw again" : "Create start list";
    // A FIS draw needs one gender. Names the entered competitors who do not fit the competition (or lack a gender).
    private static string MixedEntriesReason(CompetitionDetails competition, IReadOnlyList<CompetitorDetails> entrants)
    {
        static string Names(IEnumerable<CompetitorDetails> rows)
        {
            var names = rows.Select(x => $"{x.Values.Surname} {x.Values.FirstName}".Trim()).OrderBy(x => x, StringComparer.CurrentCulture).ToArray();
            return string.Join(", ", names.Take(8)) + (names.Length > 8 ? $" and {names.Length - 8} more" : "");
        }
        var label = competition.Values.ShortLabel;
        var missing = entrants.Where(x => x.Values.Gender is null).ToArray();
        if (CompetitionGenderRule.Required(competition.Values.Calendar?.Gender) is { } required)
        {
            var wrong = entrants.Where(x => x.Values.Gender is { } g && g != required).ToArray();
            var parts = new List<string>();
            if (wrong.Length > 0)
            {
                parts.Add($"{label} is a {(required == Gender.Male ? "men's" : "women's")} competition, but {wrong.Length} entered competitor{(wrong.Length == 1 ? " is" : "s are")} "
                    + $"{GenderLabels.Format(wrong[0].Values.Gender)}: {Names(wrong)}.");
            }
            if (missing.Length > 0) { parts.Add($"No gender for: {Names(missing)}."); }
            return string.Join(" ", parts) + " Correct the entries or genders in Competitors, then draw; nobody has been filtered out.";
        }
        var counts = entrants.GroupBy(x => x.Values.Gender).OrderBy(x => x.Key)
            .Select(x => $"{x.Count()} {(x.Key is null ? "without gender" : GenderLabels.Format(x.Key))}");
        return $"FIS competitions must contain either Men or Women; {label} has {string.Join(" and ", counts)} entered. "
            + "Set the competition gender in Competitions or correct the entries in Competitors, then draw; nobody has been filtered out.";
    }

    private FisPointsListInfo? _drawPointsList;
    private (CompetitionDetails Competition, CompetitorDeskDetails Desk, HashSet<Guid> Entered)? _drawPreviewSource;
    private string _drawPointsIssue = string.Empty;

    // One saved FIS points list for the entered competitors, or why the draw cannot use one. Competitors without saved
    // points (for example without a Code) draw without points.
    private static (FisPointsListInfo? List, string Issue) DrawPointsSource(CompetitorDeskDetails desk, IEnumerable<Guid> entrantIds)
    {
        var lists = entrantIds.Select(desk.FisPointsOf).OfType<CompetitorFisPoints>()
            .GroupBy(x => x.ListCode, StringComparer.OrdinalIgnoreCase).OrderByDescending(x => x.Count()).ToArray();
        if (lists.Length == 0)
        { return (null, "No FIS points are saved for the entered competitors. In Competitors, open Update from FIS, choose Update from FIS list and Save."); }
        if (lists.Length > 1)
        {
            return (null, "The entered competitors have points from different FIS lists ("
                + string.Join(", ", lists.Select(x => $"{x.Key}: {x.Count()}")) + "). In Competitors, choose Update from FIS list and Save.");
        }
        return desk.PointsList(lists[0].Key) is { } list ? (list, string.Empty)
            : (null, $"FIS points list {lists[0].Key} is not saved in this series. In Competitors, choose Update from FIS list and Save.");
    }

    private void ShowDrawPointsList()
    {
        DrawPointsListText = string.Empty; DrawPointsListWarning = false;
        if (DrawCompetition is not { } competition || !IsDrawFis || DrawRun != 1) { return; }
        var date = competition.Values.Date;
        static string Valid(PointsListSource list) => $"valid {list.ValidFrom:yyyy-MM-dd}–{list.ValidTo:yyyy-MM-dd}";
        if (DrawRevision is { } revision)
        {
            var used = revision.Plan.PointsList;
            DrawPointsListWarning = !used.IsEffectiveOn(date);
            DrawPointsListText = $"Drawn with FIS points list {used.Code}, {Valid(used)}"
                + (DrawPointsListWarning ? $": NOT VALID on the competition date {date:yyyy-MM-dd}." : ".");
            return;
        }
        if (_drawPointsList is not { } saved)
        {
            DrawPointsListWarning = true;
            DrawPointsListText = _drawPointsIssue;
            return;
        }
        var source = saved.ToSource();
        DrawPointsListWarning = !source.IsEffectiveOn(date);
        DrawPointsListText = $"The draw will use the saved points of FIS list {saved.Code} ({saved.Name}), {Valid(source)}"
            + (DrawPointsListWarning ? $": NOT VALID on the competition date {date:yyyy-MM-dd}. To use another list, download it, choose Update from FIS list in Competitors and Save; or draw with this list." : ".");
    }

    [RelayCommand] private void ShowDrawList() => IsResultInputOpen = false;
    [RelayCommand] private void ShowRunInput() => IsResultInputOpen = true;

    [RelayCommand]
    private async Task RefreshDrawMenuAsync()
    {
        DrawMenu.Clear();
        await GuardAsync(async () =>
        {
            foreach (var competition in Competitions.ToArray())
            {
                var desk = await workspace.ReadStartListsAsync(competition.Id);
                var runs = Enumerable.Range(1, competition.Values.RunCount)
                    .Where(run => IsRunAvailable(desk, run)).ToArray();
                DrawMenu.Add(new(competition, runs));
            }
        });
    }

    private static bool IsRunAvailable(StartListDesk desk, int run) => run == 1
        || desk.Revisions.Any(x => x.Plan.RunNumber == run)
        || desk.Revisions.Any(x => x.Plan.RunNumber == run - 1 && x.StartedAt is not null);

    [RelayCommand]
    private async Task OpenDrawRunAsync(DrawDestination destination)
    {
        if (IsDrawBusy || !CanLeaveDrawInput()) { return; }
        var competition = Competitions.FirstOrDefault(x => x.Id == destination.Competition.Id);
        if (competition is null || destination.Run < 1 || destination.Run > competition.Values.RunCount) { return; }
        var available = false;
        await GuardAsync(async () => available = IsRunAvailable(await workspace.ReadStartListsAsync(competition.Id), destination.Run));
        if (!available) { SetStatus("The previous run has not started yet.", error: true); return; }
        SwitchSection(WorkspaceSection.Draw);
        if (!IsDrawSection) { return; }
        _settingDraw = true;
        DrawCompetition = competition;
        DrawRun = destination.Run;
        _settingDraw = false;
        await LoadDrawAsync();
        SetActiveRace(competition, destination.Run, WorkspaceSection.Draw);
    }

    partial void OnDrawRevisionChanged(StartListRevision? value) => ShowDrawRevision();
    partial void OnIsDrawBusyChanged(bool value) => NotifyDraw();
    partial void OnDrawReverseCountChanged(int value)
    {
        if (!IsDrawBusy && IsLaterDrawRun && _sourceRun is not null) { HasUnsavedRunInput = true; }
    }
    partial void OnHasUnsavedRunInputChanged(bool value) => NotifyDraw();

    public bool CanLeaveDrawInput()
    {
        if (!HasUnsavedRunInput) { return true; }
        SetStatus("Create the start list to save the Run 1 input and reversal setting, or discard changes before leaving this run.", error: true);
        return false;
    }

    [RelayCommand]
    private async Task DiscardRunInputAsync()
    {
        if (!HasUnsavedRunInput || !await dialogs.ConfirmDiscardChangesAsync(DrawResults.Count)) { return; }
        HasUnsavedRunInput = false;
        await LoadDrawAsync();
    }

    private async Task LoadDrawAsync()
    {
        var load = ++_drawLoad;
        HasUnsavedRunInput = false;
        var competition = DrawCompetition;
        DrawEntries.Clear();
        DrawStartListRows.Clear();
        DrawResults.Clear();
        DrawRevision = null;
        _sourceRun = null;
        _sourceTiming = null;
        _sourceTimingVersion = null;
        DrawHasChangedEntries = false;
        DrawRunStarted = false;
        DrawEntryIssue = string.Empty;
        _drawPointsList = null; _drawPointsIssue = string.Empty; _drawPreviewSource = null;
        _drawGender = null;
        NotifyDraw();
        if (competition is null || !workspace.IsOpen) { DrawState = "Choose a competition"; return; }
        IsDrawBusy = true;
        await GuardAsync(async () =>
        {
            var desk = await workspace.ReadStartListsAsync(competition.Id);
            var competitors = await workspace.ReadCompetitorDeskAsync();
            if (load != _drawLoad) { return; }
            _drawDesk = desk;
            _desk = competitors;
            var enteredIds = competitors.Participations.Where(x => x.CompetitionId == competition.Id && x.Participates).Select(x => x.CompetitorId).ToHashSet();
            var entrants = competitors.Competitors.Where(x => enteredIds.Contains(x.Id)).ToArray();
            var genders = entrants.Select(x => x.Values.Gender).Distinct().ToArray();
            _drawGender = genders.Length == 1 && genders[0] is Gender.Male or Gender.Female ? genders[0] : null;
            (_drawPointsList, _drawPointsIssue) = DrawPointsSource(competitors, enteredIds);
            _drawPreviewSource = (competition, competitors, enteredIds);
            DrawEntryIssue = entrants.Length == 0 ? "No competitors entered in this competition."
                : _drawGender is null ? competition.Values.RaceType == RaceType.Fis
                    ? MixedEntriesReason(competition, entrants)
                    : "Local mixed competitions use category draws. That rule profile is not implemented yet; nobody has been filtered out."
                : string.Empty;
            if (!IsRunAvailable(desk, DrawRun)) { _settingDraw = true; DrawRun = 1; _settingDraw = false; }
            if (_current is not null) { _current = _current with { Revision = competitors.Revision }; }
            var lists = desk.Revisions.Where(x => x.Plan.RunNumber == DrawRun).OrderByDescending(x => x.Revision).ToArray();
            var first = desk.Revisions.Where(x => x.Plan.RunNumber == 1)
                .OrderByDescending(x => x.Revision).FirstOrDefault();
            _sourceRun = first;
            if (DrawRun > 1 && first is not null)
            {
                var captured = await workspace.ReadTimingAsync(first.Id);
                if (captured.Sessions.Count > 0)
                {
                    _sourceTiming = TimingReplay.Restore(captured, new Devices.AlgeDecoderFactory());
                    _sourceTimingVersion = TimingReplay.InputVersion(captured);
                }
            }
            FirstDrawGroup = first?.Plan.Options.FirstGroup ?? 15;
            DrawReverseCount = lists.FirstOrDefault()?.Plan.Options.ReverseCount ?? first?.Plan.Options.ReverseCount ?? 30;
            // The last bib assignment of the Run 1 list (a recorded change, otherwise the draw settings).
            var bibChange = first?.Plan.Changes?.LastOrDefault(x => x.Kind == StartListChangeKind.AssignBibs);
            DrawFirstBib = bibChange?.FirstBib ?? first?.Plan.Options.FirstBib ?? 1;
            var skipped = bibChange is not null ? bibChange.SkippedBibs : first?.Plan.Options.SkippedBibs;
            DrawSkippedBibsText = string.Join(", ", skipped ?? []);
            DrawRunStarted = lists.Any(x => x.StartedAt is not null)
                || desk.Revisions.Any(x => x.Plan.RunNumber > DrawRun);
            DrawRevision = lists.FirstOrDefault();
            ShowDrawRevision();
            IsResultInputOpen = DrawRun > 1 && DrawRevision is null;
        });
        if (load == _drawLoad) { IsDrawBusy = false; NotifyDraw(); }
    }

    private void ShowDrawRevision()
    {
        DrawResults.Clear();
        if (DrawRun > 1 && _sourceRun is { } source)
        {
            foreach (var entry in source.Plan.Entries)
            {
                var row = new ResultInputRow(entry);
                var result = DrawRevision?.Plan.SourceResults.FirstOrDefault(x => x.CompetitorId == row.CompetitorId);
                if (result is not null) { row.Time = RunResultInput.FormatTime(result.Hundredths); row.Status = result.Status.ToString(); }
                var measured = _sourceTiming?.Results.FirstOrDefault(x => x.CompetitorId == row.CompetitorId);
                if (measured is not null) { row.Time = RunResultInput.FormatTime(measured.Hundredths); row.Status = measured.Status.ToString(); }
                row.PropertyChanged += (_, _) => HasUnsavedRunInput = true;
                DrawResults.Add(row);
            }
        }
        DrawEntries.Clear();
        DrawStartListRows.Clear();
        if (DrawRevision is { } revision)
        {
            var sourceTimes = revision.Plan.SourceResults.ToDictionary(x => x.CompetitorId, x => x.Hundredths);
            // Points decide the Run 1 draw only; Run 2 follows Run 1 results.
            var equalPoints = revision.Plan.RunNumber == 1 ? FisStartOrder.EqualPointsCompetitors(revision.Plan.Entries) : new HashSet<Guid>();
            foreach (var entry in revision.Plan.Entries)
            {
                DrawEntries.Add(entry);
                DrawStartListRows.Add(new(entry, revision.Plan.RunNumber == 1 ? ""
                    : RunResultInput.FormatTime(sourceTimes[entry.Entrant.CompetitorId]), equalPoints.Contains(entry.Entrant.CompetitorId)));
            }
            DrawState = DrawRunStarted ? "Run started" : "Start list ready";
            DrawListInfo = $"{revision.Plan.Entries.Count} starters" + (IsDrawFis ? $" · FIS list {revision.Plan.PointsList.Code}" : "")
                + (equalPoints.Count > 0 ? " · Bold: equal FIS points, order decided by the draw" : "");
            DrawHelp = DrawRunStarted ? "Starting order is locked; select the next run from Start lists."
                : revision.HasCapture ? "Timing capture has begun. The start list is locked; the run starts with the first assigned start impulse."
                : "Start list saved. The run starts automatically with the first assigned start impulse.";
            var reference = revision.Plan.RunNumber == 1 ? revision.Plan.Entries : _sourceRun?.Plan.Entries ?? revision.Plan.Entries;
            var entered = _desk?.Participations.Where(x => x.CompetitionId == revision.Plan.CompetitionId && x.Participates)
                .Select(x => x.CompetitorId).ToHashSet() ?? [];
            var athletes = _desk?.Competitors.Where(x => entered.Contains(x.Id)).ToArray() ?? [];
            DrawHasChangedEntries = athletes.Length != reference.Count || reference.Any(x => !athletes.Any(a => a.Id == x.Entrant.CompetitorId && a.Values == x.Entrant.Athlete));
            if (DrawHasChangedEntries) { DrawHelp = "Registration changed after this list was prepared. Check the entries before using the list."; }
        }
        else
        {
            DrawState = DrawRun == 1 ? "Waiting for draw" : "Waiting for Run 1 results";
            DrawHelp = DrawRun == 1 ? "FIS standard draw: first group, points order, then competitors without points."
                : _sourceRun is null ? "Draw Run 1 before preparing the next run." : "Enter Run 1 times/statuses and choose the reversal, then create the start list. Bibs stay unchanged.";
            DrawListInfo = string.Empty;
        }
        if (DrawEntryIssue.Length > 0) { DrawHelp = DrawEntryIssue; }
        ShowDrawPointsList();
        if (_drawPreviewSource is { } preview) { LoadDrawPreview(preview.Competition, preview.Desk, preview.Entered); }
        else { DrawPreviewRows.Clear(); OnPropertyChanged(nameof(ShowDrawPreview)); }
        if (DrawRun > 1 && _sourceTiming is { } measuredSource)
        {
            DrawHelp = !measuredSource.Complete ? "Run 1 is incomplete. Finish or classify every starter in Timing."
                : DrawRevision is { } saved && !saved.Plan.SourceResults.SequenceEqual(measuredSource.ToRunFinishes())
                    ? "Run 1 timing changed after this starting order was created. Recreate the start list before racing."
                    : "Run 1 results loaded from Timing. Choose the reversal and create the start list.";
        }
        DrawContext = DrawCompetition is { } c
            ? $"{c.Values.ShortLabel}  /  Run {DrawRun} of {c.Values.RunCount}{CompetitionCodexLabel(c.Values)}"
            : "Choose a competition";
        NotifyDraw();
    }

    private void NotifyDraw()
    {
        OnPropertyChanged(nameof(IsDrawFis)); OnPropertyChanged(nameof(ShowDrawPoints));
        OnPropertyChanged(nameof(IsFirstDrawRun)); OnPropertyChanged(nameof(IsLaterDrawRun));
        OnPropertyChanged(nameof(CanPrepareDraw)); OnPropertyChanged(nameof(DrawBlockReason)); OnPropertyChanged(nameof(DrawActionLabel));
        OnPropertyChanged(nameof(CanEditStartList)); OnPropertyChanged(nameof(CanAssignBibs)); OnPropertyChanged(nameof(ShowDrawPreview));
        OnPropertyChanged(nameof(CanExportDraw)); OnPropertyChanged(nameof(HasDrawSource));
        OnPropertyChanged(nameof(WindowTitle));
        OnPropertyChanged(nameof(CanEditDrawResults));
        OnPropertyChanged(nameof(HasCapturedRunInput)); OnPropertyChanged(nameof(CanPasteDrawResults)); OnPropertyChanged(nameof(RunInputHelp));
    }

    [RelayCommand]
    private async Task PrepareDrawAsync()
    {
        if (DrawBlockReason is { Length: > 0 } reason) { SetStatus(reason, error: true); return; }
        if (DrawCompetition is not { } competition || _drawDesk is null) { return; }
        IsDrawBusy = true;
        await GuardAsync(async () =>
        {
            EnsureDeskClean(includeDrawInput: false);
            StartListPlan plan;
            if (DrawRun == 1)
            {
                // The draw uses the FIS points saved in the series, so it does not depend on this computer's downloaded list.
                var desk = await workspace.ReadCompetitorDeskAsync();
                if (desk.Revision != _drawDesk.SeriesRevision) { throw new SeriesConflictException(); }
                var ids = desk.Participations.Where(x => x.CompetitionId == competition.Id && x.Participates).Select(x => x.CompetitorId).ToHashSet();
                var (list, issue) = DrawPointsSource(desk, ids);
                if (list is null) { throw new DomainValidationException(issue); }
                var discipline = CompetitorFisPoints.DisciplineCode(competition.Values.Discipline);
                var entrants = desk.Competitors.Where(x => ids.Contains(x.Id))
                    .Select(x => new DrawEntrant(x.Id, x.Values, desk.FisPointsOf(x.Id)?.For(discipline))).ToArray();
                plan = FisStartOrder.FirstRun(competition.Id, competition.Values, _drawGender!.Value, entrants,
                    list.ToSource(), new(FirstDrawGroup, 30, DrawFirstBib, SkippedBibsOrNull()), Convert.ToHexString(RandomNumberGenerator.GetBytes(16)));
            }
            else
            {
                if (DrawRun != 2 || _sourceRun is null) { throw new DomainValidationException("This profile currently supports Run 2 only."); }
                plan = FisStartOrder.SecondRun(_sourceRun, _sourceTiming?.ToRunFinishes() ?? DrawResults.Select(x => x.Read()).ToArray(), DrawReverseCount);
            }
            await workspace.SaveStartListAsync(new(plan, _drawDesk.SeriesRevision, Environment.UserName,
                DrawRun == 1 ? "Draw" : "Run 2 start order", DateTimeOffset.UtcNow, _sourceTimingVersion));
            await LoadDrawAsync();
            SetStatus(DrawRun == 1 && IsDrawFis && DrawPointsListText.Length > 0 ? "Start list saved. " + DrawPointsListText : "Start list saved.");
        });
        IsDrawBusy = false;
    }

    private IReadOnlyList<int>? SkippedBibsOrNull() => FisStartOrder.ParseBibList(DrawSkippedBibsText) is { Count: > 0 } bibs ? bibs : null;

    // Assigns bibs again in the current start order from the first bib, leaving out the skipped bibs.
    [RelayCommand]
    private Task AssignBibsAsync()
    {
        IReadOnlyList<int>? skipped;
        try { skipped = SkippedBibsOrNull(); }
        catch (DomainValidationException ex) { SetStatus(ex.Message, error: true); return Task.CompletedTask; }
        // The change records every skipped bib; the reason keeps the operator's compact notation within its 200 characters.
        var reason = $"Bibs assigned from {DrawFirstBib}" + (skipped is null ? "" : $", skipping {DrawSkippedBibsText.Trim()}");
        if (reason.Length > 200) { reason = reason[..197] + "..."; }
        return SaveStartListChangeAsync(new(StartListChangeKind.AssignBibs, reason, FirstBib: DrawFirstBib, SkippedBibs: skipped), reason);
    }

    // Moves the selected competitor to another start position (for example a penalty); the bib stays with the competitor.
    [RelayCommand]
    private Task MoveDrawEntryAsync()
    {
        if (SelectedDrawRow is not { } row) { SetStatus("Select a competitor in the start list first.", error: true); return Task.CompletedTask; }
        if (!int.TryParse(DrawMoveToPosition, NumberStyles.None, CultureInfo.InvariantCulture, out var position))
        { SetStatus("Enter the new start position as a number.", error: true); return Task.CompletedTask; }
        var entry = row.Entry;
        return SaveStartListChangeAsync(new(StartListChangeKind.Move, DrawMoveReason, entry.Entrant.CompetitorId, position),
            $"Bib {entry.Bib} {entry.Entrant.Athlete.Surname} moved from position {entry.Position} to {position}: {DrawMoveReason.Trim()}");
    }

    private async Task SaveStartListChangeAsync(StartListChange change, string revisionReason)
    {
        if (DrawRevision is not { } revision || _drawDesk is null) { SetStatus("Create the start list first.", error: true); return; }
        if (!CanEditStartList) { SetStatus("The start list is locked: timing has begun or the next run refers to it.", error: true); return; }
        IsDrawBusy = true;
        await GuardAsync(async () =>
        {
            // Unsaved Run 1 results or reversal edits would be lost by reloading after this change: save or discard them first.
            if (HasUnsavedRunInput)
            { throw new DomainValidationException("Create the start list to apply the changed Run 1 results or reversal, or discard them, before editing the start order."); }
            EnsureDeskClean(includeDrawInput: false);
            var plan = FisStartOrder.Apply(revision.Plan, change);
            await workspace.SaveStartListAsync(new(plan, _drawDesk.SeriesRevision, Environment.UserName,
                revisionReason.Length > 200 ? revisionReason[..200] : revisionReason, DateTimeOffset.UtcNow, revision.SourceTimingVersion));
            if (change.Kind == StartListChangeKind.Move) { DrawMoveToPosition = string.Empty; DrawMoveReason = string.Empty; }
            await LoadDrawAsync();
            SetStatus($"Start list saved · {revisionReason}");
        });
        IsDrawBusy = false;
    }

    // Before the Run 1 draw: every entered competitor with saved points for the discipline, best points first; the
    // loaded FIS list's points beside them show saved points that differ from that list.
    private void LoadDrawPreview(CompetitionDetails competition, CompetitorDeskDetails desk, HashSet<Guid> entered)
    {
        DrawPreviewRows.Clear(); DrawPreviewInfo = string.Empty;
        if (DrawRun != 1 || DrawRevision is not null) { OnPropertyChanged(nameof(ShowDrawPreview)); return; }
        var discipline = CompetitorFisPoints.DisciplineCode(competition.Values.Discipline);
        EnsureFisListLoaded();
        var list = _fisList;
        var rows = desk.Competitors.Where(x => entered.Contains(x.Id)).Select(x =>
        {
            var saved = desk.FisPointsOf(x.Id)?.For(discipline);
            decimal? listed = null;
            if (list is not null && x.Values.FederationCode is { } code && list.Athletes.TryGetValue(code, out var athlete)
                && athlete.Points?.TryGetValue(discipline, out var value) == true) { listed = value; }
            return (Competitor: x, Saved: saved, Listed: listed);
        }).OrderBy(x => x.Saved is null).ThenBy(x => x.Saved).ThenBy(x => x.Competitor.Values.FederationCode, StringComparer.Ordinal).ToArray();
        for (var i = 0; i < rows.Length; i++)
        { DrawPreviewRows.Add(new(i + 1, rows[i].Competitor, rows[i].Saved, rows[i].Listed, list is not null)); }
        var differing = DrawPreviewRows.Count(x => x.DiffersFromList);
        DrawPreviewInfo = $"{rows.Length} entered · {discipline} points saved in the series, best first"
            + (list is null ? " · No FIS list loaded for comparison"
                : differing == 0 ? $" · Same as loaded list {list.ListCode}"
                : $" · {differing} differ from loaded list {list.ListCode} (red). Update from FIS list in Competitors and Save to use it.");
        OnPropertyChanged(nameof(ShowDrawPreview));
    }

    [RelayCommand]
    private async Task PasteDrawResultsAsync()
    {
        await GuardAsync(async () =>
        {
            if (!CanPasteDrawResults || _sourceRun is null || entryExchange is null) { return; }
            var text = await entryExchange.ReadClipboardAsync();
            if (string.IsNullOrWhiteSpace(text)) { throw new DomainValidationException("Copy Bib, Time and optional Status columns first."); }
            var results = RunResultInput.ParseTsv(text, _sourceRun).ToDictionary(x => x.CompetitorId);
            foreach (var row in DrawResults)
            { var result = results[row.CompetitorId]; row.Time = RunResultInput.FormatTime(result.Hundredths); row.Status = result.Status.ToString(); }
            SetStatus("Run 1 input pasted. Review it before preparing Run 2; no timing data was changed.");
        });
    }

    [RelayCommand]
    private async Task ExportDrawAsync()
    {
        await GuardAsync(async () =>
        {
            if (!CanExportDraw || DrawRevision is not { } revision) { return; }
            var path = await dialogs.ChooseStartListExportAsync($"{SafeFileName(DrawCompetition!.Values.ShortLabel)}-run{DrawRun}", false);
            if (path is null) { return; }
            await ExportFile.WriteTextAsync(path, StartListExchange.ToTsv(revision));
            SetStatus("Start list exported as TSV.");
        });
    }

    [RelayCommand]
    private async Task PrintDrawAsync()
    {
        await GuardAsync(async () =>
        {
            if (!CanExportDraw || DrawRevision is not { } revision) { return; }
            var path = await dialogs.ChooseStartListExportAsync($"{SafeFileName(DrawCompetition!.Values.ShortLabel)}-run{DrawRun}", true);
            if (path is null) { return; }
            var display = revision with { Plan = revision.Plan with { Competition = DrawCompetition.Values } };
            await ExportFile.WriteTextAsync(path, StartListExchange.ToPrintHtml(display));
            SetStatus($"Print-ready start list saved to {path}. Open it in a browser and print with Ctrl+P.");
        });
    }
}
