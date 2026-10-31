using Avalonia.Controls;

namespace OpenSkiTime.Desktop;

/// <summary>
/// The content column of a form view: left aligned and at most <c>ContentMaxWidth</c> wide. Every card, grid and
/// action bar of the view goes inside one PageColumn, so they all share the same left and right edge.
/// See "UI guidelines" in AGENTS.md.
/// </summary>
public class PageColumn : Grid
{
    public PageColumn()
    {
        var content = new ColumnDefinition(1, GridUnitType.Star);
        content.Bind(ColumnDefinition.MaxWidthProperty, this.GetResourceObservable("ContentMaxWidth"));
        ColumnDefinitions.Add(content);
        ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
    }
}
