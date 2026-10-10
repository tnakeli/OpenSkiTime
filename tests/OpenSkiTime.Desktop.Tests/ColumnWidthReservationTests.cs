using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using OpenSkiTime.Desktop;
using Xunit;

namespace OpenSkiTime.Tests;

public sealed class ColumnWidthReservationTests
{
    [AvaloniaFact]
    public void ReservationKeepsTheDeclaredFloorWhenHeadersAreReinstalledAfterAResize()
    {
        var column = new DataGridTextColumn { Header = "DATE", Width = new DataGridLength(105) };
        ColumnHeaderControls.ReserveWidth(column, "DATE");
        Assert.Equal(105, column.MinWidth);
        column.Width = new DataGridLength(240); // the operator widens the column
        ColumnHeaderControls.ReserveWidth(column, "DATE"); // a later run reinstalls the header controls
        Assert.Equal(105, column.MinWidth);
        var narrow = new DataGridTextColumn { Header = "RK", Width = new DataGridLength(42) };
        ColumnHeaderControls.ReserveWidth(narrow, "RK");
        Assert.Equal(ColumnHeaderControls.RequiredWidth("RK"), narrow.MinWidth);
        Assert.True(narrow.MinWidth > 42);
        var star = new DataGridTextColumn { Header = "HOMOLOGATION", Width = new DataGridLength(1, DataGridLengthUnitType.Star), MinWidth = 40 };
        ColumnHeaderControls.ReserveWidth(star, "HOMOLOGATION");
        Assert.Equal(ColumnHeaderControls.RequiredWidth("HOMOLOGATION"), star.MinWidth);
    }
}
