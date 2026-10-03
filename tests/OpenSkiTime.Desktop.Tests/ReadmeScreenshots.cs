using System.IO.Compression;
using System.Globalization;
using System.Text;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using OpenSkiTime.Application;
using OpenSkiTime.Desktop;
using OpenSkiTime.Devices;
using OpenSkiTime.Domain;
using OpenSkiTime.Persistence;
using OpenSkiTime.Timing;
using Xunit;

namespace OpenSkiTime.Tests;

public partial class DesktopWorkflowTests
{
    private sealed record DemoAthlete(string Surname, string FirstName, int Year, string Club, Gender Gender);

    [AvaloniaFact]
    public async Task GenerateReadmeScreenshotsFromSyntheticEvent()
    {
        var output = Environment.GetEnvironmentVariable("OPENSKITIME_README_SCREENSHOTS");
        if (string.IsNullOrWhiteSpace(output)) { return; }

        var athletes = new[]
        {
            new DemoAthlete("AURORA", "Mira", 2003, "North Ridge", Gender.Female),
            new DemoAthlete("LUMI", "Elina", 2005, "Snowline", Gender.Female),
            new DemoAthlete("KIDE", "Aava", 2004, "Fell Alpine", Gender.Female),
            new DemoAthlete("TUNTURI", "Helmi", 2002, "North Ridge", Gender.Female),
            new DemoAthlete("KAARNA", "Iida", 2006, "Peak Club", Gender.Female),
            new DemoAthlete("HALLA", "Saana", 2003, "Snowline", Gender.Female),
            new DemoAthlete("RINNE", "Veera", 2001, "Fell Alpine", Gender.Female),
            new DemoAthlete("TUULI", "Emilia", 2005, "Peak Club", Gender.Female),
            new DemoAthlete("KORPI", "Sofia", 2004, "North Ridge", Gender.Female),
            new DemoAthlete("PAKKA", "Linnea", 2002, "Snowline", Gender.Female),
            new DemoAthlete("JÄNNE", "Olivia", 2006, "Fell Alpine", Gender.Female),
            new DemoAthlete("SUMU", "Aino", 2003, "Peak Club", Gender.Female),
            new DemoAthlete("KALLIO", "Eero", 2002, "North Ridge", Gender.Male),
            new DemoAthlete("HUIPPU", "Leo", 2005, "Snowline", Gender.Male),
            new DemoAthlete("KAAMOS", "Mikael", 2003, "Fell Alpine", Gender.Male),
            new DemoAthlete("HARJU", "Oliver", 2004, "Peak Club", Gender.Male),
            new DemoAthlete("VIRTA", "Noel", 2001, "North Ridge", Gender.Male),
            new DemoAthlete("LUMIKKO", "Oskari", 2006, "Snowline", Gender.Male),
            new DemoAthlete("ROUTA", "Julius", 2002, "Fell Alpine", Gender.Male),
            new DemoAthlete("VANNE", "Elias", 2004, "Peak Club", Gender.Male),
            new DemoAthlete("KURU", "Aarni", 2005, "North Ridge", Gender.Male),
            new DemoAthlete("PILVI", "Vilho", 2003, "Snowline", Gender.Male),
        };
        var root = Path.Combine(Path.GetTempPath(), "openskitime-readme-demo", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var file = Path.Combine(root, "Northern Alpine Demo.ost");
            var cache = new FisLocalStore(root);
            await cache.SaveListAsync(DemoFisList(athletes));
            await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore(), new AlgeDecoderFactory());
            var date = new DateOnly(2026, 9, 27);
            var series = await workspace.CreateAsync(file, new("Northern Alpine Weekend", "Summit Arena", "Demo Race Office",
                date, date.AddDays(1), "FIN", "2026/27"));
            series = await workspace.SaveCompetitionAsync(null, new("Northern Slalom Women", "SL Women", date,
                Discipline.Slalom, RaceType.Fis, 2, 1, "9991", CourseName: "North Face", StartAltitudeMeters: 500,
                FinishAltitudeMeters: 300, VerticalDropMeters: 200, HomologationNumber: "DEMO/10/26",
                Calendar: new(2027, "Summit Arena", "FIN", "FIS", "W", new("DELEGATE", "Demo", "FIN", "9999")),
                CourseLengthMeters: 640), series.Revision);
            series = await workspace.SaveCompetitionAsync(null, new("Northern Slalom Men", "SL Men", date.AddDays(1),
                Discipline.Slalom, RaceType.Fis, 2, 1, "9992", CourseName: "North Face", StartAltitudeMeters: 500,
                FinishAltitudeMeters: 300, VerticalDropMeters: 200, HomologationNumber: "DEMO/10/26",
                Calendar: new(2027, "Summit Arena", "FIN", "FIS", "M", new("DELEGATE", "Demo", "FIN", "9999")),
                CourseLengthMeters: 640), series.Revision);
            var women = series.Competitions[0];
            var men = series.Competitions[1];
            var revision = series.Revision;
            revision = (await workspace.SaveCategoryRuleAsync(null, new("Women U23", 2004, 2008, Gender.Female, 0), revision)).Revision;
            revision = (await workspace.SaveCategoryRuleAsync(null, new("Women Senior", 1980, 2003, Gender.Female, 1), revision)).Revision;
            revision = (await workspace.SaveCategoryRuleAsync(null, new("Men U23", 2004, 2008, Gender.Male, 2), revision)).Revision;
            revision = (await workspace.SaveCategoryRuleAsync(null, new("Men Senior", 1980, 2003, Gender.Male, 3), revision)).Revision;
            for (var i = 0; i < athletes.Length; i++)
            {
                var athlete = athletes[i];
                var competition = athlete.Gender == Gender.Female ? women : men;
                var saved = await workspace.SaveDeskRowAsync(null, new(athlete.Surname, athlete.FirstName, athlete.Year,
                    (990001 + i).ToString(System.Globalization.CultureInfo.InvariantCulture), "FIN", athlete.Club, athlete.Gender),
                    competition.Id, true, null, revision);
                revision = saved.Revision;
            }

            using var vm = new MainViewModel(workspace, new FileDialogsStub { OpenPath = file, NewPath = file, BackupPath = file + ".backup" },
                fisStore: cache, recentSeriesStore: new RecentSeriesStore(root),
                categoryRulePresetStore: new CategoryRulePresetStore(root), timingPreferencesStore: new TimingPreferencesStore(root),
                reportDefaultsStore: new TimingReportDefaultsStore(root), timingDeviceCache: new FisTimingDeviceCache(root));
            await vm.OpenSeriesCommand.ExecuteAsync(null);
            vm.FileLabel = @"C:\Demo\Northern Alpine Weekend.ost";
            var window = new MainWindow { DataContext = vm, WindowState = WindowState.Normal, Width = 1440, Height = 520 };
            window.Show();
            try
            {
                vm.ShowSeriesCommand.Execute(null);
                CaptureDraw(window, output, "01-event-series.png");
                window.Height = 1150;
                vm.ShowCompetitionsCommand.Execute(null);
                vm.SelectedCompetition = vm.Competitions[0];
                CaptureDraw(window, output, "02-competitions.png");
                window.Height = 850;
                vm.ShowCompetitorsCommand.Execute(null);
                CaptureDraw(window, output, "03-competitors.png");

                await vm.OpenDrawRunCommand.ExecuteAsync(new DrawDestination(women, 1));
                await vm.PrepareDrawCommand.ExecuteAsync(null);
                Assert.False(vm.IsError, vm.StatusMessage);
                window.Height = 900;
                CaptureDraw(window, output, "04-start-lists.png");

                await vm.OpenTimingRunCommand.ExecuteAsync(new DrawDestination(women, 1));
                window.Height = 1120;
                window.UpdateLayout();
                var ranking = window.FindControl<TimingView>("TimingWorkspace")!.FindControl<DataGrid>("RankingGrid")!;
                // Leave room for the operator's sort/filter controls in compact numeric headers.
                ranking.Columns[1].Width = new DataGridLength(80);
                ranking.Columns[2].Width = new DataGridLength(80);
                ranking.Columns[4].Width = new DataGridLength(85);
                vm.TimingSource = "Simulator";
                vm.TimingIntermediateChannels = "2";
                await vm.ConnectTimingCommand.ExecuteAsync(null);
                Assert.False(vm.IsError, vm.StatusMessage);
                await vm.ToggleTimingChannelCommand.ExecuteAsync("start");
                await vm.ToggleTimingChannelCommand.ExecuteAsync("finish");
                await vm.ToggleTimingChannelCommand.ExecuteAsync("intermediate:1");
                var expected = workspace.Timing!.Snapshot!.Observations.Count;
                async Task Pulse(string channel, string time)
                {
                    vm.SimulationTime = time;
                    await vm.SimulatePulseCommand.ExecuteAsync(channel);
                    await WaitTimingAsync(vm, () => workspace.Timing!.Snapshot!.Observations.Count > expected
                        && workspace.Timing.Snapshot.Unresolved == 0);
                    expected++;
                }
                await Pulse("start", "12:00:00.0000");
                await Pulse("intermediate:1", "12:00:22.1400");
                await Pulse("start", "12:00:30.0000");
                await Pulse("finish", "12:00:54.8700");
                await Pulse("intermediate:1", "12:00:55.2300");
                await Pulse("start", "12:01:00.0000");
                await Pulse("finish", "12:01:24.5800");
                await Pulse("intermediate:1", "12:01:25.3400");
                await Pulse("start", "12:01:30.0000");
                CaptureDraw(window, output, "05-timing.png");
                await Pulse("finish", "12:01:53.6700");
                await Pulse("intermediate:1", "12:01:55.2700");
                CaptureDraw(window, output, "overview.png");
                await vm.DisconnectTimingCommand.ExecuteAsync(null);

                window.Height = 1240;
                vm.SelectTimingBibs([1], 1);
                vm.ShowTimingClassificationEditor = true;
                vm.TimingClassification = "DSQ";
                vm.TimingDsqGate = "18";
                vm.TimingDsqReason = "Missed gate (synthetic example)";
                vm.TimingDsqJudge = "Demo judge";
                vm.TimingOperator = "Demo race office";
                vm.TimingReason = "Classification review for README demonstration";
                await vm.SaveTimingClassificationCommand.ExecuteAsync(null);
                Assert.False(vm.IsError, vm.StatusMessage);
                CaptureDraw(window, output, "06-classification.png");

                await vm.ShowResultsCommand.ExecuteAsync(null);
                await vm.LoadResultsCommand.ExecuteAsync(null);
                for (var i = 0; i < vm.ResultsJury.Count; i++)
                {
                    vm.ResultsJury[i].FirstName = "Demo";
                    vm.ResultsJury[i].LastName = "OFFICIAL " + (i + 1).ToString(CultureInfo.InvariantCulture);
                    vm.ResultsJury[i].Nation = "FIN";
                }
                foreach (var run in vm.ResultsRuns)
                {
                    run.Setter.FirstName = "Demo"; run.Setter.LastName = "SETTER"; run.Setter.Nation = "FIN";
                    run.Gates = "52"; run.TurningGates = "50";
                    run.StartTime = run.Number == 1 ? "12:00" : "14:00";
                    run.Length = "640";
                    run.Conditions = "Clear"; run.Snow = "Hard";
                    run.StartTemperature = "-3.0"; run.FinishTemperature = "-1.5";
                    run.AddForerunnerCommand.Execute(null);
                    run.Forerunners[0].FirstName = "Demo";
                    run.Forerunners[0].LastName = "FORERUNNER";
                    run.Forerunners[0].Nation = "FIN";
                }
                Assert.True(await vm.FlushRaceInformationAsync(), vm.RaceInformationStatus);
                window.Height = 1120;
                CaptureDraw(window, output, "07-race-information.png");
                await vm.ShowPdfFactoryCommand.ExecuteAsync(null);
                await vm.GenerateAllPdfsCommand.ExecuteAsync(null);
                Assert.False(vm.IsError, vm.StatusMessage);
                window.Height = 980;
                CaptureDraw(window, output, "11-pdf-factory.png");
            }
            finally { window.Close(); }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [AvaloniaFact]
    public async Task GenerateTimingReportScreenshotsFromSyntheticEvent()
    {
        var output = Environment.GetEnvironmentVariable("OPENSKITIME_README_SCREENSHOTS");
        if (string.IsNullOrWhiteSpace(output)) { return; }
        var root = Path.Combine(Path.GetTempPath(), "openskitime-report-demo", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var file = Path.Combine(root, "Northern Downhill Demo.ost");
            var date = new DateOnly(2026, 10, 3);
            var now = new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.FromHours(3));
            await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore(), new AlgeDecoderFactory());
            var values = new CompetitionValues("Northern Downhill Demo", "DH Men", date, Discipline.Downhill, RaceType.Fis, 1, 0, "9993");
            var series = await workspace.CreateAsync(file, new("Northern Alpine Weekend", "Summit Arena", "Demo Race Office",
                date, date, "FIN", "2026/27"), [values]);
            var competition = series.Competitions[0];
            var revision = series.Revision;
            var entrants = new List<DrawEntrant>();
            var surnames = new[] { "KALLIO", "HUIPPU" };
            var firstNames = new[] { "Eero", "Leo" };
            for (var i = 0; i < surnames.Length; i++)
            {
                var athlete = new CompetitorValues(surnames[i], firstNames[i], 2002 + i, (990101 + i).ToString(CultureInfo.InvariantCulture),
                    "FIN", "North Ridge", Gender.Male);
                var saved = await workspace.SaveDeskRowAsync(null, athlete, competition.Id, true, null, revision);
                revision = saved.Revision;
                entrants.Add(new(saved.Value.Id, athlete, 20 + i * 10));
            }
            var plan = FisStartOrder.FirstRun(competition.Id, values, Gender.Male, entrants, new("1327", date, date), new(), "readme-report");
            var list = Assert.Single((await workspace.SaveStartListAsync(new(plan, revision, "Demo operator", "Synthetic draw", now))).Revisions);
            var timing = workspace.Timing!;
            await timing.SelectRunAsync(list.Id);
            var simulator = new SimulatorTimingSource();
            await timing.StartAsync(simulator, new("Simulator", "Demo timing", date, Simulation: true), "Demo operator");
            foreach (var entry in plan.Entries)
            {
                var start = TimeSpan.FromHours(12).Ticks + (entry.Position - 1) * 2 * TimeSpan.TicksPerMinute;
                await timing.ArmAsync(entry.Bib, null); await simulator.PulseAsync(0, start);
                await ReportUntil(() => timing.Snapshot!.Results.Single(x => x.Bib == entry.Bib).Status == TimingStatus.OnCourse);
                await timing.ArmAsync(null, entry.Bib); await simulator.PulseAsync(1, start + TimeSpan.TicksPerMinute);
                await ReportUntil(() => timing.Snapshot!.Results.Single(x => x.Bib == entry.Bib).Status == TimingStatus.Finished);
            }
            await timing.StopAsync();
            using var vm = new MainViewModel(workspace, new FileDialogsStub { OpenPath = file, NewPath = file, BackupPath = file + ".backup" },
                fisStore: new FisLocalStore(root), recentSeriesStore: new RecentSeriesStore(root),
                categoryRulePresetStore: new CategoryRulePresetStore(root), timingPreferencesStore: new TimingPreferencesStore(root),
                reportDefaultsStore: new TimingReportDefaultsStore(root), timingDeviceCache: new FisTimingDeviceCache(root));
            await vm.OpenSeriesCommand.ExecuteAsync(null);
            vm.FileLabel = @"C:\Demo\Northern Alpine Weekend.ost";
            await vm.ShowTimingReportCommand.ExecuteAsync(null);
            await vm.SelectReportCompetitionAsync(competition);
            vm.ReportLevel = 3;
            Assert.True(await vm.FlushTimingReportAsync(), vm.StatusMessage);
            var window = new MainWindow { DataContext = vm, WindowState = WindowState.Normal, Width = 1440, Height = 900 };
            window.Show();
            try
            {
                window.GetVisualDescendants().OfType<TimingReportView>().Single().FindControl<TabControl>("TimingReportTabs")!.SelectedIndex = 1;
                CaptureDraw(window, output, "08-timing-report.png");
                vm.ReportImageRole = TimingReportImageRole.B;
                vm.ReportImportRun = 1;
                await vm.ImportReportImagesAsync([Path.Combine(AppContext.BaseDirectory, "Fixtures", "timing-receipt.png")]);
                Assert.False(vm.IsError, vm.StatusMessage);
                Assert.Equal(4, vm.ReportImportPreview.Count(x => x.CanAccept));
                foreach (var row in vm.ReportImportPreview) { row.Accept = row.CanAccept; }
                var dialog = new TimingReceiptDialog { DataContext = vm, Width = 1440, Height = 850 };
                dialog.Show(window);
                try { CaptureDraw(dialog, output, "09-receipt-ocr.png"); }
                finally { dialog.Close(); }
            }
            finally { window.Close(); }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static byte[] DemoFisList(IReadOnlyList<DemoAthlete> athletes)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            Write(zip, "AL1327hdr.csv", "Listid\tSeasoncode\tListnumber\tListname\tCalculationdate\tStartracedate\tEndracedate\tValidfrom\tValidto\tLastupdate\n"
                + "465\t2027\t13\t13th FIS points list 2026/27\t2026-09-22\t2026-07-01\t2026-09-20\t2026-09-24\t2026-09-30\t2026-09-22 04:19:38\n");
            var competitors = new StringBuilder("Competitorid\tSectorcode\tFiscode\tLastname\tFirstname\tGender\tBirthdate\tNationcode\tNationalcode\tSkiclub\tAssociation\tStatus\n");
            var points = new StringBuilder("Recid\tListid\tCompetitorid\tDisciplinecode\tFispoints\tPosition\tPenalty\tLastupdate\n");
            for (var i = 0; i < athletes.Count; i++)
            {
                var athlete = athletes[i];
                competitors.AppendLine(CultureInfo.InvariantCulture, $"{i + 1}\tAL\t{990001 + i}\t{athlete.Surname}\t{athlete.FirstName}\t{(athlete.Gender == Gender.Female ? "W" : "M")}\t{athlete.Year}-01-01\tFIN\t\t{athlete.Club}\t\tA");
                points.AppendLine(CultureInfo.InvariantCulture, $"{i + 1}\t465\t{i + 1}\tSL\t{(18 + i * 3.71m):0.00}\t{i + 1}\t\t2026-09-22");
            }
            Write(zip, "AL1327com.csv", competitors.ToString());
            Write(zip, "AL1327pts.csv", points.ToString());
        }
        return output.ToArray();
    }
}
