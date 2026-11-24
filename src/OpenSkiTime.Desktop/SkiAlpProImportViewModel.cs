using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenSkiTime.Application;
using OpenSkiTime.Domain;

namespace OpenSkiTime.Desktop;

/// <summary>Where the file is imported. A null competition creates a new one.</summary>
public sealed record SkiAlpProTargetChoice(CompetitionDetails? Competition)
{
    public override string ToString() => Competition is null ? "New competition"
        : $"{Competition.Values.ShortLabel} · Codex {Competition.Values.FisCode ?? "—"} · {Competition.Values.Date:dd.MM.yyyy}";
}

public sealed record SkiAlpProScopeChoice(SkiAlpProScope Scope)
{
    public override string ToString() => SkiAlpProImport.ScopeLabel(Scope);
}

/// <summary>
/// One opened Ski-Alp-Pro file: its statistics and the choices of the test import. Import runs exactly what the choices show.
/// </summary>
public sealed partial class SkiAlpProImportViewModel : ObservableObject
{
    private readonly Func<Guid, Task<SkiAlpProReplacement>> _readReplacement;
    private readonly Func<SkiAlpProScope, Guid?, string, Task<string>> _import;
    private int _load;

    public SkiAlpProImportViewModel(SkiAlpProRace race, IReadOnlyList<CompetitionDetails> competitions,
        Func<Guid, Task<SkiAlpProReplacement>> readReplacement, Func<SkiAlpProScope, Guid?, string, Task<string>> import)
    {
        ArgumentNullException.ThrowIfNull(race); ArgumentNullException.ThrowIfNull(competitions);
        Race = race;
        _readReplacement = readReplacement;
        _import = import;
        Statistics = race.Runs.Select(SkiAlpProRunStatistics.Of).ToArray();
        Scopes = SkiAlpProImport.AvailableScopes(race).Select(x => new SkiAlpProScopeChoice(x)).ToArray();
        _scope = Scopes.Count == 0 ? null : Scopes[^1];
        Targets = competitions.Select(x => new SkiAlpProTargetChoice(x)).Append(new(Competition: null)).ToArray();
        // The competition with the file's codex; without one the operator is asked, and the file's own codex is the default.
        var match = SkiAlpProImport.Target(race, competitions);
        CodexFound = match is not null;
        _newCodex = race.Codex;
        _target = Targets.First(x => x.Competition?.Id == match?.Id);
        Status = Scopes.Count == 0 ? "The file has no competitors to import." : string.Empty;
    }

    public SkiAlpProRace Race { get; }
    public IReadOnlyList<SkiAlpProRunStatistics> Statistics { get; }
    public IReadOnlyList<SkiAlpProScopeChoice> Scopes { get; }
    public IReadOnlyList<SkiAlpProTargetChoice> Targets { get; }
    public bool CodexFound { get; }
    public string FileText => Race.FileName + (Race.TimesFileName is null ? " (no times file)" : " + " + Race.TimesFileName);
    public string CodexText => Race.CodexText.Length == 0 ? "—" : Race.CodexText;
    public string RaceText => Race.Title.Length == 0 ? "—" : Race.Title;
    public string DateText => Race.Date.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);
    public string DisciplineText => $"{Race.Discipline.DisplayName()} · {Race.RunCount} run{(Race.RunCount == 1 ? "" : "s")}";
    public string LocationText => Race.Location.Length == 0 ? "—" : Race.Location;
    public string CompetitorsText
    {
        get
        {
            int Count(Gender? gender) => Race.Competitors.Count(x => x.Gender == gender);
            var nations = Race.Competitors.Select(x => x.Nation.ToUpperInvariant()).Where(x => x.Length > 0).Distinct().Count();
            return $"{Race.Competitors.Count} ({Count(Gender.Male)} men, {Count(Gender.Female)} women) · {nations} nation{(nations == 1 ? "" : "s")}";
        }
    }
    public string PointsListText => Race.PointsList.Length == 0 ? "—" : Race.PointsList;
    public string TargetNotice => CodexFound ? string.Empty : $"Codex {CodexText} is not in this event series. Choose where to import.";
    public bool HasTargetNotice => !CodexFound;
    public string Notices => string.Join(Environment.NewLine, Race.Notices);
    public bool HasNotices => Race.Notices.Count > 0;
    public bool IsNewTarget => Target?.Competition is null;

    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(ImportCommand))] private SkiAlpProScopeChoice? _scope;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsNewTarget))] [NotifyCanExecuteChangedFor(nameof(ImportCommand))] private SkiAlpProTargetChoice? _target;
    [ObservableProperty] private string _newCodex;
    [ObservableProperty] private string _replacementText = string.Empty;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ConfirmText))] [NotifyCanExecuteChangedFor(nameof(ImportCommand))] private bool _needsConfirmation;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(ImportCommand))] private bool _isConfirmed;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(ImportCommand))] private bool _isBusy;
    [ObservableProperty] private string _status;
    [ObservableProperty] private bool _isError;
    /// <summary>Set when the import has been saved; the dialog closes.</summary>
    [ObservableProperty] private bool _isDone;
    public string ConfirmText => NeedsConfirmation ? $"Replace all data of {Target?.Competition?.Values.ShortLabel}" : string.Empty;

    partial void OnTargetChanged(SkiAlpProTargetChoice? value) => _ = LoadReplacementAsync();

    /// <summary>Shows what the chosen competition would lose. A change of target always needs a new confirmation.</summary>
    public async Task LoadReplacementAsync()
    {
        var load = ++_load;
        IsConfirmed = false; NeedsConfirmation = false; ReplacementText = string.Empty; IsError = false;
        if (Scopes.Count > 0) { Status = string.Empty; }
        if (Target?.Competition is not { } competition) { return; }
        IsBusy = true;
        try
        {
            var replaced = await _readReplacement(competition.Id);
            if (load != _load) { return; }
            NeedsConfirmation = true;
            ReplacementText = replaced.Any
                ? $"{competition.Values.ShortLabel} has {replaced.Entries} entries, {replaced.StartLists} start lists, {replaced.TimingSessions} timing sessions with {replaced.RawPackets} inputs and "
                    + $"{replaced.TimingChanges} timing changes, {replaced.ApprovedResults} approved results and {replaced.TimingReports} timing report records. All of it is replaced. "
                    + "A complete copy of the event series is saved next to the file first."
                    + (replaced.DeviceSessions > 0 ? " IT INCLUDES TIMING CAPTURED FROM REAL DEVICES." : "")
                : $"{competition.Values.ShortLabel} has no entries or timing yet. A complete copy of the event series is saved next to the file first.";
        }
        catch (Exception ex) when (IsReported(ex)) { if (load == _load) { Status = Message(ex); IsError = true; } }
        finally { if (load == _load) { IsBusy = false; } }
    }

    private bool CanImport() => Scope is not null && Target is not null && !IsBusy && !IsDone && (IsNewTarget || (NeedsConfirmation && IsConfirmed));

    [RelayCommand(CanExecute = nameof(CanImport))]
    private async Task ImportAsync()
    {
        if (!CanImport()) { return; }
        IsBusy = true; IsError = false; Status = "Importing…";
        try
        {
            Status = await _import(Scope!.Scope, Target!.Competition?.Id, NewCodex.Trim());
            IsDone = true;
        }
        catch (Exception ex) when (IsReported(ex)) { Status = Message(ex); IsError = true; }
        finally { IsBusy = false; }
    }

    private static bool IsReported(Exception ex) => ex is SeriesFileException or SeriesConflictException or DomainValidationException
        or IOException or UnauthorizedAccessException or InvalidOperationException;
    private static string Message(Exception ex) => ex is SeriesFileException or SeriesConflictException or DomainValidationException ? ex.Message
        : "The operation failed. Check the files and try again.";
}
