using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;
using OpenSkiTime.Application;

namespace OpenSkiTime.Desktop;

/// <summary>Turns the plausibility finding of a typed report time into a cell class and a tooltip.</summary>
public sealed class EvidenceIssueConverter(EvidenceIssueConverter.Part part) : IValueConverter
{
    public enum Part { IsError, IsWarning, Message }

    public static EvidenceIssueConverter IsError { get; } = new(Part.IsError);
    public static EvidenceIssueConverter IsWarning { get; } = new(Part.IsWarning);
    public static EvidenceIssueConverter Message { get; } = new(Part.Message);

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var finding = value as EvidenceFinding;
        return part switch
        {
            Part.IsError => finding?.Severity == EvidenceSeverity.Error,
            Part.IsWarning => finding?.Severity == EvidenceSeverity.Warning,
            _ => finding?.Message,
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => BindingOperations.DoNothing;
}
