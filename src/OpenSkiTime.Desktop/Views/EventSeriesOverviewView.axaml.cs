using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using OpenSkiTime.Application.Abstractions;
using OpenSkiTime.Desktop.ViewModels;

namespace OpenSkiTime.Desktop.Views;

public partial class EventSeriesOverviewView : UserControl
{
    public EventSeriesOverviewView()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private async void OnSeriesSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is EventSeriesOverviewViewModel vm
            && e.AddedItems is { Count: > 0 } added
            && added[0] is EventSeriesSummary summary)
        {
            await vm.SelectSeriesAsync(summary.Id);
        }
    }
}
