using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using OpenSkiTime.Desktop.ViewModels;

namespace OpenSkiTime.Desktop.Views;

public partial class CompetitorGridView : UserControl
{
    public CompetitorGridView()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void OnDataGridSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is not CompetitorGridViewModel vm)
        {
            return;
        }

        if (sender is not DataGrid grid)
        {
            return;
        }

        vm.SelectedItems.Clear();
        foreach (var item in grid.SelectedItems)
        {
            vm.SelectedItems.Add(item);
        }
    }
}
