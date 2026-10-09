using System.Globalization;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using OpenSkiTime.Tests.FullRace;

namespace OpenSkiTime.Desktop.E2E;

// The race office operator's workflow on the real window, step by step. Shared by the real-window tests and the
// demonstration recording, so both exercise exactly the same controls.
internal sealed class RaceOffice(DesktopApp app, SyntheticRace race, Action<string>? chapter = null)
{
    public DesktopApp App => app;
    public SyntheticRace Race => race;
    public int Pace { get; set; } = 1; // 1 = test speed; larger values slow pauses for a recording.

    public void Chapter(string title) => chapter?.Invoke(title);

    public void Pause(int milliseconds = 250) => Thread.Sleep(milliseconds * Pace);

    public void Navigate(string section)
    {
        app.Click(section);
        Pause(300);
    }

    public void CreateSeries(string path)
    {
        Navigate("1  Event series");
        var s = race.Series;
        app.SetText(app.Field("SERIES NAME *"), s.Name); Pause();
        app.SetText(app.Field("LOCATION *"), s.Location); Pause();
        app.SetText(app.Field("ORGANIZER *"), s.Organizer); Pause();
        app.SetText(app.Field("NATION *"), s.Nation);
        app.SetText(app.Field("SEASON *"), s.Season);
        app.SetText(app.ById("SeriesStartDateInput"), s.StartDate.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture));
        app.SetText(app.ById("SeriesEndDateInput"), s.EndDate.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture));
        Pause(500);
        app.Click("Create series file");
        SaveFileDialog("Create event series file", path);
        app.WaitUntil(() => app.Window.Title.Contains(Path.GetFileName(path), StringComparison.OrdinalIgnoreCase), "series file open");
        if (!app.Window.Title.StartsWith(path, StringComparison.OrdinalIgnoreCase))
        { throw new InvalidOperationException($"The series was created at an unexpected path: {app.Window.Title}"); }
        Pause(500);
    }

    // Drives the Windows common file dialog that Avalonia opens for Save/Open: navigate to the folder first, verify the
    // dialog shows it, then enter the file name and save. If the dialog is not in the intended folder the dialog is
    // cancelled, so automation can never write into the operator's own folders.
    public void SaveFileDialog(string title, string path)
    {
        var dialog = app.Retry(() => app.Window.ModalWindows.FirstOrDefault(x => x.Title == title)
            ?? app.Automation.GetDesktop().FindFirstChild(app.By.ByName(title))?.AsWindow(), TimeSpan.FromSeconds(20));
        AutomationElement NameBox() => app.Retry(() => dialog.FindFirstDescendant(app.By.ByAutomationId("1001").And(app.By.ByControlType(ControlType.Edit))));
        var folder = Path.GetDirectoryName(path)!;
        app.SetText(NameBox(), folder);
        app.Press(VirtualKeyShort.RETURN);
        var leaf = Path.GetFileName(folder);
        bool InFolder() => dialog.FindAllDescendants(app.By.ByControlType(ControlType.ToolBar))
            .Any(x => x.Name.StartsWith("Address:", StringComparison.Ordinal) && x.Name.EndsWith(leaf, StringComparison.OrdinalIgnoreCase));
        try { app.WaitUntil(InFolder, "file dialog in " + folder, TimeSpan.FromSeconds(10)); }
        catch (TimeoutException)
        {
            dialog.FindFirstDescendant(app.By.ByAutomationId("2").And(app.By.ByControlType(ControlType.Button)))?.AsButton().Invoke();
            throw;
        }
        Pause(300);
        app.SetText(NameBox(), Path.GetFileName(path));
        Pause(300);
        if (!InFolder())
        {
            dialog.FindFirstDescendant(app.By.ByAutomationId("2").And(app.By.ByControlType(ControlType.Button)))?.AsButton().Invoke();
            throw new InvalidOperationException("The file dialog left the target folder; cancelled.");
        }
        var save = app.Retry(() => dialog.FindFirstDescendant(app.By.ByAutomationId("1").And(app.By.ByControlType(ControlType.Button))));
        DesktopApp.Invoke(save);
        Thread.Sleep(500);
        if (app.Automation.GetDesktop().FindFirstDescendant(app.By.ByName("Confirm Save As")) is { } confirm)
        { confirm.FindFirstDescendant(app.By.ByName("Yes"))?.AsButton().Invoke(); }
        app.WaitUntil(() => app.Window.ModalWindows.Length == 0, "file dialog closed");
    }

    public void CreateCompetition()
    {
        Navigate("2  Competitions");
        app.Click("Add competition");
        Pause(400);
        var c = race.Competition;
        const string form = "ACTIVE RACE / DETAILS";
        app.SetText(FieldAfter(form, "SHORT NAME *"), c.ShortLabel); Pause();
        app.SetText(FieldAfter(form, "EVENT NAME *"), c.Name); Pause();
        app.SetText(FieldAfter(form, "FIS CATEGORY"), c.Calendar!.Category);
        SetNumber(FieldAfter(form, "INTERMEDIATES"), c.IntermediateCount);
        app.SetText(FieldAfter(form, "FIS CODEX"), c.FisCode!);
        Select(FieldAfter("LOCATION", "GENDER"), c.Calendar.Gender);
        var td = c.Calendar.TechnicalDelegate!;
        app.SetText(FieldAfter("TECHNICAL DELEGATE", "SURNAME"), td.LastName);
        app.SetText(FieldAfter("TECHNICAL DELEGATE", "FIRST NAME"), td.FirstName);
        app.SetText(FieldAfter("TECHNICAL DELEGATE", "NATION"), td.Nation);
        app.SetText(FieldAfter("TECHNICAL DELEGATE", "TD NUMBER"), td.Number);
        app.SetText(FieldAfter("COURSE AND HOMOLOGATION", "COURSE"), c.CourseName!);
        app.SetText(FieldAfter("COURSE AND HOMOLOGATION", "HOMOLOGATION"), c.HomologationNumber!);
        app.SetText(FieldAfter("COURSE AND HOMOLOGATION", "START ALTITUDE · M"), Number(c.StartAltitudeMeters));
        app.SetText(FieldAfter("COURSE AND HOMOLOGATION", "FINISH ALTITUDE · M"), Number(c.FinishAltitudeMeters));
        app.SetText(FieldAfter("COURSE AND HOMOLOGATION", "VERTICAL DROP · M"), Number(c.VerticalDropMeters));
        app.SetText(FieldAfter("COURSE AND HOMOLOGATION", "COURSE LENGTH · M"), Number(c.CourseLengthMeters));
        Pause(500);
        app.Click("Save competition");
        app.WaitUntil(() => app.TryByName(c.ShortLabel) is not null && app.TryByName("Edit competition") is not null, "competition saved");
        Pause(500);
    }

    public void ImportCompetitors()
    {
        Navigate("3  Competitors");
        Clipboard.SetText(race.CompetitorTsv());
        var grid = app.ById("CompetitorGrid");
        if (DesktopApp.PhysicalInput)
        {
            var bounds = grid.BoundingRectangle;
            Mouse.Click(new System.Drawing.Point(bounds.Left + bounds.Width / 2, bounds.Top + 60));
        }
        else { grid.Focus(); }
        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_V);
        app.WaitUntil(() => app.TryByName($"Pasted {SyntheticRace.AthleteCount} rows. Review the highlighted cells, then choose Save changes.") is not null,
            "pasted rows", TimeSpan.FromSeconds(30));
        Pause(1500);
        app.Click("Save changes");
        app.WaitUntil(() => app.TryByName("All changes saved") is not null, "competitors saved", TimeSpan.FromSeconds(30));
        Pause(800);
    }

    // Opens Start lists or Timing for the competition and run through the navigation menu.
    public void OpenRun(string section, int run)
    {
        app.Click(section);
        Pause(400);
        var competition = app.Retry(() => app.Automation.GetDesktop().FindAllDescendants(app.By.ByControlType(ControlType.MenuItem))
            .FirstOrDefault(x => x.Name.StartsWith(race.Competition.ShortLabel, StringComparison.Ordinal)));
        if (competition.Patterns.ExpandCollapse.IsSupported) { competition.Patterns.ExpandCollapse.Pattern.Expand(); }
        else { DesktopApp.Invoke(competition); }
        Pause(300);
        var item = app.Retry(() => app.Automation.GetDesktop().FindAllDescendants(app.By.ByControlType(ControlType.MenuItem))
            .FirstOrDefault(x => x.Name.StartsWith($"Run {run}", StringComparison.Ordinal)));
        if (item.Patterns.Invoke.IsSupported) { item.Patterns.Invoke.Pattern.Invoke(); } else { DesktopApp.Invoke(item); }
        Pause(600);
        if (app.Automation.GetDesktop().FindAllDescendants(app.By.ByControlType(ControlType.Menu)).Length > 0) { app.Press(VirtualKeyShort.ESCAPE); }
    }

    public void DrawFirstRun()
    {
        OpenRun("4  Start lists  ▾", 1);
        app.Click("Draw");
        app.WaitUntil(() => app.TryByName("Start list ready") is not null, "draw saved", TimeSpan.FromSeconds(30));
        Pause(1000);
    }

    public void SetNumber(AutomationElement field, int value)
    {
        var edit = field.ControlType == ControlType.Edit ? field : field.FindFirstDescendant(app.By.ByControlType(ControlType.Edit)) ?? field;
        app.SetText(edit, value.ToString(CultureInfo.InvariantCulture));
        app.Press(VirtualKeyShort.TAB);
    }

    public void Select(AutomationElement combo, string item)
    {
        // Avalonia materializes the items when the drop-down opens; open it the way the operator does.
        if (DesktopApp.PhysicalInput) { DesktopApp.Invoke(combo); }
        else if (combo.Patterns.ExpandCollapse.IsSupported) { combo.Patterns.ExpandCollapse.Pattern.Expand(); }
        Pause(300);
        var option = app.Retry(() => combo.FindAllDescendants(app.By.ByControlType(ControlType.ListItem)).FirstOrDefault(x => x.Name == item)
            ?? app.Automation.GetDesktop().FindAllDescendants(app.By.ByControlType(ControlType.ListItem)).FirstOrDefault(x => x.Name == item));
        if (DesktopApp.PhysicalInput) { DesktopApp.Invoke(option); }
        else
        {
            option.Patterns.SelectionItem.Pattern.Select();
            if (combo.Patterns.ExpandCollapse.IsSupported) { combo.Patterns.ExpandCollapse.Pattern.Collapse(); }
        }
        Pause(300);
    }

    // The input after `label` that follows the section heading `section`. Labels such as GENDER or COURSE also appear
    // as grid column headers earlier in the tree, so form fields are always located relative to their section.
    public AutomationElement FieldAfter(string section, string label) => app.Retry(() =>
    {
        var all = app.Window.FindAllDescendants();
        var start = Array.FindIndex(all, x => x.ControlType == ControlType.Text && x.Name == section);
        if (start < 0) { return null; }
        var index = Array.FindIndex(all, start + 1, x => x.ControlType == ControlType.Text && x.Name == label);
        return index < 0 ? null : all.Skip(index + 1).FirstOrDefault(x => x.ControlType is ControlType.Edit or ControlType.ComboBox or ControlType.Spinner);
    });

    private static string Number(int? value) => value!.Value.ToString(CultureInfo.InvariantCulture);
}
