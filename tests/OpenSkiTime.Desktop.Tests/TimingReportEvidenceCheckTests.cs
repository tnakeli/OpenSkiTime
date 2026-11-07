using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OpenSkiTime.Application;
using OpenSkiTime.Desktop;
using Xunit;

namespace OpenSkiTime.Tests;

public sealed class TimingReportEvidenceCheckTests
{
    public sealed class RunsOnly { public ObservableCollection<TimingReportRunEditor> ReportRuns { get; } = []; }

    private static bool Has(Control control, string name) => control.Classes.Any(x => x == name);

    private static TimingReportStamp At(double seconds) => new((long)(seconds * TimeSpan.TicksPerSecond) + TimeSpan.FromHours(12).Ticks, 4);

    private static (TimingReportEvidenceRow First, TimingReportEvidenceRow Last, TimingReportRunEditor Run) Sample()
    {
        TimingReportEvidenceRow Row(int bib, double start) => new() { Run = 1, Bib = bib, Sample = "First", AStartStamp = At(start), AFinishStamp = At(start + 50.23), NetHundredths = 5023 };
        var first = Row(31, 0); var last = Row(69, 600);
        return (first, last, new() { Source = new() { Run = 1 }, First = first, Last = last });
    }

    [Fact]
    public void ATypedTimeIsJudgedAgainstSystemAAsItIsTyped()
    {
        var (first, _, run) = Sample();
        Assert.Null(first.BStartIssue); Assert.False(run.HasEvidenceIssues);
        var changes = new List<string>();
        first.PropertyChanged += (_, e) => changes.Add(e.PropertyName ?? "");
        first.BStart = "12:00:00.0149"; first.BFinish = "12:00:50.2400";
        Assert.Null(first.BStartIssue); Assert.Null(first.BFinishIssue);

        first.BFinish = "12:00:53.2400";   // three seconds late on the finish only
        Assert.Equal(EvidenceSeverity.Error, first.BFinishIssue?.Severity);
        Assert.Contains(nameof(TimingReportEvidenceRow.BFinishIssue), changes);
        Assert.True(run.HasEvidenceError); Assert.False(run.HasEvidenceWarningOnly);
        Assert.Contains("Bib 31 B finish:", run.EvidenceIssues, StringComparison.Ordinal);

        first.BFinish = "12:00:50.4000";   // a few tenths: look at it, do not stop
        Assert.Equal(EvidenceSeverity.Warning, first.BFinishIssue?.Severity);
        Assert.False(run.HasEvidenceError); Assert.True(run.HasEvidenceWarningOnly);

        first.BFinish = "12:00:50.2400";
        Assert.Null(first.BFinishIssue); Assert.False(run.HasEvidenceIssues);
        first.BFinish = "12:00:5"; // unreadable text is reported when saving, not judged here
        Assert.Null(first.BFinishIssue);
    }

    [Fact]
    public void TheSecondFinisherAndHandTimesAreCheckedToo()
    {
        var (_, last, run) = Sample();
        last.HandStart = "12:10:04.0000";
        Assert.Equal(EvidenceSeverity.Error, last.HandStartIssue?.Severity);
        Assert.Contains("Bib 69 hand start:", run.EvidenceIssues, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void TheCellIsMarkedAndExplainsTheMistakeInItsTooltipAndUnderTheRun()
    {
        var (first, _, run) = Sample();
        first.BFinish = "12:00:50.2400";
        var context = new RunsOnly(); context.ReportRuns.Add(run);
        var window = new Window { Width = 1280, Height = 850, Content = new TimingReportView { DataContext = context } };
        window.Show(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
        try
        {
            window.GetVisualDescendants().OfType<TabControl>().Single().SelectedIndex = 1;
            window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            var cells = window.GetVisualDescendants().OfType<TextBox>().Where(x => !x.IsReadOnly && x.IsVisible).ToArray();
            var finish = cells.Single(x => ReferenceEquals(x.DataContext, run) && x.Text == "12:00:50.2400");
            var note = window.GetVisualDescendants().OfType<Border>().Single(x => x.Name == "ReportEvidenceIssues");
            Assert.False(Has(finish, "error")); Assert.False(note.IsVisible);

            first.BFinish = "12:00:53.2400";
            window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            Assert.True(Has(finish, "error")); Assert.False(Has(finish, "warning"));
            Assert.Contains("3.010 s later than A", Assert.IsType<string>(ToolTip.GetTip(finish)), StringComparison.Ordinal);
            Assert.True(note.IsVisible); Assert.True(Has(note, "error"));
            Assert.Contains("Bib 31 B finish", Assert.IsType<TextBlock>(note.Child).Text, StringComparison.Ordinal);

            first.BFinish = "12:00:50.4000";
            window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            Assert.True(Has(finish, "warning")); Assert.False(Has(finish, "error"));
            Assert.True(Has(note, "warning")); Assert.False(Has(note, "error"));

            first.BFinish = "12:00:50.2400";
            window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            Assert.False(Has(finish, "warning")); Assert.False(note.IsVisible);
            Assert.Null(ToolTip.GetTip(finish));
        }
        finally { window.Close(); }
    }
}
