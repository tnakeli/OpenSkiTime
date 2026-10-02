using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
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
        vm.ResultsPointsValidity = "Effective 2026-09-24–2026-09-30";
        foreach (var run in vm.ResultsRuns)
        {
            run.Course = "Test slope"; run.StartAltitude = "498"; run.FinishAltitude = "317";
            run.Drop = "181"; run.Length = "640"; run.Homologation = "12345/11/26";
            run.Conditions = "Clear"; run.Snow = "Hard"; run.StartTemperature = "0.0"; run.FinishTemperature = "-1.0";
            run.AddForerunnerCommand.Execute(null);
            run.Forerunners[0].FirstName = "Test"; run.Forerunners[0].LastName = "RUNNER"; run.Forerunners[0].Nation = "FIN";
        }
        vm.ResultsPenaltySummary = "A · best 5 in top 10: 85.00    B · best 5 starters: 80.00    C · corresponding capped race points: 62.00\nCalculated penalty (A + B − C) / 10 = 10.30\nCorrection (Z): 0.00    Category adder: 8.00    Minimum: 23.00    Maximum: 999.00\nApplied penalty: 23.00";
        vm.ResultsRuleSource = "FIS list 1327 · FIS · race level 3 · Female · FIS Points Rules 2026/27";
        vm.ResultsRuleValues = "F 730   Points cap 165.00   Correction (Z) 0.00   Category adder 8.00   Minimum 23.00   Maximum 999.00";
        var approval = new ApprovedResult(Guid.NewGuid(), vm.ResultsCompetition.Id, 1, Guid.NewGuid(), null,
            "synthetic-fingerprint", DateTimeOffset.UnixEpoch, "Synthetic TD", "FIN9991.xml", [1], 10m, 10m);
        vm.ResultApprovals.Add(approval); vm.SelectedResultApproval = approval;
        vm.ResultSubmissionId = "00000000-0000-0000-0000-000000000123";
        vm.ResultSubmissionStatus = "TEST · processed 1, rejected 0 · complete";
        vm.ResultSubmissionResponse = "{\"testMode\":true,\"summary\":{\"isComplete\":true},\"files\":[{\"status\":\"processed\",\"comments\":\"Synthetic result\"}]}";
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
            if (i <= 10) { vm.ResultTopTen.Add(new(i, "RACER " + i, "15.00", i <= 5 ? "15.00" : "—", "12.00", i <= 5 ? "Best 5" : "", i.ToString(System.Globalization.CultureInfo.InvariantCulture), "990001", "2007", "FIN", "Finished", i <= 5 ? "12.00" : "—")); }
        }
        window.Content = new ResultsView { DataContext = vm };
        window.Show(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
        var titles = window.GetVisualDescendants().OfType<TextBlock>().Select(x => x.Text).ToArray();
        Assert.True(Array.IndexOf(titles, "Race information") < Array.IndexOf(titles, "Penalty calculation"));
        Assert.True(Array.IndexOf(titles, "Penalty calculation") < Array.IndexOf(titles, "XML file"));
        Assert.DoesNotContain(window.GetVisualDescendants().OfType<Button>(), x => Equals(x.Content, "Calculate"));
        Assert.DoesNotContain(window.GetVisualDescendants().OfType<TextBlock>(), x => x.Text?.StartsWith("Enter the category", StringComparison.Ordinal) == true);
        Assert.Contains(vm.ResultsRuleValues, titles);
        Assert.DoesNotContain(window.GetVisualDescendants().OfType<Expander>(), x => x.Header?.ToString()?.StartsWith("FIS suggestions", StringComparison.Ordinal) == true);
        var send = window.GetVisualDescendants().OfType<Button>().Single(x => x.Name == "SendApprovedXmlTestButton");
        Assert.Same(vm.SendApprovedXmlTestCommand, send.Command); Assert.True(send.IsEnabled);
        Assert.Equal(vm.ResultSubmissionResponse, window.GetVisualDescendants().OfType<TextBox>().Single(x => x.Name == "FisSubmissionResponseBox").Text);
        var output = Environment.GetEnvironmentVariable("OPENSKITIME_M7_VISUAL_DIR");
        if (!string.IsNullOrWhiteSpace(output))
        {
            Directory.CreateDirectory(output);
            using var bitmap = new RenderTargetBitmap(new PixelSize(1366, 900), new Vector(96, 96));
            bitmap.Render(window); bitmap.Save(Path.Combine(output, "results-review.png"));
            var scroller = window.GetVisualDescendants().OfType<ScrollViewer>().First();
            scroller.Offset = new Vector(0, 700); window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            bitmap.Render(window); bitmap.Save(Path.Combine(output, "results-runs.png"));
            scroller.Offset = new Vector(0, 1650); window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            bitmap.Render(window); bitmap.Save(Path.Combine(output, "results-penalty.png"));
            scroller.Offset = new Vector(0, scroller.Extent.Height); window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            bitmap.Render(window); bitmap.Save(Path.Combine(output, "results-xml.png"));
        }
        Assert.True(window.IsVisible);
        window.Close();
    }
}
