using OpenSkiTime.Tests.FullRace;
using Xunit;

namespace OpenSkiTime.Desktop.E2E;

public sealed class FullRaceWindowTests
{
    private static string Output(string name)
    {
        var configured = Environment.GetEnvironmentVariable("OPENSKITIME_DESKTOP_E2E_OUTPUT");
        return Path.Combine(string.IsNullOrWhiteSpace(configured) ? Path.Combine(DesktopApp.RepositoryRoot(), "artifacts", "ux-e2e", "e2e") : configured, name);
    }

    [DesktopE2EFact]
    public void OperatorCreatesSeriesThroughTheRealWindow()
    {
        var root = Path.Combine(Path.GetTempPath(), "openskitime-e2e", Guid.NewGuid().ToString("N"));
        var race = new SyntheticRace();
        using var app = DesktopApp.Launch(root, local => File.WriteAllBytes(Path.Combine(local, "fis-points-list.zip"), race.PointsListArchive()));
        var office = new RaceOffice(app, race);
        var file = Path.Combine(root, "Synthetic Alpine Cup 2026.ost");
        try
        {
            office.CreateSeries(file);
            Assert.True(File.Exists(file));
            app.Screenshot(Output("steps"), "01-series-created.png");
            office.CreateCompetition();
            app.Screenshot(Output("steps"), "02-competition.png");
            office.ImportCompetitors();
            app.Screenshot(Output("steps"), "03-competitors-imported.png");
            office.DrawFirstRun();
            app.Screenshot(Output("steps"), "04-run1-draw.png");
        }
        catch
        {
            File.WriteAllText(Output("uia-failure.txt"), app.Tree());
            app.Screenshot(Output("steps"), "failure.png");
            throw;
        }
    }
}
