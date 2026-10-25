using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using OpenSkiTime.Tests.FullRace;

namespace OpenSkiTime.Desktop.E2E;

// Records the short demonstration video from the real running application, captured with ffmpeg (gdigrab) while a
// chapter log (title, caption, playback speed) is written for scripts/demo-video/compose.py.
// Opt-in: OPENSKITIME_DESKTOP_E2E=1, OPENSKITIME_DEMO_VIDEO=<output directory>, OPENSKITIME_DEMO_LIVE_SNAPSHOTS=<exported
// live snapshots> and OPENSKITIME_DEMO_LETTERHEAD=<A4 background PDF> (scripts/demo-video/make-letterhead.py).
// Everything the application shows or opens lives in a new demonstration folder outside the user profile
// (OPENSKITIME_DEMO_ROOT, default C:\OpenSkiTimeDemo), which is also its working directory: the series, the exported XML,
// the letterhead and the PDFs. Before recording, one unrecorded file dialog is confirmed in that folder, so every
// recorded Windows file dialog opens there instead of in the user's own folders and nothing needs to be blurred.
// Only the FIS calendar's technical-delegate columns, which name real officials, are blurred.
public sealed class DemoVideoTests
{
    private static readonly JsonSerializerOptions s_json = new() { WriteIndented = true };
    private sealed record Chapter(double Start, double End, string Title, string Caption, double Speed, bool MaskStatus);

    [DesktopE2EFact]
    public void RecordDemonstration()
    {
        var output = Environment.GetEnvironmentVariable("OPENSKITIME_DEMO_VIDEO");
        if (string.IsNullOrWhiteSpace(output)) { return; }
        output = Path.GetFullPath(output);
        Directory.CreateDirectory(output);
        var letterhead = Environment.GetEnvironmentVariable("OPENSKITIME_DEMO_LETTERHEAD");
        var root = DemoRoot();
        if (!string.IsNullOrWhiteSpace(letterhead))
        {
            var copy = Path.Combine(root, "Organizer letterhead.pdf");
            File.Copy(letterhead, copy);
            letterhead = copy;
        }
        var race = new SyntheticRace();
        var file = Path.Combine(root, "Synthetic Alpine Cup 2026.ost");
        var chapters = new List<Chapter>();
        var clock = new Stopwatch();
        (double Start, string Title, string Caption, double Speed, bool MaskStatus)? open = null;
        // maskStatus hides the application's status line, which can show local file paths (for example after an export).
        void Begin(string title, string caption, double speed = 1, bool maskStatus = false)
        {
            var now = clock.Elapsed.TotalSeconds;
            if (open is { } current) { chapters.Add(new(current.Start, now, current.Title, current.Caption, current.Speed, current.MaskStatus)); }
            open = (now, title, caption, speed, maskStatus);
        }
        void Close()
        {
            if (open is { } current) { chapters.Add(new(current.Start, clock.Elapsed.TotalSeconds, current.Title, current.Caption, current.Speed, current.MaskStatus)); }
            open = null;
        }

        void PrepareLocalData(string local) => File.WriteAllBytes(Path.Combine(local, "fis-points-list.zip"), race.PointsListArchive());
        // Unrecorded warm-up: Windows reopens file dialogs in the folder last confirmed by this application.
        using (var warmUp = DesktopApp.Launch(root, PrepareLocalData))
        {
            new RaceOffice(warmUp, race).CreateSeries(Path.Combine(root, "Setup", "Dialog warm-up.ost"));
        }
        using var app = DesktopApp.Launch(root, PrepareLocalData);
        app.Maximize();
        var office = new RaceOffice(app, race) { Pace = 2, OperatorName = "Race office" };
        var redactions = new List<(double Start, double End, System.Drawing.Rectangle Area)>();
        (double Start, System.Drawing.Rectangle Area)? dialog = null;
        // Times are measured from just before ffmpeg starts; the first captured frame follows shortly after, so marks
        // are at most slightly late. Redactions get a generous margin on both sides to absorb that offset.
        const double margin = 2.0;
        office.SensitiveArea = (area, shown) =>
        {
            var now = clock.Elapsed.TotalSeconds;
            if (shown) { dialog ??= (Math.Max(0, now - margin), area); }
            else if (dialog is { } open) { redactions.Add((open.Start, now + margin, open.Area)); dialog = null; }
        };
        clock.Start();
        using var recorder = ScreenRecorder.Start(Path.Combine(output, "raw.mp4"));
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
            office.OpenRun("Timing", 1);
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
            office.OpenRun("Timing", 2);
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
            Attempt(output, "xml", () => office.ApproveAndExportXml(Path.Combine(root, "Exports")));
            Begin("Branded PDF reports", "PDF Factory prints every list on the organizer's letterhead in one click.", 2.5);
            Attempt(output, "pdf", () => office.GeneratePdfs(letterhead));
            office.Pause(1500);
            Close();
        }
        finally
        {
            Close();
            // A step that failed while a sensitive area was on screen leaves it open: blur it to the end.
            if (dialog is { } unfinished) { redactions.Add((unfinished.Start, clock.Elapsed.TotalSeconds + 60, unfinished.Area)); }
            recorder.Stop();
            File.WriteAllText(Path.Combine(output, "chapters.json"), JsonSerializer.Serialize(chapters.Select(x => new
            { start = x.Start, end = x.End, title = x.Title, caption = x.Caption, speed = x.Speed, maskStatus = x.MaskStatus }), s_json));
            File.WriteAllText(Path.Combine(output, "series-file.txt"), file);
            // The FIS calendar's technical-delegate columns; compose.py blurs these areas (with a margin) for their duration.
            File.WriteAllText(Path.Combine(output, "redactions.json"), JsonSerializer.Serialize(redactions.Select(x => new
            { start = x.Start, end = x.End, x = x.Area.X, y = x.Area.Y, w = x.Area.Width, h = x.Area.Height }), s_json));
        }
    }

    // A new folder for this recording outside the user profile, so no path on screen shows the account name.
    internal static string DemoRoot()
    {
        var configured = Environment.GetEnvironmentVariable("OPENSKITIME_DEMO_ROOT");
        var parent = Path.GetFullPath(string.IsNullOrWhiteSpace(configured) ? @"C:\OpenSkiTimeDemo" : configured);
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (parent.StartsWith(profile, StringComparison.OrdinalIgnoreCase) || parent.Contains(Environment.UserName, StringComparison.OrdinalIgnoreCase))
        { throw new InvalidOperationException("OPENSKITIME_DEMO_ROOT must be outside the user profile and must not contain the account name."); }
        var root = Path.Combine(parent, "Demo " + DateTime.Now.ToString("yyyy-MM-dd HHmmss", CultureInfo.InvariantCulture));
        if (Directory.Exists(root)) { throw new InvalidOperationException("Demonstration folder already exists: " + root); }
        Directory.CreateDirectory(root);
        return root;
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

    internal sealed class ScreenRecorder : IDisposable
    {
        private readonly Process _ffmpeg;
        private ScreenRecorder(Process ffmpeg) => _ffmpeg = ffmpeg;

        // wallClock stamps every frame with the Unix time of its capture (Matroska output), so a recording can be aligned
        // with audio and browser frames captured by other processes.
        public static ScreenRecorder Start(string path, bool wallClock = false)
        {
            var start = new ProcessStartInfo("ffmpeg",
                $"-y -loglevel error {(wallClock ? "-use_wallclock_as_timestamps 1 " : "")}-f gdigrab -framerate 30 -offset_x 0 -offset_y 0 " +
                $"-video_size 1920x1032 -draw_mouse 1 -i desktop {(wallClock ? "-copyts " : "")}-c:v libx264 -preset ultrafast -crf 16 -pix_fmt yuv420p \"{path}\"")
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
