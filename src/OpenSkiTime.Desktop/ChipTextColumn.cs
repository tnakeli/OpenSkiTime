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
