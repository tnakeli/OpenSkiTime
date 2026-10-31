using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using OpenSkiTime.Application;
using OpenSkiTime.Desktop;
using OpenSkiTime.Domain;
using OpenSkiTime.Persistence;
using Xunit;

namespace OpenSkiTime.Tests;

public partial class DesktopWorkflowTests
{
    [AvaloniaFact]
    public async Task CompetitionEditorDerivesVerticalDropAndOffersSeasonAndNationChoices()
    {
        var folder = Path.Combine(Path.GetTempPath(), "openskitime-editor-choices-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, "test.ost"); var date = new DateOnly(2026, 12, 12);
            await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore());
            // A stored drop that does not match the altitudes: loading must show it as saved, not recompute it.
            var race = new CompetitionValues("Synthetic slalom", "SLW", date, Discipline.Slalom, RaceType.Fis, 2, 0, "9123",
                "Synthetic Fell", 520, 380, 150, "99999/12/26", new(2027, "Synthetic Fell", "FIN", "FIS", "W",
                    new("SYNTHDELEGATE", "Demo", "SWE", "9001", "")));
            await workspace.CreateAsync(path, new("Test", "Synthetic Fell", "Club", date, date, "FIN", "2026/27"), [race]);
            using var vm = new MainViewModel(workspace, new FileDialogsStub { NewPath = path, OpenPath = path, BackupPath = path + ".bak" },
                fisStore: new FisLocalStore(folder), recentSeriesStore: new RecentSeriesStore(folder));
            await vm.OpenSeriesCommand.ExecuteAsync(null); vm.ShowCompetitionsCommand.Execute(null);
            vm.SelectedCompetition = Assert.Single(vm.Competitions);
            var window = new MainWindow { DataContext = vm, Width = 1600, Height = 1000, WindowState = Avalonia.Controls.WindowState.Normal };
            window.Show(); window.UpdateLayout();

            // Every field on a form row has the same height: text, choice, number and search fields.
            static bool Nested(Control x) => x.GetVisualAncestors().Any(a => a is NumericUpDown or AutoCompleteBox or ComboBox
                || (a is Border b && b.Classes.Contains("statField")));
            var fields = window.GetVisualDescendants().OfType<Control>()
                .Where(x => x is TextBox or ComboBox or NumericUpDown or AutoCompleteBox)
                .Where(x => x.IsEffectivelyVisible && x.FindAncestorOfType<FormRow>() is not null && !Nested(x)).ToArray();
            Assert.True(fields.Length >= 10);
            var heights = fields.GroupBy(x => x.Bounds.Height).ToArray();
            Assert.True(heights.Length == 1, string.Join("; ", heights.Select(g => $"{g.Key}px: {string.Join(", ", g.Select(x => x.GetType().Name).Distinct())}")));

            // Competitions is full width: the list, the right-hand cards and the action bar end on the same edge,
            // and the share-to-all checkboxes of a card row sit on one line.
            double Right(Control c) => c.TranslatePoint(new Point(c.Bounds.Width, 0), window)!.Value.X;
            double Top(Control c) => c.TranslatePoint(new Point(0, 0), window)!.Value.Y;
            Border Card(string name) => window.FindControl<Control>(name)!.GetVisualAncestors().OfType<Border>().First(x => x.Classes.Contains("card"));
            var list = window.FindControl<DataGrid>("CompetitionsGrid")!;
            Assert.True(list.Bounds.Width > 1500, $"Competition list is {list.Bounds.Width}px wide");
            Assert.DoesNotContain(list.Columns, x => Equals(x.Header, ""));
            var saveCompetition = window.GetVisualDescendants().OfType<Button>().Single(x => Equals(x.Content, "Save competition"));
            var addCompetition = window.GetVisualDescendants().OfType<Button>().Single(x => Equals(x.Content, "Add competition"));
            Assert.All(new Control[] { Card("CompetitionFisCheckBox"), Card("SaveCourseToAllRacesCheckBox"),
                saveCompetition.GetVisualAncestors().OfType<Border>().First(x => x.Classes.Contains("card")), addCompetition },
                x => Assert.Equal(Right(list), Right(x)));
            Assert.Equal(Top(window.FindControl<CheckBox>("SaveTdToAllRacesCheckBox")!), Top(window.FindControl<CheckBox>("SaveCourseToAllRacesCheckBox")!));

            Assert.Equal("150", vm.CompetitionVerticalDrop);
            vm.CompetitionStartAltitude = "600";
            Assert.Equal("220", vm.CompetitionVerticalDrop);
            vm.CompetitionFinishAltitude = "65";
            Assert.Equal("535", vm.CompetitionVerticalDrop);
            vm.CompetitionFinishAltitude = "";
            Assert.Equal("535", vm.CompetitionVerticalDrop);

            var season = window.FindControl<ComboBox>("CompetitionSeasonInput")!;
            Assert.Equal(["2026", "2027", "2028"], season.Items.OfType<string>());
            Assert.Equal("2027", season.SelectedItem);
            season.SelectedItem = "2028";
            Assert.Equal("2028", vm.CompetitionSeason);
            vm.CompetitionDateText = "12.12.2029";
            Assert.Equal("2028", season.SelectedItem);
            Assert.Equal(["2028", "2029", "2030", "2031"], vm.CompetitionSeasonChoices);

            var nations = window.GetVisualDescendants().OfType<AutoCompleteBox>().Where(x => x.IsEffectivelyVisible).ToArray();
            Assert.Equal(["FIN", "SWE"], nations.Select(x => x.Text));
            Assert.All(nations, x => Assert.Same(FisReferenceData.Nations, x.ItemsSource));

            // Series: the season is an editable choice; a new start date changes the offered labels, never the value.
            vm.ShowSeriesCommand.Execute(null); window.UpdateLayout();
            var seriesSeason = window.FindControl<ComboBox>("SeriesSeasonInput")!;
            Assert.True(seriesSeason.IsEditable); Assert.Equal("2026/27", seriesSeason.Text);
            Assert.Equal(["2025/26", "2026/27", "2027/28"], seriesSeason.Items.OfType<string>());
            vm.StartDateText = "01.12.2030"; window.UpdateLayout();
            Assert.Equal(["2026/27", "2029/30", "2030/31", "2031/32"], seriesSeason.Items.OfType<string>());
            Assert.Equal("2026/27", vm.Season); Assert.Equal("2026/27", seriesSeason.Text);
            seriesSeason.Text = "2030/31";
            Assert.Equal("2030/31", vm.Season);

            // The FIS calendar, the series form, its actions and the header row share the page edges.
            vm.IsSeriesCalendarOpen = true; window.UpdateLayout();
            var calendar = Card("SeriesCalendarEventsGrid");
            var saveSeries = window.GetVisualDescendants().OfType<Button>().Single(x => Equals(x.Content, "Save series"));
            Assert.InRange(calendar.Bounds.Width, 1239, 1241);
            Assert.All(new Control[] { Card("SeriesSeasonInput"), saveSeries.GetVisualAncestors().OfType<Border>().First(x => x.Classes.Contains("card")),
                (Control)window.FindControl<Button>("BrowseSeriesCalendarButton")!.Parent! }, x => Assert.Equal(Right(calendar), Right(x)));
            // The calendar season is a choice; a season outside the offered range is added, so it stays selected.
            var calendarSeason = window.FindControl<ComboBox>("SeriesCalendarSeasonInput")!;
            vm.SeriesCalendarSeason = "2020"; window.UpdateLayout();
            Assert.Contains("2020", calendarSeason.Items.OfType<string>());
            Assert.Equal("2020", calendarSeason.SelectedItem);
            window.Close();
        }
        finally { Directory.Delete(folder, true); }
    }
}
