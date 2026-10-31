using Avalonia.Controls;

namespace OpenSkiTime.Desktop;

/// <summary>
/// One row of a form on the shared 12-column grid. Fields set <c>Grid.Column</c> and <c>Grid.ColumnSpan</c>, so every
/// row of the same width puts field edges on the same lines. See "UI guidelines" in AGENTS.md.
/// </summary>
public class FormRow : Grid
{
    public const int Columns = 12;
    public const double Gap = 10;

    public FormRow()
    {
        for (var i = 0; i < Columns; i++) { ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star)); }
        ColumnSpacing = Gap;
    }
}
