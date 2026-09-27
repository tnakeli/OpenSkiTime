using System.Collections.ObjectModel;
using System.Security.Cryptography;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenSkiTime.Rewrite.Application;
using OpenSkiTime.Rewrite.Domain;

namespace OpenSkiTime.Rewrite.Desktop;

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
    public ObservableCollection<int> DrawRuns { get; } = [];
    public ObservableCollection<StartListRevision> DrawRevisions { get; } = [];
    public ObservableCollection<StartListEntry> DrawEntries { get; } = [];
    public ObservableCollection<ResultInputRow> DrawResults { get; } = [];
    public IReadOnlyList<string> DrawGenders { get; } = ["Women", "Men"];
    public IReadOnlyList<int> ReverseChoices { get; } = [30, 15];
    private CompetitionDetails? _drawCompetition;
    private string _drawGender = "Women";
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
            SetDrawRuns();
            _settingDraw = false;
            _ = LoadDrawAsync();
        }
    }
    public string DrawGender
    {
        get => _drawGender;
        set
        {
            if (value == _drawGender) { return; }
            if (!_settingDraw && !CanLeaveDrawInput()) { OnPropertyChanged(); return; }
            SetProperty(ref _drawGender, value);
            if (!_settingDraw) { _ = LoadDrawAsync(); }
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
    [ObservableProperty] private string _drawOperator = Environment.UserName;
    [ObservableProperty] private string _drawReason = "Initial draw";
    [ObservableProperty] private bool _drawJuryConfirmed;
    [ObservableProperty] private string _drawState = "Choose a competition";
    [ObservableProperty] private string _drawHelp = "Select the competition, gender and run to prepare its start list.";
    [ObservableProperty] private string _drawListInfo = string.Empty;
    [ObservableProperty] private string _drawContext = string.Empty;
    [ObservableProperty] private bool _drawHasChangedEntries;
    public bool IsFirstDrawRun => DrawRun == 1;
    public bool IsLaterDrawRun => DrawRun > 1;
    public bool CanPrepareDraw => DrawCompetition is not null && !IsDrawBusy && (DrawRun == 1 || _sourceRun is not null);
    public bool CanApproveDraw => DrawRevision is { IsApproved: false } && !IsDrawBusy && !DrawHasChangedEntries && !HasUnsavedRunInput
        && DrawRevision == DrawRevisions.FirstOrDefault() && DrawJuryConfirmed;
    public bool CanExportDraw => DrawRevision is { IsApproved: true } && !IsDrawBusy && !HasUnsavedRunInput;
    public bool HasDrawSource => _sourceRun is not null;
    private Gender ActiveDrawGender => DrawGender == "Women" ? Gender.Female : Gender.Male;
    [RelayCommand] private void ShowDrawList() => IsResultInputOpen = false;
    [RelayCommand] private void ShowRunInput() => IsResultInputOpen = true;

    [RelayCommand]
    private async Task ShowDrawAsync()
    {
        if (!CanLeaveDrawInput()) { return; }
        SwitchSection(WorkspaceSection.Draw);
        if (!IsDrawSection) { return; }
        _settingDraw = true;
        DrawCompetition = Competitions.FirstOrDefault(x => x.Id == DrawCompetition?.Id)
            ?? SelectedCompetition ?? Competitions.FirstOrDefault();
        SetDrawRuns();
        _settingDraw = false;
        await LoadDrawAsync();
    }

    partial void OnDrawRevisionChanged(StartListRevision? value) => ShowDrawRevision();
    partial void OnIsDrawBusyChanged(bool value) => NotifyDraw();
    partial void OnDrawJuryConfirmedChanged(bool value) => NotifyDraw();
    partial void OnHasUnsavedRunInputChanged(bool value) => NotifyDraw();

    public bool CanLeaveDrawInput()
    {
        if (!HasUnsavedRunInput) { return true; }
        SetStatus("Prepare a start-list draft to save the Run 1 input, or discard the input before leaving this run.", error: true);
        return false;
    }

    [RelayCommand]
    private async Task DiscardRunInputAsync()
    {
        if (!HasUnsavedRunInput || !await dialogs.ConfirmDiscardChangesAsync(DrawResults.Count)) { return; }
        HasUnsavedRunInput = false;
        await LoadDrawAsync();
    }

    private void SetDrawRuns()
    {
        DrawRuns.Clear();
        for (var i = 1; i <= (DrawCompetition?.Values.RunCount ?? 1); i++) { DrawRuns.Add(i); }
        if (!DrawRuns.Contains(DrawRun)) { DrawRun = 1; }
        OnPropertyChanged(nameof(DrawRun));
    }

    private async Task LoadDrawAsync()
    {
        var load = ++_drawLoad;
        HasUnsavedRunInput = false;
        var competition = DrawCompetition;
        DrawEntries.Clear();
        DrawResults.Clear();
        DrawRevisions.Clear();
        DrawRevision = null;
        _sourceRun = null;
        DrawJuryConfirmed = false;
        DrawHasChangedEntries = false;
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
            if (_current is not null) { _current = _current with { Revision = competitors.Revision }; }
            DrawRevisions.Clear();
            foreach (var revision in desk.Revisions.Where(x => x.Plan.Gender == ActiveDrawGender && x.Plan.RunNumber == DrawRun)
                .OrderByDescending(x => x.Revision)) { DrawRevisions.Add(revision); }
            var first = desk.Revisions.Where(x => x.Plan.Gender == ActiveDrawGender && x.Plan.RunNumber == 1)
                .OrderByDescending(x => x.Revision).FirstOrDefault();
            _sourceRun = first is { IsApproved: true } ? first : null;
            FirstDrawGroup = first?.Plan.Options.FirstGroup ?? 15;
            DrawReverseCount = first?.Plan.Options.ReverseCount ?? 30;
            DrawFirstBib = first?.Plan.Options.FirstBib ?? (desk.Revisions.Where(x => x.Plan.Gender != ActiveDrawGender && x.Plan.RunNumber == 1)
                .SelectMany(x => x.Plan.Entries).Select(x => x.Bib).DefaultIfEmpty(0).Max() + 1);
            DrawRevision = DrawRevisions.FirstOrDefault();
            DrawReason = DrawRevision is null ? "Initial draw" : string.Empty;
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
                row.PropertyChanged += (_, _) => { HasUnsavedRunInput = true; DrawJuryConfirmed = false; };
                DrawResults.Add(row);
            }
        }
        DrawEntries.Clear();
        if (DrawRevision is { } revision)
        {
            foreach (var entry in revision.Plan.Entries) { DrawEntries.Add(entry); }
            DrawState = revision.IsApproved ? "Approved" : "Draft · review before approval";
            DrawListInfo = $"v{revision.Revision} · {revision.Plan.Entries.Count} starters · FIS list {revision.Plan.PointsList.Code} · {revision.Operator} · {revision.Reason}";
            DrawHelp = revision.IsApproved ? "Frozen start list. Create a new revision to replace it; this version stays in history."
                : "Check the starting order and bibs, then approve this list for use at the start.";
            var reference = revision.Plan.RunNumber == 1 ? revision.Plan.Entries : _sourceRun?.Plan.Entries ?? revision.Plan.Entries;
            var entered = _desk?.Participations.Where(x => x.CompetitionId == revision.Plan.CompetitionId && x.Participates)
                .Select(x => x.CompetitorId).ToHashSet() ?? [];
            var athletes = _desk?.Competitors.Where(x => entered.Contains(x.Id) && x.Values.Gender == revision.Plan.Gender).ToArray() ?? [];
            DrawHasChangedEntries = athletes.Length != reference.Count || reference.Any(x => !athletes.Any(a => a.Id == x.Entrant.CompetitorId && a.Values == x.Entrant.Athlete));
            if (DrawHasChangedEntries) { DrawHelp = "Registration changed after this list was prepared. This is a saved snapshot; review the entries and create a new revision."; }
        }
        else
        {
            DrawState = DrawRun == 1 ? "Waiting for draw" : "Waiting for Run 1 results";
            DrawHelp = DrawRun == 1 ? "FIS standard draw: first group, points order, then competitors without points."
                : _sourceRun is null ? "Approve Run 1 before preparing the next run." : "Enter the classified Run 1 times/statuses below, then prepare Run 2. Bibs stay unchanged.";
            DrawListInfo = string.Empty;
        }
        DrawContext = DrawCompetition is { } c
            ? $"{c.Values.ShortLabel}  /  {DrawGender}  /  Run {DrawRun} of {c.Values.RunCount}  ·  {c.Values.Date:yyyy-MM-dd}  ·  Codex {c.Values.FisCode ?? c.Values.LocalRaceCode ?? "—"}"
            : "Choose a competition";
        NotifyDraw();
    }

    private void NotifyDraw()
    {
        OnPropertyChanged(nameof(IsFirstDrawRun)); OnPropertyChanged(nameof(IsLaterDrawRun));
        OnPropertyChanged(nameof(CanPrepareDraw)); OnPropertyChanged(nameof(CanApproveDraw));
        OnPropertyChanged(nameof(CanExportDraw)); OnPropertyChanged(nameof(HasDrawSource));
        OnPropertyChanged(nameof(WindowTitle));
    }

    [RelayCommand]
    private async Task PrepareDrawAsync()
    {
        if (!CanPrepareDraw || DrawCompetition is not { } competition || _drawDesk is null) { return; }
        IsDrawBusy = true;
        await GuardAsync(async () =>
        {
            EnsureDeskClean(includeDrawInput: false);
            if (!DrawJuryConfirmed) { throw new DomainValidationException("Confirm the entry list and Jury draw settings first."); }
            StartListPlan plan;
            if (DrawRun == 1)
            {
                EnsureFisListLoaded();
                var list = _fisList ?? throw new DomainValidationException("Download the appropriate FIS points list in Competitors → Update from FIS first.");
                var desk = await workspace.ReadCompetitorDeskAsync();
                if (desk.Revision != _drawDesk.SeriesRevision) { throw new SeriesConflictException(); }
                var ids = desk.Participations.Where(x => x.CompetitionId == competition.Id && x.Participates).Select(x => x.CompetitorId).ToHashSet();
                var discipline = competition.Values.Discipline switch { Discipline.Slalom => "SL", Discipline.GiantSlalom => "GS", Discipline.SuperG => "SG", Discipline.Downhill => "DH", _ => "AC" };
                var entrants = desk.Competitors.Where(x => ids.Contains(x.Id) && x.Values.Gender == ActiveDrawGender).Select(x =>
                {
                    decimal? points = null;
                    if (x.Values.FederationCode is { } code && list.Athletes.TryGetValue(code, out var athlete)
                        && athlete.Points?.TryGetValue(discipline, out var value) == true) { points = value; }
                    return new DrawEntrant(x.Id, x.Values, points);
                }).ToArray();
                plan = FisStartOrder.FirstRun(competition.Id, competition.Values, ActiveDrawGender, entrants,
                    new(list.ListCode, list.ValidFrom, list.ValidTo), new(FirstDrawGroup, DrawReverseCount, DrawFirstBib),
                    Convert.ToHexString(RandomNumberGenerator.GetBytes(16)));
            }
            else
            {
                if (DrawRun != 2 || _sourceRun is null) { throw new DomainValidationException("This profile currently supports Run 2 only."); }
                plan = FisStartOrder.SecondRun(_sourceRun, DrawResults.Select(x => x.Read()).ToArray());
            }
            await workspace.SaveStartListAsync(new(plan, _drawDesk.SeriesRevision, DrawOperator, DrawReason, DateTimeOffset.UtcNow));
            await LoadDrawAsync();
            SetStatus("Draft start list saved. Review it, then approve it.");
        });
        IsDrawBusy = false;
    }

    [RelayCommand]
    private async Task ApproveDrawAsync()
    {
        if (!CanApproveDraw || DrawRevision is not { } revision || _drawDesk is null) { return; }
        IsDrawBusy = true;
        await GuardAsync(async () =>
        {
            await workspace.ApproveStartListAsync(revision.Id, _drawDesk.SeriesRevision, DateTimeOffset.UtcNow);
            await LoadDrawAsync();
            SetStatus($"Run {DrawRun} start list approved. Export or print the approved version.");
        });
        IsDrawBusy = false;
    }

    [RelayCommand]
    private async Task PasteDrawResultsAsync()
    {
        await GuardAsync(async () =>
        {
            if (_sourceRun is null || entryExchange is null) { return; }
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
            var path = await dialogs.ChooseStartListExportAsync($"{SafeFileName(revision.Plan.Competition.ShortLabel)}-run{DrawRun}-v{revision.Revision}", false);
            if (path is null) { return; }
            await File.WriteAllTextAsync(path, StartListExchange.ToTsv(revision));
            SetStatus("Approved start list exported as TSV.");
        });
    }

    [RelayCommand]
    private async Task PrintDrawAsync()
    {
        await GuardAsync(async () =>
        {
            if (!CanExportDraw || DrawRevision is not { } revision) { return; }
            var path = await dialogs.ChooseStartListExportAsync($"{SafeFileName(revision.Plan.Competition.ShortLabel)}-run{DrawRun}-v{revision.Revision}", true);
            if (path is null) { return; }
            await File.WriteAllTextAsync(path, StartListExchange.ToPrintHtml(revision));
            SetStatus($"Print-ready start list saved to {path}. Open it in a browser and print with Ctrl+P.");
        });
    }
}
