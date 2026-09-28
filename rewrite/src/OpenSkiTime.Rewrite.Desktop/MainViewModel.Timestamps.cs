using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using OpenSkiTime.Rewrite.Domain;
using OpenSkiTime.Rewrite.Timing;

namespace OpenSkiTime.Rewrite.Desktop;

public sealed partial class MainViewModel
{
    private Guid _timingDragWorkspace = Guid.NewGuid();
    private TimingSnapshot? _timestampSnapshot;
    public ObservableCollection<TimingTimestampRow> TimestampRows { get; } = [];
    [ObservableProperty] private bool _showIgnoredTimestamps;
    [ObservableProperty] private bool _isTimingDragging;
    [ObservableProperty] private string _timingDropHint = "Drag a competitor to At start or a timestamp.";
    partial void OnShowIgnoredTimestampsChanged(bool value) { _timestampSnapshot = null; RefreshTimestamps(); }
    partial void OnIsTimingDraggingChanged(bool value) { if (!value) { RefreshTimestamps(); } }

    private void RefreshTimestamps()
    {
        var snapshot = workspace.Timing?.Snapshot;
        if (IsTimingDragging || ReferenceEquals(snapshot, _timestampSnapshot)) { return; }
        _timestampSnapshot = snapshot;
        var rows = snapshot is null ? [] : TimingTimestampRow.Create(snapshot, TimingCheckpoints.Count, ShowIgnoredTimestamps);
        while (TimestampRows.Count > rows.Count) { TimestampRows.RemoveAt(TimestampRows.Count - 1); }
        for (var i = 0; i < rows.Count; i++)
        {
            if (i == TimestampRows.Count) { TimestampRows.Add(rows[i]); }
            else if (TimestampRows[i].Key != rows[i].Key || TimestampRows[i].Name != rows[i].Name || !TimestampRows[i].Cells.SequenceEqual(rows[i].Cells))
            { TimestampRows[i] = rows[i]; }
        }
    }

    public TimingDragCompetitor? CreateTimingDrag(int bib) => workspace.Timing?.Snapshot is { } snapshot
        && snapshot.Results.FirstOrDefault(x => x.Bib == bib) is { } result
        ? new(_timingDragWorkspace, snapshot.ListId, bib, result.Name, result.StartKey,
            snapshot.Observations.Where(x => x.Bib == bib && !x.Ignored && x.DuplicateOf is null && x.Observation.Kind == ObservationKind.Impulse)
                .Select(x => x.Observation.Key).ToArray()) : null;

    public bool IsCurrentTimingDrag(TimingDragCompetitor? item) => item is not null && item.Workspace == _timingDragWorkspace
        && item.ListId == workspace.Timing?.ListId && TimingRows.Any(x => x.Bib == item.Bib);

    public string? TimingDropProblem(TimingDragCompetitor item, TimingTimestampCell? cell, bool toStart)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!IsCurrentTimingDrag(item)) { return "Choose a competitor in the current run."; }
        if (toStart)
        {
            var result = workspace.Timing!.Snapshot!.Results.Single(x => x.Bib == item.Bib);
            return result.StartKey == item.StartKey ? null : "The competitor's start changed. Drag again.";
        }
        if (cell is null || cell.ListId != item.ListId) { return "Drop on a recorded timestamp."; }
        return null;
    }

    public Task DropTimingCompetitorAsync(TimingDragCompetitor item, TimingTimestampCell? cell, bool toStart) => GuardAsync(async () =>
    {
        if (TimingDropProblem(item, cell, toStart) is { } problem) { throw new DomainValidationException(problem); }
        var timing = workspace.Timing!;
        if (toStart) { await timing.MoveToStartAsync(item.ListId, item.Bib, item.StartKey, item.AssignedKeys, TimingOperator); }
        else { await timing.MoveTimestampAsync(item.ListId, item.Bib, cell!.Key, cell.Decision, item.AssignedKeys, TimingOperator); }
        SelectedTimingRow = TimingRows.FirstOrDefault(x => x.Bib == item.Bib);
        RefreshTiming();
        SetStatus(toStart ? $"Bib {item.Bib} restarted at start. Previous impulses retained." + (timing.IsHeld(0) ? " Start remains on hold." : " Next start is ready.")
            : $"{cell!.Position} {cell.Time} assigned to Bib {item.Bib}. Original timestamp retained.");
    });
}
