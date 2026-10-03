using System.Globalization;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
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
    [AvaloniaTheory]
    [InlineData("en-US")]
    [InlineData("fi-FI")]
    public async Task ReadOnlyCompetitionDateDoesNotWriteFormattedTextBackToTheModel(string culture)
    {
        var previous = CultureInfo.CurrentCulture;
        var folder = Path.Combine(Path.GetTempPath(), "openskitime-date-display-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            var file = Path.Combine(folder, "synthetic.ost");
            var date = new DateOnly(2026, 10, 3); // Both day and month are valid when mistakenly reversed.
            await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore());
            await workspace.CreateAsync(file, new("Synthetic date check", "Test", "Test", date, date, "FIN", "2026/27"),
                [new("Synthetic race", "SL", date, Discipline.Slalom, RaceType.Club, 2, 0)]);
            using var vm = new MainViewModel(workspace, new FileDialogsStub { OpenPath = file, NewPath = file, BackupPath = file + ".backup" },
                fisStore: new FisLocalStore(folder), recentSeriesStore: new(folder), reportDefaultsStore: new(folder), timingDeviceCache: new(folder));
            await vm.OpenSeriesCommand.ExecuteAsync(null);
            vm.ShowCompetitionsCommand.Execute(null);
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
            try
            {
                window.Show(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
                var grid = window.FindControl<DataGrid>("CompetitionsGrid")!;
                Assert.Contains(grid.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "03.10.2026");
                Assert.Equal(date, Assert.Single(vm.Competitions).Values.Date);
                Assert.Equal(date, Assert.Single((await workspace.ReadAsync()).Competitions).Values.Date);
            }
            finally { window.Close(); }
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
            Directory.Delete(folder, true);
        }
    }
}
