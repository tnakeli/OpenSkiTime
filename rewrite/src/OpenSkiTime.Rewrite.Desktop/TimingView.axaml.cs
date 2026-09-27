using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;
using System.ComponentModel;
using System.Windows.Input;

namespace OpenSkiTime.Rewrite.Desktop;

public sealed partial class TimingView : UserControl
{
    private MainViewModel? _viewModel;
    private bool _synchronizingSelection;
    private static readonly string[] GridNames = ["AtStartGrid", "RunningGrid", "RankingGrid"];
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
    }

    private void BindViewModel(MainViewModel? vm)
    {
        if (_viewModel is not null) { _viewModel.PropertyChanged -= OnViewModelChanged; }
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
                grid.SelectedItem = grid.ItemsSource?.OfType<TimingGridRow>().FirstOrDefault(x => x.Bib == _viewModel?.SelectedTimingRow?.Bib);
            }
        }
        finally { _synchronizingSelection = false; }
    }

    private void ConfigureColumns()
    {
        var count = _viewModel?.TimingCheckpoints.Count ?? 0;
        if (count == _splitCount) { return; }
        _splitCount = count;
        var grid = this.FindControl<DataGrid>("RunningGrid")!;
        foreach (var column in grid.Columns.Where(x => x.Tag is "intermediate").ToArray()) { grid.Columns.Remove(column); }
        for (var i = 0; i < count; i++)
        {
            grid.Columns.Insert(4 + i, new DataGridTextColumn
            { Header = $"INTERM {i + 1}", Binding = new Binding($"Intermediates[{i}]"), Width = new DataGridLength(82), Tag = "intermediate" });
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
            new("Next at intermediate", "F7", vm.ExpectSelectedCommand, "intermediate", Visible: nameof(vm.HasTimingIntermediates)),
            new("Back to start", "F8", vm.ReturnToStartCommand),
            new("DNS · did not start", "Ctrl+D", vm.ClassifyTimingCommand, "DNS"),
            new("DNF · did not finish", "Ctrl+F", vm.ClassifyTimingCommand, "DNF"),
            new("DSQ · disqualified", "Ctrl+Q", vm.ClassifyTimingCommand, "DSQ"),
            new("NPS · not permitted to start", "Ctrl+N", vm.ClassifyTimingCommand, "NPS"),
            new("Clear classification", "Ctrl+R", vm.ClassifyTimingCommand, "Clear"),
            new("Absent next starter · DNS", "Shift+F5", vm.NextStartDnsCommand),
            new("Hold start", "Ctrl+F5", vm.HoldTimingPositionCommand, "start", DynamicHeader: nameof(vm.StartHoldLabel)),
            new("Hold finish", "Ctrl+F6", vm.HoldTimingPositionCommand, "finish", DynamicHeader: nameof(vm.FinishHoldLabel)),
            new("Hold intermediate", "Ctrl+F7", vm.HoldTimingPositionCommand, "intermediate", Visible: nameof(vm.HasTimingIntermediates), DynamicHeader: nameof(vm.IntermediateHoldLabel)),
            new("False finish · not a racer", "Ctrl+Back", vm.IgnoreLastFinishCommand, Enabled: nameof(vm.CanIgnoreLastFinish)),
            new("Correct competitor timing…", "Ctrl+E", vm.CorrectCompetitorFinishCommand),
            new("Assign latest finish…", "Ctrl+L", vm.CorrectLastFinishCommand, Enabled: nameof(vm.CanIgnoreLastFinish)),
            new("Undo last timing change", "Ctrl+Z", vm.UndoLastTimingChangeCommand)
        ];
        foreach (var name in GridNames)
        {
            var menu = new ContextMenu();
            for (var i = 0; i < _actions.Length; i++)
            {
                if (i is 4 or 9 or 13) { menu.Items.Add(new Separator()); }
                var action = _actions[i];
                var item = new MenuItem { Header = action.Header, InputGesture = KeyGesture.Parse(action.Gesture), Command = action.Command, CommandParameter = action.Parameter };
                if (action.Enabled is { } enabled) { item.Bind(IsEnabledProperty, new Binding(enabled) { Source = vm }); }
                if (action.Visible is { } visible) { item.Bind(IsVisibleProperty, new Binding(visible) { Source = vm }); }
                if (action.DynamicHeader is { } header) { item.Bind(MenuItem.HeaderProperty, new Binding(header) { Source = vm }); }
                menu.Items.Add(item);
            }
            this.FindControl<DataGrid>(name)!.ContextMenu = menu;
        }
    }

    private void OnTimingKey(object? sender, KeyEventArgs e)
    {
        if (e.Source is not Avalonia.Visual source) { return; }
        // Never run race shortcuts while the operator is typing a correction or device setting.
        var grid = source as DataGrid ?? source.GetVisualAncestors().OfType<DataGrid>().FirstOrDefault();
        if (grid is null || !GridNames.Contains(grid.Name)) { return; }
        if (e.Key == Key.F10 && e.KeyModifiers == KeyModifiers.Shift)
        { grid.ContextMenu?.Open(grid); e.Handled = true; return; }
        var action = _actions.FirstOrDefault(x => KeyGesture.Parse(x.Gesture).Matches(e));
        if (action is null) { return; }
        e.Handled = true;
        var menuItem = grid.ContextMenu!.Items.OfType<MenuItem>().First(x => x.Command == action.Command && Equals(x.CommandParameter, action.Parameter));
        if (menuItem.IsEnabled && menuItem.IsVisible && action.Command.CanExecute(action.Parameter)) { action.Command.Execute(action.Parameter); }
    }

    private void SelectCompetitor(object? sender, SelectionChangedEventArgs e)
    {
        if (!_synchronizingSelection && DataContext is MainViewModel { IsRefreshingTimingUi: false } vm && e.AddedItems.OfType<TimingGridRow>().FirstOrDefault() is { } row)
        { vm.SelectedTimingRow = row; }
    }

    private void SelectContextCompetitor(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not DataGrid grid || !e.GetCurrentPoint(grid).Properties.IsRightButtonPressed) { return; }
        if (e.Source is Avalonia.Visual source && source.GetVisualAncestors().OfType<DataGridRow>().FirstOrDefault()?.DataContext is TimingGridRow row
            && DataContext is MainViewModel vm)
        { grid.SelectedItem = row; vm.SelectedTimingRow = row; }
    }
}
