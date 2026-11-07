using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OpenSkiTime.Application;
using OpenSkiTime.Desktop;
using Xunit;

namespace OpenSkiTime.Tests;

public sealed class NationFlagTests
{
    public sealed record Row(string Name, string Nation);

    [Fact]
    public void EveryNationOfTheReferenceListHasAFlagPictureAndUnknownCodesHaveNone()
    {
        Assert.All(FisReferenceData.Nations, nation => Assert.NotNull(NationFlags.Png(nation.Code)));
        Assert.All(NationFlags.Codes, code => Assert.True(NationFlags.Png(code) is { Length: > 100 } png && png[1] == (byte)'P' && png[2] == (byte)'N' && png[3] == (byte)'G', code));
        Assert.Equal("FI", NationFlags.IsoCode(" fin "));
        Assert.Null(NationFlags.Png("XXX")); Assert.Null(NationFlags.Png("")); Assert.Null(NationFlags.Png(null));
    }

    [Fact]
    public void TheDesktopFlagsAreTheOnesTheLiveViewDraws()
    {
        // The live page keeps its own list in JavaScript; the desktop list must not drift from it.
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "OpenSkiTime.slnx"))) { directory = directory.Parent; }
        Assert.NotNull(directory);
        var script = File.ReadAllText(Path.Combine(directory.FullName, "src", "OpenSkiTime.LiveTiming.Server", "wwwroot", "live-nations.js"));
        var live = System.Text.RegularExpressions.Regex.Matches(script, @"\b([A-Z]{3}): \['([A-Z]{2})'")
            .ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value, StringComparer.Ordinal);
        Assert.True(live.Count > 100);
        Assert.Equal(live.Keys.Order(StringComparer.Ordinal), NationFlags.Codes.Order(StringComparer.Ordinal));
        Assert.All(live, pair => Assert.Equal(pair.Value, NationFlags.IsoCode(pair.Key)));
    }

    [AvaloniaFact]
    public void TheNationColumnShowsTheFlagBeforeTheCodeAndKeepsSorting()
    {
        var rows = new[] { new Row("A", "FIN"), new Row("B", "CAN"), new Row("C", "XXX") };
        var grid = new DataGrid { ItemsSource = rows, AutoGenerateColumns = false, Width = 300, Height = 200 };
        grid.Columns.Add(new DataGridTextColumn { Header = "NAME", Binding = new Avalonia.Data.Binding(nameof(Row.Name)) });
        var column = new NationColumn { Header = "NAT", Binding = new Avalonia.Data.Binding(nameof(Row.Nation)), Width = new DataGridLength(90) };
        grid.Columns.Add(column);
        var window = new Window { Width = 320, Height = 240, Content = grid };
        window.Show(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
        try
        {
            var images = window.GetVisualDescendants().OfType<Image>().Where(x => x.Classes.Contains("flag")).ToArray();
            Assert.Equal(3, images.Length);
            Assert.Equal(2, images.Count(x => x.Source is Bitmap));
            Assert.Equal(["FIN", "CAN", "XXX"], images.Select(x => ((StackPanel)x.Parent!).Children.OfType<TextBlock>().Single().Text));
            Assert.Same(NationFlagConverter.Flag("FIN"), NationFlagConverter.Flag("fin"));
            Assert.Null(NationFlagConverter.Flag("XXX"));
            Assert.True(column.CanUserSort);
        }
        finally { window.Close(); }
    }
}
