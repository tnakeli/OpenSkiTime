using System.Globalization;
using Avalonia.Data.Converters;

namespace OpenSkiTime.Desktop.Converters;

/// <summary>
/// Returns true when an integer value is not zero. Used for the Change Log panel visibility.
/// </summary>
public sealed class NonZeroConverter : IValueConverter
{
    public static readonly NonZeroConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is int n && n != 0;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
