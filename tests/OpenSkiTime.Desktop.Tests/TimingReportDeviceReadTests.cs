using System.Text;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
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
    [AvaloniaFact]
    public async Task TimingReportDeviceReadUsesSharedReviewWritesNothingUntilOkAndKeepsSettings()
    {
        var folder = Path.Combine(Path.GetTempPath(), "openskitime-report-device", Guid.NewGuid().ToString("N"));
        var preferences = Path.Combine(folder, "preferences");
        Directory.CreateDirectory(preferences);
        try
        {
            var file = Path.Combine(folder, "report.ost");
            var date = new DateOnly(2026, 10, 3);
            await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore(), new AlgeDecoderFactory());
            var (competition, list) = await CreateTimedReportSeriesAsync(workspace, file, date);
            // Permanent role settings, including a B Clock. Reading a device in the report dialog must leave them unchanged.
            var store = new TimingPreferencesStore(preferences);
            var simulator = new TimingConnection(TimingSourceType.Simulator);
            var backup = new TimingConnection(TimingSourceType.Mt1Serial) { Port = "COM5" };
            store.Save(new TimingRoleConfiguration([new(TimingRole.Start, simulator, 0), new(TimingRole.Finish, simulator, 1),
                new(TimingRole.BackupStart, backup, 0), new(TimingRole.BackupFinish, backup, 1)]));
            using var vm = new MainViewModel(workspace, new FileDialogsStub { OpenPath = file, NewPath = file, BackupPath = file + ".backup" },
                fisStore: new FisLocalStore(folder), recentSeriesStore: new RecentSeriesStore(folder),
                timingPreferencesStore: store, reportDefaultsStore: new TimingReportDefaultsStore(folder));
            await vm.OpenSeriesCommand.ExecuteAsync(null);
            vm.TimingOperator = "Synthetic operator";
            await vm.SelectReportCompetitionAsync(competition);
            vm.ReportLevel = 3;
            Assert.True(await vm.FlushTimingReportAsync(), vm.StatusMessage);
            var preferenceFiles = SnapshotFiles(preferences);
            var savedBefore = (await workspace.ReadTimingReportAsync(competition.Id))!;

            var replay = Path.Combine(folder, "b-clock.txt");
            await File.WriteAllTextAsync(replay, "TIMY: synthetic\r\n0001 C0 12:00:00.0123\r\n0002 C1 12:01:00.0456\r\nnot a timing line\r\n"
                + "0003 C0 12:02:00.0010\r\n0004 C1 12:03:00.0020\r\n", Encoding.ASCII);
            vm.ResetReportImport();
            vm.ReportImportRun = 1; vm.ReportImageRole = TimingReportImageRole.B;
            vm.ReportInputMode = MainViewModel.ReportDeviceInputMode;
            Assert.Equal("2026-10-03", vm.ReportDeviceDate);
            vm.ReportDeviceSource = TimingSourceTypes.ReplayFileLabel; vm.ReportDeviceReplayPath = replay;
            await vm.ReadReportDeviceCommand.ExecuteAsync(null);
            await vm.ReportDeviceReadTask;
            Assert.False(vm.IsError, vm.StatusMessage);
            Assert.False(vm.IsReportDeviceReading);
            Assert.Equal(4, vm.ReportImportPreview.Count(x => x.CanAccept));
            Assert.All(vm.ReportImportPreview, x => Assert.Equal(TimingReportImageRole.B, x.Role));
            var proposed = vm.ReportImportPreview.Single(x => x.Channel == 0 && x.Proposed.StartsWith("12:00", StringComparison.Ordinal));
            Assert.Equal("12:00:00.0123", proposed.Proposed); Assert.Equal("0001 C0 12:00:00.0123", proposed.SourceText);
            Assert.StartsWith("device:Replay file:Replay:", proposed.Stamp!.SourceReference, StringComparison.Ordinal);
            Assert.Contains(vm.ReportDeviceObservations, x => x.RawText.Contains("not a timing line", StringComparison.Ordinal));
            Assert.All(vm.ReportDeviceObservations, x => Assert.StartsWith("device:Replay file:Replay:", x.Provenance, StringComparison.Ordinal));
            await AssertReportUnchangedAsync();

            // Cancel / close discards everything read in the dialog.
            var dialog = new TimingReceiptDialog { DataContext = vm };
            dialog.Show(); dialog.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            Assert.True(dialog.FindControl<Grid>("ReportDevicePanel")!.IsVisible);
            dialog.Close(); Dispatcher.UIThread.RunJobs();
            Assert.Empty(vm.ReportImportPreview); Assert.Empty(vm.ReportDeviceObservations);
            Assert.Equal(MainViewModel.ReportImageInputMode, vm.ReportInputMode);
            await AssertReportUnchangedAsync();

            // Hand start uses the same workflow with one channel; the role fixes the timing position.
            var hand = Path.Combine(folder, "hand-start.txt");
            await File.WriteAllTextAsync(hand, "0001 C2 12:00:00.12\r\n0002 C2 12:02:00.15\r\n", Encoding.ASCII);
            vm.ResetReportImport();
            vm.ReportImportRun = 1; vm.ReportImageRole = TimingReportImageRole.HandStart;
            vm.ReportInputMode = MainViewModel.ReportDeviceInputMode;
            vm.ReportDeviceSource = TimingSourceTypes.ReplayFileLabel; vm.ReportDeviceReplayPath = hand; vm.ReportDeviceHandChannel = 2;
            await vm.ReadReportDeviceCommand.ExecuteAsync(null);
            await vm.ReportDeviceReadTask;
            Assert.False(vm.IsError, vm.StatusMessage);
            Assert.Equal(2, vm.ReportImportPreview.Count(x => x.CanAccept));
            Assert.All(vm.ReportImportPreview, x => { Assert.Equal(0, x.Channel); Assert.Equal(TimingReportImageRole.HandStart, x.Role); });
            vm.ResetReportImport();
            await AssertReportUnchangedAsync();

            // Only OK applies the reviewed selection, with device provenance in the audit reason.
            vm.ReportImportRun = 1; vm.ReportImageRole = TimingReportImageRole.B;
            vm.ReportInputMode = MainViewModel.ReportDeviceInputMode;
            vm.ReportDeviceSource = TimingSourceTypes.ReplayFileLabel; vm.ReportDeviceReplayPath = replay;
            await vm.ReadReportDeviceCommand.ExecuteAsync(null);
            await vm.ReportDeviceReadTask;
            vm.ReportImportPreview.Last(x => x.Channel == 1).Accept = false;
            vm.ReportImportVerified = true;
            await vm.AcceptReportImageMatchesCommand.ExecuteAsync(null);
            Assert.False(vm.IsError, vm.StatusMessage);
            var accepted = (await workspace.ReadTimingReportAsync(competition.Id))!;
            Assert.Equal(3, accepted.Values.Associations.Count(x => x.Role == TimingReportImageRole.B));
            var startStamp = accepted.Values.Associations.Single(x => x.Role == TimingReportImageRole.B && x.Channel == 0
                && x.Stamp.Ticks == date.ToDateTime(new TimeOnly(12, 0)).Ticks + 123_000).Stamp;
            Assert.Equal(4, startStamp.Precision); Assert.True(startStamp.Verified);
            Assert.Equal("Operator verified device import (Replay file, b-clock.txt): " + vm.ReportChangeReason, accepted.Reason);
            Assert.Empty((await workspace.Auxiliary!.ReadAsync(list.Id)).Packets);
            Assert.Equal(preferenceFiles, SnapshotFiles(preferences));
            vm.ResetReportImport();

            // A device used by live capture is refused before the endpoint is opened.
            var reportRevision = accepted.Revision;
            await workspace.Timing!.SelectRunAsync(list.Id);
            await workspace.Timing.StartAsync(new SimulatorTimingSource(), new(TimingSourceTypes.Mt1SerialLabel, "COM9", date, Simulation: true), "Operator");
            vm.ReportImportRun = 1; vm.ReportImageRole = TimingReportImageRole.B;
            vm.ReportInputMode = MainViewModel.ReportDeviceInputMode;
            vm.ReportDeviceSource = TimingSourceTypes.Mt1SerialLabel; vm.ReportDevicePort = "COM9";
            await vm.ReadReportDeviceCommand.ExecuteAsync(null);
            Assert.True(vm.IsError); Assert.Contains("live timing (A)", vm.ReportImportStatus, StringComparison.Ordinal);
            Assert.False(vm.IsReportDeviceReading); Assert.Empty(vm.ReportDeviceObservations);
            await workspace.Timing.StopAsync();
            await workspace.Auxiliary.StartAsync(list.Id, AuxiliaryTimingRole.B, new SimulatorTimingSource(),
                new(TimingSourceTypes.Mt1SerialLabel, "COM8", date), "Operator", live: true);
            try
            {
                vm.ReportDevicePort = "COM8";
                await vm.ReadReportDeviceCommand.ExecuteAsync(null);
                Assert.True(vm.IsError); Assert.Contains("live B Clock", vm.ReportImportStatus, StringComparison.Ordinal);
                Assert.False(vm.IsReportDeviceReading);
            }
            finally { await workspace.Auxiliary.StopAsync(AuxiliaryTimingRole.B); }
            vm.ResetReportImport();
            Assert.Equal(reportRevision, (await workspace.ReadTimingReportAsync(competition.Id))!.Revision);
            Assert.Equal(preferenceFiles, SnapshotFiles(preferences));

            async Task AssertReportUnchangedAsync()
            {
                var saved = (await workspace.ReadTimingReportAsync(competition.Id))!;
                Assert.Equal(savedBefore.Revision, saved.Revision);
                Assert.Equal(JsonSerializer.Serialize(savedBefore.Values.Associations), JsonSerializer.Serialize(saved.Values.Associations));
                var auxiliary = await workspace.Auxiliary!.ReadAsync(list.Id);
                Assert.Empty(auxiliary.Sessions); Assert.Empty(auxiliary.Packets);
                Assert.Empty(await workspace.ReadTimingReportImagesAsync(competition.Id));
                Assert.All(vm.ReportEvidence, x => { Assert.Empty(x.BStart); Assert.Empty(x.BFinish); Assert.Empty(x.HandStart); });
                Assert.Equal(preferenceFiles, SnapshotFiles(preferences));
            }
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [AvaloniaFact]
    public void TimingObservationDialogRendersImageAndDeviceInputs()
    {
        using var vm = new MainViewModel(new SeriesWorkspace(new SqliteSeriesFileStore()),
            new FileDialogsStub { NewPath = "unused", OpenPath = "unused", BackupPath = "unused" },
            timingPreferencesStore: new TimingPreferencesStore(Path.Combine(Path.GetTempPath(), "openskitime-unused-" + Guid.NewGuid().ToString("N"))));
        var window = new TimingReceiptDialog { DataContext = vm };
        window.Show(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
        try
        {
            Assert.Equal("Read timing observations", window.Title);
            Assert.Equal(MainViewModel.ReportImageInputMode, vm.ReportInputMode);
            var imagePanel = window.FindControl<Grid>("ReportImagePanel")!;
            var devicePanel = window.FindControl<Grid>("ReportDevicePanel")!;
            Assert.True(imagePanel.IsVisible); Assert.False(devicePanel.IsVisible);
            Assert.True(window.FindControl<Button>("OpenReceiptImagesButton")!.IsVisible);

            window.FindControl<ComboBox>("ReportInputModePicker")!.SelectedItem = MainViewModel.ReportDeviceInputMode;
            window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            Assert.Equal(MainViewModel.ReportDeviceInputMode, vm.ReportInputMode);
            Assert.False(imagePanel.IsVisible); Assert.True(devicePanel.IsVisible); Assert.True(devicePanel.Bounds.Width >= 240);
            Assert.False(window.FindControl<Button>("OpenReceiptImagesButton")!.IsVisible);
            Assert.Equal(TimingSourceTypes.DeviceRead.Select(TimingSourceTypes.Label), vm.ReportDeviceSources);
            Assert.True(window.FindControl<StackPanel>("ReportDeviceUsbFields")!.IsVisible);
            Assert.False(window.FindControl<StackPanel>("ReportDeviceSerialFields")!.IsVisible);
            Assert.True(window.FindControl<ComboBox>("ReportDeviceStartChannelPicker")!.IsEffectivelyVisible);
            Assert.False(window.FindControl<ComboBox>("ReportDeviceHandChannelPicker")!.IsEffectivelyVisible);
            vm.ReportDeviceSource = TimingSourceTypes.ReplayFileLabel;
            vm.ReportImageRole = TimingReportImageRole.HandFinish;
            window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            Assert.True(window.FindControl<StackPanel>("ReportDeviceReplayFields")!.IsVisible);
            Assert.False(window.FindControl<StackPanel>("ReportDeviceUsbFields")!.IsVisible);
            Assert.False(window.FindControl<StackPanel>("ReportDeviceAlgeFields")!.IsVisible);
            Assert.True(window.FindControl<ComboBox>("ReportDeviceHandChannelPicker")!.IsEffectivelyVisible);
            Assert.Equal(1, vm.ReportDeviceHandChannel);
            Assert.True(window.FindControl<Button>("ReadReportDeviceButton")!.IsEffectivelyEnabled);
            Assert.False(window.FindControl<Button>("StopReportDeviceButton")!.IsEffectivelyEnabled);
            Assert.Equal(["TIME", "POSITION", "KIND", "RAW", "PROVENANCE"],
                window.FindControl<DataGrid>("ReportDeviceGrid")!.Columns.Select(x => (string)x.Header));
            var output = Environment.GetEnvironmentVariable("OPENSKITIME_REPORT_VISUAL_DIR");
            if (!string.IsNullOrWhiteSpace(output))
            {
                Directory.CreateDirectory(output);
                using var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(new Avalonia.PixelSize((int)window.Width, (int)window.Height), new Avalonia.Vector(96, 96));
                bitmap.Render(window); bitmap.Save(Path.Combine(output, "timing-observation-device-dialog.png"));
            }
        }
        finally { window.Close(); }
    }

    private static Dictionary<string, string> SnapshotFiles(string directory) => Directory.Exists(directory)
        ? Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
            .ToDictionary(x => x, x => Convert.ToHexString(File.ReadAllBytes(x)), StringComparer.Ordinal)
        : [];

    private static async Task<(CompetitionDetails Competition, StartListRevision List)> CreateTimedReportSeriesAsync(
        SeriesWorkspace workspace, string file, DateOnly date)
    {
        var values = new CompetitionValues("Synthetic downhill", "DH", date, Discipline.Downhill, RaceType.Fis, 1, 0, "9991");
        var series = await workspace.CreateAsync(file, new("Synthetic report", "Test slope", "Test club", date, date, "FIN", "2026/27"), [values]);
        var competition = series.Competitions[0];
        var revision = series.Revision;
        var entrants = new List<DrawEntrant>();
        for (var i = 1; i <= 2; i++)
        {
            var athlete = new CompetitorValues("SYNTHETIC" + i, "Racer", 2000, "90000" + i, "FIN", "Test", Gender.Male);
            var saved = await workspace.SaveDeskRowAsync(null, athlete, competition.Id, true, null, revision);
            revision = saved.Revision; entrants.Add(new(saved.Value.Id, athlete, i * 10));
        }
        var plan = FisStartOrder.FirstRun(competition.Id, values, Gender.Male, entrants, new("1327", date, date), new(), "report-device");
        var list = Assert.Single((await workspace.SaveStartListAsync(new(plan, revision, "Operator", "Synthetic draw", DateTimeOffset.UtcNow))).Revisions);
        var timing = workspace.Timing!;
        await timing.SelectRunAsync(list.Id);
        var simulator = new SimulatorTimingSource();
        await timing.StartAsync(simulator, new("Synthetic", "Test", date, Simulation: true), "Operator");
        foreach (var entry in plan.Entries)
        {
            var start = TimeSpan.FromHours(12).Ticks + (entry.Position - 1) * 2 * TimeSpan.TicksPerMinute;
            await timing.ArmAsync(entry.Bib, null); await simulator.PulseAsync(0, start);
            await ReportUntil(() => timing.Snapshot!.Results.Single(x => x.Bib == entry.Bib).Status == TimingStatus.OnCourse);
            await timing.ArmAsync(null, entry.Bib); await simulator.PulseAsync(1, start + TimeSpan.TicksPerMinute);
            await ReportUntil(() => timing.Snapshot!.Results.Single(x => x.Bib == entry.Bib).Status == TimingStatus.Finished);
        }
        await timing.StopAsync();
        return (competition, list);
    }
}
