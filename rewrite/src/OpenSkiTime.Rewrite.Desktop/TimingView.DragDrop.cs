using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace OpenSkiTime.Rewrite.Desktop;

public sealed partial class TimingView
{
    public const string CompetitorDragFormat = "OpenSkiTime.TimingCompetitor";
    private TimingDragCompetitor? _pressedCompetitor;
    private PointerPressedEventArgs? _press;
    private Point _pressPoint;
    private bool _dragging;
    private Control? _dropHighlight;
    public Task PendingTimingDrop { get; private set; } = Task.CompletedTask;

    private void ConfigureDragging()
    {
        DragDrop.SetAllowDrop(this, true);
        DragDrop.SetAllowDrop(this.FindControl<Border>("StartDropTarget")!, true);
        DragDrop.SetAllowDrop(this.FindControl<DataGrid>("TimestampsGrid")!, true);
        AddHandler(PointerPressedEvent, TrackDragStart, RoutingStrategies.Tunnel);
        AddHandler(PointerMovedEvent, StartDrag, RoutingStrategies.Tunnel);
        AddHandler(PointerReleasedEvent, (_, _) => { _pressedCompetitor = null; _press = null; }, RoutingStrategies.Tunnel);
        AddHandler(DragDrop.DragOverEvent, ShowDropTarget);
        AddHandler(DragDrop.DragLeaveEvent, (_, _) => HighlightDrop(null));
        AddHandler(DragDrop.DropEvent, DropCompetitor);
    }

    private static IEnumerable<Visual> Ancestry(object? source) => source is Visual visual
        ? new[] { visual }.Concat(visual.GetVisualAncestors()) : [];

    private void TrackDragStart(object? sender, PointerPressedEventArgs e)
    {
        _pressedCompetitor = null; _press = null;
        if (_viewModel is not { } vm || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) { return; }
        var ancestors = Ancestry(e.Source).ToArray();
        var row = ancestors.OfType<DataGridRow>().FirstOrDefault()?.DataContext;
        var bib = row switch { TimingGridRow racer => (int?)racer.Bib, TimingTimestampRow timestamps => timestamps.Bib, _ => null };
        if (bib is null) { return; }
        _pressedCompetitor = vm.CreateTimingDrag(bib.Value); _press = e; _pressPoint = e.GetPosition(this);
    }

    private async void StartDrag(object? sender, PointerEventArgs e)
    {
        if (_dragging || _pressedCompetitor is not { } competitor || _press is null || _viewModel is not { } vm) { return; }
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) { _pressedCompetitor = null; _press = null; return; }
        var delta = e.GetPosition(this) - _pressPoint;
        if (Math.Abs(delta.X) < 6 && Math.Abs(delta.Y) < 6) { return; }
        var trigger = _press;
        _pressedCompetitor = null; _press = null;
        _dragging = true; vm.IsTimingDragging = true;
        vm.TimingDropHint = $"Dragging Bib {competitor.Bib} · {competitor.Name}";
        string? dragError = null;
        try
        {
            var data = new DataObject(); data.Set(CompetitorDragFormat, competitor);
            e.Pointer.Capture(null);
            await DragDrop.DoDragDrop(trigger, data, DragDropEffects.Move);
            await PendingTimingDrop;
        }
        catch (InvalidOperationException ex) { dragError = $"Could not drag Bib {competitor.Bib}: {ex.Message}"; }
        catch (NotSupportedException ex) { dragError = $"Could not drag Bib {competitor.Bib}: {ex.Message}"; }
        finally
        {
            HighlightDrop(null); _dragging = false; vm.IsTimingDragging = false;
            vm.TimingDropHint = dragError ?? "Drag a competitor to At start or a timestamp.";
        }
    }

    private static (Control? Target, TimingTimestampCell? Cell, bool ToStart) DropTarget(object? source)
    {
        var ancestors = Ancestry(source).ToArray();
        if (ancestors.OfType<Control>().FirstOrDefault(x => x.Name == "StartDropTarget") is { } start) { return (start, null, true); }
        if (ancestors.OfType<Border>().FirstOrDefault(x => x.Tag is TimingTimestampCell) is { Tag: TimingTimestampCell cell } border)
        { return (border, cell, false); }
        return (null, null, false);
    }

    private void HighlightDrop(Control? control)
    {
        if (_dropHighlight == control) { return; }
        _dropHighlight?.Classes.Remove("timingDropTarget");
        _dropHighlight = control;
        _dropHighlight?.Classes.Add("timingDropTarget");
    }

    private void ShowDropTarget(object? sender, DragEventArgs e)
    {
        e.DragEffects = DragDropEffects.None;
        if (_viewModel is not { } vm || e.Data.Get(CompetitorDragFormat) is not TimingDragCompetitor item) { return; }
        var target = DropTarget(e.Source);
        var problem = target.Target is null ? "Drop on At start or a recorded timestamp." : vm.TimingDropProblem(item, target.Cell, target.ToStart);
        HighlightDrop(problem is null ? target.Target : null);
        if (problem is null)
        {
            e.DragEffects = DragDropEffects.Move;
            vm.TimingDropHint = target.ToStart ? $"Bib {item.Bib} → next at start"
                : $"Bib {item.Bib} → {target.Cell!.Position} {target.Cell.Time}"
                    + (target.Cell.Review.Bib is { } previous && previous != item.Bib ? $" · removes it from Bib {previous}" : "");
        }
        else { vm.TimingDropHint = problem; }
        e.Handled = true;
    }

    private async void DropCompetitor(object? sender, DragEventArgs e)
    {
        e.DragEffects = DragDropEffects.None;
        HighlightDrop(null);
        if (_viewModel is not { } vm || e.Data.Get(CompetitorDragFormat) is not TimingDragCompetitor item) { return; }
        var target = DropTarget(e.Source);
        if (target.Target is null) { return; }
        e.Handled = true;
        if (vm.TimingDropProblem(item, target.Cell, target.ToStart) is not null) { return; }
        PendingTimingDrop = vm.DropTimingCompetitorAsync(item, target.Cell, target.ToStart);
        await PendingTimingDrop;
        if (!vm.IsError) { e.DragEffects = DragDropEffects.Move; }
    }
}
