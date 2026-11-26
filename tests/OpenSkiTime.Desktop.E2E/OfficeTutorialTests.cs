using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;

namespace OpenSkiTime.Desktop.E2E;

// Tutorials of the race office views (scripts/tutorial-video/tutorials/{start-lists,settings,timing-report,race-reports}.json,
// docs/tutorial-videos.md) with the fictional synthetic race. Opt-in like TimingTutorialTests.
public sealed class OfficeTutorialTests
{
    private static readonly System.Drawing.Rectangle s_upperLeft = new(0, 36, 1080, 607);
    private static readonly System.Drawing.Rectangle s_upperRight = new(840, 36, 1080, 607);
    private static readonly System.Drawing.Rectangle s_lowerLeft = new(0, 425, 1080, 607);
    private static readonly System.Drawing.Rectangle s_settings = new(0, 36, 1264, 711);

    [DesktopE2EFact]
    public void RecordStartLists() => TimingTutorial.Record("start-lists", t => t.Office.Navigate("Event series"), (t, video) =>
    {
        var app = t.App;
        video.Step("intro", () => { });
        video.Step("open", () =>
        {
            video.Camera(s_upperLeft, 0);
            video.Click(app.Button("Start lists"), "lists");
            Thread.Sleep(300);
            video.Click(t.MenuItem(t.Competition), "race");
            Thread.Sleep(300);
            video.Click(t.MenuItem("Run 1"), "run");
            Thread.Sleep(600);
            CloseMenus(app);
            video.Wait(() => app.TryByName("Waiting for draw") is not null, "start list open");
        });
        video.Step("method", () =>
        {
            var heading = app.ByName("Waiting for draw").BoundingRectangle;
            video.Move(new(heading.Left + 260, heading.Top + 24), "method");
            var points = Header(app, "FIS PTS");
            video.At("points", -0.8);
            video.Overview();
            video.Move(new(points.Left + 30, points.Bottom + 60), "points");
        });
        video.Step("settings", () =>
        {
            var expander = app.ByName("Draw settings · FIS standard");
            video.Click(expander, "open");
            Thread.Sleep(700);
            video.Hover(Visible(app, "FIRST GROUP"), "group");
            video.Hover(Visible(app, "FIRST BIB"), "bib");
            Thread.Sleep(1800);
            video.Click(expander);
            Thread.Sleep(500);
        });
        video.Step("draw", () =>
        {
            video.Camera(s_upperRight, 0);
            video.Click(app.Button("Draw"), "draw");
            video.Wait(() => app.TryByName("Start list ready") is not null, "start list drawn", 30);
            video.Overview();
            var bib = Header(app, "BIB");
            video.Move(new(bib.Left + 20, bib.Bottom + 60), "bibs");
            var group = Header(app, "GROUP");
            video.Move(new(group.Left + 40, group.Bottom + 90), "group");
        });
        video.Step("move", () =>
        {
            var surname = Header(app, "SURNAME");
            var rows = StartRows(app);
            var row = rows[2].BoundingRectangle;
            video.ClickAt(new(surname.Left + 40, row.Top + row.Height / 2), "row");
            var fields = BottomFields(app, rows[^1].BoundingRectangle.Bottom);
            video.Camera(s_lowerLeft with { X = 600 }, 0);
            video.Click(fields[0], "pos");
            video.Type("8");
            video.Click(fields[1], "reason");
            video.Type("Start penalty");
            video.Click(app.Button("Move"), "move");
            Thread.Sleep(800);
            video.Overview();
            video.Move(new(surname.Left + 40, StartRows(app)[7].BoundingRectangle.Top + 14));
        });
        video.Step("export", () =>
        {
            video.Camera(s_upperRight, 0);
            video.Hover(ButtonStarting(app, "Print view"), "print");
            video.Hover(app.Button("Export TSV"), "tsv");
        });
        video.Step("end", () => { video.Overview(); video.Move(new(1300, 18)); }, gap: 1.2);
    }, TimingTutorial.Stage.Entries);

    [DesktopE2EFact]
    public void RecordSettings() => TimingTutorial.Record("settings", t =>
    {
        // The operator defaults to the Windows user name; the tutorial shows a role instead.
        t.Office.Navigate("Settings");
        t.Office.SelectTab("Timing devices");
        t.App.SetText(t.Office.FieldAfter("DEVICE DATE", "OPERATOR"), "Race office");
        Thread.Sleep(800);
        t.Office.Navigate("Event series");
    }, (t, video) =>
    {
        var app = t.App;
        video.Step("intro", () => { });
        video.Step("keys", () =>
        {
            video.Click(app.ById("SettingsMenuButton"), "settings");
            Thread.Sleep(250);
            video.Click(t.MenuItem("API keys"), "tab");
            Thread.Sleep(500);
            video.Camera(s_settings, 0);
            video.Hover(app.ByName("FIS API key"), "fis");
            video.Hover(app.ByName("FIS Member Section token"), "token");
            video.Hover(app.ByName("regs.fissi.fi Public API key"), "fissi");
        });
        video.Step("devices", () =>
        {
            video.Click(Tab(app, "Timing devices"), "devices");
            Thread.Sleep(600);
            video.Hover(t.Office.FieldAfter("DEVICE DATE", "OPERATOR"), "operator");
            var source = t.StartSource();
            video.Click(source, "source");
            Thread.Sleep(1600);
            app.Press(VirtualKeyShort.ESCAPE);
            var channel = Header(app, "CHANNEL");
            var start = source.BoundingRectangle;
            video.Move(new(channel.Left + 40, start.Top + start.Height / 2), "channel");
            video.Hover(app.Retry(() => app.Window.FindAllDescendants(app.By.ByControlType(ControlType.CheckBox))
                .FirstOrDefault(x => x.Name.StartsWith("Same as Start", StringComparison.Ordinal))), "same");
        });
        video.Step("more", () =>
        {
            video.Hover(app.Button("Add intermediate"), "inter");
            video.Hover(app.Button("Add B Clock"), "bclock");
        });
        video.Step("report", () =>
        {
            video.Click(Tab(app, "Timing report"), "report");
            Thread.Sleep(600);
            video.Overview();
            video.Hover(app.ByName("Report equipment"), "equipment");
            video.Hover(app.ByName("Chief of timing and calculations"), "chief");
            video.Hover(app.ByName("Timekeeper"), "keeper");
            video.Hover(app.ByName("Connections"), "conn");
            video.Hover(app.Button("Save report defaults"), "save");
        });
        video.Step("live", () =>
        {
            video.Click(Tab(app, "Live timing"), "live");
            Thread.Sleep(600);
            video.Camera(s_settings, 0);
            video.Hover(t.Office.FieldAfter("Cloud publisher key", "CLOUD SERVER"), "server");
            video.Hover(t.Office.FieldAfter("Cloud publisher key", "PUBLISHER KEY"), "key");
            video.Hover(app.ByName("Race radio provider keys"), "radio");
        });
        video.Step("end", () => { video.Overview(); video.Move(new(1300, 18)); }, gap: 1.2);
    }, TimingTutorial.Stage.Entries);

    [DesktopE2EFact]
    public void RecordTimingReport() => TimingTutorial.Record("timing-report", t => t.Office.Navigate("Event series"), (t, video) =>
    {
        var app = t.App;
        video.Step("intro", () => { });
        video.Step("open", () =>
        {
            video.Camera(s_upperLeft, 0);
            video.Click(app.Button("Timing report"), "report");
            Thread.Sleep(400);
            ChooseCompetition(t);
            video.Wait(() => app.TryByName("Equipment and officials") is not null, "timing report open", 20);
            video.Hover(app.Retry(() => app.Window.FindAllDescendants(app.By.ByControlType(ControlType.Text))
                .FirstOrDefault(x => x.Name.Contains("/ TD", StringComparison.Ordinal))), "head");
        });
        video.Step("overview", () =>
        {
            video.Hover(app.ByName("Equipment and officials"), "equipment");
            video.Hover(app.Button("Edit in Settings"), "edit");
            video.Hover(app.Retry(() => app.Window.FindAllDescendants(app.By.ByControlType(ControlType.CheckBox))
                .FirstOrDefault(x => x.Name.StartsWith("All times from System A", StringComparison.Ordinal))), "runs");
        });
        video.Step("replace", () => video.Hover(app.Button("Add replacement"), "replace"));
        video.Step("times", () =>
        {
            video.Click(Tab(app, "Times"), "times");
            Thread.Sleep(600);
            video.Overview();
            video.Hover(app.ByName("Synchronization"), "sync");
            video.Hover(Visible(app, "FIRST FINISHER"), "first");
        });
        video.Step("btimes", () =>
        {
            // The system A start of the first finisher of run 1, and the system B field beside it.
            var a = FirstStartA(app);
            var b = app.Window.FindAllDescendants(app.By.ByControlType(ControlType.Edit))
                .Where(x => Math.Abs(x.BoundingRectangle.Top - a.BoundingRectangle.Top) < 6 && x.BoundingRectangle.Left > a.BoundingRectangle.Right)
                .OrderBy(x => x.BoundingRectangle.Left).First();
            var time = a.Patterns.Value.Pattern.Value.Value;
            video.Camera(s_upperLeft, 0);
            video.Click(b, "type");
            video.Type(time.Length > 13 ? time[..13] : time);
            Keyboard.Type(VirtualKeyShort.TAB);
            video.Hover(ButtonStarting(app, "B times"), "scan");
        });
        video.Step("xml", () =>
        {
            video.Overview();
            video.Click(Tab(app, "Overview"), "overview");
            Thread.Sleep(600);
            var boxes = app.Window.FindAllDescendants(app.By.ByControlType(ControlType.CheckBox))
                .Where(x => x.Name.StartsWith("I reviewed", StringComparison.Ordinal) || x.Name.StartsWith("We certify", StringComparison.Ordinal))
                .OrderBy(x => x.BoundingRectangle.Top).ToArray();
            video.Camera(s_lowerLeft, 0);
            video.Click(boxes[0], "check");
            Thread.Sleep(400);
            video.Click(boxes[1]);
            Thread.Sleep(600);
            // The report lists what it still lacks, here the equipment and the timekeeper of the settings.
            var missing = app.Window.FindAllDescendants(app.By.ByControlType(ControlType.Text))
                .FirstOrDefault(x => x.Name.Contains("is required", StringComparison.Ordinal) && x.BoundingRectangle.Width > 0);
            if (missing is not null) { var m = missing.BoundingRectangle; video.Move(new(m.Left + 160, m.Top + 30), "missing"); }
            video.Hover(app.Button("Save to XML"), "save");
            video.Hover(app.Button("Send to FIS"), "send");
            video.Hover(Visible(app, "Test mode"), "test");
        });
        video.Step("end", () => { video.Overview(); video.Move(new(1300, 18)); }, gap: 1.2);
    }, TimingTutorial.Stage.Timed);

    [DesktopE2EFact]
    public void RecordRaceReports() => TimingTutorial.Record("race-reports", t => t.Office.Navigate("Event series"), (t, video) =>
    {
        var app = t.App;
        video.Step("intro", () => { });
        video.Step("open", () =>
        {
            video.Camera(s_upperLeft, 0);
            video.Click(app.Button("Results"), "results");
            Thread.Sleep(400);
            video.Click(t.MenuItem(t.Competition), "race");
            Thread.Sleep(600);
            CloseMenus(app);
            video.Wait(() => app.Window.FindAllDescendants(app.By.ByControlType(ControlType.Text)).Any(x => x.Name.Contains(" classified · ", StringComparison.Ordinal)), "results open", 30);
            video.Overview();
            var total = Header(app, "TOTAL");
            video.Move(new(total.Left + 30, total.Bottom + 80), "list");
        });
        video.Step("penalty", () =>
        {
            video.Click(Tab(app, "Penalty"), "penalty");
            Thread.Sleep(600);
            video.Move(new(700, 400));
        });
        video.Step("info", () =>
        {
            video.Click(Tab(app, "Race information"), "info");
            Thread.Sleep(700);
            video.Hover(app.ByName("Competition jury"), "jury");
            var gates = t.Office.FieldAfter("Run 1", "GATES");
            video.Camera(s_lowerLeft, 0);
            video.Click(gates, "gates");
            video.Type("58");
            Keyboard.Type(VirtualKeyShort.TAB);
            video.Click(t.Office.FieldAfter("Run 1", "START TIME"), "start");
            video.Type("10:00");
            Keyboard.Type(VirtualKeyShort.TAB);
            Thread.Sleep(600);
            video.Overview();
        });
        video.Step("xml", () =>
        {
            video.Click(Tab(app, "FIS XML"), "xml");
            Thread.Sleep(600);
            video.Hover(app.Button("Save to XML"), "save");
            video.Hover(app.Button("Send to FIS"), "send");
        });
        video.Step("pdf", () =>
        {
            video.Click(app.Button("PDF Factory"), "factory");
            Thread.Sleep(500);
            ChooseCompetition(t);
            video.Wait(() => app.TryByName("Generate All") is not null, "PDF Factory open", 20);
            foreach (var (name, cue) in new[] { ("Event Series Entries", "entries"), ("Start List - Run 1", "starts"), ("Referee Report - Run 1", "referee"), ("Official Results", "official"), ("Timing Report", "timing") })
            { video.Hover(app.ByName(name), cue); }
        });
        video.Step("settings", () =>
        {
            var settings = app.ByName("PDF settings");
            video.Click(settings, "settings");
            Thread.Sleep(600);
            video.Camera(s_upperRight, 0);
            video.Hover(app.Button("Choose PDF background"), "background");
            Thread.Sleep(1800);
            video.Overview();
            video.Click(settings);
            Thread.Sleep(500);
        });
        video.Step("generate", () =>
        {
            video.Camera(s_upperRight, 0);
            video.Click(app.Button("Generate All"), "all");
            video.Wait(() => app.Window.FindAllDescendants(app.By.ByControlType(ControlType.Text)).Any(x => x.Name.Contains("report(s) generated", StringComparison.Ordinal)), "PDFs generated", 120);
            video.Overview();
            video.Hover(app.Retry(() => app.Window.FindAllDescendants(app.By.ByName("Open")).Where(x => x.ControlType == ControlType.Button && x.BoundingRectangle.Width > 0)
                .OrderBy(x => x.BoundingRectangle.Top).FirstOrDefault()), "open");
        });
        video.Step("end", () => { video.Overview(); video.Move(new(1300, 18)); }, gap: 1.2);
    }, TimingTutorial.Stage.Timed);

    // Results, Timing report and PDF Factory open with a menu of competitions.
    private static void ChooseCompetition(TimingTutorial t)
    {
        var app = t.App;
        if (app.Automation.GetDesktop().FindAllDescendants(app.By.ByControlType(ControlType.MenuItem))
            .FirstOrDefault(x => x.Name.StartsWith(t.Competition, StringComparison.Ordinal)) is { } item)
        { DesktopApp.Invoke(item); Thread.Sleep(500); }
        CloseMenus(app);
    }

    private static void CloseMenus(DesktopApp app)
    {
        if (app.Automation.GetDesktop().FindAllDescendants(app.By.ByControlType(ControlType.Menu)).Length > 0) { app.Press(VirtualKeyShort.ESCAPE); Thread.Sleep(200); }
    }

    private static AutomationElement Tab(DesktopApp app, string name)
        => app.Retry(() => app.Window.FindFirstDescendant(app.By.ByName(name).And(app.By.ByControlType(ControlType.TabItem))));

    private static AutomationElement Visible(DesktopApp app, string name)
        => app.Retry(() => app.Window.FindAllDescendants(app.By.ByName(name)).FirstOrDefault(x => x.BoundingRectangle.Width > 0 && !x.IsOffscreen));

    private static System.Drawing.Rectangle Header(DesktopApp app, string name) => Visible(app, name).BoundingRectangle;

    private static AutomationElement ButtonStarting(DesktopApp app, string start)
        => app.Retry(() => app.Window.FindAllDescendants(app.By.ByControlType(ControlType.Button)).FirstOrDefault(x => x.Name.StartsWith(start, StringComparison.Ordinal)));

    // The rows of the drawn start list: the grid with the most visible rows.
    private static AutomationElement[] StartRows(DesktopApp app) => app.Retry(() => app.Window.FindAllDescendants(app.By.ByControlType(ControlType.DataGrid))
        .Select(g => g.FindAllDescendants(app.By.ByControlType(ControlType.DataItem)).Where(x => x.BoundingRectangle.Height > 4).OrderBy(x => x.BoundingRectangle.Top).ToArray())
        .Where(x => x.Length > 0).OrderByDescending(x => x.Length).FirstOrDefault());

    // The position and reason boxes of the move bar below the start list, left to right.
    private static AutomationElement[] BottomFields(DesktopApp app, int below) => app.Retry(() =>
    {
        var fields = app.Window.FindAllDescendants(app.By.ByControlType(ControlType.Edit)).Where(x => x.BoundingRectangle.Top > below && x.BoundingRectangle.Width > 0)
            .OrderBy(x => x.BoundingRectangle.Left).ToArray();
        return fields.Length >= 2 ? fields : null;
    });

    // The first time box of the Times tab that holds a time: the system A start of run 1's first finisher.
    private static AutomationElement FirstStartA(DesktopApp app) => app.Retry(() => app.Window.FindAllDescendants(app.By.ByControlType(ControlType.Edit))
        .Where(x => x.BoundingRectangle.Width > 0 && x.Patterns.Value.IsSupported && System.Text.RegularExpressions.Regex.IsMatch(x.Patterns.Value.Pattern.Value.Value ?? "", @"^\d\d:\d\d:\d\d\.\d+$"))
        .OrderBy(x => x.BoundingRectangle.Left).ThenBy(x => x.BoundingRectangle.Top).FirstOrDefault());
}
