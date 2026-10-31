using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Data.Converters;

namespace OpenSkiTime.Desktop;

/// <summary>
/// A read-only text column that shows each value as a chip (see <c>Border.chip</c> in App.axaml). It stays a
/// <see cref="DataGridTextColumn"/>, so sorting, filtering and the column header controls treat it like any other
/// text column. Empty values show no chip.
/// </summary>
public sealed class ChipTextColumn : DataGridTextColumn
{
    /// <summary>Optional chip colour class, such as <c>green</c>; blue when empty.</summary>
    public string? ChipClass { get; set; }

    protected override Control GenerateElement(DataGridCell cell, object dataItem)
    {
        var text = (TextBlock)base.GenerateElement(cell, dataItem);
        var chip = new Border { Child = text, Margin = new Avalonia.Thickness(4, 0) };
        chip.Classes.Add("chip");
        if (!string.IsNullOrEmpty(ChipClass)) { chip.Classes.Add(ChipClass); }
        chip.Bind(Avalonia.Visual.IsVisibleProperty, new Binding(nameof(TextBlock.Text))
        { Source = text, Converter = StringConverters.IsNotNullOrEmpty });
        return chip;
    }
}

/// <summary>
/// A time column that shows a result status (DNS, DNF, DSQ, NPS, …) as a red chip and a time as plain text. It stays a
/// <see cref="DataGridTextColumn"/>, so sorting, filtering and the column header controls are unchanged.
/// </summary>
public sealed class TimeOrStatusColumn : DataGridTextColumn
{
    private static readonly IValueConverter s_isStatus = new FuncValueConverter<string?, bool>(IsStatus);
    private static readonly IValueConverter s_isTime = new FuncValueConverter<string?, bool>(x => !IsStatus(x));

    // A time starts with a digit or is a dash; a status is a word such as DSQ or "On course".
    public static bool IsStatus(string? text) => !string.IsNullOrWhiteSpace(text) && char.IsLetter(text.TrimStart()[0]);

    protected override Control GenerateElement(DataGridCell cell, object dataItem)
    {
        var time = (TextBlock)base.GenerateElement(cell, dataItem);
        time.Bind(Avalonia.Visual.IsVisibleProperty, new Binding(nameof(TextBlock.Text)) { Source = time, Converter = s_isTime });
        var label = new TextBlock();
        label.Bind(TextBlock.TextProperty, new Binding(nameof(TextBlock.Text)) { Source = time });
        var chip = new Border { Child = label, Margin = new Avalonia.Thickness(4, 0) };
        chip.Classes.Add("chip"); chip.Classes.Add("red");
        chip.Bind(Avalonia.Visual.IsVisibleProperty, new Binding(nameof(TextBlock.Text)) { Source = time, Converter = s_isStatus });
        return new Panel { Children = { time, chip } };
    }
}
