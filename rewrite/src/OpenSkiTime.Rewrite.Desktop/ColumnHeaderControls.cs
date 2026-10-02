using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace OpenSkiTime.Rewrite.Desktop;

internal sealed class ColumnHeaderControls
{
    public Grid Content { get; }
    private readonly Action _refresh;
    public void Refresh() => _refresh();

    public ColumnHeaderControls(string prefix, string key, string label, Func<string> filterValue,
        Action<string> applyFilter, Func<bool?> direction, Action sortColumn, Action<Flyout> opened,
        Func<bool>? canChange = null)
    {
        Content = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("*,14,14") };
        var title = new TextBlock { Text = label, FontSize = 11, LineHeight = 14,
            TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        Content.Children.Add(title);
        Button Icon(string name, int position)
        {
            var button = new Button { Name = prefix + name + "_" + key, Width = 14, Height = 24,
                MinWidth = 0, MinHeight = 0, Padding = new Thickness(0), Background = Brushes.Transparent,
                BorderThickness = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(button, position); Content.Children.Add(button); return button;
        }
        var sort = Icon("Sort", 1);
        var arrow = new TextBlock { FontSize = 12 }; sort.Content = arrow;
        ToolTip.SetTip(sort, "Sort " + label + " ascending / descending");
        sort.Click += (_, e) => { e.Handled = true; if (canChange?.Invoke() != false) { sortColumn(); Refresh(); } };
        var filter = Icon("FilterButton", 2);
        var funnel = new Avalonia.Controls.Shapes.Path { Data = Geometry.Parse("M1,1 L11,1 L7,6 L7,10 L5,11 L5,6 Z"),
            Width = 12, Height = 12, StrokeThickness = 1 };
        filter.Content = funnel;
        _refresh = () =>
        {
            var value = filterValue(); var active = !string.IsNullOrWhiteSpace(value);
            title.FontStyle = active ? FontStyle.Italic : FontStyle.Normal;
            funnel.Fill = active ? Brush.Parse("#008A9B") : Brushes.Transparent;
            funnel.Stroke = Brush.Parse(active ? "#008A9B" : "#64748B");
            arrow.Text = direction() is { } descending ? descending ? "↓" : "↑" : "↕";
            ToolTip.SetTip(filter, active ? $"Filter: {value} — click to clear" : "Filter " + label);
        };
        filter.Click += (_, e) =>
        {
            e.Handled = true;
            if (canChange?.Invoke() == false) { return; }
            if (!string.IsNullOrWhiteSpace(filterValue())) { applyFilter(""); Refresh(); return; }
            var menu = new Flyout { Placement = PlacementMode.Bottom };
            opened(menu);
            var panel = new StackPanel { Width = 210, Spacing = 6 };
            panel.Children.Add(new TextBlock { Text = "Filter " + label });
            var editor = new TextBox { Name = prefix + "Filter_" + key, Watermark = "Search text" };
            panel.Children.Add(editor);
            void Submit()
            {
                if (canChange?.Invoke() == false) { return; }
                applyFilter(editor.Text ?? ""); Refresh(); menu.Hide(); filter.Focus();
            }
            editor.KeyDown += (_, args) =>
            {
                if (args.Key == Key.Enter) { args.Handled = true; Submit(); }
                else if (args.Key == Key.Escape) { args.Handled = true; menu.Hide(); filter.Focus(); }
            };
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            var apply = new Button { Name = prefix + "FilterApply", Content = "Apply" };
            var cancel = new Button { Name = prefix + "FilterCancel", Content = "Cancel" };
            apply.Click += (_, args) => { args.Handled = true; Submit(); };
            cancel.Click += (_, args) => { args.Handled = true; menu.Hide(); filter.Focus(); };
            actions.Children.Add(apply); actions.Children.Add(cancel); panel.Children.Add(actions);
            menu.Content = panel; menu.ShowAt(filter); Dispatcher.UIThread.Post(() => editor.Focus());
        };
        Refresh();
    }
}
