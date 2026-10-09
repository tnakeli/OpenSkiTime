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

    // Called with screen areas that must not appear in recordings while they are shown (true) and when they disappear
    // (false): Windows file dialogs list the user's own folders, and the FIS calendar names real officials.
    public Action<System.Drawing.Rectangle, bool>? FileDialog { get; set; }

    private void TrackDialog(AutomationElement dialog)
    {
        var bounds = dialog.BoundingRectangle;
        FileDialog?.Invoke(new System.Drawing.Rectangle(bounds.Left, bounds.Top, bounds.Width, bounds.Height), true);
    }

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

    // Drives the Windows common file dialog that Avalonia opens for Save/Open. The target folder gets a uniquely named
    // marker file; the dialog must list that marker after navigation before anything is saved, so the file can only be
    // written into the intended folder. A request to replace an existing file is always declined: automation writes
    // only new files and never overwrites anything.
    public void SaveFileDialog(string title, string path)
    {
        path = Path.GetFullPath(path);
        var folder = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(folder);
        // The marker uses the target extension so the dialog's file-type filter lists it.
        var marker = Path.Combine(folder, $"openskitime-e2e-marker-{Guid.NewGuid():N}{Path.GetExtension(path)}");
        File.WriteAllText(marker, "Target folder marker for real-window automation.");
        try
        {
            var dialog = app.Retry(() => app.Window.ModalWindows.FirstOrDefault(x => x.Title == title), TimeSpan.FromSeconds(20));
            TrackDialog(dialog);
            void Cancel() => dialog.FindFirstDescendant(app.By.ByAutomationId("2").And(app.By.ByControlType(ControlType.Button)))?.AsButton().Invoke();
            AutomationElement NameBox() => app.Retry(() => dialog.FindFirstDescendant(app.By.ByAutomationId("1001").And(app.By.ByControlType(ControlType.Edit))));
            // Explorer hides known extensions (for example .xml), so the marker is matched with or without it.
            bool InFolder() => dialog.FindFirstDescendant(app.By.ByName(Path.GetFileName(marker)).Or(app.By.ByName(Path.GetFileNameWithoutExtension(marker)))) is not null;
            app.SetText(NameBox(), folder);
            app.Press(VirtualKeyShort.RETURN);
            try { app.WaitUntil(InFolder, "file dialog showing " + folder, TimeSpan.FromSeconds(10)); }
            catch (TimeoutException) { Cancel(); throw; }
            Pause(300);
            app.SetText(NameBox(), Path.GetFileName(path));
            Pause(300);
            if (!InFolder()) { Cancel(); throw new InvalidOperationException("The file dialog left the target folder; cancelled."); }
            DesktopApp.Invoke(app.Retry(() => dialog.FindFirstDescendant(app.By.ByAutomationId("1").And(app.By.ByControlType(ControlType.Button)))));
            Thread.Sleep(500);
            // A replace confirmation belongs to this dialog's process; decline it.
            if (app.Window.ModalWindows.SelectMany(x => x.ModalWindows.Append(x)).FirstOrDefault(x => x.Title == "Confirm Save As") is { } confirm)
            {
                confirm.FindFirstDescendant(app.By.ByName("No"))?.AsButton().Invoke();
                Cancel();
                throw new InvalidOperationException("Refusing to overwrite an existing file: " + path);
            }
            app.WaitUntil(() => app.Window.ModalWindows.Length == 0, "file dialog closed");
        }
        finally { File.Delete(marker); FileDialog?.Invoke(default, false); }
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

    // Settings → Timing devices: the simulator is the start device (finish and intermediate follow it), then connect.
    public void ConnectSimulator()
    {
        Navigate("Settings");
        SelectTab("Timing devices");
        var source = FieldAfter("ROLE", "Start");
        if (source.ControlType == ControlType.ComboBox && !(source.Name ?? "").Contains("Simulator", StringComparison.Ordinal)
            && source.FindFirstDescendant(app.By.ByName("Simulator")) is null)
        { Select(source, "Simulator"); }
        Pause(400);
        for (var i = 1; i <= race.Competition.IntermediateCount && app.TryByName($"Intermediate {i}") is null; i++)
        { app.Click("Add intermediate"); Pause(400); }
        app.Click("Connect");
        app.WaitUntil(() => app.TryByName("Disconnect") is not null || app.TryByName("SIMULATION · no physical device") is not null,
            "simulator connected", TimeSpan.FromSeconds(20));
        Pause(500);
        app.Click("Back to timing");
        Pause(500);
        SwitchInputsOn();
    }

    // A new connection or a newly selected run holds every timing position; the operator switches the inputs on
    // before the first start.
    public void SwitchInputsOn()
    {
        foreach (var input in Enumerable.Repeat("START", 1).Concat(Enumerable.Range(1, race.Competition.IntermediateCount).Select(i => $"I{i}")).Append("FINISH"))
        {
            var toggle = app.Retry(() => app.Window.FindAllDescendants(app.By.ByName(input)).FirstOrDefault(x => x.Patterns.Toggle.IsSupported));
            app.WaitUntil(() => toggle.IsEnabled, input + " input available");
            if (toggle.Patterns.Toggle.Pattern.ToggleState.Value != ToggleState.On) { DesktopApp.Invoke(toggle); }
            Pause(250);
        }
        app.WaitUntil(() => app.TryByName("Next · HOLD · impulses kept unassigned") is null, "inputs switched on");
    }

    // One run on the real Timing view with the training simulator: the operator types each impulse's device time and
    // presses Test start / Test I1 / Test finish in chronological order; DNS and DNF use the quick status buttons on the
    // selected row. `order` is the start list as drawn by the application (bib by position).
    public void TimeRun(int run, IReadOnlyList<(int Position, int Bib, string Code)> order, IReadOnlyDictionary<string, SyntheticRun> plans,
        Action<int, int>? progress = null)
    {
        var events = new List<(long At, int Priority, Action Act)>();
        var onCourse = new HashSet<int>();
        foreach (var (position, bib, code) in order)
        {
            var plan = plans[code];
            var start = SyntheticRace.StartTimeOfDay(run, position);
            if (plan.Outcome == RunOutcome.DNS) { events.Add((start, 0, () => Classify("AtStartGrid", bib, "DNS", null))); continue; }
            events.Add((start, 1, () => { Pulse("Test start", start); onCourse.Add(bib); }));
            if (!plan.MissingIntermediate && !(plan.Outcome == RunOutcome.DNF && plan.MissingIntermediate))
            { events.Add((start + plan.IntermediateTicks, 1, () => Pulse("Test I1", start + plan.IntermediateTicks))); }
            if (plan.Outcome == RunOutcome.DNF)
            { events.Add((start + plan.FinishTicks, 2, () => { Classify("RunningGrid", bib, "DNF", onCourse.Count); onCourse.Remove(bib); })); }
            else { events.Add((start + plan.FinishTicks, 1, () => { Pulse("Test finish", start + plan.FinishTicks); onCourse.Remove(bib); })); }
        }
        var done = 0;
        var ordered = events.OrderBy(x => x.At).ThenBy(x => x.Priority).ToArray();
        foreach (var (_, _, act) in ordered) { act(); progress?.Invoke(++done, ordered.Length); }
    }

    private AutomationElement? _simulationTime;
    private readonly Dictionary<string, AutomationElement> _buttons = [];

    // Fast impulses: the simulator time box and buttons are driven through their UI Automation patterns (Value and
    // Invoke) instead of typed keys and pointer clicks; the same controls and commands, about five times faster.
    public bool FastImpulses { get; set; }

    private void Pulse(string button, long timeOfDay)
    {
        _simulationTime ??= app.ById("SimulationTimeBox");
        var text = new TimeOnly(timeOfDay).ToString("HH:mm:ss.ffff", CultureInfo.InvariantCulture);
        if (!_buttons.TryGetValue(button, out var element)) { _buttons[button] = element = app.Button(button); }
        if (FastImpulses && _simulationTime.Patterns.Value.IsSupported && element.Patterns.Invoke.IsSupported)
        {
            _simulationTime.Patterns.Value.Pattern.SetValue(text);
            element.Patterns.Invoke.Pattern.Invoke();
            Thread.Sleep(40);
            return;
        }
        app.SetText(_simulationTime, text);
        DesktopApp.Invoke(element);
        Thread.Sleep(Math.Max(60, 40 * Pace));
    }

    // Selects the competitor's row in a timing grid and applies a quick status (DNS/DNF/DSQ/NPS). Like a careful
    // operator, it first lets the screen catch up with the impulses already sent (the Running list shows exactly the
    // racers on course), then checks the selected-racer line before pressing the status.
    public void Classify(string grid, int bib, string status, int? expectedRows)
    {
        if (expectedRows is { } rows)
        {
            app.WaitUntil(() => app.ById(grid).FindAllDescendants(app.By.ByControlType(ControlType.DataItem)).Length == rows,
                $"{rows} racer(s) shown in {grid}");
        }
        var identity = bib.ToString(CultureInfo.InvariantCulture) + " · ";
        if (grid == "AtStartGrid")
        {
            app.WaitUntil(() => app.Window.FindAllDescendants(app.By.ByControlType(ControlType.Text)).Any(x => x.Name.StartsWith("Next · " + identity, StringComparison.Ordinal)),
                $"bib {bib} is the next starter");
        }
        for (var attempt = 0; ; attempt++)
        {
            SelectRow(grid, bib);
            try { app.WaitUntil(() => app.Window.FindAllDescendants(app.By.ByControlType(ControlType.Text)).Any(x => x.Name.StartsWith(identity, StringComparison.Ordinal)), $"bib {bib} selected", TimeSpan.FromSeconds(3)); break; }
            catch (TimeoutException) when (attempt < 2) { }
        }
        app.Click(status);
        Pause(300);
    }

    // Selects the competitor's row. Rows outside the visible part of a virtualized grid are not in the automation tree,
    // so the Ranking is first filtered by bib with its own column filter, as an operator would search it.
    public void SelectRow(string grid, int bib)
    {
        var text = bib.ToString(CultureInfo.InvariantCulture);
        var column = grid == "RankingGrid" ? 2 : 1; // marker, [rank,] bib
        AutomationElement? Find() => app.ById(grid).FindAllDescendants(app.By.ByControlType(ControlType.DataItem))
            .FirstOrDefault(r => r.FindAllDescendants(app.By.ByAutomationId("CellTextBlock")).ElementAtOrDefault(column)?.Name == text);
        var row = Find();
        if (row is null && grid == "RankingGrid")
        {
            FilterRanking(text);
            row = app.Retry(Find);
        }
        row ??= app.Retry(Find);
        var cell = row.FindAllDescendants(app.By.ByAutomationId("CellTextBlock")).ElementAt(column);
        if (FastImpulses && row.Patterns.SelectionItem.IsSupported) { row.Patterns.SelectionItem.Pattern.Select(); }
        else if (DesktopApp.PhysicalInput)
        {
            var bounds = cell.BoundingRectangle;
            Mouse.Click(new System.Drawing.Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2));
        }
        else if (row.Patterns.SelectionItem.IsSupported) { row.Patterns.SelectionItem.Pattern.Select(); }
        Pause(200);
    }

    // Ranking column filter on BIB; an empty value clears it (the filter button clears an active filter).
    public void FilterRanking(string value)
    {
        var button = app.ById("RankingFilterButton_Bib");
        DesktopApp.Invoke(button);
        if (value.Length == 0) { Pause(300); return; }
        var box = app.Retry(() => app.Automation.GetDesktop().FindFirstDescendant(app.By.ByAutomationId("RankingFilter_Bib")));
        app.SetText(box, value);
        app.Press(VirtualKeyShort.RETURN);
        Pause(400);
    }

    // Post-run disqualification through the classification editor: status, gate, reason and judge, then save.
    public void Disqualify(int bib, SyntheticRun plan)
    {
        SelectRow("RankingGrid", bib);
        var toggle = app.ById("EditClassificationToggle");
        if (toggle.Patterns.Toggle.PatternOrDefault?.ToggleState.ValueOrDefault != FlaUI.Core.Definitions.ToggleState.On) { DesktopApp.Invoke(toggle); }
        Pause(400);
        Select(app.ById("TimingClassificationChoice"), "DSQ");
        app.SetText(FieldAfter("STATUS", "GATE (OPTIONAL)"), plan.DsqGate!.Value.ToString(CultureInfo.InvariantCulture)); Pause();
        app.SetText(FieldAfter("STATUS", "DSQ REASON / ICR RULE (OPTIONAL)"), plan.DsqReason!); Pause();
        app.SetText(FieldAfter("STATUS", "JUDGE (OPTIONAL)"), "Gate judge 3"); Pause();
        DesktopApp.Invoke(app.ById("SaveTimingClassification"));
        Pause(800);
        if (app.Window.FindFirstDescendant(app.By.ByAutomationId("RankingFilterButton_Bib")) is { } filter
            && app.ById("RankingGrid").FindAllDescendants(app.By.ByControlType(ControlType.DataItem)).Length <= 1)
        { DesktopApp.Invoke(filter); Pause(300); } // clear the bib filter again
    }

    public void PrepareSecondRun()
    {
        app.Click("Prepare Run 2");
        app.WaitUntil(() => app.TryByName("Create start list") is { IsEnabled: true }, "run 2 start list can be created", TimeSpan.FromSeconds(30));
        Pause(600);
        app.Click("Create start list");
        app.WaitUntil(() => app.TryByName("Start list ready") is not null, "run 2 start list saved", TimeSpan.FromSeconds(30));
        Pause(1000);
    }

    // Timing capture belongs to a run; connect the simulator again when the selected run is not connected.
    public void EnsureConnected()
    {
        Pause(500);
        if (app.TryByName("Test start", ControlType.Button) is { IsEnabled: true }) { SwitchInputsOn(); return; }
        ConnectSimulator();
    }

    public void OpenResults()
    {
        app.Click("6  Results  ▾");
        Pause(400);
        var item = app.Retry(() => app.Automation.GetDesktop().FindAllDescendants(app.By.ByControlType(ControlType.MenuItem))
            .FirstOrDefault(x => x.Name.StartsWith(race.Competition.ShortLabel, StringComparison.Ordinal)));
        if (item.Patterns.Invoke.IsSupported) { item.Patterns.Invoke.Pattern.Invoke(); } else { DesktopApp.Invoke(item); }
        Pause(600);
        if (app.Automation.GetDesktop().FindAllDescendants(app.By.ByControlType(ControlType.Menu)).Length > 0) { app.Press(VirtualKeyShort.ESCAPE); }
        app.WaitUntil(() => app.Window.FindAllDescendants(app.By.ByControlType(ControlType.Text)).Any(x => x.Name.Contains(" classified · ", StringComparison.Ordinal)),
            "results loaded", TimeSpan.FromSeconds(30));
        Pause(800);
    }

    // Opens an existing series through the Windows open dialog (full path typed into the file name box).
    public void OpenSeries(string path)
    {
        app.Click("Open file");
        OpenFileDialog("Open event series file", path);
        app.WaitUntil(() => app.Window.Title.StartsWith(path, StringComparison.OrdinalIgnoreCase), "series opened", TimeSpan.FromSeconds(30));
        Pause(800);
    }

    // Reads an existing file through the Windows open dialog; reading never modifies the chosen file.
    public void OpenFileDialog(string title, string path)
    {
        path = Path.GetFullPath(path);
        var dialog = app.Retry(() => app.Window.ModalWindows.FirstOrDefault(x => x.Title == title), TimeSpan.FromSeconds(20));
        TrackDialog(dialog);
        var name = app.Retry(() => dialog.FindFirstDescendant(app.By.ByAutomationId("1148"))?.FindFirstDescendant(app.By.ByControlType(ControlType.Edit))
            ?? dialog.FindFirstDescendant(app.By.ByAutomationId("1148")));
        app.SetText(name, path);
        app.Press(VirtualKeyShort.RETURN);
        try { app.WaitUntil(() => app.Window.ModalWindows.Length == 0, "open dialog closed"); }
        finally { FileDialog?.Invoke(default, false); }
        Pause(500);
    }

    // Event series → Browse FIS calendar: the season calendar is loaded from the FIS API (needs the FIS API key in
    // Settings); an event is selected to show its races, then the calendar is closed without importing anything.
    public void BrowseFisCalendar()
    {
        Navigate("1  Event series");
        app.Click("Browse FIS calendar");
        var grid = app.ById("SeriesCalendarEventsGrid");
        app.WaitUntil(() => grid.FindAllDescendants(app.By.ByControlType(ControlType.DataItem)).Length > 3, "FIS calendar loaded", TimeSpan.FromSeconds(90));
        Pause(1500);
        // The races list names real technical delegates; recordings blur those columns.
        var races = app.ById("SeriesCalendarCompetitionsGrid");
        var tdHeader = app.Retry(() => races.FindAllDescendants(app.By.ByName("TD SURNAME")).FirstOrDefault());
        var area = races.BoundingRectangle;
        FileDialog?.Invoke(new System.Drawing.Rectangle(tdHeader.BoundingRectangle.Left, area.Top, area.Right - tdHeader.BoundingRectangle.Left, area.Height), true);
        var rows = grid.FindAllDescendants(app.By.ByControlType(ControlType.DataItem));
        var row = rows[Math.Min(3, rows.Length - 1)];
        var bounds = row.BoundingRectangle;
        Mouse.Click(new System.Drawing.Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2));
        Pause(2500);
        app.Click("Close calendar");
        FileDialog?.Invoke(default, false);
        Pause(500);
    }

    // Settings → FIS: refresh the FIS equipment (timing device) homologations from the FIS API.
    public void RefreshEquipmentHomologations()
    {
        Navigate("Settings");
        SelectTab("FIS");
        app.Click("Refresh homologations");
        Thread.Sleep(1000);
        app.WaitUntil(() => app.Window.FindAllDescendants(app.By.ByControlType(ControlType.Button))
            .Any(x => x.Name == "Refresh homologations" && x.IsEnabled), "homologations refreshed", TimeSpan.FromSeconds(60));
        Pause(1500);
    }

    // After the race the operator disconnects timing; registration and approvals are locked while capture runs.
    public void DisconnectTiming()
    {
        Navigate("Settings");
        SelectTab("Timing devices");
        if (app.TryByName("Disconnect", ControlType.Button) is { IsEnabled: true } disconnect)
        {
            DesktopApp.Invoke(disconnect);
            app.WaitUntil(() => app.TryByName("Connect", ControlType.Button) is { IsEnabled: true }, "timing disconnected", TimeSpan.FromSeconds(30));
        }
        Pause(500);
    }

    public void ScrollPage(int notches)
    {
        var bounds = app.Window.BoundingRectangle;
        Mouse.MoveTo(new System.Drawing.Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2));
        for (var i = 0; i < Math.Abs(notches); i++) { Mouse.Scroll(notches < 0 ? -1 : 1); Thread.Sleep(60 * Pace); }
        Pause(600);
    }

    // The competition jury in Race information: the chief of race is required for the FIS XML.
    public void EnterChiefOfRace(string firstName, string lastName, string nation)
    {
        // Each cell is edited on its own: double-click starts editing, Enter commits. The row is looked up again for
        // every cell because focusing a cell can scroll the Results page.
        AutomationElement Cell(int column) => app.Retry(() => app.Window.FindAllDescendants(app.By.ByControlType(ControlType.DataItem))
            .FirstOrDefault(r => r.FindAllDescendants(app.By.ByControlType(ControlType.Text)).Any(t => t.Name == "Chief of race"))
            ?.FindAllChildren(app.By.ByClassName("DataGridCell")).ElementAtOrDefault(column));
        foreach (var (column, value) in new[] { (1, firstName), (2, lastName), (3, nation) })
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var cell = Cell(column);
                cell.Focus();
                Pause(300);
                cell = Cell(column);
                var bounds = cell.BoundingRectangle;
                Mouse.DoubleClick(new System.Drawing.Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2));
                Pause(300);
                Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_A);
                Keyboard.Type(value);
                app.Press(VirtualKeyShort.RETURN);
                Pause(400);
                if (Cell(column).FindAllDescendants(app.By.ByControlType(ControlType.Text)).Any(t => t.Name == value)) { break; }
            }
        }
        Pause(1200);
    }

    // Race information needed for the FIS XML: gates, turning gates, start time and course setter of each run.
    public void CompleteRaceInformation()
    {
        string[] setters = ["Ola|SYNTHSETTER|NOR", "Ulla|SYNTHSETTER|AUT"];
        for (var run = 1; run <= race.Competition.RunCount; run++)
        {
            var heading = $"Run {run}";
            app.SetText(FieldAfter(heading, "GATES"), run == 1 ? "58" : "60"); Pause();
            app.SetText(FieldAfter(heading, "TURNS"), run == 1 ? "56" : "58"); Pause();
            app.SetText(FieldAfter(heading, "START HH:mm"), run == 1 ? "10:00" : "13:00"); Pause();
            var setter = setters[run - 1].Split('|');
            var first = FieldAfter(heading, "COURSE SETTER");
            app.SetText(first, setter[0]);
            app.Press(VirtualKeyShort.TAB); Keyboard.Type(setter[1]);
            app.Press(VirtualKeyShort.TAB); Keyboard.Type(setter[2]);
            Pause(600);
        }
        app.Press(VirtualKeyShort.TAB);
        Pause(1500);
    }

    public void ApproveAndExportXml(string directory)
    {
        Directory.CreateDirectory(directory);
        app.Click("TD approves · create XML");
        app.WaitUntil(() => app.Window.FindAllDescendants(app.By.ByControlType(ControlType.Text))
            .Any(x => x.Name.StartsWith("TD approval saved as immutable revision", StringComparison.Ordinal)), "results approved", TimeSpan.FromSeconds(30));
        Pause(1500);
        app.Click("Export approved XML");
        SaveFileDialog("Export approved FIS Alpine results", Path.Combine(directory, race.Series.Nation + race.Competition.FisCode + ".xml"));
        Pause(1500);
    }

    // PDF Factory: optional organizer letterhead as background (margins leave room for its header and footer bands),
    // then every available report in one click.
    public void GeneratePdfs(string? letterhead = null)
    {
        app.Click("8  PDF Factory  ▾");
        Pause(600);
        // The competition menu opens with the section; dismiss it, or the next click only closes the menu.
        app.Press(VirtualKeyShort.ESCAPE);
        Pause(400);
        if (!string.IsNullOrWhiteSpace(letterhead))
        {
            var settings = app.ByName("PDF settings");
            DesktopApp.Invoke(settings);
            Pause(600);
            app.Click("Choose PDF background");
            OpenFileDialog("Choose A4 PDF background", letterhead);
            SetNumber(FieldAfter("A4 content margins (mm)", "Top"), 40);
            SetNumber(FieldAfter("A4 content margins (mm)", "Bottom"), 24);
            Pause(400);
            app.Click("Save profile");
            Pause(800);
            DesktopApp.Invoke(app.ByName("PDF settings"));
            Pause(400);
        }
        app.Click("Generate All");
        app.WaitUntil(() => app.Window.FindAllDescendants(app.By.ByControlType(ControlType.Text))
            .Any(x => x.Name.Contains("report(s) generated", StringComparison.Ordinal)), "PDFs generated", TimeSpan.FromSeconds(120));
        Pause(2000);
    }

    public void SelectTab(string header)
    {
        var tab = app.Retry(() => app.Window.FindFirstDescendant(app.By.ByName(header).And(app.By.ByControlType(ControlType.TabItem))));
        if (DesktopApp.PhysicalInput) { DesktopApp.Invoke(tab); } else { tab.Patterns.SelectionItem.Pattern.Select(); }
        Pause(400);
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
