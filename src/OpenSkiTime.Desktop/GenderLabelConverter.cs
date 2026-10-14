using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;
using OpenSkiTime.Application;
using OpenSkiTime.Domain;

namespace OpenSkiTime.Desktop;

public sealed class GenderLabelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value switch
        {
            Gender gender => GenderLabels.Format(gender),
            // A competition's FIS calendar code (M, W, A) shows as Men, Women or Mixed.
            string code => CompetitionGenderLabels.Format(code),
            _ => string.Empty,
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => BindingOperations.DoNothing;
}
