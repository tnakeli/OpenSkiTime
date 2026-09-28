using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using OpenSkiTime.Rewrite.Application;
using OpenSkiTime.Rewrite.Desktop;
using OpenSkiTime.Rewrite.Domain;
using OpenSkiTime.Rewrite.Persistence;
using Xunit;

namespace OpenSkiTime.Rewrite.Tests;

public sealed class ResultsVisualTests
{
    [AvaloniaFact]
    public async Task PenaltyReviewViewRendersAtLaptopWidth()
    {
        await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore());
        var window = new Window { Width = 1366, Height = 900, WindowState = WindowState.Normal };
        using var vm = new MainViewModel(workspace, new AvaloniaFileDialogs(window));
        vm.ResultsCompetition = new CompetitionDetails(Guid.NewGuid(),
            new("Northern Slalom", "SL Women", new(2026, 9, 28), Discipline.Slalom, RaceType.Fis, 2, 0, "1234"));
        vm.Competitions.Add(vm.ResultsCompetition);
        vm.ResultsState = "10 classified · 2 not classified. Review penalty and race information with the TD.";
        vm.ResultsPointsList = "FIS list 1327 · valid 2026-09-24 – 2026-09-30";
        vm.ResultsPenaltySummary = "A 85.00 + B 80.00 − C 62.00 = 10.30 · adder 0.00 · applied 10.30";
        vm.ResultsMinimum = "0"; vm.ResultsMaximum = "999"; vm.ResultsAdder = "0";
        for (var i = 1; i <= 12; i++)
        {
            vm.ResultRows.Add(new(i <= 10 ? i.ToString(System.Globalization.CultureInfo.InvariantCulture) : "—", i,
                (990000 + i).ToString(System.Globalization.CultureInfo.InvariantCulture), "RACER " + i,
                "0:52.31", i <= 10 ? "0:51.42" : "—", i <= 10 ? "1:43.73" : "—",
                i <= 10 ? "Finished" : "DNF 2", i <= 10 ? "37.12" : ""));
            if (i <= 5)
            {
                vm.ResultBestClassified.Add(new(i, "RACER " + i, "15.00", "15.00", "12.00", ""));
                vm.ResultBestStarted.Add(new(i, "RACER " + i, "15.00", "15.00", "—", ""));
            }
        }
        window.Content = new ResultsView { DataContext = vm };
        window.Show(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
        var output = Environment.GetEnvironmentVariable("OPENSKITIME_M7_VISUAL_DIR");
        if (!string.IsNullOrWhiteSpace(output))
        {
            Directory.CreateDirectory(output);
            using var bitmap = new RenderTargetBitmap(new PixelSize(1366, 900), new Vector(96, 96));
            bitmap.Render(window); bitmap.Save(Path.Combine(output, "results-review.png"));
        }
        Assert.True(window.IsVisible);
        window.Close();
    }
}
