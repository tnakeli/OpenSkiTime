using System.Globalization;
using System.ComponentModel;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;

namespace OpenSkiTime.Desktop;

internal sealed class ColumnGridController<T> where T : class
{
    private readonly DataGrid _grid;
    private readonly DataGridCollectionView _view;
    private readonly Func<T, string, object?> _value;
    private readonly Action<bool>? _changing;
    private readonly Dictionary<string, string> _filters = new(StringComparer.Ordinal);
    private readonly List<ColumnHeaderControls> _headers = [];
    private string? _sortedKey;
    private bool _descending;
    internal Flyout? FilterMenu { get; private set; }

    public ColumnGridController(DataGrid grid, DataGridCollectionView view, string prefix, Func<T, string, object?> value,
        Func<T, string, string>? textValue = null, Action<bool>? changing = null)
    {
        _grid = grid; _view = view; _value = value; _changing = changing;
        grid.ItemsSource = view; grid.CanUserSortColumns = true; grid.CanUserResizeColumns = true;
        grid.Sorting += OnSorting;
        view.Filter = row => row is T item && _filters.All(filter => Matches(
            textValue?.Invoke(item, filter.Key) ?? Text(value(item, filter.Key)), filter.Value));
        foreach (var column in grid.Columns.OfType<DataGridTextColumn>())
        {
            if (column.Header is not string { Length: > 0 } label || column.Binding is not Binding binding) { continue; }
            var key = string.IsNullOrEmpty(column.SortMemberPath) ? binding.Path : column.SortMemberPath;
            if (string.IsNullOrEmpty(key)) { continue; }
            column.SortMemberPath = key;
            ColumnHeaderControls.ReserveWidth(column, label);
            // The template reads the header it is given, so a column may rename itself (such as the current run).
            column.HeaderTemplate = new FuncDataTemplate<string>((text, _) =>
            {
                var header = new ColumnHeaderControls(prefix, key, string.IsNullOrEmpty(text) ? label : text, () => _filters.GetValueOrDefault(key, ""),
                    text => ApplyFilter(key, text), () => _sortedKey == key ? _descending : null,
                    () => Sort(key), menu => { FilterMenu?.Hide(); FilterMenu = menu; });
                _headers.Add(header); return header.Content;
            });
        }
    }

    private void OnSorting(object? sender, DataGridColumnEventArgs e)
    { e.Handled = true; if (!string.IsNullOrEmpty(e.Column.SortMemberPath)) { Sort(e.Column.SortMemberPath); } }

    private void Sort(string key)
    {
        _changing?.Invoke(true);
        try
        {
            var selected = _grid.SelectedItems.OfType<T>().ToArray();
            _descending = _sortedKey == key && !_descending; _sortedKey = key;
            _view.SortDescriptions.Clear();
            var comparer = Comparer<object>.Create((left, right) => Compare(_value((T)left, key), _value((T)right, key)));
            _view.SortDescriptions.Add(DataGridSortDescription.FromComparer(comparer,
                _descending ? ListSortDirection.Descending : ListSortDirection.Ascending));
            _view.Refresh();
            RestoreSelection(selected); foreach (var header in _headers) { header.Refresh(); }
        }
        finally { _changing?.Invoke(false); }
    }

    private void ApplyFilter(string key, string text)
    {
        _changing?.Invoke(true);
        try
        {
            var selected = _grid.SelectedItems.OfType<T>().ToArray();
            if (string.IsNullOrWhiteSpace(text)) { _filters.Remove(key); } else { _filters[key] = text; }
            _view.Refresh(); RestoreSelection(selected);
        }
        finally { _changing?.Invoke(false); }
    }

    private void RestoreSelection(T[] selected)
    {
        foreach (var row in _grid.SelectedItems.OfType<T>().Where(row => !_view.Contains(row)).ToArray())
        { _grid.SelectedItems.Remove(row); }
        foreach (var row in selected.Where(row => _view.Contains(row) && !_grid.SelectedItems.Contains(row)))
        { _grid.SelectedItems.Add(row); }
    }

    internal static bool Matches(string value, string text) => text.Split((char[]?)null,
        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).All(term => value.Contains(term, StringComparison.OrdinalIgnoreCase));
    internal static string Text(object? value) => value switch
    {
        DateOnly date => date.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture),
        IFormattable number => number.ToString(null, CultureInfo.InvariantCulture), _ => value?.ToString() ?? ""
    };
    private static int Compare(object? left, object? right) => left is null ? right is null ? 0 : -1
        : right is null ? 1 : left is string text ? StringComparer.OrdinalIgnoreCase.Compare(text, Text(right))
        : left is IComparable comparable ? comparable.CompareTo(right) : StringComparer.OrdinalIgnoreCase.Compare(Text(left), Text(right));

    public void Detach() { _grid.Sorting -= OnSorting; FilterMenu?.Hide(); }
}
