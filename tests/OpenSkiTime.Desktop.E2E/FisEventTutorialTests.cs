using System.Globalization;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using OpenSkiTime.Tests.FullRace;

namespace OpenSkiTime.Desktop.E2E;

// Tutorial "Setting up a FIS event" (scripts/tutorial-video/tutorials/fis-event.json, docs/tutorial-videos.md): the real
// FIS races of Levi from the FIS calendar, their entries from regs.fissi.fi and the clubs from the FIS points list, with
// the operator's own API keys from Windows Credential Manager. The event is real, so the recording holds names of
// athletes and technical delegates: they are covered by the composer, and the raw recording stays on this computer.
// Opt-in: OPENSKITIME_DESKTOP_E2E=1 and OPENSKITIME_TUTORIAL=<work>\fis-event\<language> (with timing.json of
// narrate.py). It takes over mouse and keyboard for about six minutes.
public sealed class FisEventTutorialTests
{
    private const string Location = "Levi";
    private const string EventName = "FIS race Levi";
    private const string EventStart = "28.11.2026";
    private const int Races = 8;
    private const string Neighbour = "Club training 2026.ost";
    private static readonly System.Drawing.Rectangle s_seriesPage = new(0, 36, 1264, 676);
    private static readonly System.Drawing.Rectangle s_lowerPage = new(0, 425, 1080, 607);
    private static readonly System.Drawing.Rectangle s_upperRight = new(840, 72, 1080, 607);

    [DesktopE2EFact]
    public void RecordFisEvent()
    {
        var language = Environment.GetEnvironmentVariable("OPENSKITIME_TUTORIAL");
        if (string.IsNullOrWhiteSpace(language)) { return; }
        language = Path.GetFullPath(language);
        // Only this tutorial's folder: the other tutorials' recorders share the variable.
        if (Path.GetFileName(Path.GetDirectoryName(language)) != "fis-event") { return; }
        var output = Path.Combine(language, "recording-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
        var root = DemoVideoTests.DemoRoot();
        var events = Directory.CreateDirectory(Path.Combine(root, "Events")).FullName;
        var file = Path.Combine(events, "Levi FIS 2026.ost");
        var race = new SyntheticRace();
        // Unrecorded: the file dialog remembers the demonstration folder. The FIS points list is not in the local cache.
        using (var warmUp = DesktopApp.Launch(root))
        {
            warmUp.Maximize();
            var office = new RaceOffice(warmUp, race);
            office.CreateSeries(Path.Combine(events, Neighbour));
        }

        s_status = null;
        using var app = DesktopApp.Launch(root);
        app.Maximize();
        using var video = new TutorialRecorder(app, Path.Combine(language, "timing.json"), output);
        var facts = new Dictionary<string, string> { ["seriesFile"] = file };
        var names = new Names(app);
        TutorialRecorder.Until(TutorialRecorder.Now + 0.5);
        video.Start();
        try { Record(app, video, names, file, facts); }
        catch
        {
            app.Screenshot(output, "failure.png");
            File.WriteAllText(Path.Combine(output, "failure-tree.txt"), app.Tree());
            throw;
        }
        finally { video.Finish(facts); }
    }

    private static void Record(DesktopApp app, TutorialRecorder video, Names names, string file, Dictionary<string, string> facts)
    {
        var grid = new Lazy<AutomationElement>(() => app.ById("CompetitorGrid"));
        video.Step("intro", () => video.Move(new(700, 470)));
        video.Step("concept", () =>
        {
            var name = app.Field("SERIES NAME *");
            video.Camera(s_seriesPage, 0);
            video.Hover(name, "event");
            video.Hover(app.Button("Competitions"), "races");
        });
        video.Step("key", () =>
        {
            video.Overview();
            video.Click(app.ById("SettingsMenuButton"), "settings");
            video.Click(MenuItem(app, "API keys"), "tab");
            var key = app.ById("FisApiKeyInputBox");
            var save = app.Button("Save key");
            video.Camera(key, save);
            video.Hover(key, "card");
            video.Hover(save, "save");
            var second = app.ById("FissiApiKeyInputBox");
            video.At("second", -0.6);
            video.Camera(new System.Drawing.Rectangle(0, second.BoundingRectangle.Top - 330, 1080, 560), 0);
            video.Hover(second, "second");
            TutorialRecorder.Until(TutorialRecorder.Now + 3);
            video.Overview();
        });
        video.Step("calendar", () =>
        {
            video.Click(app.Button("Event series"), "series");
            video.Click(app.Button("Browse FIS calendar"), "browse");
            var events = app.ById("SeriesCalendarEventsGrid");
            video.Camera(s_seriesPage, 0);
            video.Wait(() => Rows(app, events).Length > 1, "FIS calendar loaded");
            video.Click(app.ById("CalendarFilterButton_Location"), "filter");
            Thread.Sleep(250);
            video.At("type");
            video.Type(Location);
            app.Press(VirtualKeyShort.RETURN);
            Thread.Sleep(300);
            var row = app.Retry(() => Rows(app, events).FirstOrDefault(x => HasText(app, x, EventName) && HasText(app, x, EventStart)));
            var races = app.ById("SeriesCalendarCompetitionsGrid");
            // The row's own text is clicked, not its empty middle.
            var text = row.FindAllDescendants(app.By.ByControlType(ControlType.Text)).First(x => x.Name == EventName);
            video.Reveal(() => video.Click(text, "event"), () => Rows(app, races).Length > 2, "races listed", () => names.Calendar(races));
            video.Hover(races, "races");
            // The four days do not fit: the wheel brings the last races into view.
            Wheel(-4);
            var use = app.ById("UseSeriesCalendarEventButton");
            video.Reveal(() => video.Click(use, "use"), () => app.TryByName("Use selected event") is null, "event used", () => []);
        });
        video.Step("create", () =>
        {
            video.Hover(app.Field("SERIES NAME *"), "fields");
            video.Reveal(() => video.Click(app.Button("Create series file"), "create"), () => SaveDialog(app) is not null, "save dialog",
                () => names.SaveDialog(SaveDialog(app)!));
            // The new event opens Competitions by itself, with the names of the technical delegates.
            SaveSeries(app, video, file, names);
            if (!app.Window.Title.StartsWith(file, StringComparison.OrdinalIgnoreCase)) { throw new InvalidOperationException("The event was created at an unexpected path."); }
        });
        video.Step("competitions", () =>
        {
            video.Hover(app.Button("Competitions"), "competitions");
            var list = app.ById("CompetitionsGrid");
            video.Camera(new System.Drawing.Rectangle(0, 72, 1400, 300), 0);
            video.Hover(list, "list");
            Thread.Sleep(700);
            Wheel(-6);
            TutorialRecorder.Until(TutorialRecorder.Now + 1.2);
            video.Overview();
        });
        video.Step("course", () =>
        {
            var list = app.ById("CompetitionsGrid");
            video.Move(new(600, list.BoundingRectangle.Top + 60));
            Wheel(6);
            Thread.Sleep(250);
            var row = app.Retry(() => list.FindAllDescendants(app.By.ByControlType(ControlType.Text)).Where(x => x.Name.StartsWith("SL1 ", StringComparison.Ordinal))
                .OrderBy(x => x.BoundingRectangle.Top).FirstOrDefault());
            video.Reveal(() => video.Click(row, "row"), () => app.TryByName("Course and homologation") is not null, "competition opened", names.Competitions);
            var save = app.Button("Save competition");
            if (save.BoundingRectangle.Bottom > 1000 || save.IsOffscreen)
            {
                video.Move(new(1500, 420));
                video.Reveal(() => Wheel(-14), null, "scrolled", names.Competitions, 400);
            }
            var browse = app.ById("BrowseCompetitionHomologationsButton");
            video.Camera(CourseView(app), 0);
            video.Click(browse, "browse");
            video.Reveal(() => { }, () => app.Window.FindFirstDescendant(app.By.ByAutomationId("CompetitionHomologationPicker")) is { IsOffscreen: false }, "homologations read", names.Competitions, 400);
            video.Camera(CourseView(app), 0);
            var picker = app.ById("CompetitionHomologationPicker");
            facts["course"] = picker.AsComboBox().SelectedItem?.Name ?? "";
            video.Hover(picker, "picker");
            video.Click(app.Button("Apply homologation"), "apply");
            video.Hover(app.ById("SaveCourseToAllRacesCheckBox").Parent.FindAllDescendants(app.By.ByName("START ALTITUDE")).FirstOrDefault() ?? app.ByName("START ALTITUDE"), "fields");
            var check = app.ById("SaveCourseToAllRacesCheckBox").BoundingRectangle;
            video.ClickAt(new(check.Left + 14, check.Top + check.Height / 2), "check");
            save = app.Button("Save competition");
            video.Reveal(() => video.Click(save, "save"), () => app.TryByName("Apply homologation") is null || app.TryByName("Wolverine") is not null, "competition saved", names.Competitions, 700);
            video.Overview();
            video.Move(new(700, 300));
            video.Reveal(() => Wheel(30), null, "scrolled to the list", names.Competitions, 400);
            list = app.ById("CompetitionsGrid");
            video.Camera(new System.Drawing.Rectangle(300, 72, 1400, 400), 0);
            var course = app.Retry(() => list.FindAllDescendants(app.By.ByControlType(ControlType.Text)).FirstOrDefault(x => x.Name == "Wolverine"));
            video.Hover(course, "done");
            TutorialRecorder.Until(TutorialRecorder.Now + 2.5);
            video.Overview();
        });
        video.Step("list", () =>
        {
            // The grid is still empty: nothing to cover before the entries are read.
            video.Reveal(() => video.Click(app.Button("Competitors"), "competitors"), () => app.Window.FindFirstDescendant(app.By.ByAutomationId("CompetitorGrid")) is not null,
                "competitors shown", () => []);
            video.Click(Expander(app, "Update from FIS"), "fis");
            Thread.Sleep(350);
            var date = app.ById("FisEffectiveDateInput");
            var download = app.Button("Download FIS point list");
            video.Camera(s_lowerPage, 0);
            video.Hover(date, "date");
            facts["listDate"] = date.AsTextBox().Text;
            video.Click(download, "download");
            video.Wait(() => StatusStarts(app, "Downloaded "), "points list downloaded");
            facts["list"] = Status(app, "Downloaded ");
        });
        video.Step("listinfo", () =>
        {
            var info = app.Retry(() => app.Window.FindAllDescendants(app.By.ByControlType(ControlType.Text)).FirstOrDefault(x => x.Name.StartsWith("Effective ", StringComparison.Ordinal)));
            facts["listValidity"] = info.Name;
            video.Move(new(info.BoundingRectangle.Left + 110, TutorialRecorder.Center(info).Y), "info");
            TutorialRecorder.Until(TutorialRecorder.Now + 4.5);
            video.Overview();
            // The section is closed again, so the next one stays in view.
            video.Click(Expander(app, "Update from FIS"));
        });
        video.Step("fissi", () =>
        {
            var expander = Expander(app, "Update from regs.fissi.fi");
            video.Click(expander, "expand");
            Thread.Sleep(350);
            var button = app.ById("UpdateFromFissiApiButton");
            video.Camera(new System.Drawing.Rectangle(840, button.BoundingRectangle.Top - 300, 1080, 607), 0);
            video.Reveal(() => video.Click(button, "fetch"), () => StatusStarts(app, "regs.fissi.fi:"), "registrations read", () => names.Competitors(imported: true), 900);
            facts["fissi"] = Status(app, "regs.fissi.fi:");
        });
        video.Step("fetched", () =>
        {
            var races = app.ById("FissiApiRacesGrid");
            video.Overview();
            video.Move(new(700, races.BoundingRectangle.Top + 40), "races");
            if (races.BoundingRectangle.Bottom > 1000) { video.Reveal(() => Wheel(-12), null, "scrolled", () => names.Competitors(), 250); }
            video.Camera(s_lowerPage, 0);
            TutorialRecorder.Until(TutorialRecorder.Now + 3.2);
            video.Overview();
            video.Reveal(() => Wheel(12), null, "scrolled back", () => names.Competitors(), 250);
        });
        video.Step("paste", () =>
        {
            var bounds = grid.Value.BoundingRectangle;
            video.Camera(new System.Drawing.Rectangle(bounds.Left + 380, bounds.Top - 40, 1080, 560), 0);
            video.ClickAt(new(bounds.Left + 800, bounds.Top + 140), "menu", right: true);
            Thread.Sleep(350);
            if (Popup(app, "Paste from Excel") is { } item) { video.Hover(item); }
            TutorialRecorder.Until(TutorialRecorder.Now + 2.2);
            app.Press(VirtualKeyShort.ESCAPE);
        });
        video.Step("changes", () =>
        {
            var bounds = grid.Value.BoundingRectangle;
            video.Camera(new System.Drawing.Rectangle(bounds.Left, bounds.Top - 100, 1080, 600), 0);
            video.Move(new(bounds.Left + 500, bounds.Top + 130), "yellow");
            TutorialRecorder.Until(TutorialRecorder.Now + 1.2);
            video.Move(new(bounds.Left + 600, bounds.Top + 330));
            var undo = app.Button("Undo");
            video.At("undo", -1.6);
            video.Overview();
            video.Reveal(() => video.Click(undo, "undo"), () => app.TryByName("UNSAVED CHANGES") is not null, "undo list shown", () => names.Competitors(undo: true), 250);
            video.Hover(UndoButtons(app)[0], "one");
            video.At("one", 1.2);
            var hide = app.Button("Hide undo");
            video.Reveal(() => video.Click(hide), () => hide.Name == "Undo", "undo list closed", () => names.Competitors(), 250);
            video.Reveal(() => video.Click(app.Button("Save"), "save"), () => app.TryByName("All changes saved") is not null, "competitors saved", () => names.Competitors());
            facts["saved"] = Status(app, "Saved ");
            video.Overview();
        });
        video.Step("update", () =>
        {
            var bounds = grid.Value.BoundingRectangle;
            video.Reveal(() => video.Click(Expander(app, "Update from FIS"), "open"), () => app.Window.FindFirstDescendant(app.By.ByAutomationId("FisEffectiveDateInput")) is not null,
                "section open", () => names.Competitors(), 250);
            var update = app.Button("Update from FIS list");
            video.Camera(new System.Drawing.Rectangle(840, update.BoundingRectangle.Top - 300, 1080, 607), 0);
            video.Reveal(() => video.Click(update, "update"), () => StatusStarts(app, "FIS list "), "clubs staged", () => names.Competitors(), 250);
            facts["update"] = Status(app, "FIS list ");
            video.Camera(new System.Drawing.Rectangle(bounds.Left + 260, bounds.Top - 100, 1080, 600), 0);
            video.Move(new(bounds.Left + 730, bounds.Top + 180), "clubs");
            TutorialRecorder.Until(TutorialRecorder.Now + 0.8);
            video.Move(new(bounds.Left + 740, bounds.Top + 380));
            var undo = app.Button("Undo");
            video.At("undo", -1.7);
            video.Overview();
            video.Reveal(() => video.Click(undo, "undo"), () => app.TryByName("UNSAVED CHANGES") is not null, "undo list shown", () => names.Competitors(undo: true), 250);
            var buttons = UndoButtons(app);
            var clubY = TutorialRecorder.Center(app.Retry(() => app.Window.FindAllDescendants(app.By.ByControlType(ControlType.Text))
                .FirstOrDefault(x => x.Name.Contains(" · Club: ", StringComparison.Ordinal)))).Y;
            video.Reveal(() => video.Click(buttons.OrderBy(x => Math.Abs(TutorialRecorder.Center(x).Y - clubY)).First(), "one"),
                () => StatusStarts(app, "Change undone."), "one change undone", () => names.Competitors(undo: true), 250);
            var hide = app.Button("Hide undo");
            video.Reveal(() => video.Click(hide), () => hide.Name == "Undo", "undo list closed", () => names.Competitors(), 250);
            video.Reveal(() => video.Click(app.Button("Save"), "save"), () => app.TryByName("All changes saved") is not null, "clubs saved", () => names.Competitors());
            facts["clubs"] = Status(app, "Saved ");
            video.Overview();
        });
        video.Step("end", () =>
        {
            video.Camera(new System.Drawing.Rectangle(260, 72, 1300, 640), 0);
            video.Move(new(app.Button("Start lists").BoundingRectangle.Left + 40, 58));
        }, gap: 1.2);
    }

    // The course card with the action bar below it.
    private static System.Drawing.Rectangle CourseView(DesktopApp app)
    {
        var title = app.ByName("Course and homologation").BoundingRectangle;
        var save = app.Button("Save competition").BoundingRectangle;
        return System.Drawing.Rectangle.FromLTRB(title.Left - 40, title.Top - 30, 1915, Math.Max(save.Bottom + 10, title.Top + 300));
    }

    private static Window? SaveDialog(DesktopApp app) => app.Window.ModalWindows.FirstOrDefault(x => x.Title == "Create event series file");

    // The dialog opens in the demonstration folder because the preparation saved there; it must list that file, so the
    // event can only be written beside it. Only the file name is typed, as an operator does.
    private static void SaveSeries(DesktopApp app, TutorialRecorder video, string file, Names names)
    {
        var dialog = SaveDialog(app)!;
        if (dialog.FindFirstDescendant(app.By.ByName(Neighbour).Or(app.By.ByName(Path.GetFileNameWithoutExtension(Neighbour)))) is null || File.Exists(file))
        {
            dialog.FindFirstDescendant(app.By.ByAutomationId("2").And(app.By.ByControlType(ControlType.Button)))?.AsButton().Invoke();
            throw new InvalidOperationException("The save dialog did not open in the demonstration folder; cancelled.");
        }
        var name = app.Retry(() => dialog.FindFirstDescendant(app.By.ByAutomationId("1001").And(app.By.ByControlType(ControlType.Edit))));
        video.Click(name);
        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_A);
        video.Type(Path.GetFileNameWithoutExtension(file));
        var save = app.Retry(() => dialog.FindFirstDescendant(app.By.ByAutomationId("1").And(app.By.ByControlType(ControlType.Button))));
        video.Overview();
        video.Reveal(() => video.Click(save), () => app.Window.ModalWindows.Length == 0 && app.TryByName("TD SURNAME") is not null, "event created", names.Competitions, 700);
    }

    // Wheel notches at the pointer; negative scrolls down.
    private static void Wheel(int notches)
    {
        for (var i = 0; i < Math.Abs(notches); i++) { Mouse.Scroll(Math.Sign(notches)); Thread.Sleep(45); }
    }

    private static AutomationElement[] Rows(DesktopApp app, AutomationElement grid) => grid.FindAllDescendants(app.By.ByControlType(ControlType.DataItem));
    private static bool HasText(DesktopApp app, AutomationElement row, string text)
        => row.FindAllDescendants(app.By.ByControlType(ControlType.Text)).Any(x => x.Name == text);
    private static AutomationElement[] UndoButtons(DesktopApp app)
    {
        var panelTop = app.ByName("UNSAVED CHANGES").BoundingRectangle.Bottom;
        return app.Window.FindAllDescendants(app.By.ByName("Undo").And(app.By.ByControlType(ControlType.Button)))
            .Where(x => x.BoundingRectangle.Top >= panelTop && !x.IsOffscreen).ToArray();
    }

    // The clickable header of an Expander; its content may hold a button of the same name.
    private static AutomationElement Expander(DesktopApp app, string header)
        => app.Retry(() => app.Window.FindAllDescendants(app.By.ByName(header)).OrderBy(x => x.BoundingRectangle.Left).FirstOrDefault(x => !x.BoundingRectangle.IsEmpty));

    private static AutomationElement MenuItem(DesktopApp app, string name) => app.Retry(() => Popup(app, name));
    private static AutomationElement? Popup(DesktopApp app, string name)
        => app.Automation.GetDesktop().FindAllDescendants(app.By.ByControlType(ControlType.MenuItem)).FirstOrDefault(x => x.Name == name);

    // The status bar's text is found once: reading every text takes seconds when the grid is full.
    private static AutomationElement? s_status;
    private static string Status(DesktopApp app, string start)
    {
        if (s_status is { } known)
        {
            try { if (known.Name.StartsWith(start, StringComparison.Ordinal)) { return known.Name; } }
            catch (System.Runtime.InteropServices.COMException) { s_status = null; }
        }
        if (s_status is not null) { return ""; }
        s_status = app.Window.FindAllDescendants(app.By.ByControlType(ControlType.Text)).FirstOrDefault(x => x.Name.StartsWith(start, StringComparison.Ordinal));
        return s_status?.Name ?? "";
    }
    private static bool StatusStarts(DesktopApp app, string start) => Status(app, start).Length > 0;

    // Where people's names are on the views of this tutorial, as cells. Everything is read in one cached query, because
    // the picture stands still while the areas are measured.
    private sealed class Names(DesktopApp app)
    {
        // The page content between the section navigation and the status bar; a scrolled grid is cut off there.
        private const int ContentTop = 74;
        private const int ContentBottom = 1006;
        // Marker column, then CODE, SURNAME and FIRST NAME with their declared widths (MainWindow.axaml) and a margin.
        private const int MarkerWidth = 40;
        private static readonly int[] s_identity = [100, 145, 133];
        private sealed record Item(string Name, ControlType Type, string Id, System.Drawing.Rectangle Bounds, bool Hidden);

        private AutomationElement? _grid;
        private List<Item> Read(AutomationElement scope)
        {
            var request = new FlaUI.Core.CacheRequest { TreeScope = TreeScope.Element };
            var p = app.Automation.PropertyLibrary.Element;
            foreach (var id in new[] { p.Name, p.ControlType, p.AutomationId, p.BoundingRectangle, p.IsOffscreen }) { request.Add(id); }
            using (request.Activate())
            {
                return scope.FindAllDescendants().Select(x => new Item(x.Properties.Name.ValueOrDefault ?? "", x.Properties.ControlType.ValueOrDefault,
                    x.Properties.AutomationId.ValueOrDefault ?? "", x.Properties.BoundingRectangle.ValueOrDefault, x.Properties.IsOffscreen.ValueOrDefault)).ToList();
            }
        }

        private static IEnumerable<System.Drawing.Rectangle> Cells(int left, int[] widths, int top, int bottom, int rowHeight)
        {
            for (var y = top; y < bottom; y += rowHeight)
            {
                var x = left;
                foreach (var width in widths)
                {
                    var cell = System.Drawing.Rectangle.FromLTRB(x, Math.Max(y, ContentTop), x + width, Math.Min(Math.Min(y + rowHeight, bottom), ContentBottom));
                    if (cell.Height > 0) { yield return cell; }
                    x += width;
                }
            }
        }

        // The technical delegate columns, from TD SURNAME to the column after TD NUMBER.
        private static IEnumerable<System.Drawing.Rectangle> Delegates(AutomationElement grid, DesktopApp app, int rowHeight)
        {
            System.Drawing.Rectangle Header(string name) => app.Retry(() => grid.FindAllDescendants(app.By.ByName(name)).FirstOrDefault()).BoundingRectangle;
            var area = grid.BoundingRectangle;
            var left = Header("TD SURNAME").Left - 8;
            var right = Header("TD NUMBER").Right + 60;
            var rows = grid.FindAllDescendants(app.By.ByControlType(ControlType.DataItem)).Select(x => x.BoundingRectangle).Where(x => x.Height > 0).OrderBy(x => x.Top).ToArray();
            var top = rows.Length > 0 ? rows[0].Top : area.Top + rowHeight;
            return Cells(left, [right - left], top, area.Bottom, rows.Length > 0 ? rows[0].Height : rowHeight);
        }

        public IEnumerable<System.Drawing.Rectangle> Calendar(AutomationElement races) => Delegates(races, app, 30);

        public List<System.Drawing.Rectangle> Competitions()
        {
            var cells = Delegates(app.ById("CompetitionsGrid"), app, 32).ToList();
            if (app.TryByName("Technical delegate") is { } card && app.TryByName("Course and homologation") is { } courseCard)
            {
                // Every field of the card: people, nation and number; and the full name of a delegate whose name could
                // not be split, shown as text below the fields. Field labels are capitals and stay readable.
                var left = card.BoundingRectangle.Left - 10;
                var right = courseCard.BoundingRectangle.Left - 24;
                var top = card.BoundingRectangle.Bottom;
                var fields = app.Window.FindAllDescendants(app.By.ByControlType(ControlType.Edit)).Select(x => x.BoundingRectangle);
                var texts = app.Window.FindAllDescendants(app.By.ByControlType(ControlType.Text))
                    .Where(x => x.Name.Any(char.IsLower)).Select(x => x.BoundingRectangle);
                cells.AddRange(fields.Concat(texts)
                    .Where(x => x.Height > 0 && x.Left >= left && x.Right <= right + 8 && x.Top >= top - 4 && x.Top < top + 260)
                    .Select(x => System.Drawing.Rectangle.FromLTRB(x.Left, Math.Max(x.Top, ContentTop), x.Right, Math.Min(x.Bottom, ContentBottom))).Where(x => x.Height > 0));
            }
            return cells;
        }

        // The folder tree of the Windows dialog shows the user's own folders.
        public System.Drawing.Rectangle[] SaveDialog(AutomationElement dialog)
        {
            var trees = dialog.FindAllDescendants(app.By.ByControlType(ControlType.Tree)).Select(x => x.BoundingRectangle).Where(x => x.Width > 20 && x.Height > 100).ToArray();
            return trees.Length > 0 ? trees : [dialog.BoundingRectangle];
        }

        // undo: the list of unsaved changes is open. imported: entries were just read, which may have brought warnings.
        // Warnings stay until the next save, so once entries were read every later measure covers them as well.
        private bool _warnings;

        public List<System.Drawing.Rectangle> Competitors(bool undo = false, bool imported = false)
        {
            _warnings |= imported;
            var cells = new List<System.Drawing.Rectangle>();
            _grid ??= app.ById("CompetitorGrid");
            var grid = _grid.BoundingRectangle;
            cells.AddRange(Cells(grid.Left + MarkerWidth, s_identity, grid.Top + 34, grid.Bottom - 2, 30));
            if (undo)
            {
                // Title and changes are before the grid in the tree, so these queries end early.
                var panel = app.ByName("UNSAVED CHANGES").Parent.Parent;
                var bottom = app.ByName("Discard all changes", scope: panel).BoundingRectangle.Top;
                foreach (var change in Read(panel).Where(x => x.Type == ControlType.Text && x.Bounds.Height > 0 && x.Bounds.Top < bottom - 6))
                {
                    var end = change.Name.IndexOf(" · ", StringComparison.Ordinal);
                    // A change of a name or code shows the old and new value too: the whole line is covered.
                    var detail = end > 0 ? change.Name[(end + 3)..] : "";
                    if (detail.StartsWith("Surname:", StringComparison.Ordinal) || detail.StartsWith("First name:", StringComparison.Ordinal)
                        || detail.Contains("code:", StringComparison.OrdinalIgnoreCase)) { end = change.Name.Length; }
                    if (end > 0) { cells.Add(System.Drawing.Rectangle.FromLTRB(change.Bounds.Left - 3, change.Bounds.Top - 4, change.Bounds.Left + TextWidth(change.Name[..end]) + 3, Math.Min(change.Bounds.Bottom + 4, bottom - 2))); }
                }
            }
            if (_warnings)
            {
                var items = Read(app.Window);
                // Import warnings, which can name athletes, appear above the save card; a match to check below the grid.
                var save = items.First(x => x is { Name: "Save", Type: ControlType.Button }).Bounds;
                cells.AddRange(items.Where(x => x.Type == ControlType.Text && !x.Hidden && x.Bounds.Height > 0 && x.Bounds.Top >= ContentTop && x.Bounds.Bottom <= save.Top - 4)
                    .Select(x => new System.Drawing.Rectangle(grid.Left, x.Bounds.Top - 2, grid.Width, x.Bounds.Height + 4)));
                if (items.FirstOrDefault(x => x.Name == "Match checked" && !x.Hidden) is { } match)
                { cells.Add(System.Drawing.Rectangle.FromLTRB(grid.Left, match.Bounds.Top - 6, match.Bounds.Left, match.Bounds.Bottom + 6)); }
            }
            return cells;
        }

        // An upper bound of the text's width at 13 px; a little too wide rather than a letter too narrow.
        private static int TextWidth(string text) => (int)Math.Ceiling(text.Sum(c => char.IsUpper(c) ? "MWÅÄÖ".Contains(c, StringComparison.Ordinal) ? 12.0 : 9.6
            : char.IsDigit(c) ? 8.6 : c is ' ' or '-' or '\'' ? 5.5 : 8.2));
    }
}
