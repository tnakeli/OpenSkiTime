using System.Globalization;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Layout;
using Avalonia.Media.Imaging;
using OpenSkiTime.Application;

namespace OpenSkiTime.Desktop;

/// <summary>Turns a nation code into its flag picture, as the live view shows it before the code; null without a flag.</summary>
public sealed class NationFlagConverter : IValueConverter
{
    private static readonly Dictionary<string, Bitmap?> s_bitmaps = new(StringComparer.Ordinal);

    public static NationFlagConverter Instance { get; } = new();

    public static Bitmap? Flag(string? code)
    {
        var iso = NationFlags.IsoCode(code);
        if (iso is null) { return null; }
        if (s_bitmaps.TryGetValue(iso, out var known)) { return known; }
        var png = NationFlags.Png(code);
        Bitmap? bitmap = null;
        if (png is not null) { using var stream = new MemoryStream(png); bitmap = new Bitmap(stream); }
        s_bitmaps[iso] = bitmap;
        return bitmap;
    }

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => Flag(value as string);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => BindingOperations.DoNothing;
}

/// <summary>
/// A read-only nation column: the flag, then the three-letter code, as in the live view. It stays a
/// <see cref="DataGridTextColumn"/>, so sorting, filtering and the column header controls treat it like any other text column.
/// A code without a flag keeps the space of one, so the codes line up.
/// </summary>
public sealed class NationColumn : DataGridTextColumn
{
    protected override Control GenerateElement(DataGridCell cell, object dataItem)
    {
        var text = (TextBlock)base.GenerateElement(cell, dataItem);
        // The cell text keeps its usual left edge; the flag takes the place of the text's left margin.
        text.VerticalAlignment = VerticalAlignment.Center; text.Margin = new Avalonia.Thickness(0);
        var flag = new Image();
        flag.Classes.Add("flag");
        flag.Bind(Image.SourceProperty, new Binding(nameof(TextBlock.Text)) { Source = text, Converter = NationFlagConverter.Instance });
        return new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Avalonia.Thickness(12, 0), Children = { flag, text } };
    }
}
