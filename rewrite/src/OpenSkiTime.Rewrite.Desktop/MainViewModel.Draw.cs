using System.Collections.ObjectModel;
using System.Security.Cryptography;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenSkiTime.Rewrite.Application;
using OpenSkiTime.Rewrite.Domain;

namespace OpenSkiTime.Rewrite.Desktop;

public sealed record DrawDestination(CompetitionDetails Competition, int Run);
public sealed record DrawMenuCompetition(CompetitionDetails Competition, IReadOnlyList<int> Runs);

public sealed partial class ResultInputRow(StartListEntry entry) : ObservableObject
{
    public Guid CompetitorId => entry.Entrant.CompetitorId;
    public int Bib => entry.Bib;
    public string Code => entry.Entrant.Athlete.FederationCode ?? string.Empty;
    public string Name => entry.Entrant.Athlete.Surname + " " + entry.Entrant.Athlete.FirstName;
    [ObservableProperty] private string _time = string.Empty;
    [ObservableProperty] private string _status = "Finished";
    public RunFinish Read()
    {
        if (!Enum.TryParse<FinishStatus>(Status, out var status) || !Enum.IsDefined(status))
        { throw new DomainValidationException($"Check status for bib {Bib}."); }
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
    public ObservableCollection<ResultInputRow> DrawResults { get; } = [];
    public IReadOnlyList<int> ReverseChoices { get; } = [30, 15];
    private CompetitionDetails? _drawCompetition;
    private Gender? _drawGender;
    private Timing.TimingSnapshot? _sourceTiming;
    private string? _sourceTimingVersion;
    public bool HasCapturedRunInput => _sourceTiming is not null;
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
    public bool IsFirstDrawRun => DrawRun == 1;
    public bool IsLaterDrawRun => DrawRun > 1;
    public bool CanPrepareDraw => DrawCompetition is not null && !IsDrawBusy && !DrawRunStarted
        && DrawEntryIssue.Length == 0 && (DrawRun == 1 || (DrawRun == 2 && _sourceRun is not null && (_sourceTiming is null || _sourceTiming.Complete)));
    public bool CanExportDraw => DrawRevision is not null && !IsDrawBusy && !HasUnsavedRunInput
        && (_sourceTiming is null || (_sourceTiming.Complete && _sourceTimingVersion is not null && TimingReplay.InputVersionMatches(DrawRevision.SourceTimingVersion, _sourceTimingVersion)));
    public bool HasDrawSource => _sourceRun is not null;
    public bool CanEditDrawResults => HasDrawSource && !DrawRunStarted && !IsDrawBusy;
    public bool CanMarkRunStarted => CanExportDraw && !DrawRunStarted && !DrawHasChangedEntries && DrawEntryIssue.Length == 0;
    public string DrawActionLabel => IsFirstDrawRun ? DrawRevision is null ? "Draw" : "Draw again" : "Create start list";
    public string DrawNavigationLabel => IsDrawSection && DrawCompetition is { } c
        ? $"4  Draw / Start lists · {c.Values.ShortLabel} / Run {DrawRun}  ▾" : "4  Draw / Start lists  ▾";
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
        DrawResults.Clear();
        DrawRevision = null;
        _sourceRun = null;
        _sourceTiming = null;
        _sourceTimingVersion = null;
        DrawHasChangedEntries = false;
        DrawRunStarted = false;
        DrawEntryIssue = string.Empty;
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
            DrawEntryIssue = entrants.Length == 0 ? "No competitors entered in this competition."
                : _drawGender is null ? competition.Values.RaceType == RaceType.Fis
                    ? "FIS competitions must contain either Men or Women. Check the competition entries; nobody has been filtered out."
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
            DrawFirstBib = first?.Plan.Options.FirstBib ?? 1;
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
        if (DrawRevision is { } revision)
        {
            foreach (var entry in revision.Plan.Entries) { DrawEntries.Add(entry); }
            DrawState = DrawRunStarted ? "Run started" : "Start list ready";
            DrawListInfo = $"{revision.Plan.Entries.Count} starters · FIS list {revision.Plan.PointsList.Code}";
            DrawHelp = DrawRunStarted ? "Run started. Starting order is locked; select the next run from the Draw / Start lists menu."
                : "Start list saved. Mark the run started when racing begins.";
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
        if (DrawRun > 1 && _sourceTiming is { } measuredSource)
        {
            DrawHelp = !measuredSource.Complete ? "Run 1 is incomplete. Finish or classify every starter in Timing."
                : DrawRevision is { } saved && !saved.Plan.SourceResults.SequenceEqual(measuredSource.ToRunFinishes())
                    ? "Run 1 timing changed after this starting order was created. Recreate the start list before racing."
                    : "Run 1 results loaded from Timing. Choose the reversal and create the start list.";
        }
        DrawContext = DrawCompetition is { } c
            ? $"{c.Values.ShortLabel}  /  Run {DrawRun} of {c.Values.RunCount}  ·  Codex {c.Values.FisCode ?? c.Values.LocalRaceCode ?? "—"}"
            : "Choose a competition";
        NotifyDraw();
    }

    private void NotifyDraw()
    {
        OnPropertyChanged(nameof(IsFirstDrawRun)); OnPropertyChanged(nameof(IsLaterDrawRun));
        OnPropertyChanged(nameof(CanPrepareDraw)); OnPropertyChanged(nameof(DrawActionLabel));
        OnPropertyChanged(nameof(CanExportDraw)); OnPropertyChanged(nameof(HasDrawSource));
        OnPropertyChanged(nameof(WindowTitle));
        OnPropertyChanged(nameof(DrawNavigationLabel)); OnPropertyChanged(nameof(CanMarkRunStarted));
        OnPropertyChanged(nameof(CanEditDrawResults));
        OnPropertyChanged(nameof(HasCapturedRunInput)); OnPropertyChanged(nameof(CanPasteDrawResults)); OnPropertyChanged(nameof(RunInputHelp));
    }

    [RelayCommand]
    private async Task PrepareDrawAsync()
    {
        if (!CanPrepareDraw || DrawCompetition is not { } competition || _drawDesk is null) { return; }
        IsDrawBusy = true;
        await GuardAsync(async () =>
        {
            EnsureDeskClean(includeDrawInput: false);
            StartListPlan plan;
            if (DrawRun == 1)
            {
                EnsureFisListLoaded();
                var list = _fisList ?? throw new DomainValidationException("Download the appropriate FIS points list in Competitors → Update from FIS first.");
                var desk = await workspace.ReadCompetitorDeskAsync();
                if (desk.Revision != _drawDesk.SeriesRevision) { throw new SeriesConflictException(); }
                var ids = desk.Participations.Where(x => x.CompetitionId == competition.Id && x.Participates).Select(x => x.CompetitorId).ToHashSet();
                var discipline = competition.Values.Discipline switch { Discipline.Slalom => "SL", Discipline.GiantSlalom => "GS", Discipline.SuperG => "SG", Discipline.Downhill => "DH", _ => "AC" };
                var entrants = desk.Competitors.Where(x => ids.Contains(x.Id)).Select(x =>
                {
                    decimal? points = null;
                    if (x.Values.FederationCode is { } code && list.Athletes.TryGetValue(code, out var athlete)
                        && athlete.Points?.TryGetValue(discipline, out var value) == true) { points = value; }
                    return new DrawEntrant(x.Id, x.Values, points);
                }).ToArray();
                plan = FisStartOrder.FirstRun(competition.Id, competition.Values, _drawGender!.Value, entrants,
                    new(list.ListCode, list.ValidFrom, list.ValidTo), new(FirstDrawGroup, 30, DrawFirstBib),
                    Convert.ToHexString(RandomNumberGenerator.GetBytes(16)));
            }
            else
            {
                if (DrawRun != 2 || _sourceRun is null) { throw new DomainValidationException("This profile currently supports Run 2 only."); }
                plan = FisStartOrder.SecondRun(_sourceRun, _sourceTiming?.ToRunFinishes() ?? DrawResults.Select(x => x.Read()).ToArray(), DrawReverseCount);
            }
            await workspace.SaveStartListAsync(new(plan, _drawDesk.SeriesRevision, Environment.UserName,
                DrawRun == 1 ? "Draw" : "Run 2 start order", DateTimeOffset.UtcNow, _sourceTimingVersion));
            await LoadDrawAsync();
            SetStatus("Start list saved.");
        });
        IsDrawBusy = false;
    }

    [RelayCommand]
    private async Task MarkRunStartedAsync()
    {
        if (!CanMarkRunStarted || DrawRevision is not { } revision || _drawDesk is null) { return; }
        IsDrawBusy = true;
        await GuardAsync(async () =>
        {
            await workspace.MarkRunStartedAsync(revision.Id, _drawDesk.SeriesRevision, Environment.UserName, DateTimeOffset.UtcNow);
            await LoadDrawAsync();
            SetStatus($"Run {DrawRun} marked started. This records run progress; it does not start timing capture.");
        });
        IsDrawBusy = false;
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
            await File.WriteAllTextAsync(path, StartListExchange.ToTsv(revision));
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
            await File.WriteAllTextAsync(path, StartListExchange.ToPrintHtml(display));
            SetStatus($"Print-ready start list saved to {path}. Open it in a browser and print with Ctrl+P.");
        });
    }
}
