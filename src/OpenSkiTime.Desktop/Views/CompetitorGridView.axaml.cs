using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using OpenSkiTime.Desktop.ViewModels;

namespace OpenSkiTime.Desktop.Views;

public partial class CompetitorGridView : UserControl
{
    public CompetitorGridView()
    {
        AvaloniaXamlLoader.Load(this);
        AddHandler(KeyDownEvent, OnKeyDown, handledEventsToo: true);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not CompetitorGridViewModel vm)
        {
            return;
        }

        if (e.KeyModifiers == KeyModifiers.Control && e.Key == Key.V)
        {
            if (vm.PasteCommand.CanExecute(null))
            {
                vm.PasteCommand.Execute(null);
            }

            e.Handled = true;
        }
        else if (e.KeyModifiers == KeyModifiers.Control && e.Key == Key.S)
        {
            if (vm.SaveCommand.CanExecute(null))
            {
                vm.SaveCommand.Execute(null);
            }

            e.Handled = true;
        }
        else if (e.KeyModifiers == KeyModifiers.Control && e.Key == Key.C)
        {
            if (vm.SelectedItems.Count > 0 && vm.CopySelectedRowsCommand.CanExecute(null))
            {
                vm.CopySelectedRowsCommand.Execute(null);
                e.Handled = true;
            }
        }
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
