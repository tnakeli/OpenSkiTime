using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;
using Avalonia.Layout;
using System.ComponentModel;
using System.Windows.Input;

namespace OpenSkiTime.Rewrite.Desktop;

public sealed partial class TimingView : UserControl
{
    private MainViewModel? _viewModel;
    private bool _synchronizingSelection;
    private static readonly string[] GridNames = ["AtStartGrid", "RunningGrid", "TimestampsGrid", "RankingGrid"];
    private TimingAction[] _actions = [];
    private int _splitCount = -1;
    // One definition drives both the context menus and actual keyboard shortcuts.
    private sealed record TimingAction(string Header, string Gesture, ICommand Command, string? Parameter = null,
        string? Enabled = null, string? Visible = null, string? DynamicHeader = null);

    public TimingView()
    {
        AvaloniaXamlLoader.Load(this);
        DataContextChanged += (_, _) => BindViewModel(DataContext as MainViewModel);
        AttachedToVisualTree += (_, _) => BindViewModel(DataContext as MainViewModel);
        DetachedFromVisualTree += (_, _) => BindViewModel(null);
        AddHandler(KeyDownEvent, OnTimingKey, RoutingStrategies.Tunnel);
        ConfigureDragging();
    }

    private void BindViewModel(MainViewModel? vm)
    {
        if (_viewModel is not null) { _viewModel.PropertyChanged -= OnViewModelChanged; }
        if (!ReferenceEquals(_viewModel, vm)) { _splitCount = -1; }
        _viewModel = vm;
        if (vm is not null) { vm.PropertyChanged += OnViewModelChanged; BuildActions(vm); ConfigureColumns(); SynchronizeSelection(); }
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.HasTimingIntermediates)) { ConfigureColumns(); }
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
                grid.SelectedItem = name == "TimestampsGrid"
                    ? grid.ItemsSource?.OfType<TimingTimestampRow>().FirstOrDefault(x => x.Bib == _viewModel?.SelectedTimingRow?.Bib && x.Bib is not null)
                    : grid.ItemsSource?.OfType<TimingGridRow>().FirstOrDefault(x => x.Bib == _viewModel?.SelectedTimingRow?.Bib);
            }
        }
        finally { _synchronizingSelection = false; }
    }

    private void ConfigureColumns()
    {
        var count = _viewModel?.TimingCheckpoints.Count ?? 0;
        if (count == _splitCount) { return; }
        _splitCount = count;
        var simulatorButtons = this.FindControl<StackPanel>("SimulatorIntermediateButtons")!;
        simulatorButtons.Children.Clear();
        for (var i = 1; i <= count; i++)
        {
            var button = new Button { Content = $"Test I{i}", Command = _viewModel?.SimulatePulseCommand,
                CommandParameter = $"intermediate:{i}" };
            button.Classes.Add("secondaryAction");
            button.Bind(IsEnabledProperty, new Binding(nameof(MainViewModel.IsTimingConnected)) { Source = _viewModel });
            simulatorButtons.Children.Add(button);
        }
        var grid = this.FindControl<DataGrid>("RunningGrid")!;
        foreach (var column in grid.Columns.Where(x => x.Tag is "intermediate").ToArray()) { grid.Columns.Remove(column); }
        for (var i = 0; i < count; i++)
        {
            grid.Columns.Insert(4 + i, new DataGridTextColumn
            { Header = $"INTERM {i + 1}", Binding = new Binding($"Intermediates[{i}]"), Width = new DataGridLength(82), Tag = "intermediate" });
        }
        var timestamps = this.FindControl<DataGrid>("TimestampsGrid")!;
        while (timestamps.Columns.Count > 2) { timestamps.Columns.RemoveAt(2); }
        for (var i = 0; i < count + 2; i++)
        {
            var index = i;
            timestamps.Columns.Add(new DataGridTemplateColumn
            {
                Header = i == 0 ? "START" : i == count + 1 ? "FINISH" : $"INTERM {i}", Width = new DataGridLength(150),
                CellTemplate = new FuncDataTemplate<TimingTimestampRow>((row, _) =>
                {
                    var cell = row?.Cells.ElementAtOrDefault(index);
                    var border = new Border { Tag = cell, Padding = new(6,0), Child = new TextBlock
                    { Text = cell?.Time ?? "—", FontFamily = new("Cascadia Mono,Consolas,DejaVu Sans Mono,monospace"), FontSize = 11, VerticalAlignment = VerticalAlignment.Center } };
                    border.Classes.Add("timestampCell");
                    if (cell is not null) { DragDrop.SetAllowDrop(border, true); }
                    if (cell is not null) { ToolTip.SetTip(border, cell.Hint); }
                    return border;
                })
            });
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
            new("Next at finish", "F6", vm.ExpectSelectedCommand, "finish"),
            new("Back to start", "F8", vm.ReturnToStartCommand),
            new("DNS · did not start", "Ctrl+D", vm.ClassifyTimingCommand, "DNS"),
            new("DNF · did not finish", "Ctrl+F", vm.ClassifyTimingCommand, "DNF"),
            new("DSQ · disqualified", "Ctrl+Q", vm.ClassifyTimingCommand, "DSQ"),
            new("NPS · not permitted to start", "Ctrl+N", vm.ClassifyTimingCommand, "NPS"),
            new("Clear classification", "Ctrl+R", vm.ClassifyTimingCommand, "Clear"),
            new("Absent next starter · DNS", "Shift+F5", vm.NextStartDnsCommand),
            new("Hold start", "Ctrl+F5", vm.HoldTimingPositionCommand, "start", DynamicHeader: nameof(vm.StartHoldLabel)),
            new("Hold finish", "Ctrl+F6", vm.HoldTimingPositionCommand, "finish", DynamicHeader: nameof(vm.FinishHoldLabel)),
            new("False finish · not a racer", "Ctrl+Back", vm.IgnoreLastFinishCommand, Enabled: nameof(vm.CanIgnoreLastFinish)),
        ];
        foreach (var name in GridNames)
        {
            var menu = new ContextMenu();
            var status = new MenuItem { Header = "Status" };
            var more = new MenuItem { Header = "More…" };
            for (var i = 0; i < _actions.Length; i++)
            {
                var action = _actions[i];
                var item = new MenuItem { Header = action.Header, InputGesture = KeyGesture.Parse(action.Gesture), Command = action.Command, CommandParameter = action.Parameter };
                if (action.Enabled is { } enabled) { item.Bind(IsEnabledProperty, new Binding(enabled) { Source = vm }); }
                if (action.Visible is { } visible) { item.Bind(IsVisibleProperty, new Binding(visible) { Source = vm }); }
                if (action.DynamicHeader is { } header) { item.Bind(MenuItem.HeaderProperty, new Binding(header) { Source = vm }); }
                if (i is >= 3 and <= 7) { status.Items.Add(item); }
                else if (i >= 8) { more.Items.Add(item); }
                else if (name == "AtStartGrid" && i == 1) { more.Items.Add(item); }
                else { menu.Items.Add(item); }
            }
            menu.Items.Add(new Separator()); menu.Items.Add(status); menu.Items.Add(more);
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
        e.Handled = true;
        var menuItem = MenuActions(grid.ContextMenu!.Items).First(x => x.Command == action.Command && Equals(x.CommandParameter, action.Parameter));
        if (menuItem.IsEnabled && menuItem.IsVisible && action.Command.CanExecute(action.Parameter)) { action.Command.Execute(action.Parameter); }
    }

    private static IEnumerable<MenuItem> MenuActions(IEnumerable<object?> items) => items.OfType<MenuItem>()
        .SelectMany(x => new[] { x }.Concat(MenuActions(x.Items)));

    private void SelectCompetitor(object? sender, SelectionChangedEventArgs e)
    {
        if (_synchronizingSelection || DataContext is not MainViewModel { IsRefreshingTimingUi: false } vm) { return; }
        if (e.AddedItems.OfType<TimingGridRow>().FirstOrDefault() is { } row) { vm.SelectedTimingRow = row; }
        else if (e.AddedItems.OfType<TimingTimestampRow>().FirstOrDefault() is { } timestamps)
        { vm.SelectedTimingRow = vm.TimingRows.FirstOrDefault(x => x.Bib == timestamps.Bib); }
    }

    private void SelectContextCompetitor(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not DataGrid grid || !e.GetCurrentPoint(grid).Properties.IsRightButtonPressed) { return; }
        if (DataContext is not MainViewModel vm) { return; }
        var row = Ancestry(e.Source).OfType<DataGridRow>().FirstOrDefault()?.DataContext;
        grid.SelectedItem = row;
        vm.SelectedTimingRow = row switch
        {
            TimingGridRow racer => racer,
            TimingTimestampRow timestamps => vm.TimingRows.FirstOrDefault(x => x.Bib == timestamps.Bib),
            _ => null
        };
    }
}
