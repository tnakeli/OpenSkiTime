using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace OpenSkiTime.Rewrite.Desktop;

public partial class MainWindow : Window
{
    private MainViewModel? _gridViewModel;

    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);
        DataContextChanged += (_, _) => BindCompetitionColumns();
        AddHandler(KeyDownEvent, OnGridKeyDown, handledEventsToo: true);
    }

    private void BindCompetitionColumns()
    {
        if (_gridViewModel is not null)
        {
            _gridViewModel.Competitions.CollectionChanged -= OnCompetitionsChanged;
        }
        _gridViewModel = DataContext as MainViewModel;
        if (_gridViewModel is not null)
        {
            _gridViewModel.Competitions.CollectionChanged += OnCompetitionsChanged;
        }
        RebuildCompetitionColumns();
    }

    private void OnCompetitionsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        => RebuildCompetitionColumns();

    private void RebuildCompetitionColumns()
    {
        var grid = this.FindControl<DataGrid>("CompetitorGrid");
        if (grid is null) { return; }
        for (var i = grid.Columns.Count - 1; i >= 0; i--)
        {
            if (grid.Columns[i].Tag is string tag && tag == "competition-entry") { grid.Columns.RemoveAt(i); }
        }
        if (_gridViewModel is null) { return; }
        for (var index = 0; index < _gridViewModel.Competitions.Count; index++)
        {
            var position = index;
            var competition = _gridViewModel.Competitions[index];
            grid.Columns.Insert(8 + index, new DataGridTemplateColumn
            {
                Header = competition.Values.ShortLabel,
                Width = new DataGridLength(78),
                Tag = "competition-entry",
                IsReadOnly = true,
                CellTemplate = new FuncDataTemplate<CompetitorGridRow>((row, _) =>
                {
                    var check = new CheckBox { HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center };
                    if (row is not null && position < row.GridEntries.Count)
                    {
                        check.Bind(CheckBox.IsCheckedProperty,
                            new Binding($"GridEntries[{position}].IsParticipating") { Mode = BindingMode.TwoWay });
                        check.Click += async (_, _) =>
                        {
                            if (_gridViewModel is not null && position < row.GridEntries.Count)
                            {
                                await _gridViewModel.SaveGridParticipationAsync(row, row.GridEntries[position]);
                            }
                        };
                    }
                    return check;
                }),
            });
        }
    }

    private void OnGridKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainViewModel vm || !vm.IsCompetitorsSection ||
            e.KeyModifiers != KeyModifiers.Control || e.Source is TextBox) { return; }
        if (e.Key == Key.V)
        {
            vm.PasteFromExcelCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.C)
        {
            vm.CopySelectedCompetitorCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void Navigation_Click(object? sender, RoutedEventArgs e)
    {
        if (this.FindControl<ScrollViewer>("WorkspaceScroll") is { } scroll)
        {
            scroll.Offset = Vector.Zero;
        }
    }

    private async void CompetitorGrid_RowEditEnded(object? sender, DataGridRowEditEndedEventArgs e)
    {
        if (e.EditAction == DataGridEditAction.Commit && e.Row.DataContext is CompetitorGridRow row
            && DataContext is MainViewModel viewModel)
        {
            if (viewModel.IsImportReviewOpen) { return; }
            await viewModel.SaveCompetitorRowAsync(row);
        }
    }

    private async void Participation_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { DataContext: CompetitionEntryChoice choice }
            && DataContext is MainViewModel viewModel)
        {
            await viewModel.SaveParticipationChoiceAsync(choice);
        }
    }

    private void RestoreDeskChange_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: DeskChangeLogEntry entry }
            && DataContext is MainViewModel viewModel)
        {
            viewModel.RestoreDeskChangeCommand.Execute(entry);
        }
    }

    private void CompetitorGrid_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is DataGrid grid && DataContext is MainViewModel viewModel)
        {
            viewModel.SetSelectedExportRows(grid.SelectedItems.OfType<CompetitorGridRow>());
        }
    }

    private void DatePickerButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button)
        {
            return;
        }

        var inputName = button.Name switch
        {
            "SeriesStartDatePickerButton" => "SeriesStartDateInput",
            "SeriesEndDatePickerButton" => "SeriesEndDateInput",
            "CompetitionDatePickerButton" => "CompetitionDateInput",
            _ => null
        };
        if (inputName is null || this.FindControl<TextBox>(inputName) is not { } input)
        {
            return;
        }

        var picker = new DatePicker
        {
            DayFormat = "dd",
            MonthFormat = "MM MMMM",
            YearFormat = "yyyy",
            MinWidth = 310
        };
        if (DateTime.TryParseExact(input.Text, ["dd.MM.yyyy", "d.M.yyyy"],
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            picker.SelectedDate = new DateTimeOffset(date);
        }

        var flyout = new Flyout { Content = picker };
        button.Flyout = flyout;
        picker.SelectedDateChanged += (_, _) =>
        {
            if (picker.SelectedDate is { } selected)
            {
                input.Text = selected.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);
                flyout.Hide();
            }
        };
        flyout.ShowAt(button);
    }
}
