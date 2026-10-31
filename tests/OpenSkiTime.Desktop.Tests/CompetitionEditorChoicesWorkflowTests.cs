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
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 1000, WindowState = Avalonia.Controls.WindowState.Normal };
            window.Show(); window.UpdateLayout();

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

            var nations = window.GetVisualDescendants().OfType<AutoCompleteBox>().ToArray();
            Assert.Equal(["FIN", "SWE"], nations.Select(x => x.Text));
            Assert.All(nations, x => Assert.Same(FisReferenceData.Nations, x.ItemsSource));
            window.Close();
        }
        finally { Directory.Delete(folder, true); }
    }
}
