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
    private static readonly string[] s_textFields =
    [
        nameof(CompetitorRowViewModel.FisCode),
        nameof(CompetitorRowViewModel.LastName),
        nameof(CompetitorRowViewModel.FirstName),
        nameof(CompetitorRowViewModel.YearOfBirth),
        nameof(CompetitorRowViewModel.Gender),
        nameof(CompetitorRowViewModel.NationCode),
        nameof(CompetitorRowViewModel.ClubName),
    ];

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

        grid.CellEditEnded -= OnCellEditEnded;
        grid.Columns.Clear();

        grid.Columns.Add(new DataGridTextColumn { Header = "FIS Code",   Binding = new Binding(nameof(CompetitorRowViewModel.FisCode)),      Width = new DataGridLength(100) });
        grid.Columns.Add(new DataGridTextColumn { Header = "Last Name",  Binding = new Binding(nameof(CompetitorRowViewModel.LastName)),     Width = new DataGridLength(150) });
        grid.Columns.Add(new DataGridTextColumn { Header = "First Name", Binding = new Binding(nameof(CompetitorRowViewModel.FirstName)),    Width = new DataGridLength(130) });
        grid.Columns.Add(new DataGridTextColumn { Header = "YOB",        Binding = new Binding(nameof(CompetitorRowViewModel.YearOfBirth)),  Width = new DataGridLength(60) });

        grid.Columns.Add(new DataGridTemplateColumn
        {
            Header = "Gender",
            Width = new DataGridLength(90),
            CellTemplate = new FuncDataTemplate<CompetitorRowViewModel>((_, _2) =>
                new TextBlock
                {
                    [!TextBlock.TextProperty] = new Binding(nameof(CompetitorRowViewModel.Gender)),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Avalonia.Thickness(8, 0),
                }),
            CellEditingTemplate = new FuncDataTemplate<CompetitorRowViewModel>((_, _2) =>
            {
                var acb = new AutoCompleteBox
                {
                    MinimumPrefixLength = 0,
                    FilterMode = AutoCompleteFilterMode.None,
                    ItemsSource = CompetitorRowViewModel.GenderOptions,
                };
                acb.Bind(AutoCompleteBox.TextProperty, new Binding(nameof(CompetitorRowViewModel.Gender)) { Mode = BindingMode.TwoWay });
                return acb;
            }),
        });

        grid.Columns.Add(new DataGridTextColumn { Header = "Nation", Binding = new Binding(nameof(CompetitorRowViewModel.NationCode)), Width = new DataGridLength(60) });
        grid.Columns.Add(new DataGridTextColumn { Header = "Club",   Binding = new Binding(nameof(CompetitorRowViewModel.ClubName)),   Width = new DataGridLength(150) });

        if (_vm is not null)
        {
            for (int i = 0; i < _vm.CompetitionColumns.Count; i++)
            {
                var idx = i;
                var compCol = _vm.CompetitionColumns[idx];
                grid.Columns.Add(new DataGridTemplateColumn
                {
                    Header = compCol.ShortLabel,
                    Width = new DataGridLength(65),
                    IsReadOnly = true,
                    CellTemplate = new FuncDataTemplate<CompetitorRowViewModel>((row, _2) =>
                    {
                        var cb = new CheckBox { HorizontalAlignment = HorizontalAlignment.Center };
                        if (row is not null && idx < row.Participations.Count)
                        {
                            cb.Bind(CheckBox.IsCheckedProperty,
                                new Binding($"Participations[{idx}].IsParticipating") { Mode = BindingMode.TwoWay });
                            cb.IsCheckedChanged += async (s, e2) =>
                            {
                                if (_vm is not null && row is not null && idx < row.Participations.Count)
                                {
                                    await _vm.SaveParticipationAsync(row, row.Participations[idx]);
                                }
                            };
                        }

                        return cb;
                    }),
                });
            }
        }

        grid.Columns.Add(new DataGridTemplateColumn
        {
            Header = string.Empty,
            Width = new DataGridLength(70),
            IsReadOnly = true,
            CellTemplate = new FuncDataTemplate<CompetitorRowViewModel>((row, _2) =>
            {
                var btn = new Button { Content = "Delete", FontSize = 11, Padding = new Avalonia.Thickness(6, 2) };
                if (_vm is not null)
                {
                    btn.Command = _vm.DeleteCompetitorCommand;
                    btn.CommandParameter = row;
                }

                return btn;
            }),
        });

        grid.CellEditEnded += OnCellEditEnded;
    }

    private void OnCellEditEnded(object? sender, DataGridCellEditEndedEventArgs e)
    {
        if (_vm is null)
        {
            return;
        }

        if (e.EditAction != DataGridEditAction.Commit)
        {
            return;
        }

        if (e.Row.DataContext is not CompetitorRowViewModel row)
        {
            return;
        }

        var colIdx = e.Column.DisplayIndex;
        string? fieldName = colIdx < s_textFields.Length ? s_textFields[colIdx] : null;

        if (fieldName is not null)
        {
            _ = _vm.OnCellEditCommittedAsync(row, fieldName);
        }
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
