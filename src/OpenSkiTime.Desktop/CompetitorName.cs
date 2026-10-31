using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace OpenSkiTime.Desktop;

/// <summary>
/// The OpenSkiTime person name rule: surname in capitals and bold, then the first name in regular weight
/// ("<b>KORPI</b> Sofia"). Every view, the live page and the PDF reports follow it.
/// </summary>
internal static class PersonNameText
{
    public static IValueConverter Surname { get; } = new FuncValueConverter<object?, string>(
        value => (value as string ?? value?.ToString() ?? string.Empty).ToUpper(CultureInfo.CurrentCulture));

    /// <summary>Fills the text block with a bold surname run and a regular first name run.</summary>
    public static void Fill(TextBlock text, BindingBase surname, BindingBase firstName)
    {
        var inlines = text.Inlines ??= [];
        inlines.Clear();
        var last = new Run { FontWeight = FontWeight.Bold };
        last.Bind(Run.TextProperty, surname);
        var first = new Run();
        first.Bind(Run.TextProperty, firstName);
        inlines.Add(last);
        inlines.Add(new Run(" "));
        inlines.Add(first);
    }
}

/// <summary>
/// A read-only competitor column that shows the name by the person name rule. <see cref="DataGridBoundColumn.Binding"/>
/// stays the plain "SURNAME First" text, so sorting, filtering, the column header controls and copying treat it like
/// any other text column; <see cref="SurnamePath"/> and <see cref="FirstNamePath"/> supply the two parts.
/// </summary>
public sealed class CompetitorNameColumn : DataGridTextColumn
{
    public string SurnamePath { get; set; } = "Surname";
    public string FirstNamePath { get; set; } = "FirstName";

    protected override Control GenerateElement(DataGridCell cell, object dataItem)
    {
        // A fresh text block styled like the column's own: the column's Text binding would replace the runs.
        var template = (TextBlock)base.GenerateElement(cell, dataItem);
        var text = new TextBlock { Name = template.Name, Theme = template.Theme, Margin = template.Margin,
            VerticalAlignment = template.VerticalAlignment, HorizontalAlignment = template.HorizontalAlignment };
        text.Classes.AddRange(template.Classes);
        PersonNameText.Fill(text, new Binding(SurnamePath) { Converter = PersonNameText.Surname }, new Binding(FirstNamePath));
        return text;
    }
}

/// <summary>A competitor name outside a grid (banners, panels), shown by the person name rule.</summary>
public sealed class CompetitorNameBlock : TextBlock
{
    public static readonly StyledProperty<string?> SurnameProperty = AvaloniaProperty.Register<CompetitorNameBlock, string?>(nameof(Surname));
    public static readonly StyledProperty<string?> FirstNameProperty = AvaloniaProperty.Register<CompetitorNameBlock, string?>(nameof(FirstName));

    public string? Surname { get => GetValue(SurnameProperty); set => SetValue(SurnameProperty, value); }
    public string? FirstName { get => GetValue(FirstNameProperty); set => SetValue(FirstNameProperty, value); }

    protected override Type StyleKeyOverride => typeof(TextBlock);

    public CompetitorNameBlock()
    {
        PersonNameText.Fill(this,
            new Binding(nameof(Surname)) { Source = this, Converter = PersonNameText.Surname },
            new Binding(nameof(FirstName)) { Source = this });
    }
}

/// <summary>A read-only surname column for grids that keep surname and first name apart: bold, like every surname.</summary>
public sealed class SurnameColumn : DataGridTextColumn
{
    protected override Control GenerateElement(DataGridCell cell, object dataItem)
    {
        var text = (TextBlock)base.GenerateElement(cell, dataItem);
        text.FontWeight = FontWeight.Bold;
        return text;
    }
}
