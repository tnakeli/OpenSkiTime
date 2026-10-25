using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using System.Text.Json;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using OpenSkiTime.Tests.FullRace;

namespace OpenSkiTime.Desktop.E2E;

// Records the competitor editing tutorial from the real running application: entry tables copied from LibreOffice Calc
// and pasted into the competitor grid (a full entry list, club racers without FIS codes in another column order, a
// club-transfer update of one column and race entries), editing in the grid, undo of a single staged change, multi-row
// entry editing and the entry-list PDFs. The spreadsheet uses the grid's own column headings. A chapter log (title,
// caption, speed), the moments of shown shortcuts (keys.json) and the recorded window area (window.json) are written for
// scripts/demo-video/compose-editing.py.
// Opt-in: OPENSKITIME_DESKTOP_E2E=1 and OPENSKITIME_EDITING_VIDEO=<output directory>. OPENSKITIME_EDITING_VIDEO_WIDTH
// records in a window of that width at the left of the screen instead of maximized windows, for the vertical video,
// which shows the whole window including the Save and Undo buttons. OPENSKITIME_DEMO_LETTERHEAD names
// an optional A4 background PDF (scripts/demo-video/make-letterhead.py). OPENSKITIME_DEMO_CALC overrides the path of
// LibreOffice's soffice.exe. Everything shown lives in a new demonstration folder outside the user profile
// (OPENSKITIME_DEMO_ROOT), including the workbook and a private LibreOffice profile, so no recent-file list or account
// name appears on screen.
public sealed class CompetitorEditingVideoTests
{
    private static readonly JsonSerializerOptions s_json = new() { WriteIndented = true };
    private sealed record Chapter(double Start, double End, string Title, string Caption, double Speed);
    private const string GiantSlalom = "GSW";
    private const int EntryRows = 16;

    [DesktopE2EFact]
    public void RecordCompetitorEditing()
    {
        var output = Environment.GetEnvironmentVariable("OPENSKITIME_EDITING_VIDEO");
        if (string.IsNullOrWhiteSpace(output)) { return; }
        output = Path.GetFullPath(output);
        Directory.CreateDirectory(output);
        var root = DemoVideoTests.DemoRoot();
        var race = new SyntheticRace();
        var book = Workbook.For(race);
        var workbook = Path.Combine(root, "Entries.fods");
        File.WriteAllText(workbook, book.ToFlatOds(), new UTF8Encoding(false));
        var file = Path.Combine(root, "Synthetic Alpine Cup 2026.ost");

        var width = int.TryParse(Environment.GetEnvironmentVariable("OPENSKITIME_EDITING_VIDEO_WIDTH"), NumberStyles.None, CultureInfo.InvariantCulture, out var w) ? w : 0;
        var area = width > 0 ? new System.Drawing.Rectangle(0, 0, width, 1032) : (System.Drawing.Rectangle?)null;
        using var app = DesktopApp.Launch(root, local => File.WriteAllBytes(Path.Combine(local, "fis-points-list.zip"), race.PointsListArchive()));
        void Front() { if (area is { } a) { app.Place(a.Width, a.Height); } else { app.Maximize(); } }
        Front();
        var office = new RaceOffice(app, race) { Pace = 2 };
        // Unrecorded preparation: the series with a slalom and a giant slalom, and the optional PDF background.
        office.CreateSeries(file);
        office.CreateCompetition();
        AddGiantSlalom(app, office, race);
        var letterhead = Environment.GetEnvironmentVariable("OPENSKITIME_DEMO_LETTERHEAD");
        if (!string.IsNullOrWhiteSpace(letterhead))
        {
            var copy = Path.Combine(root, "Organizer letterhead.pdf");
            File.Copy(letterhead, copy);
            office.UsePdfBackground(copy);
        }
        using var calc = Calc.Open(app, workbook, root, area);
        Front();
        office.Navigate("Competitors");
        office.Pause(1000);

        var chapters = new List<Chapter>();
        var keys = new List<object>();
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
        // Shortcuts are invisible on screen; the composer shows each one as a key badge.
        void Key(string label, params VirtualKeyShort[] chord)
        {
            keys.Add(new { at = clock.Elapsed.TotalSeconds, label });
            Keyboard.TypeSimultaneously(chord);
            Thread.Sleep(150);
        }
        var grid = new Grid(app, office);
        void CopySheet(int sheet)
        {
            calc.Show(sheet);
            office.Pause(700);
            Key("Ctrl+Home", VirtualKeyShort.CONTROL, VirtualKeyShort.HOME);
            office.Pause(300);
            Key("Ctrl+Shift+End", VirtualKeyShort.CONTROL, VirtualKeyShort.SHIFT, VirtualKeyShort.END);
            office.Pause(900);
            Key("Ctrl+C", VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_C);
            office.Pause(700);
        }
        void Paste(int rows)
        {
            Front();
            office.Pause(500);
            grid.ClickFirstRow();
            office.Pause(300);
            Key("Ctrl+V", VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_V);
            app.WaitUntil(() => app.TryByName($"Pasted {rows} rows. Review the highlighted cells, then choose Save.") is not null,
                "pasted rows", TimeSpan.FromSeconds(30));
            office.Pause(2200);
        }
        void Save()
        {
            app.Click("Save");
            app.WaitUntil(() => app.TryByName("All changes saved") is not null, "competitors saved", TimeSpan.FromSeconds(30));
            office.Pause(900);
        }

        clock.Start();
        using var recorder = DemoVideoTests.ScreenRecorder.Start(Path.Combine(output, "raw.mp4"));
        try
        {
            Begin("Copy from LibreOffice Calc", "Select the entry list in the spreadsheet and copy it");
            CopySheet(0);
            Begin("Paste into Competitors", "Ctrl+V adds the rows; every new value is highlighted in yellow until Save");
            Paste(book.Sheets[0].Rows.Count);
            Begin("Review, then Save", "Surnames are normalized to capitals; Save writes the whole paste in one transaction");
            grid.Hover(book.Entries[0].Surname.ToUpperInvariant(), 1200);
            Save();

            Begin("Columns in any order", "Club racers without a FIS Code are added by name; only the columns you paste are used");
            CopySheet(1);
            Paste(book.Sheets[1].Rows.Count);
            Save();

            Begin("Update only one column", "Rows are matched by FIS Code; only the cells that change turn yellow");
            CopySheet(2);
            Paste(book.Sheets[2].Rows.Count);
            Begin("Columns you leave out stay as they are", "The unchanged row and all other columns keep their saved values");
            grid.Hover(book.Transfers[0].Club, 1500);
            Save();

            Begin("Race entries from a list", "An X in a race column enters the competitor; the changed boxes are highlighted");
            CopySheet(3);
            Paste(book.Sheets[3].Rows.Count);
            Save();

            Begin("Edit in the grid", "Enter edits a cell, Enter commits it; edited cells are highlighted until Save");
            var first = book.Visible[4];
            grid.Edit(first.Surname.ToUpperInvariant(), first.FirstName, first.FirstName + "-Maria", Key);
            var yearRow = book.Visible[7];
            grid.Edit(yearRow.Surname.ToUpperInvariant(), yearRow.Year, (int.Parse(yearRow.Year, CultureInfo.InvariantCulture) + 1).ToString(CultureInfo.InvariantCulture), Key);
            var clubRow = book.Visible[8];
            grid.Edit(clubRow.Surname.ToUpperInvariant(), clubRow.Club, "Polar Night SC", Key);
            office.Pause(1200);

            Begin("Undo one change", "Undo lists every unsaved change with old → new values; undo any one of them");
            app.Click("Undo");
            office.Pause(1800);
            grid.UndoChange("· Year:");
            office.Pause(1800);
            app.Click("Hide undo");
            office.Pause(600);
            Save();

            Begin("Edit many rows at once", "Filter, select rows (Ctrl+A, Ctrl/Shift-click) and tick one box: every selected row follows");
            var nation = book.MultiEditNation;
            grid.Filter("Nation", nation, Key);
            office.Pause(900);
            grid.ClickFirstRow();
            Key("Ctrl+A", VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_A);
            office.Pause(1000);
            grid.ClickEntry(book.MultiEditTarget.ToUpperInvariant(), GiantSlalom);
            office.Pause(2200);
            Save();
            grid.ClearFilter("Nation");
            office.Pause(1500);

            Begin("Entry lists as PDF", "PDF Factory prints the series and competition entry lists in one click", 1.5);
            app.Click("PDF Factory");
            office.Pause(600);
            app.Press(VirtualKeyShort.ESCAPE);
            office.Pause(800);
            app.Click("Generate All");
            app.WaitUntil(() => app.Window.FindAllDescendants(app.By.ByControlType(ControlType.Text))
                .Any(x => x.Name.Contains("report(s) generated", StringComparison.Ordinal)), "PDFs generated", TimeSpan.FromSeconds(120));
            office.Pause(2500);
            Close();
        }
        finally
        {
            Close();
            recorder.Stop();
            File.WriteAllText(Path.Combine(output, "chapters.json"), JsonSerializer.Serialize(chapters.Select(x => new
            { start = x.Start, end = x.End, title = x.Title, caption = x.Caption, speed = x.Speed }), s_json));
            File.WriteAllText(Path.Combine(output, "keys.json"), JsonSerializer.Serialize(keys, s_json));
            var bounds = app.Window.BoundingRectangle;
            File.WriteAllText(Path.Combine(output, "window.json"), JsonSerializer.Serialize(new
            { x = bounds.Left, y = bounds.Top, w = bounds.Width, h = bounds.Height, maximized = area is null }, s_json));
            File.WriteAllText(Path.Combine(output, "series-file.txt"), file);
        }
    }

    private static void AddGiantSlalom(DesktopApp app, RaceOffice office, SyntheticRace race)
    {
        office.Navigate("Competitions");
        app.Click("Add competition");
        office.Pause(400);
        const string form = "ACTIVE RACE / DETAILS";
        app.SetText(office.FieldAfter(form, "SHORT NAME *"), GiantSlalom);
        app.SetText(office.FieldAfter(form, "EVENT NAME *"), "Synthetic Women's Giant Slalom");
        office.Select(office.FieldAfter(form, "DISCIPLINE"), nameof(OpenSkiTime.Domain.Discipline.GiantSlalom));
        office.Select(office.FieldAfter("LOCATION", "GENDER"), OpenSkiTime.Application.CompetitionGenderLabels.Format(race.Competition.Calendar!.Gender));
        app.Click("Save competition");
        app.WaitUntil(() => app.TryByName(GiantSlalom) is not null && app.TryByName("Edit competition") is not null, "giant slalom saved");
        office.Pause(400);
    }

    // The competitor grid of the real window, driven with the pointer and keyboard as an operator does.
    private sealed class Grid(DesktopApp app, RaceOffice office)
    {
        private AutomationElement View => app.ById("CompetitorGrid");

        private AutomationElement Row(string surname) => app.Retry(() => View.FindAllDescendants(app.By.ByControlType(ControlType.DataItem))
            .FirstOrDefault(r => r.FindAllDescendants(app.By.ByControlType(ControlType.Text)).Any(t => t.Name == surname)));

        private AutomationElement Cell(string surname, string value) => app.Retry(() => Row(surname).FindAllChildren(app.By.ByClassName("DataGridCell"))
            .FirstOrDefault(c => c.FindAllDescendants(app.By.ByControlType(ControlType.Text)).Any(t => t.Name == value)));

        private static System.Drawing.Point Center(AutomationElement element)
        {
            var bounds = element.BoundingRectangle;
            return new(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
        }

        // Clicks the first row's read-only change-marker cell: it selects the row without starting a cell edit.
        public void ClickFirstRow()
        {
            var bounds = View.BoundingRectangle;
            Mouse.MoveTo(new System.Drawing.Point(bounds.Left + 14, bounds.Top + 50));
            Mouse.Click();
            Thread.Sleep(200);
        }

        public void Hover(string value, int milliseconds)
        {
            var target = app.Retry(() => View.FindAllDescendants(app.By.ByControlType(ControlType.Text)).FirstOrDefault(t => t.Name == value));
            Mouse.MoveTo(Center(target));
            office.Pause(milliseconds / office.Pace);
        }

        // Selects the cell, Enter starts editing, the new value replaces the old one and Enter commits. A click on a cell of
        // the current row already starts editing, so Enter is pressed only when no cell editor is open.
        public void Edit(string surname, string oldValue, string newValue, Action<string, VirtualKeyShort[]> key)
        {
            Mouse.MoveTo(Center(Cell(surname, oldValue)));
            office.Pause(250);
            Mouse.Click();
            office.Pause(400);
            bool Editing() => View.FindFirstDescendant(app.By.ByControlType(ControlType.Edit)) is not null;
            if (!Editing()) { key("Enter", [VirtualKeyShort.RETURN]); }
            app.WaitUntil(Editing, "cell editor open");
            office.Pause(300);
            Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_A);
            foreach (var c in newValue) { Keyboard.Type(c); Thread.Sleep(45); }
            office.Pause(300);
            key("Enter", [VirtualKeyShort.RETURN]);
            office.Pause(700);
            app.WaitUntil(() => Row(surname).FindAllDescendants(app.By.ByControlType(ControlType.Text)).Any(t => t.Name == newValue), "edited " + newValue);
        }

        public void UndoChange(string fragment)
        {
            var panel = app.ByName("UNSAVED CHANGES").Parent.Parent;
            var label = app.Retry(() => panel.FindAllDescendants(app.By.ByControlType(ControlType.Text)).FirstOrDefault(t => t.Name.Contains(fragment, StringComparison.Ordinal)));
            var undo = app.Retry(() => label.Parent.FindFirstDescendant(app.By.ByName("Undo").And(app.By.ByControlType(ControlType.Button))));
            Mouse.MoveTo(Center(label));
            office.Pause(700);
            DesktopApp.Invoke(undo);
        }

        public void Filter(string column, string value, Action<string, VirtualKeyShort[]> key)
        {
            DesktopApp.Invoke(app.ById("CompetitorFilterButton_" + column));
            office.Pause(500);
            foreach (var c in value) { Keyboard.Type(c); Thread.Sleep(60); }
            office.Pause(300);
            key("Enter", [VirtualKeyShort.RETURN]);
        }

        public void ClearFilter(string column) => DesktopApp.Invoke(app.ById("CompetitorFilterButton_" + column));

        // Clicks the competitor's entry check box in the column headed by the competition's short label.
        public void ClickEntry(string surname, string competition)
        {
            var header = app.Retry(() => View.FindAllDescendants(app.By.ByControlType(ControlType.Text)).FirstOrDefault(t => t.Name == competition));
            var x = Center(header).X;
            var box = app.Retry(() => Row(surname).FindAllDescendants(app.By.ByControlType(ControlType.CheckBox))
                .OrderBy(b => Math.Abs(Center(b).X - x)).FirstOrDefault());
            Mouse.MoveTo(Center(box));
            office.Pause(500);
            Mouse.Click();
        }
    }

    // The spreadsheet shown in LibreOffice Calc: one sheet per paste use case, all from the synthetic race.
    private sealed record Sheet(string Name, string[] Header, List<string[]> Rows);
    private sealed record Entry(string Code, string Surname, string FirstName, string Year, string Nation, string Club);

    private sealed class Workbook
    {
        public List<Sheet> Sheets { get; } = [];
        public List<Entry> Entries { get; } = [];
        public List<Entry> Transfers { get; } = [];
        public List<Entry> Visible { get; } = [];
        public string MultiEditNation { get; private set; } = "";
        public string MultiEditTarget { get; private set; } = "";

        public static Workbook For(SyntheticRace race)
        {
            var book = new Workbook();
            static string Proper(string surname) => surname[..1] + surname[1..].ToLowerInvariant();
            static string Year(int year) => year.ToString(CultureInfo.InvariantCulture);
            foreach (var a in race.Athletes.Take(EntryRows))
            { book.Entries.Add(new(a.Code, Proper(a.Surname), a.FirstName, Year(a.BirthYear), a.Nation, a.Club)); }
            book.Sheets.Add(new("Entry list", ["CODE", "SURNAME", "FIRST NAME", "YEAR", "GENDER", "NAT", "CLUB", SyntheticRace.ShortLabel, GiantSlalom],
                [.. book.Entries.Select(e => new[] { e.Code, e.Surname, e.FirstName, e.Year, "Women", e.Nation, e.Club, "X", "" })]));
            // Club racers without FIS codes, from a club's own list in another column order and without a CODE column.
            var clubRacers = race.Athletes.Skip(race.Athletes.Count - 5).ToArray();
            book.Sheets.Add(new("Club racers", ["SURNAME", "FIRST NAME", "CLUB", "NAT", "YEAR", "GENDER", SyntheticRace.ShortLabel, GiantSlalom],
                [.. clubRacers.Select(a => new[] { Proper(a.Surname), a.FirstName, a.Club, "FIN", Year(a.BirthYear + 4), "Women", "X", "X" })]));
            // The grid lists competitors by surname and shows about 17 rows; later steps use rows that are on screen.
            var onScreen = book.Entries.Select(e => e.Surname.ToUpperInvariant()).Concat(clubRacers.Select(a => a.Surname))
                .Order(StringComparer.Ordinal).Take(15).ToHashSet();
            book.Visible.AddRange(book.Entries.Where(e => onScreen.Contains(e.Surname.ToUpperInvariant()))
                .OrderBy(e => e.Surname, StringComparer.Ordinal));
            // Clubs that are not among the synthetic race's clubs, so every transfer is a change.
            string[] clubs = ["Northern Lights SC", "Tunturi Racing", "Kaamos Ski Team"];
            var moved = new[] { 1, 5, 9 };
            for (var i = 0; i < moved.Length; i++) { book.Transfers.Add(book.Visible[moved[i]] with { Club = clubs[i] }); }
            var same = book.Visible[3];
            book.Sheets.Add(new("Club transfers", ["CODE", "CLUB"],
                [.. book.Transfers.Take(2).Select(t => new[] { t.Code, t.Club }), [same.Code, same.Club], [book.Transfers[2].Code, book.Transfers[2].Club]]));
            var gs = book.Visible.Where((_, i) => i % 2 == 0).Take(6).ToArray();
            book.Sheets.Add(new("GS entries", ["CODE", "SURNAME", GiantSlalom], [.. gs.Select(e => new[] { e.Code, e.Surname, "X" })]));
            // Multi-edit: the nation with most entered competitors; its first one without a GS entry gets the click.
            var groups = book.Entries.GroupBy(e => e.Nation).OrderByDescending(g => g.Count(e => !gs.Contains(e))).ThenBy(g => g.Key, StringComparer.Ordinal).First();
            book.MultiEditNation = groups.Key;
            book.MultiEditTarget = groups.First(e => !gs.Contains(e)).Surname;
            return book;
        }

        // A flat OpenDocument spreadsheet (one XML file) that LibreOffice opens directly.
        public string ToFlatOds()
        {
            static string X(string value) => SecurityElement.Escape(value);
            static string Cell(string value, string? style = null)
            {
                var styled = style is null ? "" : $" table:style-name=\"{style}\"";
                if (value.Length == 0) { return $"<table:table-cell{styled}/>"; }
                return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out _) && value.Length == 4
                    ? $"<table:table-cell{styled} office:value-type=\"float\" office:value=\"{value}\"><text:p>{value}</text:p></table:table-cell>"
                    : $"<table:table-cell{styled} office:value-type=\"string\"><text:p>{X(value)}</text:p></table:table-cell>";
            }
            var xml = new StringBuilder("""
                <?xml version="1.0" encoding="UTF-8"?>
                <office:document xmlns:office="urn:oasis:names:tc:opendocument:xmlns:office:1.0" xmlns:style="urn:oasis:names:tc:opendocument:xmlns:style:1.0"
                  xmlns:text="urn:oasis:names:tc:opendocument:xmlns:text:1.0" xmlns:table="urn:oasis:names:tc:opendocument:xmlns:table:1.0"
                  xmlns:fo="urn:oasis:names:tc:opendocument:xmlns:xsl-fo-compatible:1.0"
                  office:version="1.3" office:mimetype="application/vnd.oasis.opendocument.spreadsheet">
                <office:styles>
                  <style:default-style style:family="table-cell"><style:text-properties fo:font-size="15pt"/></style:default-style>
                </office:styles>
                <office:automatic-styles>
                  <style:style style:name="narrow" style:family="table-column"><style:table-column-properties style:column-width="0.85in"/></style:style>
                  <style:style style:name="name" style:family="table-column"><style:table-column-properties style:column-width="1.3in"/></style:style>
                  <style:style style:name="club" style:family="table-column"><style:table-column-properties style:column-width="1.75in"/></style:style>
                  <style:style style:name="gender" style:family="table-column"><style:table-column-properties style:column-width="1.0in"/></style:style>
                  <style:style style:name="head" style:family="table-cell"><style:table-cell-properties fo:background-color="#dbe5f4"/><style:text-properties fo:font-weight="bold"/></style:style>
                </office:automatic-styles>
                <office:body><office:spreadsheet>
                """);
            foreach (var sheet in Sheets)
            {
                xml.Append(CultureInfo.InvariantCulture, $"<table:table table:name=\"{X(sheet.Name)}\">");
                foreach (var header in sheet.Header)
                {
                    var style = header switch { "CLUB" => "club", "SURNAME" or "FIRST NAME" => "name", "GENDER" => "gender", _ => "narrow" };
                    xml.Append(CultureInfo.InvariantCulture, $"<table:table-column table:style-name=\"{style}\"/>");
                }
                xml.Append("<table:table-row>").AppendJoin("", sheet.Header.Select(h => Cell(h, "head"))).Append("</table:table-row>");
                foreach (var row in sheet.Rows) { xml.Append("<table:table-row>").AppendJoin("", row.Select(v => Cell(v))).Append("</table:table-row>"); }
                xml.Append("</table:table>");
            }
            return xml.Append("</office:spreadsheet></office:body></office:document>").ToString();
        }
    }

    // LibreOffice Calc with a private profile in the demonstration folder (no tips, no recent documents, no first-run
    // notices), maximized like the race office window.
    private sealed class Calc : IDisposable
    {
        private readonly Process _process;
        private readonly Window _window;
        private int _sheet;

        private readonly System.Drawing.Rectangle? _area;

        private Calc(Process process, Window window, System.Drawing.Rectangle? area) { _process = process; _window = window; _area = area; }

        // area: the screen rectangle the visible window covers (as the race office window); null maximizes it.
        public static Calc Open(DesktopApp app, string workbook, string root, System.Drawing.Rectangle? area)
        {
            var soffice = Environment.GetEnvironmentVariable("OPENSKITIME_DEMO_CALC");
            if (string.IsNullOrWhiteSpace(soffice)) { soffice = @"C:\Program Files\LibreOffice\program\soffice.exe"; }
            if (!File.Exists(soffice)) { throw new FileNotFoundException("LibreOffice was not found; set OPENSKITIME_DEMO_CALC.", soffice); }
            var profile = Path.Combine(root, "libreoffice-profile");
            Directory.CreateDirectory(Path.Combine(profile, "user"));
            File.WriteAllText(Path.Combine(profile, "user", "registrymodifications.xcu"), """
                <?xml version="1.0" encoding="UTF-8"?>
                <oor:items xmlns:oor="http://openoffice.org/2001/registry" xmlns:xs="http://www.w3.org/2001/XMLSchema" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
                <item oor:path="/org.openoffice.Office.Common/Misc"><prop oor:name="ShowTipOfTheDay" oor:op="fuse"><value>false</value></prop></item>
                <item oor:path="/org.openoffice.Office.Common/Misc"><prop oor:name="FirstRun" oor:op="fuse"><value>false</value></prop></item>
                <item oor:path="/org.openoffice.Office.Common/History"><prop oor:name="PickListSize" oor:op="fuse"><value>0</value></prop></item>
                <item oor:path="/org.openoffice.Setup/Office"><prop oor:name="FirstStartWizardCompleted" oor:op="fuse"><value>true</value></prop></item>
                <item oor:path="/org.openoffice.Setup/Product"><prop oor:name="ooSetupLastVersion" oor:op="fuse"><value>99.0</value></prop></item>
                <item oor:path="/org.openoffice.Setup/Product"><prop oor:name="LastTimeGetInvolvedShown" oor:op="fuse"><value>4102444800</value></prop></item>
                <item oor:path="/org.openoffice.Setup/Product"><prop oor:name="LastTimeDonateShown" oor:op="fuse"><value>4102444800</value></prop></item>
                <item oor:path="/org.openoffice.Setup/L10N"><prop oor:name="ooLocale" oor:op="fuse"><value>en-US</value></prop></item>
                <item oor:path="/org.openoffice.Office.Linguistic/General"><prop oor:name="UILocale" oor:op="fuse"><value>en-US</value></prop></item>
                </oor:items>
                """);
            var start = new ProcessStartInfo(soffice) { UseShellExecute = false, WorkingDirectory = root };
            foreach (var argument in new[] { "-env:UserInstallation=" + new Uri(profile).AbsoluteUri, "--nologo", "--norestore", "--calc", workbook })
            { start.ArgumentList.Add(argument); }
            var process = Process.Start(start) ?? throw new InvalidOperationException("LibreOffice did not start.");
            var name = Path.GetFileName(workbook);
            var window = app.Retry(() => app.Automation.GetDesktop().FindAllChildren()
                .FirstOrDefault(x => (x.Name ?? "").Contains(name, StringComparison.OrdinalIgnoreCase) && x.ControlType == ControlType.Window), TimeSpan.FromSeconds(90)).AsWindow();
            var calc = new Calc(process, window, area);
            Thread.Sleep(1500);
            calc.Front();
            return calc;
        }

        private void Front()
        {
            var handle = _window.Properties.NativeWindowHandle.Value;
            if (_area is { } area)
            {
                // The window rectangle includes invisible resize borders; place it so the visible frame covers the area.
                ShowWindow(handle, 9); // SW_RESTORE
                SetWindowPos(handle, IntPtr.Zero, area.X, area.Y, area.Width, area.Height, 0x0040);
                GetWindowRect(handle, out var outer);
                if (DwmGetWindowAttribute(handle, 9, out var visible, Marshal.SizeOf<Rect>()) == 0) // DWMWA_EXTENDED_FRAME_BOUNDS
                {
                    SetWindowPos(handle, IntPtr.Zero, area.X - (visible.Left - outer.Left), area.Y - (visible.Top - outer.Top),
                        area.Width + (visible.Left - outer.Left) + (outer.Right - visible.Right),
                        area.Height + (visible.Top - outer.Top) + (outer.Bottom - visible.Bottom), 0x0040);
                }
            }
            else { ShowWindow(handle, 3); } // SW_MAXIMIZE
            _window.SetForeground();
            Thread.Sleep(500);
        }

        // Brings Calc forward on the given sheet (0-based), moving with Ctrl+Page Down as a user does.
        public void Show(int sheet)
        {
            Front();
            while (_sheet < sheet)
            {
                Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.NEXT);
                _sheet++;
                Thread.Sleep(500);
            }
        }

        public void Dispose()
        {
            // The workbook is only read, so closing it asks nothing.
            try { _window.Close(); } catch (Exception ex) when (ex is COMException or InvalidOperationException) { }
            _process.Dispose();
        }

        [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int command);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out Rect rect);
        [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hWnd, int attribute, out Rect value, int size);
    }
}
