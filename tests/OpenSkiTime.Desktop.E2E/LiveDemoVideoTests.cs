using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using Microsoft.Data.Sqlite;
using OpenSkiTime.Tests.FullRace;

namespace OpenSkiTime.Desktop.E2E;

// Records the short Live Timing and race radio demonstration from the real running application: the live timing control
// panel with the race radio settings, then the public live page in a visible browser with the radio playing while the
// first racers of the synthetic slalom are timed in real time with the simulator.
// Opt-in: OPENSKITIME_DESKTOP_E2E=1 and OPENSKITIME_LIVE_DEMO_VIDEO=<output directory>. The radio uses the operator's
// current settings: the configuration in OPENSKITIME_LIVE_DEMO_RADIO_SETTINGS (default: this user's live-radio.json; with
// several competitions, OPENSKITIME_LIVE_DEMO_RADIO_COMPETITION picks one) is copied to the demonstration competition,
// and the provider keys come from Windows Credential Manager. Real providers are billed against that configuration's
// budget. Background music is copied into the demonstration folder so that no path on screen names the user's account.
// OPENSKITIME_LIVE_DEMO_PYTHON names a Python with Playwright and PyAudioWPatch for scripts/demo-video/live-viewer.py.
public sealed class LiveDemoVideoTests
{
    private static readonly JsonSerializerOptions s_json = new() { WriteIndented = true };
    private const string PanelTitle = "OpenSkiTime · Live timing control panel";
    private const int Racers = 4;
    private static readonly TimeSpan StartInterval = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan BeforeFirstStart = TimeSpan.FromSeconds(20);
    // Start positions (0-based) that ski out: half of the field, for the radio to react to.
    private static readonly int[] OutOfCourse = [1, 3];

    [DesktopE2EFact]
    public void RecordLiveTimingDemonstration()
    {
        var output = Environment.GetEnvironmentVariable("OPENSKITIME_LIVE_DEMO_VIDEO");
        if (string.IsNullOrWhiteSpace(output)) { return; }
        output = Path.GetFullPath(output);
        Directory.CreateDirectory(output);
        var radio = OperatorRadioConfiguration();
        var root = DemoVideoTests.DemoRoot();
        var music = CopyMusic(radio["musicFolder"]?.GetValue<string>() ?? "", Path.Combine(root, "Music"));
        radio["localEnabled"] = false;
        radio["cloudEnabled"] = false;
        radio["musicFolder"] = music;

        var race = new SyntheticRace(raceDate: DateOnly.FromDateTime(DateTime.Now));
        var file = Path.Combine(root, "Synthetic Alpine Cup 2026.ost");
        using var app = DesktopApp.Launch(root, local => File.WriteAllBytes(Path.Combine(local, "fis-points-list.zip"), race.PointsListArchive()));
        app.Maximize();
        var office = new RaceOffice(app, race) { OperatorName = "Race office" };
        // Unrecorded preparation: series, competition, entries, draw and the simulator on the timing view.
        office.CreateSeries(file);
        office.CreateCompetition();
        office.ImportCompetitors();
        office.DrawFirstRun();
        var order = SeriesFileEvidence.StartOrder(file, 1).Take(Racers).ToArray();
        WriteRadioSettings(app.LocalData, CompetitionId(file), radio);
        office.OpenRun("Timing", 1);
        office.ConnectSimulator();
        office.Pause(1500);

        var marks = new JsonObject();
        void Mark(string name) => marks[name] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
        using var recorder = DemoVideoTests.ScreenRecorder.Start(Path.Combine(output, "raw.mkv"), wallClock: true);
        // The browser part has no input for minutes; a zero mouse move keeps the screen saver away.
        using var awake = new Timer(_ => mouse_event(0x0001, 0, 0, 0, 0), null, TimeSpan.Zero, TimeSpan.FromSeconds(15));
        Process? viewer = null;
        try
        {
            Mark("panel");
            app.Click("Live timing…");
            var panel = app.Retry(() => app.Automation.GetDesktop().FindFirstChild(app.By.ByName(PanelTitle)), TimeSpan.FromSeconds(30)).AsWindow();
            PlacePanel(panel);
            Thread.Sleep(1200);
            ScrollToRadioSettings(app, panel);
            Thread.Sleep(2500);
            Mark("settings");
            ShowFields(app, panel);
            DesktopApp.Invoke(app.ById("RadioLocalBox", panel));
            Thread.Sleep(900);
            DesktopApp.Invoke(app.ById("SaveRadioButton", panel));
            Thread.Sleep(1500);
            Mark("start");
            var local = panel.FindAllDescendants(app.By.ByName("Start").And(app.By.ByControlType(ControlType.Button))).First();
            DesktopApp.Invoke(local);
            var url = app.Retry(() => panel.FindAllDescendants(app.By.ByControlType(ControlType.Text))
                .FirstOrDefault(x => (x.Name ?? "").StartsWith("http://", StringComparison.Ordinal))?.Name, TimeSpan.FromSeconds(60));
            app.WaitUntil(() => app.ById("RadioStatusText", panel).Name == "Running", "radio running", TimeSpan.FromSeconds(90));
            Mark("running");
            Thread.Sleep(4000);

            // The pointer rests on the taskbar, outside the recorded area, while the browser shows the live page.
            Mouse.MoveTo(new System.Drawing.Point(1900, 1060));
            Mark("viewer");
            var duration = BeforeFirstStart + StartInterval * (Racers - 1) + TimeSpan.FromSeconds(85);
            viewer = StartViewer(url, output, duration);
            WaitForLine(viewer, "radio-on", TimeSpan.FromSeconds(90));
            // The browser stays above the timing view, which UI Automation would otherwise bring forward.
            s_browser = GetForegroundWindow();
            OnTop(s_browser, true);
            Mark("radio");
            Thread.Sleep(BeforeFirstStart);
            Mark("race");
            TimeRealTime(app, office, race, order, Mark);
            Mark("finished");
            if (!viewer.WaitForExit(TimeSpan.FromMinutes(5))) { throw new TimeoutException("The live viewer did not finish."); }
            if (viewer.ExitCode != 0)
            {
                throw new InvalidOperationException($"The live viewer failed with exit code {viewer.ExitCode}; the recording is incomplete.");
            }
        }
        finally
        {
            Mark("end");
            if (viewer is { HasExited: false }) { viewer.Kill(entireProcessTree: true); }
            recorder.Stop();
            File.WriteAllText(Path.Combine(output, "marks.json"), marks.ToJsonString(s_json));
            File.WriteAllText(Path.Combine(output, "series-file.txt"), file);
        }
    }

    // Racers start every StartInterval from now with the synthetic run times of their plans; the device time of every
    // impulse is the computer's time of day, so the live page's running clocks and the radio follow the real clock.
    // Impulses go through UI Automation patterns while the browser stays in front. Racers in OutOfCourse pass the
    // intermediate and are then marked DNF: the timing view comes forward for a moment, the operator clicks the racer and
    // DNF, and the browser returns (its rows offer no selection pattern). Those moments are marked dnf-<bib>/back-<bib>.
    private static void TimeRealTime(DesktopApp app, RaceOffice office, SyntheticRace race, (int Position, int Bib, string Code)[] order,
        Action<string> mark)
    {
        var first = DateTime.Now.TimeOfDay.Ticks;
        var events = new List<(long At, Action<long> Act)>();
        for (var i = 0; i < order.Length; i++)
        {
            var plan = race.Run1[order[i].Code];
            var finish = plan.Outcome == RunOutcome.Finished ? plan.FinishTicks : race.Run1.Values.First(x => x.Outcome == RunOutcome.Finished).FinishTicks;
            var intermediate = plan.IntermediateTicks > 0 ? plan.IntermediateTicks : finish * 45 / 100;
            var start = first + StartInterval.Ticks * i + i * 7_919L % 30_000 * 1_000;
            var bib = order[i].Bib;
            events.Add((start, at => Pulse(app, "Test start", at)));
            events.Add((start + intermediate, at => Pulse(app, "Test I1", at)));
            if (OutOfCourse.Contains(i)) { events.Add((start + intermediate + TimeSpan.FromSeconds(6).Ticks, _ => Dnf(app, office, bib, mark))); }
            else { events.Add((start + finish, at => Pulse(app, "Test finish", at))); }
        }
        foreach (var (at, act) in events.OrderBy(x => x.At))
        {
            var wait = TimeSpan.FromTicks(at - DateTime.Now.TimeOfDay.Ticks);
            if (wait > TimeSpan.Zero) { Thread.Sleep(wait); }
            act(at - at % 1_000);
        }
    }

    // One simulator impulse: the device time into the simulation time box, then the test button. Elements are looked up
    // again on every attempt, because the timing view may rebuild them while it updates.
    internal static void Pulse(DesktopApp app, string button, long timeOfDay)
    {
        var text = new TimeOnly(timeOfDay).ToString("HH:mm:ss.ffff", CultureInfo.InvariantCulture);
        Retry(() =>
        {
            app.ById("SimulationTimeBox").Patterns.Value.Pattern.SetValue(text);
            app.Button(button).Patterns.Invoke.Pattern.Invoke();
        });
        Thread.Sleep(60);
    }

    private static void Dnf(DesktopApp app, RaceOffice office, int bib, Action<string> mark)
    {
        mark("dnf-" + bib.ToString(CultureInfo.InvariantCulture));
        OnTop(s_browser, false);
        SetForegroundWindow(app.Window.Properties.NativeWindowHandle.Value);
        Thread.Sleep(300);
        try { office.Classify("RunningGrid", bib, "DNF", null); }
        finally
        {
            OnTop(s_browser, true);
            Mouse.MoveTo(new System.Drawing.Point(1900, 1060));
            mark("back-" + bib.ToString(CultureInfo.InvariantCulture));
        }
    }

    private static IntPtr s_browser;

    private static void OnTop(IntPtr window, bool top)
    {
        SetWindowPos(window, top ? -1 : -2, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010); // HWND_(NO)TOPMOST; NOSIZE | NOMOVE | NOACTIVATE
        if (!top) { SetWindowPos(window, 1, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010); } // HWND_BOTTOM
    }

    private static void Retry(Action act)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { act(); return; }
            catch (Exception ex) when (attempt < 4 && ex is System.Runtime.InteropServices.COMException or InvalidOperationException or TimeoutException)
            { Thread.Sleep(300); }
        }
    }

    private static JsonObject OperatorRadioConfiguration()
    {
        var path = Environment.GetEnvironmentVariable("OPENSKITIME_LIVE_DEMO_RADIO_SETTINGS") is { Length: > 0 } configured ? configured
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenSkiTime", "live-radio.json");
        var all = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        var id = Environment.GetEnvironmentVariable("OPENSKITIME_LIVE_DEMO_RADIO_COMPETITION");
        var entry = id is { Length: > 0 } ? all[id] : all.Count == 1 ? all.Single().Value : null;
        return entry?["configuration"]?.DeepClone().AsObject()
            ?? throw new InvalidOperationException("Name the competition whose radio settings to use in OPENSKITIME_LIVE_DEMO_RADIO_COMPETITION.");
    }

    private static string CopyMusic(string source, string target)
    {
        if (source.Length == 0 || !Directory.Exists(source)) { return ""; }
        Directory.CreateDirectory(target);
        foreach (var track in Directory.EnumerateFiles(source, "*.mp3").Order(StringComparer.OrdinalIgnoreCase))
        { File.Copy(track, Path.Combine(target, Path.GetFileName(track))); }
        return target;
    }

    private static Guid CompetitionId(string file)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = file, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id FROM Competitions";
        return Guid.Parse((string)command.ExecuteScalar()!);
    }

    private static void WriteRadioSettings(string localData, Guid competition, JsonObject configuration)
    {
        var all = new JsonObject { [competition.ToString()] = new JsonObject { ["configuration"] = configuration, ["spentUsd"] = 0 } };
        File.WriteAllText(Path.Combine(localData, "live-radio.json"), all.ToJsonString(s_json));
    }

    // Large enough to show both columns and the whole race radio card, centred over the timing view.
    private static void PlacePanel(Window panel)
    {
        var handle = panel.Properties.NativeWindowHandle.Value;
        SetWindowPos(handle, 0, 360, 0, 1200, 1032, 0x0040);
        SetForegroundWindow(handle);
    }

    // Wheels the connection-settings column down, as the operator does, until Save radio settings is in view.
    private static void ScrollToRadioSettings(DesktopApp app, Window panel)
    {
        var column = panel.BoundingRectangle;
        Mouse.MoveTo(new System.Drawing.Point(column.Left + column.Width * 3 / 4, column.Top + column.Height / 2));
        for (var i = 0; i < 20; i++)
        {
            var save = app.ById("SaveRadioButton", panel).BoundingRectangle;
            if (save.Bottom > 0 && save.Bottom < column.Bottom - 60) { return; }
            Mouse.Scroll(-2);
            Thread.Sleep(350);
        }
    }

    // The pointer rests briefly on the language model, speech and event background fields, as an operator checks them.
    private static void ShowFields(DesktopApp app, Window panel)
    {
        foreach (var label in new[] { "Language model", "Speech", "Event background for the hosts", "Background music folder (MP3, optional)" })
        {
            var text = panel.FindFirstDescendant(app.By.ByName(label).And(app.By.ByControlType(ControlType.Text)));
            if (text is null) { continue; }
            var r = text.BoundingRectangle;
            Mouse.MoveTo(new System.Drawing.Point(r.Left + 120, r.Bottom + 14));
            Thread.Sleep(1300);
        }
    }

    private static Process StartViewer(string url, string output, TimeSpan duration)
    {
        var python = Environment.GetEnvironmentVariable("OPENSKITIME_LIVE_DEMO_PYTHON") is { Length: > 0 } p ? p : "python";
        var script = Path.Combine(DesktopApp.RepositoryRoot(), "scripts", "demo-video", "live-viewer.py");
        var start = new ProcessStartInfo(python)
        { UseShellExecute = false, RedirectStandardOutput = true, WorkingDirectory = output };
        foreach (var argument in new[] { "-I", script, "--url", url, "--output", output, "--seconds",
            ((int)duration.TotalSeconds).ToString(CultureInfo.InvariantCulture) }) { start.ArgumentList.Add(argument); }
        return Process.Start(start) ?? throw new InvalidOperationException("The live viewer did not start.");
    }

    private static void WaitForLine(Process process, string expected, TimeSpan timeout)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < timeout)
        {
            // A viewer that hangs without output must still end the wait at the timeout.
            var read = process.StandardOutput.ReadLineAsync();
            var left = timeout - watch.Elapsed;
            if (left <= TimeSpan.Zero || !read.Wait(left)) { break; }
            var line = read.Result;
            if (line is null) { throw new InvalidOperationException("The live viewer stopped before " + expected + "."); }
            if (line.Trim() == expected)
            {
                _ = Task.Run(() => { while (process.StandardOutput.ReadLine() is not null) { } });
                return;
            }
        }
        throw new TimeoutException("The live viewer did not report " + expected + ".");
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr window, nint after, int x, int y, int width, int height, uint flags);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern void mouse_event(uint flags, int dx, int dy, uint data, UIntPtr extra);
}
