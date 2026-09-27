using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;
using System.ComponentModel;

namespace OpenSkiTime.Rewrite.Desktop;

public sealed partial class TimingView : UserControl
{
    private MainViewModel? _viewModel;
    private bool _synchronizingSelection;

    public TimingView()
    {
        AvaloniaXamlLoader.Load(this);
        DataContextChanged += (_, _) => BindViewModel(DataContext as MainViewModel);
        AttachedToVisualTree += (_, _) => BindViewModel(DataContext as MainViewModel);
        DetachedFromVisualTree += (_, _) => BindViewModel(null);
        KeyDown += (_, e) =>
        {
            if (DataContext is not MainViewModel vm) { return; }
            if (e.Key == Key.F5) { vm.ExpectSelectedCommand.Execute("start"); e.Handled = true; }
            if (e.Key == Key.F6) { vm.ExpectSelectedCommand.Execute("finish"); e.Handled = true; }
        };
    }

    private void BindViewModel(MainViewModel? vm)
    {
        if (_viewModel is not null) { _viewModel.PropertyChanged -= OnViewModelChanged; }
        _viewModel = vm;
        if (vm is not null) { vm.PropertyChanged += OnViewModelChanged; SynchronizeSelection(); }
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
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
            foreach (var name in new[] { "TimingResultsGrid", "OnCourseGrid", "IntermediateGrid", "FinishedGrid" })
            {
                var grid = this.FindControl<DataGrid>(name)!;
                grid.SelectedItem = grid.ItemsSource?.OfType<TimingGridRow>().FirstOrDefault(x => x.Bib == _viewModel?.SelectedTimingRow?.Bib);
            }
        }
        finally { _synchronizingSelection = false; }
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
