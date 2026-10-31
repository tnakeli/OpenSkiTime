using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OpenSkiTime.Desktop;
using Xunit;

namespace OpenSkiTime.Tests;

public sealed class PersonNameDisplayTests
{
    public sealed record Person(string Surname, string FirstName)
    {
        public string Name => Surname + " " + FirstName;
    }

    private static (string Text, FontWeight Weight)[] Runs(TextBlock text)
        => text.Inlines!.OfType<Run>().Select(x => (x.Text ?? "", x.FontWeight)).ToArray();

    [AvaloniaFact]
    public void NameColumnShowsTheSurnameInBoldCapitalsAndKeepsThePlainTextForSorting()
    {
        var column = new CompetitorNameColumn { Header = "COMPETITOR", Binding = new Avalonia.Data.Binding("Name") };
        var grid = new DataGrid { AutoGenerateColumns = false, IsReadOnly = true, ItemsSource = new[] { new Person("Korpi", "Sofia") } };
        grid.Columns.Add(column);
        grid.Columns.Add(new DataGridTextColumn { Header = "PLAIN", Binding = new Avalonia.Data.Binding("FirstName") });
        var window = new Window { Width = 600, Height = 300, Content = grid };
        window.Show(); Dispatcher.UIThread.RunJobs();

        var cell = grid.GetVisualDescendants().OfType<DataGridCell>().First(x => x.DataContext is Person);
        var text = cell.GetVisualDescendants().OfType<TextBlock>().First(x => x.Inlines?.OfType<Run>().Any() == true);
        Assert.Equal([("KORPI", FontWeight.Bold), (" ", FontWeight.Normal), ("Sofia", FontWeight.Normal)], Runs(text));
        Assert.Equal("Name", ((Avalonia.Data.Binding)column.Binding!).Path);
        // The name cell is laid out like any other text cell of the grid.
        var plain = grid.GetVisualDescendants().OfType<DataGridCell>().Where(x => x.DataContext is Person)
            .SelectMany(x => x.GetVisualDescendants().OfType<TextBlock>()).First(x => x.Text == "Sofia" && x.Inlines is not { Count: > 0 });
        Assert.Equal(plain.Margin, text.Margin);
        Assert.Equal(plain.VerticalAlignment, text.VerticalAlignment);
        window.Close();
    }

    [AvaloniaFact]
    public void NameBlockFollowsItsSurnameAndFirstName()
    {
        var block = new CompetitorNameBlock { Surname = "sumu", FirstName = "Aino" };
        var window = new Window { Content = block };
        window.Show(); Dispatcher.UIThread.RunJobs();
        Assert.Equal([("SUMU", FontWeight.Bold), (" ", FontWeight.Normal), ("Aino", FontWeight.Normal)], Runs(block));
        block.Surname = "Jänne"; block.FirstName = "Olivia"; Dispatcher.UIThread.RunJobs();
        Assert.Equal([("JÄNNE", FontWeight.Bold), (" ", FontWeight.Normal), ("Olivia", FontWeight.Normal)], Runs(block));
        window.Close();
    }
}
