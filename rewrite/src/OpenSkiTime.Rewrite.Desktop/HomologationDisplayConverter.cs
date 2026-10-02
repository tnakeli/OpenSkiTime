using System.Globalization;
using Avalonia.Data.Converters;
using OpenSkiTime.Rewrite.Domain;

namespace OpenSkiTime.Rewrite.Desktop;

public sealed class HomologationDisplayConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not CompetitionValues race) { return string.Empty; }
        var number = race.HomologationNumber ?? string.Empty;
        if (race.StartAltitudeMeters is null && race.FinishAltitudeMeters is null) { return number; }
        var start = race.StartAltitudeMeters is { } startMeters ? FormattableString.Invariant($"{startMeters}m") : "—";
        var finish = race.FinishAltitudeMeters is { } finishMeters ? FormattableString.Invariant($"{finishMeters}m") : "—";
        return $"{number} {start} - {finish}".Trim();
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
