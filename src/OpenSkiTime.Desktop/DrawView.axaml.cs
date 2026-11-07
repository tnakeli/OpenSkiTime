using System.Collections;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace OpenSkiTime.Desktop;

public partial class DrawView : UserControl
{
    private MainViewModel? _model;
    private int? _firstVisibleRow;

    public DrawView()
    {
        AvaloniaXamlLoader.Load(this);
        DataContextChanged += (_, _) => Attach(DataContext as MainViewModel);
    }

    private void Attach(MainViewModel? model)
    {
        if (_model is not null) { _model.PropertyChanged -= OnModelChanged; }
        _model = model;
        if (_model is not null) { _model.PropertyChanged += OnModelChanged; }
    }

    private DataGrid? StartListGrid => this.FindControl<DataGrid>("DrawStartListGrid");

    // Drawing again (or moving a competitor, assigning bibs) reloads the start list. The list keeps the rows it showed at the
    // top, so the operator does not lose their place among the starters.
    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainViewModel.IsDrawBusy) || _model is null) { return; }
        if (_model.IsDrawBusy)
        {
            _firstVisibleRow ??= FirstVisibleRow();
            return;
        }
        if (_firstVisibleRow is not { } index) { return; }
        _firstVisibleRow = null;
        // The grid is hidden while the list is reloaded; wait until it has been laid out again with its new rows.
        Dispatcher.UIThread.Post(() => Dispatcher.UIThread.Post(() => RestoreFirstVisibleRow(index), DispatcherPriority.Background), DispatcherPriority.Loaded);
    }

    private int? FirstVisibleRow()
    {
        if (StartListGrid is not { IsEffectivelyVisible: true } grid) { return null; }
        var rows = grid.GetVisualDescendants().OfType<DataGridRow>().Where(x => x.IsVisible && x.Bounds.Height > 0).ToArray();
        // A row scrolled half out of sight at the top still counts as the first one.
        return rows.Length == 0 ? null : rows.Min(x => x.Index);
    }

    private void RestoreFirstVisibleRow(int index)
    {
        if (StartListGrid is not { } grid || grid.ItemsSource is not IList items || items.Count == 0 || index <= 0) { return; }
        // Scrolling to the end first makes the target row come into view from above, which puts it at the top.
        grid.ScrollIntoView(items[^1], null);
        grid.UpdateLayout();
        grid.ScrollIntoView(items[Math.Min(index, items.Count - 1)], null);
    }
}
