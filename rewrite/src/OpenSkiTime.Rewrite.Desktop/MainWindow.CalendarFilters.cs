using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Threading;

namespace OpenSkiTime.Rewrite.Desktop;

public partial class MainWindow
{
    internal Flyout? CalendarFilterMenu { get; private set; }
    private void UpdateCalendarHeaders()
    {
        if (DataContext is not MainViewModel vm || this.FindControl<DataGrid>("SeriesCalendarEventsGrid") is not { } grid) { return; }
        foreach (var c in grid.Columns)
        {
            if (c.Header is Button { Tag: string key } button && Enum.TryParse<CalendarColumn>(key, out var column))
            { button.Content = vm.CalendarHeader(column, key.ToUpperInvariant()); }
        }
    }
    private void CalendarFilter_Click(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not Button { Tag: string key } anchor || DataContext is not MainViewModel vm
            || vm.IsSeriesCalendarBusy || !Enum.TryParse<CalendarColumn>(key, out var column)) { return; }
        var options = vm.CalendarFilterValues(column);
        var chosen = options.Where(x => x.Selected).Select(x => x.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var menu = new Flyout { Placement = PlacementMode.Bottom };
        CalendarFilterMenu = menu;
        var panel = new StackPanel { Width = 280, Spacing = 6 };
        var sort = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 6 };
        void AddAction(StackPanel target, string label, Action action, string name)
        {
            var button = new Button { Content = label, Name = name };
            button.Click += (_, args) => { args.Handled = true; action(); menu.Hide(); anchor.Focus(); };
            target.Children.Add(button);
        }
        AddAction(sort, "Sort ↑", () => vm.SortCalendar(column, false), "CalendarSortAscending");
        AddAction(sort, "Sort ↓", () => vm.SortCalendar(column, true), "CalendarSortDescending");
        panel.Children.Add(sort);
        var search = new TextBox { Watermark = "Search values", Name = "CalendarFilterSearch" };
        panel.Children.Add(search);
        var selectAll = new CheckBox { Content = "Select all shown", IsThreeState = true, Name = "CalendarFilterSelectAll" };
        panel.Children.Add(selectAll);
        var choices = new StackPanel { Spacing = 2 };
        panel.Children.Add(new ScrollViewer { Content = choices, Height = 200 });
        CalendarFilterValue[] visible = [];
        var updating = false;
        void UpdateSelectAll()
        {
            updating = true;
            var count = visible.Count(x => chosen.Contains(x.Value));
            selectAll.IsChecked = count == 0 ? false : count == visible.Length ? true : null;
            updating = false;
        }
        void RenderChoices()
        {
            visible = options.Where(x => x.Label.Contains(search.Text?.Trim() ?? "", StringComparison.OrdinalIgnoreCase)).ToArray();
            choices.Children.Clear();
            foreach (var value in visible)
            {
                var box = new CheckBox { Content = value.Label, IsChecked = chosen.Contains(value.Value) };
                box.IsCheckedChanged += (_, _) => { if (box.IsChecked == true) { chosen.Add(value.Value); } else { chosen.Remove(value.Value); } UpdateSelectAll(); };
                choices.Children.Add(box);
            }
            if (visible.Length == 0) { choices.Children.Add(new TextBlock { Text = "No matching values" }); }
            UpdateSelectAll();
        }
        selectAll.IsCheckedChanged += (_, _) =>
        {
            if (updating) { return; }
            foreach (var value in visible) { if (selectAll.IsChecked == true) { chosen.Add(value.Value); } else { chosen.Remove(value.Value); } }
            RenderChoices();
        };
        search.TextChanged += (_, _) => RenderChoices();
        search.KeyDown += (_, args) =>
        {
            if (args.Key != Key.Enter) { return; }
            vm.ApplyCalendarFilter(column, visible.Where(x => chosen.Contains(x.Value)).Select(x => x.Value));
            args.Handled = true; menu.Hide(); anchor.Focus();
        };
        panel.Children.Add(new TextBlock { Text = "Apply uses checked values shown.", FontSize = 11 });
        var actions = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 6 };
        AddAction(actions, "Apply", () => vm.ApplyCalendarFilter(column, visible.Where(x => chosen.Contains(x.Value)).Select(x => x.Value)), "CalendarFilterApply");
        AddAction(actions, "Clear filter", () => vm.ClearCalendarFilter(column), "CalendarFilterClear");
        AddAction(actions, "Cancel", () => { }, "CalendarFilterCancel");
        panel.Children.Add(actions); menu.Content = panel;
        RenderChoices(); menu.ShowAt(anchor);
        Dispatcher.UIThread.Post(() => search.Focus());
    }
}
