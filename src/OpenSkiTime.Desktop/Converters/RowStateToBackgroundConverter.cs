using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using OpenSkiTime.Desktop.Models;

namespace OpenSkiTime.Desktop.Converters;

/// <summary>
/// Converts a <see cref="RowState"/> to a background brush for DataGrid rows.
/// Colour palette per research.md R-007.
/// </summary>
public sealed class RowStateToBackgroundConverter : IValueConverter
{
    private static readonly IBrush Added = SolidColorBrush.Parse("#d1fae5");
    private static readonly IBrush Edited = SolidColorBrush.Parse("#fef9c3");
    private static readonly IBrush Deleted = SolidColorBrush.Parse("#fee2e2");
    private static readonly IBrush PasteHighlighted = SolidColorBrush.Parse("#dbeafe");
    private static readonly IBrush Unchanged = Brushes.Transparent;

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is RowState state
            ? state switch
            {
                RowState.Added => Added,
                RowState.Edited => Edited,
                RowState.Deleted => Deleted,
                RowState.PasteHighlighted => PasteHighlighted,
                _ => Unchanged,
            }
            : Unchanged;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
