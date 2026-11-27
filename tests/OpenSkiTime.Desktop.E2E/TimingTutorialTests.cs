using System.Globalization;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;

namespace OpenSkiTime.Desktop.E2E;

// Tutorials of the Timing view (scripts/tutorial-video/tutorials/timing-*.json, docs/tutorial-videos.md) with a fictional
// race and the training simulator. Opt-in: OPENSKITIME_DESKTOP_E2E=1 and OPENSKITIME_TUTORIAL=<work>\<name>\<language>
// (with timing.json of narrate.py); each test records only its own tutorial.
public sealed class TimingTutorialTests
{
    private const string Simulator = "Simulator";
    private const string Timy = "Timy 2/3 · USB";
    private const string SimulatorConnection = "SIMULATION · no physical device";
    // Camera areas of the 1920 x 1032 view, all 16:9.
    private static readonly System.Drawing.Rectangle s_upperLeft = new(0, 36, 1080, 607);
    private static readonly System.Drawing.Rectangle s_upperRight = new(840, 36, 1080, 607);
    private static readonly System.Drawing.Rectangle s_lowerLeft = new(0, 425, 1080, 607);
    private static readonly System.Drawing.Rectangle s_lowerRight = new(840, 425, 1080, 607);
    private static readonly System.Drawing.Rectangle s_settings = new(0, 72, 1264, 711);
    private static readonly (string Input, string Cue)[] s_inputs = [("START", "start"), ("I1", "i1"), ("FINISH", "finish")];

    [DesktopE2EFact]
    public void RecordBasics() => TimingTutorial.Record("timing-basics",
        // Leaving the view puts the inputs on hold again, as when an operator opens the run.
        t => t.Office.Navigate("Event series"),
        (t, video) =>
        {
            var app = t.App;
            video.Step("intro", () => { });
            video.Step("open", () =>
            {
                video.Camera(s_upperLeft, 0);
                video.Click(app.Button("Timing"), "timing");
                Thread.Sleep(300);
                video.Click(t.MenuItem(t.Competition), "race");
                Thread.Sleep(300);
                video.Click(t.MenuItem("Run 1"), "run");
                Thread.Sleep(600);
                if (app.Automation.GetDesktop().FindAllDescendants(app.By.ByControlType(ControlType.Menu)).Length > 0) { app.Press(VirtualKeyShort.ESCAPE); }
                video.Wait(() => app.TryByName(SimulatorConnection) is not null, "timing open");
                video.Overview();
                video.Move(new(960, 760));
            });
            video.Step("strip", () =>
            {
                t.Find("Test start", "Test I1", "Test finish");
                var connection = app.ByName(SimulatorConnection);
                var toggles = s_inputs.Select(x => (Element: t.Toggle(x.Input), x.Cue)).ToArray();
                video.Camera(s_upperLeft, 0);
                video.Hover(connection, "a");
                video.Hover(app.Retry(() => app.Window.FindAllDescendants(app.By.ByControlType(ControlType.Text)).FirstOrDefault(x => x.Name.StartsWith("HOLD", StringComparison.Ordinal))), "hold");
                foreach (var (element, cue) in toggles) { video.Click(element, cue); }
                TutorialRecorder.Until(TutorialRecorder.Now + 1);
                video.Overview();
            });
            video.Step("panes", () =>
            {
                foreach (var (grid, cue) in new[] { ("AtStartGrid", "atstart"), ("RunningGrid", "course"), ("TimestampsGrid", "stamps"), ("RankingGrid", "ranking") })
                {
                    var b = app.ById(grid).BoundingRectangle;
                    video.Move(new(b.Left + Math.Min(b.Width / 2, 420), b.Top + Math.Min(b.Height / 2, 120)), cue);
                }
            });
            // A fictional forerunner: starts before the racers, is timed, never ranked.
            video.Step("forerunners", () =>
            {
                video.Camera(s_upperLeft, 0);
                video.Click(app.ById("ForerunnersToggle"), "open");
                var add = t.InDialog("AddForerunnerButton");
                var editor = add.BoundingRectangle;
                video.Camera(new System.Drawing.Rectangle(Math.Clamp(editor.Right - 900, 0, 840), Math.Clamp(editor.Bottom - 480, 36, 437), 1080, 607), 0);
                // The editor opens with the first row (A) ready for a name.
                var grid = t.InDialog("ForerunnersGrid");
                var row = grid.FindAllDescendants(app.By.ByControlType(ControlType.DataItem)).Where(x => x.BoundingRectangle.Height > 4).OrderBy(x => x.BoundingRectangle.Top).First().BoundingRectangle;
                int Column(string name) => grid.FindAllDescendants(app.By.ByName(name)).First(x => x.BoundingRectangle.Width > 0).BoundingRectangle.Left + 40;
                var y = row.Top + row.Height / 2;
                video.DoubleClickAt(new(Column("SURNAME"), y), "name");
                video.Type("KOKKO");
                Keyboard.Type(VirtualKeyShort.RETURN);
                Thread.Sleep(200);
                video.DoubleClickAt(new(Column("FIRST NAME"), y));
                video.Type("Eero");
                Keyboard.Type(VirtualKeyShort.RETURN);
                video.Hover(add, "add");
                video.Click(t.InDialog("SaveForerunnersButton"), "save");
                Thread.Sleep(800);
                video.Camera(s_upperLeft, 0);
                var queued = t.RowWith("AtStartGrid", "KOKKO").BoundingRectangle;
                video.Move(new(t.Header("AtStartGrid", "COMPETITOR").Left + 60, queued.Top + queued.Height / 2), "queue");
                video.Fact("forerunner", app.ById("ForerunnersToggle").Properties.HelpText.ValueOrDefault ?? "");
            });
            video.Step("start", () =>
            {
                video.Camera(s_upperLeft, 0);
                video.Fire(() => t.Pulse("Test start"), "pulse");
                Thread.Sleep(500);
                var runner = t.RowWith("RunningGrid", "KOKKO").BoundingRectangle;
                video.Move(new(t.Header("RunningGrid", "COMPETITOR").Left + 60, runner.Top + runner.Height / 2));
                video.Fire(() => t.Pulse("Test start"), "first");
                Thread.Sleep(500);
                video.Move(t.Name("RunningGrid", t.Bibs[0]));
                video.Fire(() => t.Pulse("Test finish"), "finish");
                Thread.Sleep(500);
                video.Camera(s_upperRight, 0);
                var stamps = t.RowWith("TimestampsGrid", "KOKKO");
                video.Move(t.Stamp(stamps, "FINISH"));
            });
            video.Step("next", () =>
            {
                video.Overview();
                video.Fire(() => t.Pulse("Test start"), "pulse");
                video.Fire(() => t.Pulse("Test I1"), "inter");
                Thread.Sleep(400);
                video.Camera(s_upperRight, 0);
                video.Move(t.Stamp(t.Row("TimestampsGrid", t.Bibs[0]), "INTERM 1"));
            });
            video.Step("order", () =>
            {
                video.Camera(s_upperLeft, 0);
                var rows = t.Rows("AtStartGrid");
                var third = rows[^3].BoundingRectangle;
                var next = rows[^1].BoundingRectangle;
                var x = t.Header("AtStartGrid", "COMPETITOR").Left + 60;
                video.DragTo(new(x, third.Top + third.Height / 2), new(x, next.Bottom - 6), "drag");
            });
            video.Step("dns", () =>
            {
                var row = t.Rows("AtStartGrid")[^1].BoundingRectangle;
                var status = TutorialRecorder.Center(app.ById("StatusDropDNS"));
                video.Drag(new(t.Header("AtStartGrid", "COMPETITOR").Left + 60, row.Top + row.Height / 2), status, "grab", "drop", 900);
            });
            video.Step("dnf", () =>
            {
                var status = app.ById("StatusDropDNF");
                video.ClickAt(t.Name("RunningGrid", t.Bibs[1]), "row");
                video.Click(status, "status");
                Thread.Sleep(500);
                // The menu of a racer: the same actions with their shortcuts.
                video.ClickAt(t.Name("RunningGrid", t.Bibs[0]), "menu", right: true);
                Thread.Sleep(2200);
                app.Press(VirtualKeyShort.ESCAPE);
                TutorialRecorder.Until(TutorialRecorder.Now + 0.4);
                video.Overview();
            });
            video.Step("finish", () =>
            {
                video.Fire(() => t.Pulse("Test finish"), "pulse");
                Thread.Sleep(500);
                video.Move(t.Name("RankingGrid", t.Bibs[0]));
                var stamp = t.Stamp(t.Row("TimestampsGrid", t.Bibs[0]), "FINISH");
                video.At("stamps", -0.8);
                video.Camera(s_upperRight, 0);
                video.Move(stamp, "stamps");
                video.At("split", -1.0);
                video.Overview();
                var split = app.Retry(() => app.ById("RankingGrid").FindAllDescendants(app.By.ByControlType(ControlType.Text))
                    .FirstOrDefault(x => x.Name.EndsWith("(1)", StringComparison.Ordinal)));
                video.Hover(split, "split");
            });
            video.Step("end", () =>
            {
                // Prepare Run 2 appears beside the status buttons of On course.
                var clear = app.ById("StatusDropClear").BoundingRectangle;
                video.Move(new(clear.Right + 110, clear.Top + clear.Height / 2), "button");
            }, gap: 1.2);
        });

    [DesktopE2EFact]
    public void RecordErrors() => TimingTutorial.Record("timing-errors",
        t =>
        {
            t.Find("Test start", "Test I1", "Test finish");
            t.Pulse("Test start");
            Thread.Sleep(12000);
        },
        (t, video) =>
        {
            var app = t.App;
            var (first, second, third, fourth, fifth) = (t.Bibs[0], t.Bibs[1], t.Bibs[2], t.Bibs[3], t.Bibs[4]);
            video.Step("intro", () =>
            {
                var stamps = app.ById("TimestampsGrid").BoundingRectangle;
                video.Move(new(stamps.Left + stamps.Width / 2, stamps.Top + 80), "stamps");
                t.Pulse("Test start");
            });
            // The video shows the mouse only: drag and drop and buttons. Shortcuts are for experienced operators.
            video.Step("false", () =>
            {
                t.Pulse("Test start"); // the third racer, for the missing finish later
                video.Fire(() => t.Pulse("Test finish"), "pulse");
                Thread.Sleep(400);
                video.Camera(s_upperRight, 0);
                video.Move(t.Stamp(t.Row("TimestampsGrid", first), "FINISH"));
                video.Fire(() => t.Pulse("Test finish"), "real");
                Thread.Sleep(400);
                video.Move(t.Stamp(t.Row("TimestampsGrid", second), "FINISH"));
            });
            video.Step("drag", () =>
            {
                var from = t.Name("RankingGrid", first);
                var to = t.Stamp(t.Row("TimestampsGrid", second), "FINISH");
                // Ranking and Timestamps are both on the right: the zoom shows the drag and its preview.
                video.Camera(new System.Drawing.Rectangle(840, Math.Clamp((from.Y + to.Y) / 2 - 303, 36, 437), 1080, 607), 0);
                Thread.Sleep(400);
                video.Drag(from, to, "grab", "drop", 4000);
                Thread.Sleep(300);
                var running = t.Name("RunningGrid", second);
                video.At("course", -0.6);
                video.Overview();
                video.Move(running, "course");
                video.At("left", -0.6);
                video.Camera(s_upperRight, 0);
                video.Move(t.Stamp(t.Unassigned(), "FINISH"), "left");
            });
            video.Step("second", () =>
            {
                video.Overview();
                video.Fire(() => t.Pulse("Test finish"), "pulse");
                Thread.Sleep(400);
                video.Move(t.Name("RankingGrid", second));
            });
            video.Step("missing", () =>
            {
                t.Pulse("Test start"); // the fourth
                video.Camera(s_upperRight, 0);
                var grid = app.ById("TimestampsGrid").BoundingRectangle;
                var x = t.Header("TimestampsGrid", "FINISH").Left + 40;
                video.DoubleClickAt(new(x, grid.Bottom - 30), "open");
                video.Wait(() => app.Window.FindFirstDescendant(app.By.ByAutomationId("ManualTimestampTime")) is not null, "manual time editor");
                video.Camera(s_lowerLeft, 0);
                var add = app.ById("SaveManualTimestamp");
                video.At("type", -0.2);
                var time = DateTime.Now.AddSeconds(-2).ToString("HH:mm:ss.ff", CultureInfo.InvariantCulture);
                video.Type(time);
                Thread.Sleep(500); // the typed time stays in view
                var button = add.BoundingRectangle;
                video.Camera(new System.Drawing.Rectangle(Math.Clamp(button.Left + button.Width / 2 - 540, 0, 840), Math.Clamp(button.Top - 303, 36, 437), 1080, 607), 0);
                video.Click(add, "add");
                video.Overview();
                Thread.Sleep(500);
                video.Drag(t.Name("RunningGrid", third), t.Stamp(t.Unassigned(time), "FINISH"), "grab", "drop", 1400);
                Thread.Sleep(400);
                // The time with its mark is right of the name, away from the pointer.
                var ranked = t.Row("RankingGrid", third).BoundingRectangle;
                video.At("mark", -0.6);
                video.Camera(new System.Drawing.Rectangle(840, Math.Clamp(ranked.Top - 303, 36, 437), 1080, 607), 0);
                video.Move(t.Name("RankingGrid", third), "mark");
                t.Pulse("Test start"); // the fifth
            });
            video.Step("pass", () =>
            {
                // The fifth racer passes the fourth, who was expected at the finish.
                video.Overview();
                video.Fire(() => t.Pulse("Test finish"), "pulse");
                Thread.Sleep(400);
                video.Drag(t.Name("RunningGrid", fifth), t.Stamp(t.Row("TimestampsGrid", fourth), "FINISH"), "grab", "drop", 1400);
                Thread.Sleep(300);
                video.Move(t.Name("RunningGrid", fourth), "course");
            });
            video.Step("hold", () =>
            {
                var finish = t.Toggle("FINISH");
                video.Camera(s_upperLeft, 0);
                video.Click(finish, "hold");
                video.Fire(() => t.Pulse("Test finish"), "pulse");
                Thread.Sleep(400);
                video.Camera(s_upperRight, 0);
                video.Move(t.Stamp(t.Rows("TimestampsGrid")[0], "FINISH"));
                video.At("on", -1.2);
                video.Camera(s_upperLeft, 0);
                video.Click(finish, "on");
                Thread.Sleep(500);
                video.Overview();
            });
            video.Step("rerun", () =>
            {
                // Dropped on the empty space of At start, the racer starts next (a row would reorder the waiting racers).
                var start = app.ById("AtStartGrid").BoundingRectangle;
                video.Drag(t.Name("RankingGrid", second), new(start.Left + 160, start.Bottom - 20), "grab", "drop", 900);
                Thread.Sleep(400);
                video.Fire(() => t.Pulse("Test start"), "pulse");
                Thread.Sleep(600);
                video.Move(t.Name("RunningGrid", second));
                Thread.Sleep(1500);
                video.Move(new(1300, 18));
            });
            video.Step("end", () => { }, gap: 1.2);
        });

    [DesktopE2EFact]
    public void RecordTraining() => TimingTutorial.Record("timing-training",
        t =>
        {
            // The operator's name and the intermediate are set; the view starts without a simulator.
            t.Office.Navigate("Settings");
            t.Office.SelectTab("Timing devices");
            t.Office.Select(t.StartSource(), Timy);
            Thread.Sleep(1500);
            t.App.Click("Back to timing");
            Thread.Sleep(1500);
        },
        (t, video) =>
        {
            var app = t.App;
            var (first, second, third) = (t.Bibs[0], t.Bibs[1], t.Bibs[2]);
            void ChooseSource(string name, string open, string choose)
            {
                video.Click(t.StartSource(), open);
                Thread.Sleep(300);
                video.Click(t.Option(name), choose);
            }
            void OpenDevices(string settings, string devices)
            {
                video.Click(app.ById("SettingsMenuButton"), settings);
                Thread.Sleep(250);
                video.Click(t.MenuItem("Timing devices"), devices);
            }
            video.Step("intro", () => { });
            video.Step("settings", () =>
            {
                OpenDevices("settings", "devices");
                video.Camera(s_settings, 0);
                ChooseSource(Simulator, "source", "simulator");
                var same = app.Retry(() => app.Window.FindAllDescendants(app.By.ByControlType(ControlType.CheckBox))
                    .FirstOrDefault(x => x.Name.StartsWith("Same as Start", StringComparison.Ordinal)));
                video.Hover(same, "same");
                video.Click(app.Button("Back to timing"), "back");
                video.Overview();
                video.Wait(() => app.TryByName(SimulatorConnection) is not null, "simulator in timing");
            });
            video.Step("bar", () =>
            {
                var box = app.ById("SimulationTimeBox");
                var toggles = s_inputs.Select(x => (Element: t.Toggle(x.Input), x.Cue)).ToArray();
                video.Camera(s_upperLeft, 0);
                video.Hover(app.ByName(SimulatorConnection), "label");
                video.At("bar", -1);
                video.Camera(s_lowerLeft, 0);
                var label = app.ByName("TRAINING").BoundingRectangle;
                video.Move(new(label.Left + label.Width / 2, label.Top - 14), "bar");
                video.At("start", -1.3);
                video.Camera(s_upperLeft, 0);
                foreach (var (element, cue) in toggles) { video.Click(element, cue); }
                video.Overview();
                t.Find("Test start", "Test I1", "Test finish");
            });
            video.Step("pulses", () =>
            {
                var start = app.Button("Test start");
                var intermediate = app.Button("Test I1");
                var finish = app.Button("Test finish");
                video.Camera(s_lowerLeft, 0);
                video.Click(start, "p1");
                video.Click(intermediate, "p2");
                video.Click(start, "p3");
                video.Click(finish, "p4");
                Thread.Sleep(500);
                video.Move(t.Name("RankingGrid", first));
                video.Overview();
            });
            video.Step("typed", () =>
            {
                // The second racer gets exactly the first one's time.
                var one = t.Times(first);
                var finish = t.Times(second)[0] + (one[^1] - one[0]);
                var box = app.ById("SimulationTimeBox");
                var button = app.Button("Test finish");
                var live = app.ById("SimulationClockToggle");
                video.Camera(s_lowerLeft, 0);
                video.Click(box, "box");
                Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_A);
                video.Type(finish.ToString(@"hh\:mm\:ss\.fffffff", CultureInfo.InvariantCulture));
                video.Click(button, "finish");
                Thread.Sleep(700);
                video.Overview();
                video.Move(t.Name("RankingGrid", second), "tie");
                // Away from the ranking first: a tooltip right after another one opens under the pointer and takes the click.
                video.At("live", -2.3);
                video.Move(new(1100, 985));
                Thread.Sleep(800);
                video.Click(live, "live");
                video.Wait(() => live.Name == "⏸ Pause", "simulator clock running", 4);
            });
            video.Step("ideas", () =>
            {
                var start = app.Button("Test start");
                var finish = app.Button("Test finish");
                video.Camera(s_lowerLeft, 0);
                video.Click(start, "next");
                video.Click(finish, "extra");
                // Nobody else is on course: the real finish stays without a racer.
                video.Click(finish, "real");
                Thread.Sleep(400);
                var from = t.Name("RankingGrid", third);
                var to = t.Stamp(t.Unassigned(), "FINISH");
                video.Camera(new System.Drawing.Rectangle(840, Math.Clamp((from.Y + to.Y) / 2 - 303, 36, 437), 1080, 607), 0);
                video.Drag(from, to, "grab", "drop", 1400);
                Thread.Sleep(600);
                video.Overview();
            });
            video.Step("safe", () =>
            {
                OpenDevices("settings", "devices");
                video.Camera(s_settings, 0);
                ChooseSource(Timy, "source", "real");
                video.Click(app.Button("Back to timing"), "back");
                video.Overview();
                video.Wait(() => app.TryByName("Set training times aside", ControlType.Button) is { IsOffscreen: false }, "training note", 30);
            });
            video.Step("aside", () =>
            {
                var aside = app.Button("Set training times aside");
                video.Camera(s_upperRight, 0);
                video.Click(aside, "aside");
                video.Wait(() => t.Rows("AtStartGrid").Length == TimingTutorial.Racers, "clean run", 30);
                video.Overview();
                var rows = app.ById("AtStartGrid").BoundingRectangle;
                video.Move(new(rows.Left + 160, rows.Top + 120), "done");
                Thread.Sleep(1500);
                video.Move(new(1300, 18));
            });
            video.Step("end", () => { }, gap: 1.2);
        });
}
