using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace OpenSkiTime.Desktop;

public partial class DrawView : UserControl
{
    private MainViewModel? _model;
    private double? _scrollBeforeReload;

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

    // Drawing again (or moving a competitor, assigning bibs) reloads the start list. The list keeps the scroll position it
    // had, so the operator does not lose their place among the starters.
    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainViewModel.IsDrawBusy) || _model is null) { return; }
        if (_model.IsDrawBusy)
        {
            _scrollBeforeReload ??= StartListScrollBar()?.Value;
            return;
        }
        if (_scrollBeforeReload is not { } offset) { return; }
        _scrollBeforeReload = null;
        Dispatcher.UIThread.Post(() =>
        {
            if (StartListScrollBar() is { } bar) { bar.Value = Math.Min(offset, bar.Maximum); }
        }, DispatcherPriority.Loaded);
    }

    private ScrollBar? StartListScrollBar()
        => this.FindControl<DataGrid>("DrawStartListGrid")?.GetVisualDescendants().OfType<ScrollBar>()
            .FirstOrDefault(x => x.Orientation == Orientation.Vertical);
}
