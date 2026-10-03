using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
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
    public async Task CompetitionColumnsFilterCombineClearAndSortDatesWithoutLosingSelectedEditor()
    {
        var folder = Path.Combine(Path.GetTempPath(), "openskitime-competition-columns-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, "Synthetic.ost");
            await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore());
            var races = new[] {
                new CompetitionValues("Alpha race", "SL1 W", new(2026, 12, 10), Discipline.Slalom, RaceType.Fis, 2, 0, "1001"),
                new CompetitionValues("Beta race", "SL2 W", new(2026, 2, 1), Discipline.Slalom, RaceType.Fis, 2, 0, "1002"),
                new CompetitionValues("Alpha race", "SG1 M", new(2026, 1, 31), Discipline.SuperG, RaceType.Fis, 1, 0, "1003") };
            await workspace.CreateAsync(path, new("Synthetic", "Test", "Club", new(2026, 1, 31), new(2026, 12, 10), "FIN", "2025/26"), races);
            using var vm = new MainViewModel(workspace, new FileDialogsStub { OpenPath = path, NewPath = path, BackupPath = path + ".bak" },
                fisStore: new FisLocalStore(folder), recentSeriesStore: new RecentSeriesStore(folder));
            await vm.OpenSeriesCommand.ExecuteAsync(null); vm.ShowCompetitionsCommand.Execute(null);
            var window = new MainWindow { DataContext = vm, Width = 1800, Height = 900, WindowState = WindowState.Normal };
            window.Show(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            try
            {
                var grid = window.FindControl<DataGrid>("CompetitionsGrid")!;
                CompetitionDetails[] Rows() => grid.ItemsSource!.OfType<CompetitionDetails>().ToArray();
                Button Icon(string name) => grid.GetVisualDescendants().OfType<Button>().Single(x => x.Name == name);
                void Press(Button button) { button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Dispatcher.UIThread.RunJobs(); }
                void Filter(string key, string text)
                {
                    Press(Icon("CompetitionFilterButton_" + key));
                    var panel = (StackPanel)window.CompetitionFilterMenu!.Content!;
                    panel.Children.OfType<TextBox>().Single().Text = text;
                    Press(panel.Children.OfType<StackPanel>().Single().Children.OfType<Button>().Single(x => Equals(x.Content, "Apply")));
                }
                Assert.True(grid.CanUserSortColumns); Assert.True(grid.CanUserResizeColumns);
                Assert.DoesNotContain(window.GetVisualDescendants().OfType<TextBlock>(), x => x.Text == "SERIES / RACES");
                var selected = vm.Competitions.Single(x => x.Values.Name == "Beta race");
                grid.SelectedItem = selected; vm.SaveCourseToAllRaces = true;
                Press(Icon("CompetitionSort_Values.Date"));
                Assert.Equal(["SG1 M", "SL2 W", "SL1 W"], Rows().Select(x => x.Values.ShortLabel));
                Assert.Same(selected, vm.SelectedCompetition); Assert.True(vm.SaveCourseToAllRaces);
                Press(Icon("CompetitionSort_Values.Date"));
                Assert.Equal(System.ComponentModel.ListSortDirection.Descending,
                    ((Avalonia.Collections.DataGridCollectionView)grid.ItemsSource!).SortDescriptions.Single().Direction);
                Assert.Equal(["SL1 W", "SL2 W", "SG1 M"], Rows().Select(x => x.Values.ShortLabel));
                Filter("Values.Name", "alpha RACE"); Assert.Equal(2, Rows().Length);
                Filter("Values.ShortLabel", "SL W"); Assert.Equal("SL1 W", Assert.Single(Rows()).Values.ShortLabel);
                Press(Icon("CompetitionFilterButton_Values.ShortLabel")); Assert.Equal(2, Rows().Length);
                Press(Icon("CompetitionFilterButton_Values.Name")); Assert.Equal(3, Rows().Length);
                Assert.Equal(3, (await workspace.ReadAsync()).Competitions.Count);
            }
            finally { window.Close(); }
        }
        finally { Directory.Delete(folder, true); }
    }
}
