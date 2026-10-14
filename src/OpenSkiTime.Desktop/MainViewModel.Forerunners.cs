using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenSkiTime.Application;
using OpenSkiTime.Domain;
using OpenSkiTime.Timing;

namespace OpenSkiTime.Desktop;

// Forerunners of the timing run. They are the run's forerunners in the race information (shown in Results and the
// result XML), so there is one list: editing them here or in Results changes the same data. Timing puts them in the
// start queue before the competitors and times them, but never ranks them or shows their time.
public sealed partial class MainViewModel
{
    public ObservableCollection<ForerunnerEditor> TimingForerunners { get; } = [];
    [ObservableProperty] private ForerunnerEditor? _selectedTimingForerunner;
    [ObservableProperty] private bool _showForerunnerEditor;
    [ObservableProperty] private string _forerunnerSummary = "No forerunners";
    public bool CanCopyForerunnersFromRunOne => TimingRun > 1;

    private static TimingForerunner ToTiming(RaceForerunner runner)
        => new(runner.Letter, runner.Person.FirstName, runner.Person.LastName, runner.Person.Nation);

    private async Task<RaceInformation> ReadRaceInformationForAsync(CompetitionDetails competition)
        => ((await workspace.ReadRaceInformationAsync(competition.Id))?.Values ?? RaceInformation.Empty(competition.Values))
            .WithCompetitionCourse(competition.Values);

    // Reads the run's saved forerunners into the timing queues and, unless an open editor holds a draft, the editor.
    private async Task LoadTimingForerunnersAsync(bool keepEditorDraft = false)
    {
        if (TimingCompetition is not { } competition || workspace.Timing is not { ListId: { } listId } timing) { return; }
        var run = TimingRun;
        var file = workspace.FilePath;
        var information = await ReadRaceInformationForAsync(competition);
        // A load that finishes after the operator moved to another run or file is stale: it must not touch the editor.
        if (TimingRun != run || TimingCompetition?.Id != competition.Id || workspace.Timing?.ListId != listId || workspace.FilePath != file) { return; }
        var runners = information.Runs.FirstOrDefault(x => x.Number == run)?.Forerunners ?? [];
        if (!keepEditorDraft)
        {
            TimingForerunners.Clear();
            foreach (var runner in runners)
            { TimingForerunners.Add(new() { Letter = runner.Letter, FirstName = runner.Person.FirstName, LastName = runner.Person.LastName, Nation = runner.Person.Nation }); }
        }
        ForerunnerSummary = runners.Count == 0 ? "No forerunners" : $"Forerunners: {string.Join(", ", runners.Select(x => x.Letter))}";
        await timing.SetForerunnersAsync(listId, runners.Select(ToTiming).ToArray());
        OnPropertyChanged(nameof(CanCopyForerunnersFromRunOne));
    }

    [RelayCommand]
    private void AddTimingForerunner()
    {
        var used = TimingForerunners.Select(x => x.Letter.Trim().ToUpperInvariant()).ToHashSet();
        var letter = Enumerable.Range('A', 26).Select(x => ((char)x).ToString()).FirstOrDefault(x => !used.Contains(x)) ?? $"F{TimingForerunners.Count + 1}";
        var runner = new ForerunnerEditor { Letter = letter, Nation = _current?.Values.Nation ?? "" };
        TimingForerunners.Add(runner);
        SelectedTimingForerunner = runner;
        ShowForerunnerEditor = true;
    }

    [RelayCommand]
    private void RemoveTimingForerunner()
    {
        if (SelectedTimingForerunner is { } runner) { TimingForerunners.Remove(runner); SelectedTimingForerunner = TimingForerunners.LastOrDefault(); }
    }

    // Forerunners are often the same in both runs: copy Run 1's into the editor (Save keeps them for this run).
    [RelayCommand]
    private async Task CopyForerunnersFromRunOneAsync() => await GuardAsync(async () =>
    {
        if (TimingCompetition is not { } competition || TimingRun <= 1) { return; }
        var run = TimingRun;
        var file = workspace.FilePath;
        var information = await ReadRaceInformationForAsync(competition);
        if (TimingRun != run || TimingCompetition?.Id != competition.Id || workspace.FilePath != file)
        { throw new DomainValidationException("The timing run changed before Run 1's forerunners were copied. Nothing was copied."); }
        var first = information.Runs.FirstOrDefault(x => x.Number == 1)?.Forerunners ?? [];
        if (first.Count == 0) { SetStatus("Run 1 has no forerunners to copy.", error: true); return; }
        TimingForerunners.Clear();
        foreach (var runner in first)
        { TimingForerunners.Add(new() { Letter = runner.Letter, FirstName = runner.Person.FirstName, LastName = runner.Person.LastName, Nation = runner.Person.Nation }); }
        SetStatus($"Copied {first.Count} forerunners from Run 1. Save to use them in Run {TimingRun}.");
    });

    [RelayCommand]
    private async Task SaveTimingForerunnersAsync() => await GuardAsync(async () =>
    {
        if (TimingCompetition is not { } competition || workspace.Timing is not { ListId: { } listId }) { return; }
        // The run being edited, fixed before any await: switching runs meanwhile must not save into the other run.
        var run = TimingRun;
        var file = workspace.FilePath;
        void EnsureSameRun()
        {
            if (TimingRun != run || TimingCompetition?.Id != competition.Id || workspace.Timing?.ListId != listId || workspace.FilePath != file)
            { throw new DomainValidationException($"The timing run changed before the Run {run} forerunners were saved. Nothing was saved."); }
        }
        var runners = TimingForerunners.Select(x => x.Values).ToArray();
        TimingForerunner.Validate(runners.Select(ToTiming).ToArray());
        // Edits waiting in Results are saved first (and must save), then this run's forerunners replace the saved ones.
        if (!await FlushRaceInformationAsync())
        { throw new DomainValidationException("Correct the unsaved Results changes first; the forerunners were not saved."); }
        // The revision is read before the information, so a Results save in between makes this save a conflict
        // instead of overwriting that save with an older copy.
        var current = await workspace.ReadAsync();
        var information = await ReadRaceInformationForAsync(competition);
        EnsureSameRun();
        if (!information.Runs.Any(x => x.Number == run)) { throw new DomainValidationException($"Run {run} is not part of this competition."); }
        var updated = information with { Runs = information.Runs.Select(x => x.Number == run ? x with { Forerunners = runners } : x).ToArray() };
        updated.Validate(competition.Values.RunCount);
        // The save and the queue change happen together under the capture state gate: no impulse is attributed while
        // the save runs, and timing changes only after it committed (a failed save leaves timing unchanged).
        EnsureSameRun();
        var timing = workspace.Timing ?? throw new DomainValidationException("The timing run changed. Nothing was saved.");
        var revision = 0L;
        await timing.SetForerunnersAsync(listId, runners.Select(ToTiming).ToArray(), async token =>
            revision = await workspace.SaveRaceInformationAsync(competition.Id, updated, current.Revision, DateTimeOffset.UtcNow, token));
        if (_current is not null) { _current = _current with { Revision = Math.Max(_current.Revision, revision) }; }
        // A Results draft queued meanwhile carries these forerunners too, so its later save cannot bring back the old
        // ones; a new generation makes an autosave already writing the older copy save again.
        if (_pendingInformation.TryGetValue((file, competition.Id), out var pending))
        {
            _pendingInformation[(file, competition.Id)] = (pending.Values with { Runs = pending.Values.Runs
                .Select(x => x.Number == run ? x with { Forerunners = runners } : x).ToArray() }, ++_informationGeneration);
        }
        InvalidateCompetitionInformation([competition.Id]);
        await LoadTimingForerunnersAsync();
        ShowForerunnerEditor = false;
        RefreshTiming();
        SetStatus(runners.Length == 0 ? $"Run {run}: no forerunners." : $"Run {run} forerunners saved: {string.Join(", ", runners.Select(x => x.Letter))}. They are shown in Results.");
    });

    [RelayCommand]
    private async Task CancelTimingForerunnersAsync() => await GuardAsync(async () =>
    {
        ShowForerunnerEditor = false;
        await LoadTimingForerunnersAsync();
    });

    partial void OnShowForerunnerEditorChanged(bool value) { if (value && TimingForerunners.Count == 0) { AddTimingForerunner(); } }
}
