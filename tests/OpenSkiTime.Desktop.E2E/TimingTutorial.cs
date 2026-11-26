using System.Globalization;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using OpenSkiTime.Tests.FullRace;

namespace OpenSkiTime.Desktop.E2E;

// What the tutorials of the Timing view share (docs/tutorial-videos.md): a small fictional race prepared before the
// recording, the impulses of the training simulator and the controls of the view. Simulated impulses are sent through
// UI Automation without the pointer, as an impulse of a timing device arrives without the operator.
internal sealed class TimingTutorial
{
    public const int Racers = 10;
    private readonly Dictionary<string, AutomationElement> _buttons = [];
    private readonly SyntheticRace _race;
    public DesktopApp App { get; }
    public RaceOffice Office { get; }
    public int[] Bibs { get; }
    public string Competition => _race.Competition.ShortLabel;

    private TimingTutorial(DesktopApp app, RaceOffice office, SyntheticRace race, int[] bibs)
    {
        App = app; Office = office; _race = race; Bibs = bibs;
    }

    // Records one tutorial when OPENSKITIME_TUTORIAL names its language folder.
    public static void Record(string tutorial, Action<TimingTutorial> prepare, Action<TimingTutorial, TutorialRecorder> record)
    {
        var language = Environment.GetEnvironmentVariable("OPENSKITIME_TUTORIAL");
        if (string.IsNullOrWhiteSpace(language)) { return; }
        language = Path.GetFullPath(language);
        if (Path.GetFileName(Path.GetDirectoryName(language)) != tutorial) { return; }
        var output = Path.Combine(language, "recording-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
        var root = DemoVideoTests.DemoRoot();
        var file = Path.Combine(Directory.CreateDirectory(Path.Combine(root, "Events")).FullName, "Synthetic Alpine Cup 2026.ost");
        var race = new SyntheticRace(raceDate: DateOnly.FromDateTime(DateTime.Now));
        using var app = DesktopApp.Launch(root, local => File.WriteAllBytes(Path.Combine(local, "fis-points-list.zip"), race.PointsListArchive()));
        app.Maximize();
        var office = new RaceOffice(app, race) { OperatorName = "Race office" };
        office.CreateSeries(file);
        office.CreateCompetition();
        Import(app, office, race);
        office.DrawFirstRun();
        var bibs = SeriesFileEvidence.StartOrder(file, 1).OrderBy(x => x.Position).Select(x => x.Bib).ToArray();
        var tutorialRun = new TimingTutorial(app, office, race, bibs);
        office.OpenRun("Timing", 1);
        office.ConnectSimulator();
        prepare(tutorialRun);
        var facts = new Dictionary<string, string> { ["seriesFile"] = file, ["fictional"] = "true" };
        using var video = new TutorialRecorder(app, Path.Combine(language, "timing.json"), output);
        video.Move(new(960, 560));
        video.Start();
        try { record(tutorialRun, video); }
        finally { video.Finish(facts); }
    }

    private static void Import(DesktopApp app, RaceOffice office, SyntheticRace race)
    {
        office.Navigate("Competitors");
        Clipboard.SetText(string.Join("\r\n", race.CompetitorTsv().Split('\n').Select(x => x.TrimEnd('\r')).Where(x => x.Length > 0).Take(Racers + 1)));
        var bounds = app.ById("CompetitorGrid").BoundingRectangle;
        Mouse.Click(new System.Drawing.Point(bounds.Left + bounds.Width / 2, bounds.Top + 60));
        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_V);
        app.WaitUntil(() => app.TryByName($"Pasted {Racers} rows. Review the highlighted cells, then choose Save.") is not null, "pasted rows", TimeSpan.FromSeconds(30));
        app.Click("Save");
        app.WaitUntil(() => app.TryByName("All changes saved") is not null, "competitors saved", TimeSpan.FromSeconds(30));
    }

    // Buttons are looked up before they are needed, so an impulse comes at its word.
    public void Find(params string[] buttons) { foreach (var b in buttons) { _buttons[b] = App.Button(b); } }

    public void Pulse(string button)
    {
        if (!_buttons.TryGetValue(button, out var element)) { _buttons[button] = element = App.Button(button); }
        element.Patterns.Invoke.Pattern.Invoke();
    }

    public AutomationElement Toggle(string name)
        => App.Retry(() => App.Window.FindAllDescendants(App.By.ByName(name)).FirstOrDefault(x => x.Patterns.Toggle.IsSupported));

    public AutomationElement[] Rows(string grid) => App.ById(grid).FindAllDescendants(App.By.ByControlType(ControlType.DataItem))
        .Where(x => x.BoundingRectangle.Height > 4).OrderBy(x => x.BoundingRectangle.Top).ToArray();

    public string[] Texts(AutomationElement row) => row.FindAllDescendants(App.By.ByControlType(ControlType.Text))
        .Where(x => x.BoundingRectangle.Width > 0).OrderBy(x => x.BoundingRectangle.Left).Select(x => x.Name).ToArray();

    public AutomationElement Row(string grid, int bib)
    {
        var text = bib.ToString(CultureInfo.InvariantCulture);
        var column = Header(grid, "BIB");
        return App.Retry(() => Rows(grid).FirstOrDefault(row => row.FindAllDescendants(App.By.ByControlType(ControlType.Text))
            .Any(x => x.Name == text && x.BoundingRectangle.Left >= column.Left - 12 && x.BoundingRectangle.Left < column.Left + 56)));
    }

    // A point on the racer's name, where a row can be clicked and grabbed.
    public System.Drawing.Point Name(string grid, int bib)
    {
        var row = Row(grid, bib).BoundingRectangle;
        return new(Header(grid, "COMPETITOR").Left + 60, row.Top + row.Height / 2);
    }

    public System.Drawing.Rectangle Header(string grid, string name)
        => App.Retry(() => App.ById(grid).FindAllDescendants(App.By.ByName(name)).FirstOrDefault(x => x.BoundingRectangle.Width > 0)).BoundingRectangle;

    // The time cell of a Timestamps row.
    public System.Drawing.Point Stamp(AutomationElement row, string column)
    {
        var bounds = row.BoundingRectangle;
        return new(Header("TimestampsGrid", column).Left + 40, bounds.Top + bounds.Height / 2);
    }

    // The Timestamps row with this time, or the newest one without a bib.
    public AutomationElement Unassigned(string? time = null)
    {
        var column = Header("TimestampsGrid", "COMPETITOR").Left;
        return App.Retry(() => Rows("TimestampsGrid").FirstOrDefault(row => time is not null
            ? Texts(row).Any(x => x.Contains(time, StringComparison.Ordinal))
            : !row.FindAllDescendants(App.By.ByControlType(ControlType.Text)).Any(x => x.BoundingRectangle.Width > 0 && x.BoundingRectangle.Left < column - 8 && x.Name.Length > 0)));
    }

    // The times of day of a racer's Timestamps row, in the order of the columns.
    public TimeSpan[] Times(int bib) => Texts(Row("TimestampsGrid", bib)).Where(x => x.Count(c => c == ':') == 2)
        .Select(x => TimeSpan.Parse(new string(x.Where(c => char.IsAsciiDigit(c) || c is ':' or '.').ToArray()), CultureInfo.InvariantCulture)).ToArray();

    // Settings → Timing devices: the source of the start device; the other roles follow it.
    public AutomationElement StartSource() => Office.FieldAfter("ROLE", "Start");

    // The Timing row that shows this text, such as a forerunner's surname.
    public AutomationElement RowWith(string grid, string text) => App.Retry(() => Rows(grid)
        .FirstOrDefault(row => row.FindAllDescendants(App.By.ByControlType(ControlType.Text)).Any(x => x.Name.Contains(text, StringComparison.Ordinal))));

    // A control of a dialog window (a window of its own, as the forerunner editor).
    public AutomationElement InDialog(string automationId) => App.Retry(() => App.Automation.GetDesktop()
        .FindAllChildren(App.By.ByProcessId(App.Window.Properties.ProcessId.Value))
        .Select(x => x.FindFirstDescendant(App.By.ByAutomationId(automationId))).FirstOrDefault(x => x is not null && !x.BoundingRectangle.IsEmpty));

    public AutomationElement Option(string name) => App.Retry(() => Popups(ControlType.ListItem).FirstOrDefault(x => x.Name == name && !x.BoundingRectangle.IsEmpty));

    public AutomationElement MenuItem(string start) => App.Retry(() => Popups(ControlType.MenuItem)
        .FirstOrDefault(x => x.Name.StartsWith(start, StringComparison.Ordinal) && !x.BoundingRectangle.IsEmpty));

    // Menus and drop-down lists are windows of their own. Only this application's windows are searched: reading every
    // window of the desktop (a browser among them) takes seconds.
    private IEnumerable<AutomationElement> Popups(ControlType type)
    {
        var process = App.Window.Properties.ProcessId.Value;
        return App.Automation.GetDesktop().FindAllChildren(App.By.ByProcessId(process))
            .SelectMany(x => x.FindAllDescendants(App.By.ByControlType(type)));
    }
}
