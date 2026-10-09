using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using OpenSkiTime.Tests.FullRace;

namespace OpenSkiTime.Desktop.E2E;

// Records the short demonstration video from the real running application, captured with ffmpeg (gdigrab) while a
// chapter log (title, caption, playback speed) is written for scripts/demo-video/compose.py.
// Opt-in: OPENSKITIME_DESKTOP_E2E=1, OPENSKITIME_DEMO_VIDEO=<output directory>, OPENSKITIME_DEMO_LIVE_SNAPSHOTS=<exported
// live snapshots> and OPENSKITIME_DEMO_LETTERHEAD=<A4 background PDF> (scripts/demo-video/make-letterhead.py).
public sealed class DemoVideoTests
{
    private static readonly JsonSerializerOptions s_json = new() { WriteIndented = true };
    private sealed record Chapter(double Start, double End, string Title, string Caption, double Speed);

    [DesktopE2EFact]
    public void RecordDemonstration()
    {
        var output = Environment.GetEnvironmentVariable("OPENSKITIME_DEMO_VIDEO");
        if (string.IsNullOrWhiteSpace(output)) { return; }
        output = Path.GetFullPath(output);
        Directory.CreateDirectory(output);
        var letterhead = Environment.GetEnvironmentVariable("OPENSKITIME_DEMO_LETTERHEAD");
        var root = Path.Combine(Path.GetTempPath(), "openskitime-demo", Guid.NewGuid().ToString("N"));
        var race = new SyntheticRace();
        var file = Path.Combine(root, "Synthetic Alpine Cup 2026.ost");
        var chapters = new List<Chapter>();
        var clock = new Stopwatch();
        (double Start, string Title, string Caption, double Speed)? open = null;
        void Begin(string title, string caption, double speed = 1)
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
        var office = new RaceOffice(app, race) { Pace = 2 };
        var redactions = new List<(double Start, double End, System.Drawing.Rectangle Area)>();
        (double Start, System.Drawing.Rectangle Area)? dialog = null;
        office.FileDialog = (area, shown) =>
        {
            var now = clock.Elapsed.TotalSeconds;
            if (shown) { dialog ??= (Math.Max(0, now - 1.0), area); }
            else if (dialog is { } open) { redactions.Add((open.Start, now + 0.5, open.Area)); dialog = null; }
        };
        using var recorder = ScreenRecorder.Start(Path.Combine(output, "raw.mp4"));
        clock.Start();
        try
        {
            Begin("FIS calendar at hand", "Browse the official FIS calendar straight from the FIS API and start an event series from it.", 2);
            Attempt(output, "fis-calendar", office.BrowseFisCalendar);
            Begin("Event series", "One portable .ost file holds the whole weekend: entries, start lists, raw timing, corrections and results.", 3);
            office.CreateSeries(file);
            Begin("FIS slalom", "Two runs, one intermediate, TD and homologated course.", 5);
            office.CreateCompetition();
            Begin("100 athletes from Excel", "Paste the entry list; every changed cell is highlighted for review before saving.", 2);
            office.ImportCompetitors();
            Begin("Fair FIS draw", "Best 15 drawn (16 with equal points), then points order. Bold rows share FIS points.", 1.5);
            office.DrawFirstRun();
            Begin("Homologated equipment and timing", "FIS equipment homologations come from the FIS API; the simulator stands in for start, intermediate and finish.", 4);
            Attempt(output, "homologations", office.RefreshEquipmentHomologations);
            var order1 = SeriesFileEvidence.StartOrder(file, 1);
            office.OpenRun("5  Timing  ▾", 1);
            office.ConnectSimulator();
            Begin("Run 1 · 100 racers", "Impulses are saved before they are shown, assigned in start order and ranked live. DNS and DNF are one click.", 5);
            office.FastImpulses = true;
            office.TimeRun(1, order1, FullRaceWindowTests.WindowPlan(race.Run1));
            Begin("Corrections with audit", "A referee DSQ with gate, reason and judge. Every change is kept in the audit history.", 3);
            var dsq1 = order1.Single(x => x.Code == race.Athletes[SyntheticRace.Run1Dsq].Code).Bib;
            office.Disqualify(dsq1, race.Run1[race.Athletes[SyntheticRace.Run1Dsq].Code]);
            Begin("Run 2 start order", "Best 30 reversed from Run 1; a tie at 30th brings the tied racer along. Bibs stay.", 2);
            office.PrepareSecondRun();
            var order2 = SeriesFileEvidence.StartOrder(file, 2);
            office.OpenRun("5  Timing  ▾", 2);
            office.EnsureConnected();
            Begin("Run 2 · combined ranking", "The ranking switches to combined times; equal totals share a rank.", 5);
            office.TimeRun(2, order2, FullRaceWindowTests.WindowPlan(race.Run2));
            var dsq2 = order2.Single(x => x.Code == race.Athletes[SyntheticRace.Run2Dsq].Code).Bib;
            office.Disqualify(dsq2, race.Run2[race.Athletes[SyntheticRace.Run2Dsq].Code]);
            office.DisconnectTiming();
            Begin("Live Timing", "Spectators follow intermediates, run times, totals and ties live in any browser.", 2);
            ShowLiveTiming();
            Begin("Results and FIS XML", "The FIS penalty is calculated for the TD; approval stores the exact FIS XML in the series file.", 4);
            office.OpenResults();
            office.ScrollPage(-12);
            Attempt(output, "jury", () => office.EnterChiefOfRace("Pekka", "SYNTHCHIEF", "FIN"));
            Attempt(output, "race-information", office.CompleteRaceInformation);
            Attempt(output, "xml", () => office.ApproveAndExportXml(Path.Combine(output, "exported-" + DateTime.Now.ToString("HHmmss", CultureInfo.InvariantCulture))));
            Begin("Branded PDF reports", "PDF Factory prints every list on the organizer's letterhead in one click.", 2.5);
            Attempt(output, "pdf", () => office.GeneratePdfs(letterhead));
            office.Pause(1500);
            Close();
        }
        finally
        {
            Close();
            recorder.Stop();
            File.WriteAllText(Path.Combine(output, "chapters.json"), JsonSerializer.Serialize(chapters.Select(x => new
            { start = x.Start, end = x.End, title = x.Title, caption = x.Caption, speed = x.Speed }), s_json));
            File.WriteAllText(Path.Combine(output, "series-file.txt"), file);
            // File dialogs show the user's own folders; compose.py blurs these areas (with a margin) for their duration.
            File.WriteAllText(Path.Combine(output, "redactions.json"), JsonSerializer.Serialize(redactions.Select(x => new
            { start = x.Start, end = x.End, x = x.Area.X, y = x.Area.Y, w = x.Area.Width, h = x.Area.Height }), s_json));
        }
    }

    // A failing step is recorded (and reported) instead of discarding the whole recording.
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
        using var browser = Process.Start(new ProcessStartInfo("python", $"\"{script}\" --snapshots \"{snapshots}\" --headed --hold 0.8")
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
