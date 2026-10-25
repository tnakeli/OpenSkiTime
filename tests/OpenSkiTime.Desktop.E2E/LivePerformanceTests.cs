using System.Globalization;
using System.Text;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using OpenSkiTime.Tests.FullRace;

namespace OpenSkiTime.Desktop.E2E;

// Publishes the hundred-racer synthetic slalom from the real running application through the live timing control panel to
// a Cloud server while simulated viewers (src/OpenSkiTime.LiveTiming.LoadTest) follow it, and records what the operator
// sees: the panel's Server load every five seconds and screenshots of the panel at the stages of the race.
// Opt-in: OPENSKITIME_DESKTOP_E2E=1 and OPENSKITIME_LIVE_PERF=<output directory outside the repository>.
// OPENSKITIME_LIVE_PERF_CLOUD names the server (default https://live.openskiti.me); its publisher key must already be saved
// in Settings → Live timing (Windows Credential Manager). The test writes the public race URL to <output>/session-url.txt,
// waits until the panel reports OPENSKITIME_LIVE_PERF_VIEWERS viewers (default 900, at most 20 minutes), then starts a
// racer every OPENSKITIME_LIVE_PERF_INTERVAL seconds (default 15) on the computer's clock; OPENSKITIME_LIVE_PERF_RACERS
// (default all 100) times only the first racers of the start list, for a rehearsal. After the last finish it keeps
// publishing until <output>/stop exists (at most 30 minutes), so that a server restart can be observed, and finally deletes
// the public session.
public sealed class LivePerformanceTests
{
    private const string PanelTitle = "OpenSkiTime · Live timing control panel";
    private static readonly TimeSpan s_sampleInterval = TimeSpan.FromSeconds(5);

    [DesktopE2EFact]
    public void PublishRaceUnderViewerLoad()
    {
        var output = Environment.GetEnvironmentVariable("OPENSKITIME_LIVE_PERF");
        if (string.IsNullOrWhiteSpace(output)) { return; }
        output = Path.GetFullPath(output);
        Directory.CreateDirectory(output);
        var cloud = Environment.GetEnvironmentVariable("OPENSKITIME_LIVE_PERF_CLOUD") is { Length: > 0 } c ? c : "https://live.openskiti.me";
        var target = int.Parse(Environment.GetEnvironmentVariable("OPENSKITIME_LIVE_PERF_VIEWERS") ?? "900", CultureInfo.InvariantCulture);
        var racers = int.Parse(Environment.GetEnvironmentVariable("OPENSKITIME_LIVE_PERF_RACERS") ?? "100", CultureInfo.InvariantCulture);
        var interval = TimeSpan.FromSeconds(int.Parse(Environment.GetEnvironmentVariable("OPENSKITIME_LIVE_PERF_INTERVAL") ?? "15", CultureInfo.InvariantCulture));

        var race = new SyntheticRace(raceDate: DateOnly.FromDateTime(DateTime.Now));
        var root = DemoVideoTests.DemoRoot();
        var file = Path.Combine(root, "Synthetic Alpine Cup 2026.ost");
        using var app = DesktopApp.Launch(root, local => File.WriteAllBytes(Path.Combine(local, "fis-points-list.zip"), race.PointsListArchive()));
        app.Maximize();
        var office = new RaceOffice(app, race) { OperatorName = "Race office" };
        office.CreateSeries(file);
        office.CreateCompetition();
        office.ImportCompetitors();
        office.DrawFirstRun();
        var order = SeriesFileEvidence.StartOrder(file, 1).Take(racers).ToArray();
        office.OpenRun("Timing", 1);
        office.ConnectSimulator();

        app.Click("Live timing…");
        var panel = app.Retry(() => app.Automation.GetDesktop().FindFirstChild(app.By.ByName(PanelTitle)), TimeSpan.FromSeconds(30)).AsWindow();
        var handle = panel.Properties.NativeWindowHandle.Value;
        SetWindowPos(handle, 0, 860, 20, 1000, 1000, 0x0040); // SWP_SHOWWINDOW: tall enough for the Server load of both cards
        app.SetText(app.Field("Cloud URL", panel), cloud);
        DesktopApp.Invoke(panel.FindAllDescendants(app.By.ByName("Start").And(app.By.ByControlType(ControlType.Button)))[1]);
        var url = app.Retry(() => panel.FindAllDescendants(app.By.ByControlType(ControlType.Text))
            .FirstOrDefault(x => (x.Name ?? "").StartsWith(cloud + "/r/", StringComparison.OrdinalIgnoreCase))?.Name, TimeSpan.FromMinutes(3));
        File.WriteAllText(Path.Combine(output, "session-url.txt"), url);

        var log = new StringBuilder("local_time\tcloud_status\tdetail\tserver_load\n");
        var lastSample = DateTime.MinValue;
        var shots = 0;
        var viewers = 0;
        void Sample(bool force = false)
        {
            if (!force && DateTime.Now - lastSample < s_sampleInterval) { return; }
            lastSample = DateTime.Now;
            var texts = panel.FindAllDescendants(app.By.ByControlType(ControlType.Text)).Select(x => x.Name ?? "").ToArray();
            var load = texts.FirstOrDefault(x => x.StartsWith("Viewers ", StringComparison.Ordinal)) ?? "";
            var status = texts.SkipWhile(x => x != "CLOUD").Skip(1).FirstOrDefault() ?? "";
            var detail = texts.SkipWhile(x => x != "CLOUD").Skip(2).FirstOrDefault() ?? "";
            if (load.Split(' ') is [_, var now, ..] && int.TryParse(now, CultureInfo.InvariantCulture, out var n)) { viewers = n; }
            log.Append(CultureInfo.InvariantCulture, $"{DateTime.Now:HH:mm:ss}\t{status}\t{detail}\t{load.ReplaceLineEndings(" | ")}\n");
            File.WriteAllText(Path.Combine(output, "server-load.tsv"), log.ToString());
        }
        void Shot(string name)
        {
            Sample(force: true);
            using var bitmap = app.RenderWindow(panel);
            bitmap.Save(Path.Combine(output, $"{++shots:00}-{name}.png"), System.Drawing.Imaging.ImageFormat.Png);
        }
        // Waits for the moment in slices of at most a second, sampling the panel meanwhile.
        void Until(DateTime at)
        {
            while (DateTime.Now < at) { Sample(); Thread.Sleep(TimeSpan.FromMilliseconds(Math.Min(1000, Math.Max(1, (at - DateTime.Now).TotalMilliseconds)))); }
        }

        try
        {
            Shot("published");
            var waitUntil = DateTime.Now.AddMinutes(20);
            while (viewers < target && DateTime.Now < waitUntil) { Until(DateTime.Now.AddSeconds(5)); }
            Shot("viewers-connected");

            // Every racer starts on the interval and finishes after the planned run time (the slowest plan for racers who
            // do not finish in the plan), so the later racers of the seeded order never overtake an earlier one.
            var first = DateTime.Now.TimeOfDay.Ticks + TimeSpan.FromSeconds(5).Ticks;
            var slowest = race.Run1.Values.Where(x => x.Outcome == RunOutcome.Finished).Max(x => x.FinishTicks);
            var events = new List<(long At, string Button, int Racer)>();
            for (var i = 0; i < order.Length; i++)
            {
                var plan = race.Run1[order[i].Code];
                var finish = plan.Outcome == RunOutcome.Finished ? plan.FinishTicks : slowest;
                var start = first + interval.Ticks * i + i * 7_919L % 30_000 * 1_000;
                events.Add((start, "Test start", i));
                events.Add((start + (plan.IntermediateTicks > 0 ? plan.IntermediateTicks : finish * 45 / 100), "Test I1", i));
                events.Add((start + finish, "Test finish", i));
            }
            var midnight = DateTime.Today;
            foreach (var (at, button, racer) in events.OrderBy(x => x.At))
            {
                Until(midnight.AddTicks(at));
                LiveDemoVideoTests.Pulse(app, button, at - at % 1_000);
                if (button == "Test finish" && (racer + 1) % 25 == 0) { Shot($"finished-{racer + 1}"); }
            }

            var holdUntil = DateTime.Now.AddMinutes(30);
            var nextShot = DateTime.Now.AddMinutes(2);
            while (!File.Exists(Path.Combine(output, "stop")) && DateTime.Now < holdUntil)
            {
                Until(DateTime.Now.AddSeconds(2));
                if (DateTime.Now >= nextShot) { Shot("after-race"); nextShot = DateTime.Now.AddMinutes(2); }
            }
            Shot("before-delete");
        }
        finally
        {
            File.WriteAllText(Path.Combine(output, "server-load.tsv"), log.ToString());
            var delete = panel.FindAllDescendants(app.By.ByName("Delete session").And(app.By.ByControlType(ControlType.Button))).FirstOrDefault();
            if (delete is not null) { DesktopApp.Invoke(delete); Thread.Sleep(3000); }
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr window, nint after, int x, int y, int width, int height, uint flags);
}
