using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using OpenSkiTime.Application;
using OpenSkiTime.Desktop;
using Xunit;

namespace OpenSkiTime.Tests;

public sealed class NationSearchTests
{
    [Theory]
    [InlineData("Switzerland", "SUI")]
    [InlineData("  finland ", "FIN")]
    [InlineData("Austria", "AUT")]
    [InlineData("Austr", null)]   // Australia and Austria: keep typing
    [InlineData("FIN", null)]     // a code stays as typed
    [InlineData("ABC", null)]     // codes outside the list are allowed
    [InlineData("Atlantis", null)]
    [InlineData("", null)]
    public void ACountryNameMatchingOneNationBecomesItsCode(string text, string? code)
        => Assert.Equal(code, NationSearch.ToCode(text));

    [AvaloniaFact]
    public void TheBoxAcceptsAWholeNameAndKeepsTheCodeWhenFocusLeaves()
    {
        var box = new AutoCompleteBox { ItemsSource = FisReferenceData.Nations, ItemFilter = NationSearch.Filter, FilterMode = AutoCompleteFilterMode.Custom };
        var next = new TextBox();
        var window = new Window { Content = new StackPanel { Children = { box, next } } };
        window.Show();
        try
        {
            box.Focus();
            box.Text = "Switzerland";
            Assert.Equal("Switzerland", box.Text); // no three-character limit on the search
            next.Focus();
            Assert.Equal("SUI", box.Text);
        }
        finally { window.Close(); }
    }
}
