using System.Diagnostics;
using System.Text.Json;
using OpenSkiTime.Tests.FullRace;

namespace OpenSkiTime.Desktop.E2E;

// Records the demonstration video from the real running application: the same operator workflow as the real-window
// test, at a watchable pace, captured with ffmpeg (gdigrab) while a chapter log is written for composition.
// Opt-in: OPENSKITIME_DESKTOP_E2E=1 and OPENSKITIME_DEMO_VIDEO=<output directory>. Then run
// scripts/demo-video/compose.py --recording <output directory> --output <file.mp4>.
public sealed class DemoVideoTests
{
    private static readonly JsonSerializerOptions s_json = new() { WriteIndented = true };
    private sealed record Chapter(double Start, double End, string Title, string Caption, int Speed);

    [DesktopE2EFact]
    public void RecordDemonstration()
    {
        var output = Environment.GetEnvironmentVariable("OPENSKITIME_DEMO_VIDEO");
        if (string.IsNullOrWhiteSpace(output)) { return; }
        output = Path.GetFullPath(output);
        Directory.CreateDirectory(output);
        var root = Path.Combine(Path.GetTempPath(), "openskitime-demo", Guid.NewGuid().ToString("N"));
        var race = new SyntheticRace();
        var file = Path.Combine(root, "Synthetic Alpine Cup 2026.ost");
        var chapters = new List<Chapter>();
        var clock = new Stopwatch();
        (double Start, string Title, string Caption, int Speed)? open = null;
        void Begin(string title, string caption, int speed = 1)
        {
            var now = clock.Elapsed.TotalSeconds;
            if (open is { } current) { chapters.Add(new(current.Start, now, current.Title, current.Caption, current.Speed)); }
            open = (now, title, caption, speed);
        }
        void Close()
        {
            if (open is { } current) { chapters.Add(new(current.Start, clock.Elapsed.TotalSeconds, current.Title, current.Caption, current.Speed)); }
            open = null;
        }

        using var app = DesktopApp.Launch(root, local => File.WriteAllBytes(Path.Combine(local, "fis-points-list.zip"), race.PointsListArchive()));
        app.Maximize();
        var office = new RaceOffice(app, race) { Pace = 3 };
        using var recorder = ScreenRecorder.Start(Path.Combine(output, "raw.mp4"));
        clock.Start();
        try
        {
            Begin("Introduction", "OpenSkiTime runs the race office: entries, draw, timing, results and reports, locally and offline.");
            Thread.Sleep(6000);
            Begin("Event series", "An event series is one portable .ost file. Enter the weekend's details and create the file.");
            office.CreateSeries(file);
            office.Pause(1200);
            Begin("Slalom competition", "Add a two-run women's FIS slalom with one intermediate, the TD and the homologated course.");
            office.CreateCompetition();
            office.Pause(1500);
            Begin("Importing 100 athletes", "Copy the entry list from Excel and paste it into Competitors; review the highlighted rows, then save.");
            office.ImportCompetitors();
            office.Pause(1500);
            Begin("First-run draw", "The FIS draw: the best 15 (16 with equal points) are drawn, the rest start in points order. Bold rows share points.");
            office.DrawFirstRun();
            office.Pause(3000);
            var order1 = SeriesFileEvidence.StartOrder(file, 1);
            Begin("Connecting simulated timing", "Settings → Timing devices: the training simulator stands in for the start, intermediate and finish clocks.");
            office.OpenRun("5  Timing  ▾", 1);
            office.ConnectSimulator();
            office.Pause(1500);
            Begin("Run 1", "Each impulse is captured, saved and assigned in start order. DNS and DNF are one click on the selected racer.", 8);
            office.Pace = 1;
            office.TimeRun(1, order1, FullRaceWindowTests.WindowPlan(race.Run1));
            office.Pace = 3;
            Begin("Corrections and classifications", "After the run the referee's DSQ is recorded with gate, reason and judge. Every change is kept in the audit history.");
            var dsq1 = order1.Single(x => x.Code == race.Athletes[SyntheticRace.Run1Dsq].Code).Bib;
            office.Disqualify(dsq1, race.Run1[race.Athletes[SyntheticRace.Run1Dsq].Code]);
            office.Pause(2000);
            Begin("Second-run start list", "Run 2 comes from the Run 1 results: the best 30 start in reverse order; a tie at 30th adds the tied racer.");
            office.PrepareSecondRun();
            office.Pause(3000);
            var order2 = SeriesFileEvidence.StartOrder(file, 2);
            Begin("Run 2", "The second run is timed the same way; the ranking switches to combined times.", 8);
            office.OpenRun("5  Timing  ▾", 2);
            office.EnsureConnected();
            office.Pace = 1;
            office.TimeRun(2, order2, FullRaceWindowTests.WindowPlan(race.Run2));
            office.Pace = 3;
            var dsq2 = order2.Single(x => x.Code == race.Athletes[SyntheticRace.Run2Dsq].Code).Bib;
            office.Disqualify(dsq2, race.Run2[race.Athletes[SyntheticRace.Run2Dsq].Code]);
            office.Pause(2000);
            Begin("Live Timing and final rankings", "Spectators follow the race in a browser: run times, intermediates, combined totals and shared ranks for ties.");
            ShowLiveTiming();
            Begin("Results, reports and XML", "Results apply the FIS penalty; the TD approves the FIS XML, and PDF Factory produces the official lists.");
            office.OpenResults();
            Attempt(output, "jury", () => office.EnterChiefOfRace("Pekka", "SYNTHCHIEF", "FIN"));
            Attempt(output, "race-information", office.CompleteRaceInformation);
            Attempt(output, "xml", () => office.ApproveAndExportXml(Path.Combine(output, "exported")));
            Attempt(output, "pdf", office.GeneratePdfs);
            office.Pause(3000);
            Close();
        }
        finally
        {
            Close();
            recorder.Stop();
            File.WriteAllText(Path.Combine(output, "chapters.json"), JsonSerializer.Serialize(chapters.Select(x => new
            { start = x.Start, end = x.End, title = x.Title, caption = x.Caption, speed = x.Speed }), s_json));
        }
    }

    // The closing chapter on its own: the same running application opens the race file completed by the main recording
    // and finishes the race office work. Opt-in: OPENSKITIME_DEMO_VIDEO=<dir> and OPENSKITIME_DEMO_RESULTS_FILE=<.ost>.
    // Writes raw-results.mp4 and chapters-results.json, which compose.py uses in place of the last recorded chapter.
    [DesktopE2EFact]
    public void RecordResultsChapter()
    {
        var output = Environment.GetEnvironmentVariable("OPENSKITIME_DEMO_VIDEO");
        var completed = Environment.GetEnvironmentVariable("OPENSKITIME_DEMO_RESULTS_FILE");
        if (string.IsNullOrWhiteSpace(output) || string.IsNullOrWhiteSpace(completed)) { return; }
        output = Path.GetFullPath(output);
        var root = Path.Combine(Path.GetTempPath(), "openskitime-demo-results", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var file = Path.Combine(root, "Synthetic Alpine Cup 2026.ost");
        File.Copy(completed, file);
        var race = new SyntheticRace();
        using var app = DesktopApp.Launch(root, local => File.WriteAllBytes(Path.Combine(local, "fis-points-list.zip"), race.PointsListArchive()));
        app.Maximize();
        var office = new RaceOffice(app, race) { Pace = 3 };
        office.OpenSeries(file);
        var clock = Stopwatch.StartNew();
        using var recorder = ScreenRecorder.Start(Path.Combine(output, "raw-results.mp4"));
        clock.Restart();
        try
        {
            office.OpenResults();
            office.EnterChiefOfRace("Pekka", "SYNTHCHIEF", "FIN");
            office.CompleteRaceInformation();
            office.ApproveAndExportXml(Path.Combine(output, "exported-" + Guid.NewGuid().ToString("N")[..8]));
            office.GeneratePdfs();
            office.Pause(3000);
        }
        finally
        {
            recorder.Stop();
            File.WriteAllText(Path.Combine(output, "chapters-results.json"), JsonSerializer.Serialize(new[]
            {
                new { start = 0.0, end = clock.Elapsed.TotalSeconds, title = "Results, reports and XML",
                    caption = "Results apply the FIS penalty; the TD approves the FIS XML, and PDF Factory produces the official lists.",
                    speed = 1, source = "raw-results.mp4" },
            }, s_json));
        }
    }

    // A failing closing step is recorded (and reported) instead of discarding the whole recording.
    private static void Attempt(string output, string step, Action act)
    {
        try { act(); File.AppendAllText(Path.Combine(output, "steps.log"), step + ": ok" + Environment.NewLine); }
        catch (Exception ex) when (ex is TimeoutException or InvalidOperationException)
        { File.AppendAllText(Path.Combine(output, "steps.log"), step + ": FAILED " + ex.Message + Environment.NewLine); }
    }

    // The Live Timing viewer in a visible Chromium window, fed with the race's live snapshots (desktop LiveSnapshotMapper)
    // through a real local live server; see tests/live-timing-full-race-e2e.py.
    private static void ShowLiveTiming()
    {
        var snapshots = Environment.GetEnvironmentVariable("OPENSKITIME_DEMO_LIVE_SNAPSHOTS");
        if (string.IsNullOrWhiteSpace(snapshots)) { throw new InvalidOperationException("Set OPENSKITIME_DEMO_LIVE_SNAPSHOTS to the exported live snapshots."); }
        var script = Path.Combine(DesktopApp.RepositoryRoot(), "tests", "live-timing-full-race-e2e.py");
        using var browser = Process.Start(new ProcessStartInfo("python", $"\"{script}\" --snapshots \"{snapshots}\" --headed --hold 3")
        { UseShellExecute = false, WorkingDirectory = DesktopApp.RepositoryRoot() })!;
        if (!browser.WaitForExit(TimeSpan.FromMinutes(5)) || browser.ExitCode != 0)
        { throw new InvalidOperationException("The live-timing browser run failed."); }
    }

    private sealed class ScreenRecorder : IDisposable
    {
        private readonly Process _ffmpeg;
        private ScreenRecorder(Process ffmpeg) => _ffmpeg = ffmpeg;

        public static ScreenRecorder Start(string path)
        {
            var start = new ProcessStartInfo("ffmpeg",
                $"-y -loglevel error -f gdigrab -framerate 30 -offset_x 0 -offset_y 0 -video_size 1920x1032 -draw_mouse 1 -i desktop " +
                $"-c:v libx264 -preset ultrafast -crf 16 -pix_fmt yuv420p \"{path}\"")
            { UseShellExecute = false, RedirectStandardInput = true, CreateNoWindow = true };
            var process = Process.Start(start) ?? throw new InvalidOperationException("ffmpeg did not start.");
            Thread.Sleep(1500);
            return new ScreenRecorder(process);
        }

        public void Stop()
        {
            if (_ffmpeg.HasExited) { return; }
            _ffmpeg.StandardInput.Write('q');
            _ffmpeg.StandardInput.Flush();
            if (!_ffmpeg.WaitForExit(30000)) { _ffmpeg.Kill(); }
        }

        public void Dispose() { Stop(); _ffmpeg.Dispose(); }
    }

}
