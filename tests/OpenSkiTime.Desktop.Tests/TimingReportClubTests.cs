using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using OpenSkiTime.Application;
using OpenSkiTime.Desktop;
using OpenSkiTime.Devices;
using OpenSkiTime.Domain;
using OpenSkiTime.Persistence;
using Xunit;

namespace OpenSkiTime.Tests;

// The timing report is made for every competition. A Club race has no FIS XML: the report is a PDF only.
public sealed class TimingReportClubTests
{
    private static readonly DateOnly s_date = new(2026, 11, 7);

    [AvaloniaFact]
    public async Task ClubRaceHasATimingReportWithoutFisSubmission()
    {
        var folder = Path.Combine(Path.GetTempPath(), "openskitime-club-report-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, "club.ost");
            await using (var setup = new SeriesWorkspace(new SqliteSeriesFileStore(), new AlgeDecoderFactory()))
            {
                var series = await setup.CreateAsync(path, new("Synthetic", "Test", "Club", s_date, s_date, "FIN", "2026/27"));
                await setup.SaveCompetitionAsync(null, new("Club race", "CLUB", s_date, Discipline.GiantSlalom, RaceType.Club, 1, 0), series.Revision);
            }
            await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore(), new AlgeDecoderFactory());
            using var vm = new MainViewModel(workspace, new Dialogs(path), fisStore: new FisLocalStore(folder), recentSeriesStore: new RecentSeriesStore(folder));
            var window = new MainWindow { DataContext = vm, Width = 1500, Height = 900 };
            window.Show();
            await vm.OpenSeriesCommand.ExecuteAsync(null);
            await vm.ShowTimingReportCommand.ExecuteAsync(null);
            Assert.True(vm.IsTimingReportSection);
            Assert.Equal("CLUB", vm.ReportCompetition?.Values.ShortLabel);
            Assert.True(vm.IsReportClub);
            Assert.False(vm.IsReportFis);
            Assert.False(vm.IsError, vm.StatusMessage);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            var cards = window.GetVisualDescendants().OfType<Border>().ToArray();
            Assert.True(cards.Single(x => x.Name == "TimingReportPdfCard").IsEffectivelyVisible);
            Assert.False(cards.Single(x => x.Name == "TimingReportXmlCard").IsEffectivelyVisible);
            Assert.False(window.GetVisualDescendants().OfType<Button>().Single(x => x.Name == "SendTimingReportToFisButton").IsEffectivelyVisible);
            window.Close();
        }
        finally { Directory.Delete(folder, true); }
    }

    private sealed class Dialogs(string path) : IFileDialogs
    {
        public Task<string?> ChooseNewAsync(string suggestedName) => Task.FromResult<string?>(path);
        public Task<string?> ChooseOpenAsync() => Task.FromResult<string?>(path);
        public Task<string?> ChooseBackupAsync(string suggestedName) => Task.FromResult<string?>(path + ".backup");
        public Task<bool> ConfirmRemoveAsync(string competitionName) => Task.FromResult(false);
        public Task<bool> ConfirmDiscardChangesAsync(int changeCount) => Task.FromResult(false);
        public Task<string?> ChooseStartListExportAsync(string suggestedName, bool print) => Task.FromResult<string?>(null);
        public Task<string?> ChooseResultXmlExportAsync(string suggestedName) => Task.FromResult<string?>(null);
    }
}
