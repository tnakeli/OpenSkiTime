using FlaUI.Core.Definitions;
using FlaUI.Core.WindowsAPI;
using OpenSkiTime.Tests.FullRace;

namespace OpenSkiTime.Desktop.E2E;

// Diagnostic for writing tutorials (docs/tutorial-videos.md): a small synthetic race timed through both runs, then a
// screenshot and the UI Automation tree of each race office view. Opt-in: OPENSKITIME_TUTORIAL_PROBE=<folder>.
public sealed class OfficeTutorialProbe
{
    [DesktopE2EFact]
    public void ProbeOfficeViews()
    {
        var output = Environment.GetEnvironmentVariable("OPENSKITIME_TUTORIAL_PROBE");
        if (string.IsNullOrWhiteSpace(output)) { return; }
        Directory.CreateDirectory(output);
        var root = DemoVideoTests.DemoRoot();
        var file = Path.Combine(Directory.CreateDirectory(Path.Combine(root, "Events")).FullName, "Synthetic Alpine Cup 2026.ost");
        var race = new SyntheticRace(raceDate: DateOnly.FromDateTime(DateTime.Now));
        using var app = DesktopApp.Launch(root, local => File.WriteAllBytes(Path.Combine(local, "fis-points-list.zip"), race.PointsListArchive()));
        app.Maximize();
        var office = new RaceOffice(app, race) { OperatorName = "Race office", FastImpulses = true };
        void Dump(string name)
        {
            Thread.Sleep(800);
            app.Screenshot(output, name + ".png");
            File.WriteAllText(Path.Combine(output, name + ".txt"), app.Tree());
        }
        office.CreateSeries(file);
        office.CreateCompetition();
        TimingTutorial.Import(app, office, race);
        office.OpenRun("Start lists", 1);
        Dump("01-start-lists-before-draw");
        app.Click("Draw");
        app.WaitUntil(() => app.TryByName("Start list ready") is not null, "draw saved", TimeSpan.FromSeconds(30));
        Dump("02-start-lists-drawn");
        office.OpenRun("Timing", 1);
        office.ConnectSimulator();
        office.SwitchInputsOn();
        TimingTutorial.Simulate(app, TimingTutorial.Racers);
        Dump("03-timing-run1-done");
        office.PrepareSecondRun();
        Dump("04-start-lists-run2");
        office.OpenRun("Timing", 2);
        office.EnsureConnected();
        TimingTutorial.Simulate(app, SeriesFileEvidence.StartOrder(file, 2).Count);
        office.OpenResults();
        Dump("05-results");
        foreach (var tab in app.Window.FindAllDescendants(app.By.ByControlType(ControlType.TabItem)).Select(x => x.Name).ToArray())
        {
            office.SelectTab(tab);
            Dump("06-results-" + Safe(tab));
        }
        office.Navigate("Timing report");
        app.Press(VirtualKeyShort.ESCAPE);
        Dump("07-timing-report");
        foreach (var tab in app.Window.FindAllDescendants(app.By.ByControlType(ControlType.TabItem)).Select(x => x.Name).ToArray())
        {
            office.SelectTab(tab);
            Dump("08-timing-report-" + Safe(tab));
        }
        office.Navigate("PDF Factory");
        app.Press(VirtualKeyShort.ESCAPE);
        Dump("09-pdf-factory");
        DesktopApp.Invoke(app.ByName("PDF settings"));
        Dump("10-pdf-settings");
        office.Navigate("Settings");
        foreach (var tab in app.Window.FindAllDescendants(app.By.ByControlType(ControlType.TabItem)).Select(x => x.Name).ToArray())
        {
            office.SelectTab(tab);
            Dump("11-settings-" + Safe(tab));
        }
    }

    private static string Safe(string name) => string.Concat(name.Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-'));
}
