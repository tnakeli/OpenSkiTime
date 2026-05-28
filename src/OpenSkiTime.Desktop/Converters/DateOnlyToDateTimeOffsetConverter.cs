using System.Globalization;
using Avalonia.Data.Converters;

namespace OpenSkiTime.Desktop.Converters;

/// <summary>
/// Avalonia's <c>DatePicker</c> binds to <see cref="DateTimeOffset"/>; our
/// domain uses <see cref="DateOnly"/> (no timezone, FR-003). This converter
/// bridges the two without introducing any time-of-day component.
/// </summary>
public sealed class DateOnlyToDateTimeOffsetConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is DateOnly d)
        {
            return new DateTimeOffset(d.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        }
        return null;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value switch
        {
            DateTimeOffset dto => DateOnly.FromDateTime(dto.Date),
            DateTime dt => DateOnly.FromDateTime(dt.Date),
            _ => default(DateOnly),
        };
    }
}
