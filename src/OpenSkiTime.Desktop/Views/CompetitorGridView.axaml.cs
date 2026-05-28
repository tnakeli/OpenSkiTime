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

        grid.Columns.Add(MakeTextCol("FIS Code",   nameof(CompetitorRowViewModel.FisCode),    100));
        grid.Columns.Add(MakeTextCol("Last Name",  nameof(CompetitorRowViewModel.LastName),   150));
        grid.Columns.Add(MakeTextCol("First Name", nameof(CompetitorRowViewModel.FirstName),  130));
        grid.Columns.Add(MakeTextCol("YOB",        nameof(CompetitorRowViewModel.YearOfBirth), 60));

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

        grid.Columns.Add(MakeTextCol("Nation", nameof(CompetitorRowViewModel.NationCode), 60));
        grid.Columns.Add(MakeTextCol("Club",   nameof(CompetitorRowViewModel.ClubName),  150));

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

    private static readonly Avalonia.Media.IBrush s_modifiedCellBrush =
        Avalonia.Media.SolidColorBrush.Parse("#fed7aa"); // orange-200
    private static readonly Avalonia.Media.IBrush s_transparentBrush =
        Avalonia.Media.Brushes.Transparent;

    /// <summary>
    /// Creates an editable DataGridTemplateColumn whose display cell turns orange
    /// reactively when <c>IsModified_{fieldName}</c> is true on the row VM.
    /// </summary>
    private static DataGridTemplateColumn MakeTextCol(string header, string fieldName, double width)
    {
        var modifiedPropName = $"IsModified{fieldName}";

        return new DataGridTemplateColumn
        {
            Header = header,
            Width = new DataGridLength(width),
            CellTemplate = new FuncDataTemplate<CompetitorRowViewModel>((_, _2) =>
            {
                var tb = new TextBlock
                {
                    [!TextBlock.TextProperty] = new Binding(fieldName),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Avalonia.Thickness(8, 0),
                };
                var border = new Border { Child = tb };
                // Reactive binding: IsModified_NationCode etc. fires PropertyChanged via MarkFieldModified
                border.Bind(Border.BackgroundProperty, new Binding(modifiedPropName)
                {
                    Converter = new Avalonia.Data.Converters.FuncValueConverter<bool, Avalonia.Media.IBrush>(
                        v => v ? s_modifiedCellBrush : s_transparentBrush),
                });
                return border;
            }),
            CellEditingTemplate = new FuncDataTemplate<CompetitorRowViewModel>((_, _2) =>
            {
                var box = new TextBox();
                box.Bind(TextBox.TextProperty, new Binding(fieldName) { Mode = BindingMode.TwoWay });
                return box;
            }),
        };
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
