using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using OpenSkiTime.Desktop.ViewModels;

namespace OpenSkiTime.Desktop.Views;

public partial class CompetitorGridView : UserControl
{
    private CompetitorGridViewModel? _vm;

    public CompetitorGridView()
    {
        AvaloniaXamlLoader.Load(this);
        AddHandler(KeyDownEvent, OnKeyDown, handledEventsToo: true);
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_vm is not null)
        {
            _vm.CompetitionColumns.CollectionChanged -= OnCompetitionColumnsChanged;
        }

        _vm = DataContext as CompetitorGridViewModel;

        if (_vm is not null)
        {
            _vm.CompetitionColumns.CollectionChanged += OnCompetitionColumnsChanged;
        }

        RebuildColumns();
    }

    private void OnCompetitionColumnsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RebuildColumns();
    }

    private void RebuildColumns()
    {
        if (this.Find<DataGrid>("CompetitorDataGrid") is not DataGrid grid)
        {
            return;
        }

        grid.Columns.Clear();

        // FIS Code
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = "FIS Code",
            Binding = new Binding(nameof(CompetitorRowViewModel.FisCode)),
            Width = new DataGridLength(90),
        });

        // Last Name
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = "Last Name",
            Binding = new Binding(nameof(CompetitorRowViewModel.LastName)),
            Width = new DataGridLength(150),
        });

        // First Name
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = "First Name",
            Binding = new Binding(nameof(CompetitorRowViewModel.FirstName)),
            Width = new DataGridLength(130),
        });

        // YOB
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = "YOB",
            Binding = new Binding(nameof(CompetitorRowViewModel.YearOfBirth)),
            Width = new DataGridLength(55),
        });

        // Gender (AutoCompleteBox in edit mode)
        grid.Columns.Add(new DataGridTemplateColumn
        {
            Header = "Gender",
            Width = new DataGridLength(90),
            CellTemplate = new FuncDataTemplate<CompetitorRowViewModel>((row, _) =>
                new TextBlock
                {
                    [!TextBlock.TextProperty] = new Binding(nameof(CompetitorRowViewModel.Gender)),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Avalonia.Thickness(8, 0),
                }),
            CellEditingTemplate = new FuncDataTemplate<CompetitorRowViewModel>((row, _) =>
            {
                var acb = new AutoCompleteBox
                {
                    MinimumPrefixLength = 0,
                    FilterMode = AutoCompleteFilterMode.None,
                    ItemsSource = CompetitorRowViewModel.GenderOptions,
                };
                acb.Bind(AutoCompleteBox.TextProperty, new Binding(nameof(CompetitorRowViewModel.Gender))
                {
                    Mode = BindingMode.TwoWay,
                });
                return acb;
            }),
        });

        // Nation
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = "Nation",
            Binding = new Binding(nameof(CompetitorRowViewModel.NationCode)),
            Width = new DataGridLength(60),
        });

        // Club
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = "Club",
            Binding = new Binding(nameof(CompetitorRowViewModel.ClubName)),
            Width = new DataGridLength(150),
        });

        // Dynamic competition columns (checkboxes)
        if (_vm is not null)
        {
            for (int i = 0; i < _vm.CompetitionColumns.Count; i++)
            {
                var idx = i;
                var col = _vm.CompetitionColumns[idx];
                grid.Columns.Add(new DataGridTemplateColumn
                {
                    Header = col.ShortLabel,
                    Width = new DataGridLength(70),
                    IsReadOnly = false,
                    CellTemplate = new FuncDataTemplate<CompetitorRowViewModel>((row, _) =>
                    {
                        var cb = new CheckBox
                        {
                            HorizontalAlignment = HorizontalAlignment.Center,
                            IsHitTestVisible = false,
                        };
                        if (row is not null && idx < row.Participations.Count)
                        {
                            cb.Bind(CheckBox.IsCheckedProperty,
                                new Binding($"Participations[{idx}].IsParticipating"));
                        }

                        return cb;
                    }),
                    CellEditingTemplate = new FuncDataTemplate<CompetitorRowViewModel>((row, _) =>
                    {
                        var cb = new CheckBox { HorizontalAlignment = HorizontalAlignment.Center };
                        if (row is not null && idx < row.Participations.Count)
                        {
                            cb.Bind(CheckBox.IsCheckedProperty,
                                new Binding($"Participations[{idx}].IsParticipating")
                                {
                                    Mode = BindingMode.TwoWay,
                                });
                        }

                        return cb;
                    }),
                });
            }
        }

        // Delete button (last, read-only)
        grid.Columns.Add(new DataGridTemplateColumn
        {
            Header = string.Empty,
            Width = new DataGridLength(70),
            IsReadOnly = true,
            CellTemplate = new FuncDataTemplate<CompetitorRowViewModel>((row, _) =>
            {
                var btn = new Button
                {
                    Content = "Delete",
                    FontSize = 11,
                    Padding = new Avalonia.Thickness(6, 2),
                };
                if (_vm is not null)
                {
                    btn.Command = _vm.DeleteCompetitorCommand;
                    btn.CommandParameter = row;
                }

                return btn;
            }),
        });
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
