using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
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
    private static (bool Visible, string Bib, string Result, bool Invalid) HoverTimingRacer(TimingView view, TimingDragCompetitor racer, string timestampKey)
    {
        var vm = Assert.IsType<MainViewModel>(view.DataContext);
        var row = vm.TimestampRows.Single(x => x.Cells.Any(c => c?.Key == timestampKey));
        view.FindControl<DataGrid>("TimestampsGrid")!.ScrollIntoView(row, null);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        ((Window)view.GetVisualRoot()!).UpdateLayout();
        var target = view.FindControl<DataGrid>("TimestampsGrid")!.GetVisualDescendants().OfType<Border>()
            .First(x => x.Tag is TimingTimestampCell cell && cell.Key == timestampKey && x.Bounds.Width > 0);
        var data = new DataObject(); data.Set(TimingView.CompetitorDragFormat, racer);
        var over = new DragEventArgs(DragDrop.DragOverEvent, data, target, new Avalonia.Point(2, 2), KeyModifiers.None) { Source = target };
        target.RaiseEvent(over);
        Assert.Equal(DragDropEffects.Move, over.DragEffects); // the preview never changes which cells accept a drop
        var preview = view.FindControl<Border>("TimingDropPreview")!;
        return (preview.IsVisible, view.FindControl<TextBlock>("TimingDropPreviewBib")!.Text ?? "",
            view.FindControl<TextBlock>("TimingDropPreviewResult")!.Text ?? "", preview.Classes.Contains("invalid"));
    }

    [AvaloniaFact]
    public async Task DraggingACompetitorOverTimestampsPreviewsTheResultingTimeWithoutChangingTiming()
    {
        var root = Path.Combine(Path.GetTempPath(), "openskitime-drop-preview-ui", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var file = Path.Combine(root, "Synthetic-Drop-Preview.ost");
            var cache = new FisLocalStore(root);
            await cache.SaveListAsync(SyntheticFisArchive());
            await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore(), new AlgeDecoderFactory());
            var date = new DateOnly(2026, 9, 27);
            var series = await workspace.CreateAsync(file, new("Synthetic race weekend", "Test slope", "Test club", date, date, "FIN", "2026/27"));
            series = await workspace.SaveCompetitionAsync(null, new("Synthetic Slalom", "SL1", date, Discipline.Slalom, RaceType.Fis, 2, 2, "1234"), series.Revision);
            var competition = series.Competitions[0];
            var revision = series.Revision;
            revision = (await workspace.SaveCategoryRuleAsync(null, new("Test 2000", 2000, 2000, null, 0), revision)).Revision;
            for (var i = 0; i < 4; i++)
            {
                revision = (await workspace.SaveDeskRowAsync(null, new($"TEST{i:00}", "Athlete", 2000, $"{123456 + i}", "FIN", "Synthetic club", Gender.Female),
                    competition.Id, true, null, revision)).Revision;
            }
            var preferences = new TimingPreferencesStore(root);
            using var vm = new MainViewModel(workspace, new FileDialogsStub { OpenPath = file, NewPath = file, BackupPath = file + ".backup" }, fisStore: cache,
                recentSeriesStore: new RecentSeriesStore(root), timingPreferencesStore: preferences);
            await vm.OpenSeriesCommand.ExecuteAsync(null);
            await vm.OpenDrawRunCommand.ExecuteAsync(new DrawDestination(competition, 1));
            await vm.PrepareDrawCommand.ExecuteAsync(null);
            await vm.OpenTimingRunCommand.ExecuteAsync(new DrawDestination(competition, 1));
            Assert.False(vm.IsError, vm.StatusMessage);
            var window = new MainWindow { DataContext = vm, WindowState = WindowState.Normal, Width = 1366, Height = 850 };
            window.Show();
            var view = window.FindControl<TimingView>("TimingWorkspace")!;
            vm.ShowSettingsCommand.Execute(null);
            SelectTimingSettingsTab(window);
            vm.TimingSource = "Simulator"; vm.TimingIntermediateChannels = "2,3";
            Click(window, "Save timing settings"); await vm.SaveTimingPreferencesCommand.ExecutionTask!;
            Click(window, "Connect"); await vm.ConnectTimingCommand.ExecutionTask!;
            Click(window, "Back to timing"); await vm.ReturnToTimingCommand.ExecutionTask!;
            Assert.False(vm.IsError, vm.StatusMessage);
            foreach (var channel in new[] { "start", "finish", "intermediate:1", "intermediate:2" })
            { await vm.ToggleTimingChannelCommand.ExecuteAsync(channel); }

            async Task Pulse(string button, string at, Func<bool> done)
            {
                vm.SimulationTime = at;
                Click(window, button); await vm.SimulatePulseCommand.ExecutionTask!;
                await WaitTimingAsync(vm, done);
            }
            await Pulse("Test start", "12:00:00.0000", () => vm.OnCourseRows.Count == 1);
            var a = Assert.Single(vm.OnCourseRows).Bib;
            await Pulse("Test I1", "12:00:25.0000", () => vm.TimestampRows.SelectMany(x => x.Cells).Any(x => x?.Channel == 2));
            await Pulse("Test start", "12:00:30.0000", () => vm.OnCourseRows.Count == 2);
            var b = vm.OnCourseRows.Single(x => x.Bib != a).Bib;
            var c = vm.TimingRows.First(x => x.Bib != a && x.Bib != b).Bib;
            await Pulse("Test I2", "12:00:52.5000", () => vm.TimestampRows.SelectMany(x => x.Cells).Any(x => x?.Channel == 3));
            await Pulse("Test finish", "12:01:13.3100", () => vm.TimestampRows.SelectMany(x => x.Cells).Any(x => x?.Channel == 1));
            string Key(int channel) => vm.TimestampRows.SelectMany(x => x.Cells).OfType<TimingTimestampCell>().Single(x => x.Channel == channel).Key;
            var (i1, i2, finish, start) = (Key(2), Key(3), Key(1), vm.TimestampRows.SelectMany(x => x.Cells).OfType<TimingTimestampCell>().First(x => x.Channel == 0).Key);
            var before = workspace.Timing!.Snapshot!;
            var preview = view.FindControl<Border>("TimingDropPreview")!;
            Assert.False(preview.IsVisible);

            // Each intermediate and the finish are measured from the competitor's own start, never from the previous split.
            Assert.Equal((true, $"Bib: {a}", "Intermediate 1: 0:25.00", false), HoverTimingRacer(view, vm.CreateTimingDrag(a)!, i1));
            Assert.Equal((true, $"Bib: {a}", "Intermediate 2: 0:52.50", false), HoverTimingRacer(view, vm.CreateTimingDrag(a)!, i2));
            Assert.Equal((true, $"Bib: {a}", "Finish: 1:13.31", false), HoverTimingRacer(view, vm.CreateTimingDrag(a)!, finish));
            // The same timestamp gives another competitor their own time.
            Assert.Equal((true, $"Bib: {b}", "Finish: 0:43.31", false), HoverTimingRacer(view, vm.CreateTimingDrag(b)!, finish));
            Assert.Equal((true, $"Bib: {b}", "Intermediate 2: 0:22.50", false), HoverTimingRacer(view, vm.CreateTimingDrag(b)!, i2));
            Assert.Equal((true, $"Bib: {b}", "Invalid: time is before start", true), HoverTimingRacer(view, vm.CreateTimingDrag(b)!, i1));
            Assert.Equal((true, $"Bib: {c}", "No start time", true), HoverTimingRacer(view, vm.CreateTimingDrag(c)!, finish));
            // A start cell remains a drop target but has no elapsed-time preview.
            Assert.False(HoverTimingRacer(view, vm.CreateTimingDrag(c)!, start).Visible);
            Assert.True(HoverTimingRacer(view, vm.CreateTimingDrag(b)!, i2).Visible);

            // Cancelling (leaving the view) hides the preview and changes nothing.
            var leave = new DragEventArgs(DragDrop.DragLeaveEvent, new DataObject(), view, new Avalonia.Point(0, 0), KeyModifiers.None) { Source = view };
            view.RaiseEvent(leave);
            Assert.False(preview.IsVisible);
            Assert.Same(before, workspace.Timing.Snapshot); // hovering wrote no audit, decision or observation

            // A normal drop still uses the existing assignment path and produces exactly the previewed time.
            Assert.True(HoverTimingRacer(view, vm.CreateTimingDrag(b)!, i2).Visible);
            await DropTimingRacer(view, vm.CreateTimingDrag(b)!, i2);
            Assert.False(preview.IsVisible);
            Assert.True(workspace.Timing.Snapshot!.AuditVersion > before.AuditVersion);
            Assert.Equal("0:22.50", workspace.Timing.Snapshot.Results.Single(x => x.Bib == b).Splits[1].Time);
            Assert.Equal(b, workspace.Timing.Snapshot.Observations.Single(x => x.Observation.Key == i2).Bib);
        }
        finally
        {
            if (Path.GetFullPath(root).StartsWith(Path.Combine(Path.GetTempPath(), "openskitime-drop-preview-ui") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            { Directory.Delete(root, true); }
        }
    }
}
