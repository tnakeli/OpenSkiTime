using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OpenSkiTime.Application;
using OpenSkiTime.Desktop;
using OpenSkiTime.Devices;
using OpenSkiTime.Domain;
using OpenSkiTime.Persistence;
using Xunit;

namespace OpenSkiTime.Tests;

public partial class DesktopWorkflowTests
{
    // A draw that cannot run explains why when pressed, naming the competitors entered in the wrong competition;
    // ticking a competitor into a competition of the other gender is refused with the reason.
    [AvaloniaFact]
    public async Task DrawExplainsWrongGenderEntriesAndTheGridRefusesThem()
    {
        var folder = Path.Combine(Path.GetTempPath(), "openskitime-gender-draw", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var file = Path.Combine(folder, "synthetic.ost");
            var date = new DateOnly(2026, 11, 19);
            await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore(), new AlgeDecoderFactory());
            CompetitionValues Race(string label, string gender) => new($"Synthetic {label}", label, date, Discipline.GiantSlalom, RaceType.Fis, 2, 0, "9123",
                Calendar: new CompetitionCalendarData(2027, "Synthetic Fell", "FIN", "NJR", gender));
            var series = await workspace.CreateAsync(file, new("Synthetic gender", "Synthetic Fell", "Synthetic Club", date, date, "FIN", "2026/27"),
                [Race("GS M", "M"), Race("GS W", "W")]);
            var men = series.Competitions.Single(x => x.Values.ShortLabel == "GS M");
            var woman = await workspace.SaveDeskRowAsync(null, new("Synthwoman", "Aino", 2008, "990001", "FIN", "Club", Gender.Female), null, false, null, series.Revision);
            var man = await workspace.SaveDeskRowAsync(null, new("Synthman", "Eino", 2007, "990002", "FIN", "Club", Gender.Male), men.Id, true, null, woman.Revision);
            // An older file: the woman entered while the race had no gender, then the race became a men's race.
            var open = await workspace.SaveCompetitionAsync(men.Id, Race("GS M", ""), man.Revision);
            var entered = await workspace.SaveDeskRowAsync(woman.Value.Id, woman.Value.Values, men.Id, true, null, open.Revision);
            await workspace.SaveCompetitionAsync(men.Id, Race("GS M", "M"), entered.Revision);
            await workspace.CloseAsync();

            using var vm = new MainViewModel(workspace, new FileDialogsStub { OpenPath = file, NewPath = file, BackupPath = file + ".backup" },
                fisStore: new FisLocalStore(folder), recentSeriesStore: new(folder), reportDefaultsStore: new(folder), timingDeviceCache: new(folder));
            await vm.OpenSeriesCommand.ExecuteAsync(null);
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
            window.Show();
            try
            {
                await vm.OpenDrawRunCommand.ExecuteAsync(new DrawDestination(vm.Competitions.Single(x => x.Id == men.Id), 1));
                Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
                Assert.False(vm.CanPrepareDraw);
                Assert.Contains("GS M is a men's competition", vm.DrawBlockReason, StringComparison.Ordinal);
                Assert.Contains("SYNTHWOMAN Aino", vm.DrawBlockReason, StringComparison.Ordinal);
                var draw = window.GetVisualDescendants().OfType<Button>().Single(x => Equals(x.Content, "Draw") && x.IsEffectivelyVisible);
                Assert.True(draw.IsEffectivelyEnabled);
                draw.Command!.Execute(null);
                Dispatcher.UIThread.RunJobs();
                Assert.True(vm.IsError);
                Assert.Contains("SYNTHWOMAN Aino", vm.StatusMessage, StringComparison.Ordinal);
                Assert.Empty((await workspace.ReadStartListsAsync(men.Id)).Revisions);

                // Grid: ticking the woman into the men's race again is refused at once.
                vm.ShowCompetitorsCommand.Execute(null);
                var row = vm.VisibleCompetitors.Single(x => x.Surname == "SYNTHWOMAN");
                var women = vm.Competitions.Single(x => x.Values.ShortLabel == "GS W");
                var womenChoice = row.GridEntries.Single(x => x.CompetitionId == women.Id);
                womenChoice.IsParticipating = true;
                await vm.StageGridEntryAsync(row, womenChoice);
                Assert.True(womenChoice.IsParticipating);
                var menChoice = row.GridEntries.Single(x => x.CompetitionId == men.Id);
                menChoice.IsParticipating = false;
                await vm.StageGridEntryAsync(row, menChoice);
                menChoice.IsParticipating = true;
                await vm.StageGridEntryAsync(row, menChoice);
                Assert.False(menChoice.IsParticipating);
                Assert.True(vm.IsError);
                Assert.Contains("SYNTHWOMAN Aino (Women) cannot be entered in GS M, a men's competition", vm.StatusMessage, StringComparison.Ordinal);
            }
            finally { window.Close(); }
            await workspace.CloseAsync();
        }
        finally { Directory.Delete(folder, recursive: true); }
    }
}

