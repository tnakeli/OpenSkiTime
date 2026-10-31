using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;

namespace OpenSkiTime.Desktop;

/// <summary>
/// Forerunner entry for the timed run. The Timing view opens it while <see cref="MainViewModel.ShowForerunnerEditor"/>
/// is set; Save or Cancel clears the flag and the view closes the dialog. Closing the window any other way cancels the draft.
/// Rows are listed in start order: drag a row (or Ctrl+Up/Down) to change the order; Save keeps it.
/// </summary>
public sealed partial class TimingForerunnerDialog : Window
{
    private static readonly DataFormat<ForerunnerEditor> RunnerFormat = DataFormat.CreateInProcessFormat<ForerunnerEditor>("OpenSkiTime.Forerunner");
    private ForerunnerEditor? _pressed;
    private PointerPressedEventArgs? _press;
    private Point _pressPoint;
    private DataGridRow? _highlight;

    public TimingForerunnerDialog()
    {
        AvaloniaXamlLoader.Load(this);
        AddHandler(KeyDownEvent, (_, e) =>
        {
            // Esc inside a cell being edited only ends that edit (the grid handles it first).
            if (e.Key == Key.Escape && !e.Handled) { e.Handled = true; Close(); }
        });
        // A save in progress decides itself whether the editor closes.
        Closing += (_, e) => { if (DataContext is MainViewModel { SaveTimingForerunnersCommand.IsRunning: true, ShowForerunnerEditor: true }) { e.Cancel = true; } };
        var grid = this.FindControl<DataGrid>("ForerunnersGrid")!;
        DragDrop.SetAllowDrop(grid, true);
        grid.LoadingRow += (_, e) => DragDrop.SetAllowDrop(e.Row, true);
        grid.AddHandler(PointerPressedEvent, TrackPress, RoutingStrategies.Tunnel);
        grid.AddHandler(PointerMovedEvent, StartDrag, RoutingStrategies.Tunnel);
        grid.AddHandler(PointerReleasedEvent, (_, _) => { _pressed = null; _press = null; }, RoutingStrategies.Tunnel);
        grid.AddHandler(DragDrop.DragOverEvent, ShowDropRow);
        grid.AddHandler(DragDrop.DragLeaveEvent, (_, _) => Highlight(null, false));
        grid.AddHandler(DragDrop.DropEvent, DropRow);
        grid.AddHandler(KeyDownEvent, MoveWithKeys, RoutingStrategies.Tunnel);
    }

    internal static DataTransfer CreateRunnerDragData(ForerunnerEditor runner)
    {
        var data = new DataTransfer();
        data.Add(DataTransferItem.Create(RunnerFormat, runner));
        return data;
    }

    private static (DataGridRow? Row, ForerunnerEditor? Runner) RowAt(object? source)
    {
        var row = (source as Visual)?.GetSelfAndVisualAncestors().OfType<DataGridRow>().FirstOrDefault();
        return (row, row?.DataContext as ForerunnerEditor);
    }

    private void TrackPress(object? sender, PointerPressedEventArgs e)
    {
        _pressed = null; _press = null;
        // A press inside a cell being edited selects text; only a press on a row starts a drag.
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed
            || (e.Source as Visual)?.GetSelfAndVisualAncestors().OfType<TextBox>().Any() == true) { return; }
        if (RowAt(e.Source).Runner is { } runner) { _pressed = runner; _press = e; _pressPoint = e.GetPosition(this); }
    }

    private async void StartDrag(object? sender, PointerEventArgs e)
    {
        if (_pressed is not { } runner || _press is not { } trigger) { return; }
        var delta = e.GetPosition(this) - _pressPoint;
        if (Math.Abs(delta.X) < 6 && Math.Abs(delta.Y) < 6) { return; }
        _pressed = null; _press = null;
        try
        {
            using var data = CreateRunnerDragData(runner);
            e.Pointer.Capture(null);
            await DragDrop.DoDragDropAsync(trigger, data, DragDropEffects.Move);
        }
        finally { Highlight(null, false); }
    }

    // Drop above the upper half of a row (earlier start) or below its lower half (later start).
    private static (ForerunnerEditor Runner, DataGridRow Row, ForerunnerEditor Target, bool Above)? DropAt(DragEventArgs e)
    {
        if (e.DataTransfer.TryGetValue(RunnerFormat) is not { } runner || RowAt(e.Source) is not { Row: { } row, Runner: { } target }
            || ReferenceEquals(runner, target)) { return null; }
        return (runner, row, target, e.GetPosition(row).Y < row.Bounds.Height / 2);
    }

    private void ShowDropRow(object? sender, DragEventArgs e)
    {
        var drop = DropAt(e);
        e.DragEffects = drop is null ? DragDropEffects.None : DragDropEffects.Move;
        Highlight(drop?.Row, drop?.Above == true);
        e.Handled = true;
    }

    private void DropRow(object? sender, DragEventArgs e)
    {
        Highlight(null, false);
        e.Handled = true;
        if (DropAt(e) is not { } drop || DataContext is not MainViewModel vm) { e.DragEffects = DragDropEffects.None; return; }
        var from = vm.TimingForerunners.IndexOf(drop.Runner);
        var to = vm.TimingForerunners.IndexOf(drop.Target) + (drop.Above ? 0 : 1);
        vm.MoveTimingForerunner(drop.Runner, from < to ? to - 1 : to);
        e.DragEffects = DragDropEffects.Move;
    }

    private void MoveWithKeys(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers != KeyModifiers.Control || e.Key is not (Key.Up or Key.Down) || sender is not DataGrid grid
            || DataContext is not MainViewModel { SelectedTimingForerunner: { } runner } vm
            || (e.Source as Visual)?.GetSelfAndVisualAncestors().OfType<TextBox>().Any() == true) { return; }
        e.Handled = true;
        vm.MoveTimingForerunner(runner, vm.TimingForerunners.IndexOf(runner) + (e.Key == Key.Up ? -1 : 1));
        grid.ScrollIntoView(runner, null);
    }

    private void Highlight(DataGridRow? row, bool above)
    {
        if (_highlight is not null && !ReferenceEquals(_highlight, row)) { _highlight.Classes.Remove("timingDropAbove"); _highlight.Classes.Remove("timingDropBelow"); }
        _highlight = row;
        if (row is null) { return; }
        row.Classes.Set("timingDropAbove", above);
        row.Classes.Set("timingDropBelow", !above);
    }
}
