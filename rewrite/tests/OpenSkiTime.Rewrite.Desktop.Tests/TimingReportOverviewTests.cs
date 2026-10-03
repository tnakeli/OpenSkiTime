using System.Text;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using OpenSkiTime.Rewrite.Application;
using OpenSkiTime.Rewrite.Desktop;
using OpenSkiTime.Rewrite.Devices;
using OpenSkiTime.Rewrite.Domain;
using OpenSkiTime.Rewrite.Timing;
using Xunit;

namespace OpenSkiTime.Rewrite.Tests;

public partial class DesktopWorkflowTests
{
    [Fact]
    public void KeyboardATimestampsExplainMissingBestWithoutChangingMeasuredTime()
    {
        var date = new DateOnly(2026, 10, 3);
        var at = new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);
        var competition = new CompetitionValues("Synthetic", "DH", date, Discipline.Downhill, RaceType.Fis, 1, 0, "9991");
        var athlete = new CompetitorValues("SYNTHETIC", "Racer", 2000, "900001", "FIN", "Test", Gender.Male);
        var plan = FisStartOrder.FirstRun(Guid.NewGuid(), competition, Gender.Male, [new(Guid.NewGuid(), athlete, 10)],
            new("1327", date, date), new(), "keyboard-timing");
        var list = new StartListRevision(Guid.NewGuid(), 1, at, null, "Operator", "Synthetic draw", plan);
        var capture = new CaptureSession(Guid.NewGuid(), list.Id, new("Synthetic", "Test", date), at, at.AddMinutes(1), true);
        var packet = new RawTimingPacket(capture.Id, 1, at, "alge-ascii/v1", "Synthetic", "1",
            Encoding.ASCII.GetBytes("0001 C0M 12:00:00.24\r0002 C1M 12:00:01.52\r"));
        var decoders = new AlgeDecoderFactory();
        var observations = decoders.Create(capture, packet.Protocol, packet.Source, packet.Stream).Feed(packet);
        var audit = observations.Select((o, index) => new TimingAudit(index + 1, list.Id, at, "Operator", "Synthetic assignment",
            new(DecisionKind.Assignment, o.Key), new(DecisionKind.Assignment, o.Key, plan.Entries[0].Entrant.CompetitorId, plan.Entries[0].Bib))).ToArray();
        var data = new TimingReplayData(list, [capture], [packet], audit);
        var snapshot = TimingReplay.Restore(data, decoders);
        Assert.Equal(128, Assert.Single(snapshot.Results).Hundredths);
        var source = TimingReportProjection.FromTiming(data, snapshot);
        var editor = TimingReportRunEditor.FromTiming(source, snapshot);
        Assert.Null(source.BestBib);
        Assert.NotNull(source.First.AStart); Assert.NotNull(source.First.AFinish);
        Assert.Contains("manual", editor.Best, StringComparison.Ordinal);
        Assert.Contains("C0M/C1M", editor.BestExplanation, StringComparison.Ordinal);
        Assert.Single(source.MissedA);
    }

    [AvaloniaFact]
    public void OverviewKeepsLevelAndSynchronizationHelpInTooltips()
    {
        using var vm = new MainViewModel(new SeriesWorkspace(new OpenSkiTime.Rewrite.Persistence.SqliteSeriesFileStore()),
            new FileDialogsStub { NewPath = "unused", OpenPath = "unused", BackupPath = "unused" });
        var view = new TimingReportView { DataContext = vm };
        var window = new Window { Content = view, Width = 1280, Height = 800 };
        window.Show(); window.UpdateLayout();
        try
        {
            var level = view.FindControl<ComboBox>("TimingReportLevel")!;
            var help = Assert.IsType<TextBlock>(ToolTip.GetTip(level));
            Assert.Contains("0 -", help.Text, StringComparison.Ordinal);
            Assert.Contains("1 -", help.Text, StringComparison.Ordinal);
            Assert.Contains("2 - National Championships", help.Text, StringComparison.Ordinal);
            Assert.Contains("3 -", help.Text, StringComparison.Ordinal);
            Assert.Contains("4 -", help.Text, StringComparison.Ordinal);
            var synchronization = view.FindControl<WrapPanel>("ReportSynchronizationFields")!;
            var syncHelp = Assert.IsType<TextBlock>(ToolTip.GetTip(synchronization));
            Assert.Contains("0.001 s", syncHelp.Text, StringComparison.Ordinal);
            Assert.DoesNotContain(view.GetVisualDescendants().OfType<TextBlock>(), t => t.IsEffectivelyVisible
                && t.Text is { } text && text.StartsWith("Use the common-impulse check", StringComparison.Ordinal));
        }
        finally { window.Close(); }
    }
}
