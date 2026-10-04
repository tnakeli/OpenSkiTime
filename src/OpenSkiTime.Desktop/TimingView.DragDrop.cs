using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace OpenSkiTime.Desktop;

public sealed partial class TimingView
{
    public const string CompetitorDragFormat = "OpenSkiTime.TimingCompetitor";
    private TimingDragCompetitor? _pressedCompetitor;
    private PointerPressedEventArgs? _press;
    private Point _pressPoint;
    private bool _dragging;
    private Control? _dropHighlight;
    private string? _dropHighlightClass;
    public Task PendingTimingDrop { get; private set; } = Task.CompletedTask;

    private void ConfigureDragging()
    {
        DragDrop.SetAllowDrop(this, true);
        DragDrop.SetAllowDrop(this.FindControl<Border>("StartDropTarget")!, true);
        DragDrop.SetAllowDrop(this.FindControl<DataGrid>("AtStartGrid")!, true);
        DragDrop.SetAllowDrop(this.FindControl<DataGrid>("TimestampsGrid")!, true);
        foreach (var status in new[] { "DNS", "DNF", "DSQ", "NPS", "Clear" })
        { DragDrop.SetAllowDrop(this.FindControl<Button>("StatusDrop" + status)!, true); }
        AddHandler(PointerPressedEvent, TrackDragStart, RoutingStrategies.Tunnel);
        AddHandler(PointerMovedEvent, StartDrag, RoutingStrategies.Tunnel);
        AddHandler(PointerReleasedEvent, (_, _) => { _pressedCompetitor = null; _press = null; }, RoutingStrategies.Tunnel);
        AddHandler(DragDrop.DragOverEvent, ShowDropTarget);
        AddHandler(DragDrop.DragLeaveEvent, (_, _) => HighlightDrop(null));
        AddHandler(DragDrop.DropEvent, DropCompetitor);
    }

    private void EnableStartRowDrop(object? sender, DataGridRowEventArgs e)
        => DragDrop.SetAllowDrop(e.Row, true);

    private static IEnumerable<Visual> Ancestry(object? source) => source is Visual visual
        ? new[] { visual }.Concat(visual.GetVisualAncestors()) : [];

    private static Button? StatusTarget(object? source) => Ancestry(source).OfType<Button>()
        .FirstOrDefault(x => x.Classes.Contains("statusDrop"));

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

    private static (Control? Target, TimingTimestampCell? Cell, bool ToStart, int? QueueTargetBib,
        bool VisuallyAbove) DropTarget(object? source, DragEventArgs e)
    {
        var ancestors = Ancestry(source).ToArray();
        if (ancestors.OfType<DataGrid>().Any(x => x.Name == "AtStartGrid")
            && ancestors.OfType<DataGridRow>().FirstOrDefault() is { DataContext: TimingGridRow racer } row)
        { return (row, null, true, racer.Bib, e.GetPosition(row).Y < row.Bounds.Height / 2); }
        if (ancestors.OfType<Control>().FirstOrDefault(x => x.Name == "StartDropTarget") is { } start)
        { return (start, null, true, null, false); }
        if (ancestors.OfType<Border>().FirstOrDefault(x => x.Tag is TimingTimestampCell) is { Tag: TimingTimestampCell cell } border)
        { return (border, cell, false, null, false); }
        return (null, null, false, null, false);
    }

    private void HighlightDrop(Control? control, string className = "timingDropTarget")
    {
        if (control is null) { ShowDropPreview(null, null, null); }
        if (_dropHighlight == control && _dropHighlightClass == className) { return; }
        if (_dropHighlightClass is not null) { _dropHighlight?.Classes.Remove(_dropHighlightClass); }
        _dropHighlight = control;
        _dropHighlightClass = control is null ? null : className;
        if (_dropHighlightClass is not null) { _dropHighlight!.Classes.Add(_dropHighlightClass); }
    }

    private void ShowDropTarget(object? sender, DragEventArgs e)
    {
        e.DragEffects = DragDropEffects.None;
        if (_viewModel is not { } vm || e.Data.Get(CompetitorDragFormat) is not TimingDragCompetitor item) { return; }
        if (StatusTarget(e.Source) is { Tag: string status } button)
        {
            var statusProblem = vm.StatusDropProblem(item, status);
            HighlightDrop(statusProblem is null ? button : null, "timingStatusDropTarget");
            ShowDropPreview(null, null, null);
            vm.TimingDropHint = statusProblem ?? $"Bib {item.Bib} · {item.Name} → {status}";
            e.DragEffects = statusProblem is null ? DragDropEffects.Move : DragDropEffects.None;
            e.Handled = true;
            return;
        }
        var target = DropTarget(e.Source, e);
        var problem = target.Target is null ? "Drop on At start or a recorded timestamp."
            : target.QueueTargetBib is { } bib ? vm.StartQueueDropProblem(item, bib)
            : vm.TimingDropProblem(item, target.Cell, target.ToStart);
        var highlightClass = target.QueueTargetBib is not null
            ? target.VisuallyAbove ? "timingDropAbove" : "timingDropBelow" : "timingDropTarget";
        HighlightDrop(problem is null ? target.Target : null, highlightClass);
        ShowDropPreview(problem is null ? target.Target : null, item, target.Cell);
        if (problem is null)
        {
            e.DragEffects = DragDropEffects.Move;
            vm.TimingDropHint = target.QueueTargetBib is { } targetBib
                ? $"Bib {item.Bib} → {(target.VisuallyAbove ? "above" : "below")} Bib {targetBib} · {(target.VisuallyAbove ? "later" : "earlier")} start"
                : target.ToStart ? $"Bib {item.Bib} → next at start"
                : $"Bib {item.Bib} → {target.Cell!.Position} {target.Cell.Time}"
                    + (target.Cell.Review.Bib is { } previous && previous != item.Bib ? $" · removes it from Bib {previous}" : "");
        }
        else { vm.TimingDropHint = problem; }
        e.Handled = true;
    }

    // Shows what the dragged competitor would get for the hovered intermediate/finish timestamp.
    // Recalculated on every drag-over from the loaded snapshot (cheap, and never stale if impulses
    // arrive mid-drag); it writes nothing.
    private void ShowDropPreview(Control? target, TimingDragCompetitor? item, TimingTimestampCell? cell)
    {
        var popup = this.FindControl<Border>("TimingDropPreview")!;
        if (target is null || item is null || cell is null || _viewModel?.TimingDropPreviewFor(item, cell) is not { } preview)
        { popup.IsVisible = false; return; }
        this.FindControl<TextBlock>("TimingDropPreviewBib")!.Text = preview.Bib;
        this.FindControl<TextBlock>("TimingDropPreviewResult")!.Text = preview.Result;
        if (preview.IsValid) { popup.Classes.Remove("invalid"); } else if (!popup.Classes.Contains("invalid")) { popup.Classes.Add("invalid"); }
        popup.IsVisible = true;
        var layer = this.FindControl<Canvas>("TimingDropPreviewLayer")!;
        popup.Measure(Size.Infinity);
        var size = popup.DesiredSize;
        // Prefer just above the hovered cell so the cell, its row and the pointer stay visible.
        var origin = target.TranslatePoint(default, layer) ?? default;
        var y = origin.Y - size.Height - 2;
        if (y < 0) { y = origin.Y + target.Bounds.Height + 2; }
        var x = Math.Clamp(origin.X, 0, Math.Max(0, layer.Bounds.Width - size.Width));
        Canvas.SetLeft(popup, x);
        Canvas.SetTop(popup, y);
    }

    private async void DropCompetitor(object? sender, DragEventArgs e)
    {
        e.DragEffects = DragDropEffects.None;
        HighlightDrop(null);
        if (_viewModel is not { } vm || e.Data.Get(CompetitorDragFormat) is not TimingDragCompetitor item) { return; }
        if (StatusTarget(e.Source) is { Tag: string status })
        {
            e.Handled = true;
            if (vm.StatusDropProblem(item, status) is not null) { return; }
            PendingTimingDrop = vm.DropTimingStatusAsync(item, status);
            await PendingTimingDrop;
            if (!vm.IsError) { e.DragEffects = DragDropEffects.Move; }
            return;
        }
        var target = DropTarget(e.Source, e);
        if (target.Target is null) { return; }
        e.Handled = true;
        if (target.QueueTargetBib is { } bib)
        {
            if (vm.StartQueueDropProblem(item, bib) is not null) { return; }
            PendingTimingDrop = vm.DropStartQueueAsync(item, bib, target.VisuallyAbove);
        }
        else
        {
            if (vm.TimingDropProblem(item, target.Cell, target.ToStart) is not null) { return; }
            PendingTimingDrop = vm.DropTimingCompetitorAsync(item, target.Cell, target.ToStart);
        }
        await PendingTimingDrop;
        if (!vm.IsError) { e.DragEffects = DragDropEffects.Move; }
    }
}
