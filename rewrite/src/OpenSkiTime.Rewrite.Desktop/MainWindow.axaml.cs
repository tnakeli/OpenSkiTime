using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace OpenSkiTime.Rewrite.Desktop;

public partial class MainWindow : Window
{
    private MainViewModel? _gridViewModel;
    private DataGridColumn? _sortedCompetitorColumn;
    private string? _sortedCompetitorHeader;
    private bool _competitorSortDescending;
    private CompetitorGridRow? _entryClickedRow;
    private CompetitorGridRow[] _entrySelectionBeforeClick = [];

    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);
        DataContextChanged += (_, _) => BindCompetitionColumns();
        SizeChanged += (_, _) => UpdateCompetitorGridHeight();
        Closing += (_, e) => { if (DataContext is MainViewModel vm && !vm.CanLeaveDrawInput()) { e.Cancel = true; } };
        AddHandler(KeyDownEvent, OnGridKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(TextInputEvent, OnGridTextInput, handledEventsToo: true);
        AddHandler(PointerPressedEvent, OnGridPointerPressed, handledEventsToo: true);
    }

    private void BindCompetitionColumns()
    {
        if (_gridViewModel is not null)
        {
            _gridViewModel.Competitions.CollectionChanged -= OnCompetitionsChanged;
            _gridViewModel.RecentFiles.CollectionChanged -= OnRecentFilesChanged;
            _gridViewModel.PropertyChanged -= OnGridViewModelPropertyChanged;
        }
        _gridViewModel = DataContext as MainViewModel;
        if (_gridViewModel is not null)
        {
            _gridViewModel.Competitions.CollectionChanged += OnCompetitionsChanged;
            _gridViewModel.RecentFiles.CollectionChanged += OnRecentFilesChanged;
            _gridViewModel.PropertyChanged += OnGridViewModelPropertyChanged;
        }
        RebuildCompetitionColumns();
        RebuildRecentMenu();
        UpdateCompetitorGridHeight();
    }

    private void OnCompetitionsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        => RebuildCompetitionColumns();

    private void OnRecentFilesChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        => RebuildRecentMenu();

    private async void DrawMenu_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button || DataContext is not MainViewModel vm || vm.IsDrawBusy) { return; }
        await vm.RefreshDrawMenuCommand.ExecuteAsync(null);
        var menu = new MenuFlyout { Placement = Avalonia.Controls.PlacementMode.Bottom };
        foreach (var competition in vm.DrawMenu)
        {
            var item = new MenuItem { Header = $"{competition.Competition.Values.ShortLabel} · {competition.Competition.Values.Name}" };
            foreach (var run in competition.Runs)
            {
                item.Items.Add(new MenuItem { Header = $"Run {run}", Command = vm.OpenDrawRunCommand,
                    CommandParameter = new DrawDestination(competition.Competition, run) });
            }
            menu.Items.Add(item);
        }
        if (menu.Items.Count == 0) { menu.Items.Add(new MenuItem { Header = "Add a competition first", IsEnabled = false }); }
        button.Flyout = menu;
        menu.ShowAt(button);
    }

    private void RebuildRecentMenu()
    {
        if (this.FindControl<SplitButton>("OpenFileButton")?.Flyout is not MenuFlyout flyout) { return; }
        flyout.Items.Clear();
        if (_gridViewModel is null || _gridViewModel.RecentFiles.Count == 0)
        {
            flyout.Items.Add(new MenuItem { Header = "No recent files", IsEnabled = false });
            return;
        }
        foreach (var path in _gridViewModel.RecentFiles)
        {
            var header = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 10 };
            header.Children.Add(new TextBlock { Text = Path.GetFileName(path), FontWeight = Avalonia.Media.FontWeight.SemiBold });
            var directory = new TextBlock
            {
                Text = Path.GetDirectoryName(path), MaxWidth = 300,
                TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis,
            };
            directory.Classes.Add("supportingText");
            header.Children.Add(directory);
            var item = new MenuItem
            {
                Header = header, Command = _gridViewModel.OpenRecentSeriesCommand, CommandParameter = path,
            };
            ToolTip.SetTip(item, path);
            flyout.Items.Add(item);
        }
    }

    private void OnGridViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.CompetitorGrouping)) { ClearCompetitorSortHeader(); }
        if (e.PropertyName == nameof(MainViewModel.IsChangeReviewOpen)) { UpdateCompetitorGridHeight(); }
    }

    private void ClearCompetitorSortHeader()
    {
        if (_sortedCompetitorColumn is not null) { _sortedCompetitorColumn.Header = _sortedCompetitorHeader; }
        _sortedCompetitorColumn = null;
        _sortedCompetitorHeader = null;
        _competitorSortDescending = false;
    }

    private void RebuildCompetitionColumns()
    {
        var grid = this.FindControl<DataGrid>("CompetitorGrid");
        if (grid is null) { return; }
        if (_sortedCompetitorColumn?.Tag is string sortedTag && sortedTag == "competition-entry") { ClearCompetitorSortHeader(); }
        var items = grid.ItemsSource;
        grid.SetCurrentValue(DataGrid.ItemsSourceProperty, null);
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
                SortMemberPath = $"entry:{competition.Id}",
                IsReadOnly = true,
                CellTemplate = new FuncDataTemplate<CompetitorGridRow>((row, _) =>
                {
                    var check = new CheckBox { HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center };
                    if (row is not null && position < row.GridEntries.Count)
                    {
                        var choice = row.GridEntries[position];
                        check.Classes.Add("entryCell");
                        UpdateEntryHighlight(check, choice);
                        choice.PropertyChanged += (_, args) =>
                        {
                            if (args.PropertyName == nameof(CompetitionEntryChoice.IsChanged))
                            {
                                UpdateEntryHighlight(check, choice);
                            }
                        };
                        check.Bind(CheckBox.IsCheckedProperty,
                            new Binding($"GridEntries[{position}].IsParticipating") { Mode = BindingMode.TwoWay });
                        check.Click += async (_, _) =>
                        {
                            if (_gridViewModel is not null && position < row.GridEntries.Count)
                            {
                                var selected = ReferenceEquals(_entryClickedRow, row)
                                    ? _entrySelectionBeforeClick
                                    : grid.SelectedItems.OfType<CompetitorGridRow>().ToArray();
                                _entryClickedRow = null;
                                _entrySelectionBeforeClick = [];
                                await _gridViewModel.StageGridEntryAsync(row, row.GridEntries[position], selected);
                            }
                        };
                    }
                    return check;
                }),
            });
        }
        grid.SetCurrentValue(DataGrid.ItemsSourceProperty, items);
    }

    private static void UpdateEntryHighlight(CheckBox check, CompetitionEntryChoice choice)
    {
        if (choice.IsChanged) { check.Classes.Add("changed"); }
        else { check.Classes.Remove("changed"); }
    }

    private void UpdateCompetitorGridHeight()
    {
        if (this.FindControl<DataGrid>("CompetitorGrid") is { } grid)
        {
            grid.Height = Math.Clamp(Bounds.Height - 545 - (_gridViewModel?.IsChangeReviewOpen == true ? 155 : 0),
                260, 760);
        }
    }

    private void CompetitorGrid_Sorting(object? sender, DataGridColumnEventArgs e)
    {
        if (sender is not DataGrid || DataContext is not MainViewModel vm
            || string.IsNullOrEmpty(e.Column.SortMemberPath)) { return; }
        var descending = ReferenceEquals(_sortedCompetitorColumn, e.Column) && !_competitorSortDescending;
        e.Handled = true;
        ClearCompetitorSortHeader();
        _sortedCompetitorColumn = e.Column;
        _sortedCompetitorHeader = e.Column.Header?.ToString();
        _competitorSortDescending = descending;
        e.Column.Header = _sortedCompetitorHeader + (descending ? " ↓" : " ↑");
        vm.SortCompetitors(e.Column.SortMemberPath, descending);
    }

    private void OnGridKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainViewModel vm || !vm.IsCompetitorsSection
            || this.FindControl<DataGrid>("CompetitorGrid") is not { } grid) { return; }
        if (e.KeyModifiers == KeyModifiers.Control && e.Source is not TextBox)
        {
            if (e.Key == Key.V) { vm.PasteFromExcelCommand.Execute(null); e.Handled = true; }
            else if (e.Key == Key.C) { vm.CopySelectedCompetitorCommand.Execute(null); e.Handled = true; }
            else if (e.Key == Key.A && IsInsideGrid(e.Source, grid))
            {
                SelectAllCompetitors(grid);
                e.Handled = true;
            }
            else if (e.Key == Key.R && IsInsideGrid(e.Source, grid))
            {
                vm.RestoreSelectedRowCommand.Execute(null);
                e.Handled = true;
            }
            return;
        }
        if (e.KeyModifiers != KeyModifiers.None || !IsInsideGrid(e.Source, grid)) { return; }
        if (e.Key == Key.Delete && e.Source is not TextBox and not ComboBox)
        {
            RemoveSelectedCompetitors(grid, vm);
            e.Handled = true;
            return;
        }
        var editingText = e.Source is TextBox;
        if (e.Key == Key.Enter)
        {
            if (editingText || e.Source is ComboBox) { MoveGridCell(grid, 0, 1); }
            else { grid.BeginEdit(); }
            e.Handled = true;
        }
        else if (editingText && e.Key is Key.Tab or Key.Up or Key.Down or Key.Left or Key.Right)
        {
            var columnStep = e.Key switch { Key.Tab or Key.Right => 1, Key.Left => -1, _ => 0 };
            var rowStep = e.Key switch { Key.Up => -1, Key.Down => 1, _ => 0 };
            MoveGridCell(grid, columnStep, rowStep);
            e.Handled = true;
        }
    }

    private static bool IsInsideGrid(object? source, DataGrid grid)
        => source is Visual visual && (ReferenceEquals(visual, grid)
            || visual.GetVisualAncestors().Contains(grid));

    private void OnGridPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (this.FindControl<DataGrid>("CompetitorGrid") is not { } grid
            || !IsInsideGrid(e.Source, grid) || e.Source is not Visual visual) { return; }
        var ancestors = visual.GetVisualAncestors().Prepend(visual).ToArray();
        if (ancestors.OfType<DataGridRow>().FirstOrDefault() is not { DataContext: CompetitorGridRow row }) { return; }
        var point = e.GetCurrentPoint(this).Properties;
        if (point.IsLeftButtonPressed && ancestors.OfType<CheckBox>().Any()
            && grid.SelectedItems.Contains(row) && grid.SelectedItems.Count > 1)
        {
            _entryClickedRow = row;
            _entrySelectionBeforeClick = grid.SelectedItems.OfType<CompetitorGridRow>().ToArray();
        }
        if (point.IsRightButtonPressed)
        {
            if (!grid.SelectedItems.Contains(row)) { grid.SelectedItem = row; }
        }
    }

    private static void MoveGridCell(DataGrid grid, int columnStep, int rowStep)
    {
        var column = grid.CurrentColumn;
        grid.CommitEdit(DataGridEditingUnit.Cell, true);
        if (columnStep != 0 && column is not null)
        {
            var target = grid.Columns.IndexOf(column) + columnStep;
            while (target >= 1 && target < grid.Columns.Count && grid.Columns[target].IsReadOnly)
            {
                target += columnStep;
            }
            if (target < 1 || target >= grid.Columns.Count)
            {
                rowStep += columnStep;
                target = columnStep > 0 ? 1 : grid.Columns.Count - 1;
                while (target >= 1 && target < grid.Columns.Count && grid.Columns[target].IsReadOnly)
                {
                    target -= columnStep;
                }
            }
            if (target >= 1 && target < grid.Columns.Count) { grid.CurrentColumn = grid.Columns[target]; }
        }
        if (rowStep != 0)
        {
            grid.SelectedIndex = Math.Clamp(grid.SelectedIndex + rowStep, 0,
                (grid.ItemsSource?.Cast<object>().Count() ?? 1) - 1);
        }
        grid.Focus();
    }

    private void OnGridTextInput(object? sender, TextInputEventArgs e)
    {
        if (this.FindControl<DataGrid>("CompetitorGrid") is not { } grid
            || DataContext is not MainViewModel { IsCompetitorsSection: true }
            || !IsInsideGrid(e.Source, grid) || e.Source is TextBox or ComboBox
            || string.IsNullOrEmpty(e.Text) || char.IsControl(e.Text[0])) { return; }
        if (!grid.BeginEdit()) { return; }
        var typed = e.Text;
        Dispatcher.UIThread.Post(() =>
        {
            var editor = grid.GetVisualDescendants().OfType<TextBox>()
                .FirstOrDefault(x => x.IsVisible && ReferenceEquals(x.DataContext, grid.SelectedItem));
            if (editor is not null)
            {
                editor.Text = typed;
                editor.CaretIndex = typed.Length;
                editor.Focus();
            }
            else
            {
                var combo = grid.GetVisualDescendants().OfType<ComboBox>()
                    .FirstOrDefault(x => x.IsVisible && ReferenceEquals(x.DataContext, grid.SelectedItem));
                if (combo is not null)
                {
                    combo.SelectedItem = CompetitorGridRow.GenderOptions.FirstOrDefault(x =>
                        x.StartsWith(typed, StringComparison.OrdinalIgnoreCase));
                    combo.Focus();
                }
            }
        });
        e.Handled = true;
    }

    private void Navigation_Click(object? sender, RoutedEventArgs e)
    {
        if (this.FindControl<ScrollViewer>("WorkspaceScroll") is { } scroll)
        {
            scroll.Offset = Vector.Zero;
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

    private void PasteContext_Click(object? sender, RoutedEventArgs e)
        => (DataContext as MainViewModel)?.PasteFromExcelCommand.Execute(null);

    private void SelectAllContext_Click(object? sender, RoutedEventArgs e)
    {
        if (this.FindControl<DataGrid>("CompetitorGrid") is { } grid) { SelectAllCompetitors(grid); }
    }

    private static void SelectAllCompetitors(DataGrid grid)
    {
        grid.SelectedItems.Clear();
        if (grid.ItemsSource is null) { return; }
        foreach (var row in grid.ItemsSource.OfType<CompetitorGridRow>().Where(x => !x.IsPlaceholder))
        {
            grid.SelectedItems.Add(row);
        }
        grid.Focus();
    }

    private void CopyContext_Click(object? sender, RoutedEventArgs e)
        => (DataContext as MainViewModel)?.CopySelectedCompetitorCommand.Execute(null);

    private void DeleteContext_Click(object? sender, RoutedEventArgs e)
    {
        if (this.FindControl<DataGrid>("CompetitorGrid") is { } grid && DataContext is MainViewModel vm)
        {
            RemoveSelectedCompetitors(grid, vm);
        }
    }

    private static void RemoveSelectedCompetitors(DataGrid grid, MainViewModel vm)
    {
        var rows = grid.SelectedItems.OfType<CompetitorGridRow>().Where(x => !x.IsPlaceholder).ToArray();
        if (rows.Length == 0 && vm.SelectedCompetitorRow is { IsPlaceholder: false } selected)
        {
            rows = [selected];
        }
        vm.RemoveCompetitors(rows);
    }

    private void FocusFisSearch_Click(object? sender, RoutedEventArgs e)
        => this.FindControl<TextBox>("FisSearchInput")?.Focus();

    private void FisSearchResults_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is ListBox list && DataContext is MainViewModel vm)
        {
            vm.SetSelectedFisSearchResults(list.SelectedItems?.OfType<FisSearchResult>() ?? []);
        }
    }

    private void RestoreRowContext_Click(object? sender, RoutedEventArgs e)
        => (DataContext as MainViewModel)?.RestoreSelectedRowCommand.Execute(null);

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
            "FisDatePickerButton" => "FisEffectiveDateInput",
            _ => null
        };
        if (inputName is null || this.FindControl<TextBox>(inputName) is not { } input)
        {
            return;
        }

        var calendar = new Avalonia.Controls.Calendar { MinWidth = 290, FirstDayOfWeek = DayOfWeek.Monday };
        var monthLabel = new TextBlock { Margin = new Thickness(10, 8, 10, 0), FontWeight = Avalonia.Media.FontWeight.SemiBold };
        var format = button.Name == "FisDatePickerButton" ? "yyyy-MM-dd" : "dd.MM.yyyy";
        if (DateTime.TryParseExact(input.Text, button.Name == "FisDatePickerButton"
                ? ["yyyy-MM-dd"] : ["dd.MM.yyyy", "d.M.yyyy"],
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            calendar.DisplayDate = date;
            calendar.SelectedDate = date;
        }
        monthLabel.Text = calendar.DisplayDate.ToString("MM MMMM yyyy", CultureInfo.CurrentCulture);
        calendar.DisplayDateChanged += (_, _) =>
            monthLabel.Text = calendar.DisplayDate.ToString("MM MMMM yyyy", CultureInfo.CurrentCulture);
        var flyout = new Flyout { Content = new StackPanel { Children = { monthLabel, calendar } } };
        button.Flyout = flyout;
        calendar.SelectedDatesChanged += (_, _) =>
        {
            if (calendar.SelectedDate is { } selected)
            {
                input.Text = selected.ToString(format, CultureInfo.InvariantCulture);
                flyout.Hide();
            }
        };
        flyout.ShowAt(button);
    }
}
