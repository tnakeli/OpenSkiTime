using System.Collections.Specialized;
using System.ComponentModel;
using OpenSkiTime.Application;
using OpenSkiTime.Domain;

namespace OpenSkiTime.Desktop;

public sealed partial class MainViewModel
{
    private readonly Dictionary<(string?, Guid), (RaceInformation Values, long Generation)> _pendingInformation = [];
    private readonly Dictionary<(string?, Guid), string> _informationErrors = [];
    private readonly Dictionary<(string?, Guid), FisPerson> _legacyDelegates = [];
    private readonly List<INotifyPropertyChanged> _informationObservers = [];
    private readonly List<INotifyCollectionChanged> _informationCollections = [];
    private readonly SemaphoreSlim _informationSaveGate = new(1, 1);
    private CancellationTokenSource? _informationSaveDelay;
    private Task _informationSaveTask = Task.CompletedTask;
    private readonly List<Task> _informationSaveTasks = [];
    private long _informationGeneration;

    private void StopInformationTracking()
    {
        foreach (var item in _informationObservers) { item.PropertyChanged -= InformationEditorChanged; }
        foreach (var item in _informationCollections) { item.CollectionChanged -= InformationCollectionChanged; }
        _informationObservers.Clear(); _informationCollections.Clear();
    }

    private void TrackInformationEditors()
    {
        StopInformationTracking();
        void Observe(INotifyPropertyChanged item) { _informationObservers.Add(item); item.PropertyChanged += InformationEditorChanged; }
        void Collection(INotifyCollectionChanged item) { _informationCollections.Add(item); item.CollectionChanged += InformationCollectionChanged; }
        Collection(ResultsJury); Collection(ResultsRuns);
        foreach (var member in ResultsJury) { Observe(member); }
        foreach (var run in ResultsRuns)
        {
            Observe(run); Observe(run.Setter); Collection(run.Forerunners);
            foreach (var runner in run.Forerunners) { Observe(runner); }
        }
    }

    private void InformationCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    { TrackInformationEditors(); QueueInformationSave(); }
    private void InformationEditorChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(RaceRunEditor.SelectedForerunner) or nameof(RaceRunEditor.ForecastTemperature)) { return; }
        QueueInformationSave();
    }

    private void QueueInformationSave()
    {
        if (_informationKey is not { } key || ResultsCompetition?.Id != key.Item2 || !workspace.IsOpen) { return; }
        _informationSaveDelay?.Cancel(); _informationSaveDelay?.Dispose();
        _informationSaveDelay = new();
        try
        {
            var values = CurrentRaceInformation();
            _pendingInformation[key] = (values, ++_informationGeneration); _informationErrors.Remove(key);
            RaceInformationStatus = "Saving changes…";
        }
        catch (DomainValidationException ex)
        {
            _pendingInformation.Remove(key); _informationErrors[key] = ex.Message;
            RaceInformationStatus = "Not saved: " + ex.Message;
            return;
        }
        RememberInformationDraft();
        _informationSaveTask = SaveInformationAfterPauseAsync(_informationSaveDelay.Token);
        _informationSaveTasks.RemoveAll(x => x.IsCompleted);
        _informationSaveTasks.Add(_informationSaveTask);
    }

    private async Task SaveInformationAfterPauseAsync(CancellationToken ct)
    {
        try { await Task.Delay(400, ct); await DrainInformationSavesAsync(); }
        catch (OperationCanceledException) { }
    }

    private async Task DrainInformationSavesAsync()
    {
        await _informationSaveGate.WaitAsync();
        try
        {
            foreach (var (key, draft) in _pendingInformation.ToArray())
            {
                if (workspace.FilePath != key.Item1) { continue; }
                try
                {
                    var current = await workspace.ReadAsync();
                    if (workspace.FilePath != key.Item1) { continue; }
                    var revision = await workspace.SaveRaceInformationAsync(key.Item2, draft.Values, current.Revision, DateTimeOffset.UtcNow);
                    if (_current is not null && workspace.FilePath == key.Item1) { _current = _current with { Revision = Math.Max(_current.Revision, revision) }; }
                    if (_pendingInformation.TryGetValue(key, out var latest) && latest.Generation == draft.Generation)
                    { _pendingInformation.Remove(key); _informationErrors.Remove(key); }
                    if (_informationKey == key && !_pendingInformation.ContainsKey(key) && !_informationErrors.ContainsKey(key))
                    { RaceInformationStatus = "All changes saved."; }
                }
                catch (Exception ex) when (ex is DomainValidationException or SeriesFileException or SeriesConflictException or IOException)
                {
                    _informationErrors[key] = ex.Message;
                    if (_informationKey == key) { RaceInformationStatus = "Not saved: " + ex.Message; }
                    SetStatus("Race information was not saved. " + ex.Message, error: true);
                }
            }
        }
        finally { _informationSaveGate.Release(); }
    }

    public async Task<bool> FlushRaceInformationAsync()
    {
        _informationSaveDelay?.Cancel();
        await _informationSaveTask;
        await DrainInformationSavesAsync();
        if (_pendingInformation.Count == 0 && _informationErrors.Count == 0) { return true; }
        var error = _informationErrors.FirstOrDefault();
        var race = _current?.Competitions.FirstOrDefault(x => x.Id == error.Key.Item2)?.Values.ShortLabel ?? "Race";
        SetStatus($"{race}: race information was not saved. {error.Value} Correct the fields or retry before closing or changing event files.", error: true);
        return false;
    }

    private void DisposeInformationAutosave()
    {
        StopInformationTracking(); _informationSaveDelay?.Cancel(); _informationSaveDelay?.Dispose();
        // Closing flushes writes before disposal; pending invalid fields prevent closing.
        if (_informationSaveTasks.All(x => x.IsCompleted)) { _informationSaveGate.Dispose(); }
        else { _ = Task.WhenAll(_informationSaveTasks).ContinueWith(_ => _informationSaveGate.Dispose(), TaskScheduler.Default); }
    }
}
