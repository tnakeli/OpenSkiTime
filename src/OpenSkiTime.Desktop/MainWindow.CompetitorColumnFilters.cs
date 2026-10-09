using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace OpenSkiTime.Desktop;

public partial class MainWindow
{
    internal Flyout? CompetitorFilterMenu { get; private set; }

    private void InstallCompetitorHeaders(DataGrid grid)
    {
        foreach (var column in grid.Columns.Where(c => !string.IsNullOrEmpty(c.SortMemberPath)))
        {
            var captured = column;
            column.HeaderTemplate = new FuncDataTemplate<string>((label, _) => CreateCompetitorHeader(grid, captured, label ?? ""));
        }
    }

    private Grid CreateCompetitorHeader(DataGrid grid, DataGridColumn column, string label)
    {
        var key = column.SortMemberPath;
        var ascending = label.EndsWith(" ↑", StringComparison.Ordinal);
        var descending = label.EndsWith(" ↓", StringComparison.Ordinal);
        var text = ascending || descending ? label[..^2] : label;
        if (column.Tag is "competition-entry")
        {
            var split = text.LastIndexOf(' ');
            if (split > 0 && text[(split + 1)..].Contains('.')) { text = text[..split] + "\n" + text[(split + 1)..]; }
        }
        var root = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("*,14,14") };
        var title = new TextBlock { Text = text, FontSize = 11, LineHeight = 14, TextWrapping = TextWrapping.WrapWithOverflow, VerticalAlignment = VerticalAlignment.Center };
        if (key.StartsWith("entry:", StringComparison.Ordinal) && Guid.TryParse(key[6..], out var competitionId)
            && _gridViewModel?.Competitions.FirstOrDefault(x => x.Id == competitionId) is { } competition)
        { ToolTip.SetTip(title, $"{competition.Values.ShortLabel} · {competition.Values.Name}"); }
        root.Children.Add(title);
        Button Icon(string name, int position)
        {
            var button = new Button { Name = name + key, Width = 14, Height = 24, MinWidth = 0, MinHeight = 0,
                Padding = new Thickness(0), Background = Brushes.Transparent, BorderThickness = new Thickness(0),
                HorizontalContentAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(button, position); root.Children.Add(button); return button;
        }
        var sort = Icon("CompetitorSort_", 1);
        sort.Content = new TextBlock { Text = ascending ? "↑" : descending ? "↓" : "↕", FontSize = 12 };
        ToolTip.SetTip(sort, "Sort " + text.Replace('\n', ' ') + " ascending / descending");
        sort.Click += (_, args) => { args.Handled = true; SortCompetitorColumn(grid, column); };
        var filter = Icon("CompetitorFilterButton_", 2);
        var funnel = new Avalonia.Controls.Shapes.Path { Data = Geometry.Parse("M1,1 L11,1 L7,6 L7,10 L5,11 L5,6 Z"), Width = 12, Height = 12,
            StrokeThickness = 1, Stroke = Brush.Parse("#64748B") };
        filter.Content = funnel;
        void UpdateIndicator()
        {
            var value = _gridViewModel?.CompetitorColumnFilter(key) ?? "";
            var active = !string.IsNullOrWhiteSpace(value);
            title.FontStyle = active ? FontStyle.Italic : FontStyle.Normal;
            funnel.Fill = active ? Brush.Parse("#008A9B") : Brushes.Transparent;
            funnel.Stroke = active ? Brush.Parse("#008A9B") : Brush.Parse("#64748B");
            ToolTip.SetTip(filter, active ? $"Filter: {value} — click to clear" : "Filter " + text.Replace('\n', ' '));
        }
        bool Apply(string value)
        {
            if (_gridViewModel is not { } vm || !grid.CommitEdit(DataGridEditingUnit.Cell, true) || !grid.CommitEdit(DataGridEditingUnit.Row, true)) { return false; }
            var selected = grid.SelectedItems.OfType<CompetitorGridRow>().ToArray();
            vm.SetCompetitorColumnFilter(key, value);
            RestoreCompetitorSelection(grid, selected);
            UpdateIndicator(); return true;
        }
        filter.Click += (_, args) =>
        {
            args.Handled = true;
            if (!string.IsNullOrWhiteSpace(_gridViewModel?.CompetitorColumnFilter(key))) { Apply(""); return; }
            CompetitorFilterMenu?.Hide();
            var menu = new Flyout { Placement = PlacementMode.Bottom };
            CompetitorFilterMenu = menu;
            var panel = new StackPanel { Width = 210, Spacing = 6 };
            panel.Children.Add(new TextBlock { Text = "Filter " + text.Replace('\n', ' ') });
            var editor = new TextBox { Name = "CompetitorFilter_" + key, Watermark = column.Tag is "competition-entry" ? "Yes / No" : "Search text" };
            panel.Children.Add(editor);
            void Submit() { if (Apply(editor.Text ?? "")) { menu.Hide(); filter.Focus(); } }
            editor.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter) { e.Handled = true; Submit(); }
                else if (e.Key == Key.Escape) { e.Handled = true; menu.Hide(); filter.Focus(); }
            };
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            var apply = new Button { Content = "Apply", Name = "CompetitorFilterApply", Classes = { "primaryAction" } };
            var cancel = new Button { Content = "Cancel", Name = "CompetitorFilterCancel", Classes = { "secondaryAction" } };
            apply.Click += (_, e) => { e.Handled = true; Submit(); };
            cancel.Click += (_, e) => { e.Handled = true; menu.Hide(); filter.Focus(); };
            actions.Children.Add(apply); actions.Children.Add(cancel); panel.Children.Add(actions);
            menu.Content = panel; menu.ShowAt(filter);
            Dispatcher.UIThread.Post(() => editor.Focus());
        };
        UpdateIndicator();
        return root;
    }
}
