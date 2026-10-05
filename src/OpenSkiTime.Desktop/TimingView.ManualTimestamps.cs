using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace OpenSkiTime.Desktop;

// Manual time entry from the TIMESTAMPS grid: double-click a START/INTERM/FINISH cell, header or the empty space below
// it (or press F2 in that column) to type a missing time of day for that timing position.
public sealed partial class TimingView
{
    // Timing position (0 start, 1 finish, 2.. intermediates) of a TIMESTAMPS time cell, empty or not.
    public static readonly AttachedProperty<int?> TimestampChannelProperty =
        AvaloniaProperty.RegisterAttached<TimingView, Control, int?>("TimestampChannel");
    public static int? GetTimestampChannel(Control control)
    { ArgumentNullException.ThrowIfNull(control); return control.GetValue(TimestampChannelProperty); }
    public static void SetTimestampChannel(Control control, int? value)
    { ArgumentNullException.ThrowIfNull(control); control.SetValue(TimestampChannelProperty, value); }

    private MainViewModel? _manualTimestampViewModel;

    // Grid cell index (0 start, 1–20 intermediates, 21 finish) to timing position.
    internal static int TimestampChannel(int cellIndex) => cellIndex == 0 ? 0 : cellIndex == 21 ? 1 : cellIndex + 1;

    private void ConfigureManualTimestamps()
    {
        var grid = this.FindControl<DataGrid>("TimestampsGrid")!;
        // Tunnel on the second press: column headers handle pointer presses themselves, so DoubleTapped would miss them.
        grid.AddHandler(PointerPressedEvent, BeginManualTimestampFromCell, RoutingStrategies.Tunnel);
        grid.AddHandler(KeyDownEvent, BeginManualTimestampFromKey, RoutingStrategies.Tunnel);
        DataContextChanged += (_, _) => WatchManualTimestampEditor(DataContext as MainViewModel);
    }

    private void WatchManualTimestampEditor(MainViewModel? vm)
    {
        if (_manualTimestampViewModel is not null) { _manualTimestampViewModel.PropertyChanged -= OnManualTimestampEditorChanged; }
        _manualTimestampViewModel = vm;
        if (vm is not null) { vm.PropertyChanged += OnManualTimestampEditorChanged; }
    }

    private void OnManualTimestampEditorChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainViewModel.ShowManualTimestampEditor) || sender is not MainViewModel vm) { return; }
        // Typing starts immediately; closing returns focus to the grid the operator came from.
        Dispatcher.UIThread.Post(() =>
        {
            if (vm.ShowManualTimestampEditor)
            {
                var box = this.FindControl<TextBox>("ManualTimestampTime")!;
                box.Focus(NavigationMethod.Tab);
                box.SelectAll();
            }
            else { this.FindControl<DataGrid>("TimestampsGrid")!.Focus(); }
        }, DispatcherPriority.Input);
    }

    private void BeginManualTimestampFromCell(object? sender, PointerPressedEventArgs e)
    {
        if (_viewModel is not { } vm || sender is not DataGrid grid || e.ClickCount != 2
            || !e.GetCurrentPoint(grid).Properties.IsLeftButtonPressed) { return; }
        var cell = Ancestry(e.Source).OfType<Control>().FirstOrDefault(x => GetTimestampChannel(x) is not null);
        // Before the first timestamp there are no cells: the column under the pointer (header or empty space) decides.
        var channel = cell is not null ? GetTimestampChannel(cell) : ChannelAt(grid, e.GetPosition(grid).X);
        if (channel is null) { return; }
        e.Handled = true;
        vm.BeginManualTimestamp(channel.Value, cell?.Tag as TimingTimestampCell);
    }

    private static int? ChannelAt(DataGrid grid, double x) => grid.GetVisualDescendants().OfType<DataGridColumnHeader>()
        .Where(header => header.IsVisible && header.Bounds.Width > 0 && header.TranslatePoint(new Point(0, 0), grid) is { } origin
            && x >= origin.X && x < origin.X + header.Bounds.Width)
        .Select(header => ChannelOf(header.Content)).FirstOrDefault(channel => channel is not null);

    private static int? ChannelOf(object? header) => header switch
    {
        "START" => 0,
        "FINISH" => 1,
        string text when text.StartsWith("INTERM ", StringComparison.Ordinal)
            && int.TryParse(text.AsSpan("INTERM ".Length), out var number) => number + 1,
        _ => null
    };

    private void BeginManualTimestampFromKey(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.F2 || e.KeyModifiers != KeyModifiers.None || _viewModel is not { } vm
            || sender is not DataGrid grid || ChannelOf(grid.CurrentColumn?.Header) is not { } channel) { return; }
        e.Handled = true;
        var row = grid.SelectedItem as TimingTimestampRow;
        vm.BeginManualTimestamp(channel, row?.Cells.ElementAtOrDefault(channel switch { 0 => 0, 1 => 21, _ => channel - 1 }));
    }
}
