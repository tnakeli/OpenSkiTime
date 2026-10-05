using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.Controls.Primitives;
using System.ComponentModel;
using System.Windows.Input;

namespace OpenSkiTime.Desktop;

public sealed partial class TimingView : UserControl
{
    private MainViewModel? _viewModel;
    private bool _synchronizingSelection;
    private (DataGrid Grid, int[] Bibs)? _contextSelection;
    private static readonly string[] GridNames = ["AtStartGrid", "RunningGrid", "TimestampsGrid", "RankingGrid"];
    private TimingAction[] _actions = [];
    private int _splitCount = -1;
    // One definition drives both the context menus and actual keyboard shortcuts.
    private sealed record TimingAction(string Header, string Gesture, ICommand Command, string? Parameter = null);

    public TimingView()
    {
        AvaloniaXamlLoader.Load(this);
        DataContextChanged += (_, _) => BindViewModel(DataContext as MainViewModel);
        AttachedToVisualTree += (_, _) => BindViewModel(DataContext as MainViewModel);
        DetachedFromVisualTree += (_, _) => BindViewModel(null);
        SizeChanged += (_, _) => Dispatcher.UIThread.Post(ScrollToNext, DispatcherPriority.Background);
        AddHandler(KeyDownEvent, OnTimingKey, RoutingStrategies.Tunnel);
        AddHandler(PointerPressedEvent, CaptureContextSelection, RoutingStrategies.Tunnel, handledEventsToo: true);
        ConfigureDragging();
        ConfigureManualTimestamps();
    }

    private void BindViewModel(MainViewModel? vm)
    {
        if (!ReferenceEquals(_viewModel, vm)) { InstallRankingColumnControls(vm); }
        if (_viewModel is not null) { _viewModel.PropertyChanged -= OnViewModelChanged; }
        if (!ReferenceEquals(_viewModel, vm)) { _splitCount = -1; }
        _viewModel = vm;
        if (vm is not null) { vm.PropertyChanged += OnViewModelChanged; BuildActions(vm); ConfigureColumns(); SynchronizeSelection(); }
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.HasTimingIntermediates)) { ConfigureColumns(); }
        if (e.PropertyName == nameof(MainViewModel.TimingChannelStates)) { UpdateChannelVisibility(); }
        if (e.PropertyName == nameof(MainViewModel.RaceQueueVersion))
        { Dispatcher.UIThread.Post(ScrollToNext, DispatcherPriority.Background); }
        if (e.PropertyName is nameof(MainViewModel.SelectedTimingRow) or nameof(MainViewModel.IsRefreshingTimingUi)
            && _viewModel?.IsRefreshingTimingUi == false) { SynchronizeSelection(); }
    }

    private void SynchronizeSelection()
    {
        // DataGrid may move selection when an item is replaced/removed. Keep every visual
        // representation on the operator's chosen bib before accepting another quick action.
        _synchronizingSelection = true;
        try
        {
            foreach (var name in GridNames)
            {
                var grid = this.FindControl<DataGrid>(name)!;
                var selected = _viewModel?.SelectedTimingBibs ?? [];
                var items = name == "TimestampsGrid"
                    ? grid.ItemsSource?.OfType<TimingTimestampRow>().Where(x => x.Bib is { } bib && selected.Contains(bib)).Cast<object>().ToArray() ?? []
                    : grid.ItemsSource?.OfType<TimingGridRow>().Where(x => selected.Contains(x.Bib)).Cast<object>().ToArray() ?? [];
                grid.SelectedItems.Clear();
                foreach (var item in items) { grid.SelectedItems.Add(item); }
            }
        }
        finally { _synchronizingSelection = false; }
    }

    private void ScrollToNext()
    {
        if (_viewModel is not { } vm || !IsVisible) { return; }
        ScrollLast("AtStartGrid", vm.AtStartRows.LastOrDefault());
        ScrollLast("RunningGrid", vm.RunningRows.LastOrDefault());
    }

    private void ScrollLast(string name, object? item)
    {
        if (item is null) { return; }
        var grid = this.FindControl<DataGrid>(name)!;
        grid.ScrollIntoView(item, null);
        var scroller = grid.GetVisualDescendants().OfType<ScrollViewer>()
            .FirstOrDefault(x => x.Extent.Height > x.Viewport.Height);
        if (scroller is not null)
        { scroller.Offset = new(scroller.Offset.X, scroller.Extent.Height - scroller.Viewport.Height); }
    }

    private void ConfigureColumns()
    {
        var count = _viewModel?.TimingCheckpoints.Count ?? 0;
        if (count == _splitCount) { UpdateChannelVisibility(); return; }
        _splitCount = count;
        var simulatorButtons = this.FindControl<StackPanel>("SimulatorIntermediateButtons")!;
        simulatorButtons.Children.Clear();
        var switches = this.FindControl<StackPanel>("TimingIntermediateSwitches")!;
        switches.Children.Clear();
        for (var i = 1; i <= count; i++)
        {
            var position = i;
            var channelSwitch = new ToggleButton { Content = $"I{i}", Command = _viewModel?.ToggleTimingChannelCommand,
                CommandParameter = $"intermediate:{i}" };
            channelSwitch.Bind(IsEnabledProperty, new Binding(nameof(MainViewModel.IsTimingConnected)) { Source = _viewModel });
            channelSwitch.Bind(ToggleButton.IsCheckedProperty, new Binding($"TimingChannelStates[{i + 1}]")
                { Source = _viewModel, Mode = BindingMode.OneWay });
            switches.Children.Add(channelSwitch);
            var button = new Button { Content = $"Test I{i}", Command = _viewModel?.SimulatePulseCommand,
                CommandParameter = $"intermediate:{i}" };
            button.Classes.Add("secondaryAction");
            button.Bind(IsEnabledProperty, new Binding(nameof(MainViewModel.IsTimingConnected)) { Source = _viewModel });
            simulatorButtons.Children.Add(button);
        }
        var grid = this.FindControl<DataGrid>("RunningGrid")!;
        var existing = grid.Columns.Count(x => x.Tag is "intermediate");
        for (var i = existing; i < count; i++)
        {
            grid.Columns.Insert(4 + i, new DataGridTextColumn
            { Header = $"INTERM {i + 1}", Binding = new Binding($"Intermediates[{i}]"), Width = new DataGridLength(82), Tag = "intermediate" });
        }
        var timestamps = this.FindControl<DataGrid>("TimestampsGrid")!;
        if (timestamps.Columns.Count == 2) { timestamps.Columns.Add(TimestampColumn(0, "START")); timestamps.Columns.Add(TimestampColumn(21, "FINISH")); }
        existing = timestamps.Columns.Count(x => x.Tag is "intermediate");
        for (var i = existing + 1; i <= count; i++)
        {
            timestamps.Columns.Insert(timestamps.Columns.Count - 1, TimestampColumn(i, $"INTERM {i}", "intermediate"));
        }
        UpdateChannelVisibility();
    }

    private static DataGridTemplateColumn TimestampColumn(int index, string header, string? tag = null)
    {
        return new DataGridTemplateColumn
            {
                Header = header, Width = new DataGridLength(150), Tag = tag,
                CellTemplate = new FuncDataTemplate<TimingTimestampRow>((row, _) =>
                {
                    var cell = row?.Cells.ElementAtOrDefault(index);
                    var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
                    content.Children.Add(new TextBlock
                    { Text = cell?.Time ?? "—", FontFamily = new("Cascadia Mono,Consolas,DejaVu Sans Mono,monospace"), FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
                    if (cell?.IsManual == true)
                    { content.Children.Add(new TextBlock { Text = "m", FontSize = 9, FontWeight = Avalonia.Media.FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, Classes = { "manualTimestampMark" } }); }
                    // The timestampCell style's transparent background makes empty cells hit-testable for manual entry.
                    var border = new Border { Tag = cell, Padding = new(6,0), Child = content };
                    SetTimestampChannel(border, TimestampChannel(index));
                    border.Classes.Add("timestampCell");
                    if (cell is not null) { DragDrop.SetAllowDrop(border, true); }
                    if (cell is not null) { ToolTip.SetTip(border, cell.Hint); }
                    return border;
                })
            };
    }

    private void UpdateChannelVisibility()
    {
        var count = _viewModel?.TimingCheckpoints.Count ?? 0;
        foreach (var name in new[] { "RunningGrid", "TimestampsGrid" })
        {
            var grid = this.FindControl<DataGrid>(name)!;
            var splitColumns = grid.Columns.Where(x => x.Tag is "intermediate").ToArray();
            for (var i = 0; i < splitColumns.Length; i++)
            { splitColumns[i].IsVisible = i < count; }
        }
    }

    private void DescribeCategory(object? sender, DataGridRowGroupHeaderEventArgs e)
    {
        e.RowGroupHeader.PropertyName = "Category";
    }

    private void BuildActions(MainViewModel vm)
    {
        _actions = [
            new("Next at start", "F5", vm.ExpectSelectedCommand, "start"),
            new("Move up in start order", "Ctrl+Up", vm.MoveStartRowCommand, "up"),
            new("Move down in start order", "Ctrl+Down", vm.MoveStartRowCommand, "down"),
            new("Next at finish", "F6", vm.ExpectSelectedCommand, "finish"),
            new("Back to start", "F8", vm.ReturnToStartCommand),
            new("DNS · did not start", "Ctrl+D", vm.ClassifyTimingCommand, "DNS"),
            new("DNF · did not finish", "Ctrl+F", vm.ClassifyTimingCommand, "DNF"),
            new("DSQ · disqualified", "Ctrl+Q", vm.ClassifyTimingCommand, "DSQ"),
            new("NPS · not permitted to start", "Ctrl+N", vm.ClassifyTimingCommand, "NPS"),
            new("Clear classification", "Ctrl+R", vm.ClassifyTimingCommand, "Clear"),
            new("Absent next starter · DNS", "Shift+F5", vm.NextStartDnsCommand),
            new("Hold start", "Ctrl+F5", vm.HoldTimingPositionCommand, "start"),
            new("Hold finish", "Ctrl+F6", vm.HoldTimingPositionCommand, "finish"),
            new("False finish · not a racer", "Ctrl+Back", vm.IgnoreLastFinishCommand),
        ];
        foreach (var name in GridNames)
        {
            var menu = new ContextMenu();
            foreach (var action in _actions.Where(x => x.Command == vm.ClassifyTimingCommand))
            {
                var item = new MenuItem { Header = action.Header, InputGesture = KeyGesture.Parse(action.Gesture), Command = action.Command, CommandParameter = action.Parameter };
                menu.Items.Add(item);
            }
            this.FindControl<DataGrid>(name)!.ContextMenu = menu;
        }
    }

    private void OnTimingKey(object? sender, KeyEventArgs e)
    {
        if (e.Source is not Avalonia.Visual source) { return; }
        // Never run race shortcuts while the operator is typing outside the race grids.
        var grid = source as DataGrid ?? source.GetVisualAncestors().OfType<DataGrid>().FirstOrDefault();
        if (grid is null || !GridNames.Contains(grid.Name)) { return; }
        if (e.Key == Key.F10 && e.KeyModifiers == KeyModifiers.Shift)
        { grid.ContextMenu?.Open(grid); e.Handled = true; return; }
        var action = _actions.FirstOrDefault(x => KeyGesture.Parse(x.Gesture).Matches(e));
        if (action is null) { return; }
        if (grid.Name != "AtStartGrid" && action.Command == _viewModel?.MoveStartRowCommand) { return; }
        e.Handled = true;
        if (action.Command == _viewModel?.IgnoreLastFinishCommand && _viewModel.CanIgnoreLastFinish != true) { return; }
        if (action.Command.CanExecute(action.Parameter)) { action.Command.Execute(action.Parameter); }
    }

    private void SelectCompetitor(object? sender, SelectionChangedEventArgs e)
    {
        if (_synchronizingSelection || sender is not DataGrid grid
            || DataContext is not MainViewModel { IsRefreshingTimingUi: false } vm) { return; }
        var bibs = grid.SelectedItems.OfType<object>().Select(BibOf).OfType<int>().Distinct().ToArray();
        var active = e.AddedItems.OfType<object>().Select(BibOf).OfType<int>().LastOrDefault();
        vm.SelectTimingBibs(bibs, active == 0 ? bibs.FirstOrDefault() : active);
    }

    private static int? BibOf(object? item) => item switch
    {
        TimingGridRow racer => racer.Bib,
        TimingTimestampRow timestamps => timestamps.Bib,
        _ => null
    };

    private void CaptureContextSelection(object? sender, PointerPressedEventArgs e)
    {
        _contextSelection = null;
        if (!e.GetCurrentPoint(this).Properties.IsRightButtonPressed) { return; }
        var ancestors = Ancestry(e.Source).ToArray();
        var grid = ancestors.OfType<DataGrid>().FirstOrDefault();
        var row = ancestors.OfType<DataGridRow>().FirstOrDefault()?.DataContext;
        if (grid is null || BibOf(row) is not { } bib) { return; }
        var selected = grid.SelectedItems.OfType<object>().Select(BibOf).OfType<int>().Distinct().ToArray();
        if (selected.Length > 1 && selected.Contains(bib))
        {
            _contextSelection = (grid, selected);
            Dispatcher.UIThread.Post(() => RestoreContextSelection(bib), DispatcherPriority.Background);
        }
    }

    private void RestoreContextSelection(int activeBib)
    {
        if (_contextSelection is not { } preserved || _viewModel is not { } vm) { return; }
        _contextSelection = null;
        _synchronizingSelection = true;
        try
        {
            preserved.Grid.SelectedItems.Clear();
            foreach (var item in preserved.Grid.ItemsSource?.OfType<object>() ?? [])
            { if (BibOf(item) is { } bib && preserved.Bibs.Contains(bib)) { preserved.Grid.SelectedItems.Add(item); } }
        }
        finally { _synchronizingSelection = false; }
        vm.SelectTimingBibs(preserved.Bibs, activeBib);
    }

    private void SelectContextCompetitor(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not DataGrid grid || !e.GetCurrentPoint(grid).Properties.IsRightButtonPressed) { return; }
        if (DataContext is not MainViewModel vm) { return; }
        var row = Ancestry(e.Source).OfType<DataGridRow>().FirstOrDefault()?.DataContext;
        if (BibOf(row) is not { } bib) { return; }
        if (_contextSelection is { } preserved && preserved.Grid == grid && preserved.Bibs.Contains(bib))
        {
            RestoreContextSelection(bib);
            return;
        }
        _contextSelection = null;
        if (!grid.SelectedItems.Contains(row)) { grid.SelectedItem = row; }
        var bibs = grid.SelectedItems.OfType<object>().Select(BibOf).OfType<int>().Distinct().ToArray();
        vm.SelectTimingBibs(bibs, bib);
    }
}
