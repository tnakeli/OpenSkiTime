using OpenSkiTime.Domain;
using OpenSkiTime.Timing;
using Xunit;

namespace OpenSkiTime.Tests;

// Drag preview: "what would this competitor get if this recorded impulse were assigned to them".
// Every expected value is also checked against Replay after really applying the assignment.
public sealed class TimingAssignmentPreviewTests
{
    private static readonly Guid Session = Guid.NewGuid();

    [Theory]
    [InlineData("10:32:15.4200", "10:33:28.7300", 7331, "1:13.31")]
    [InlineData("12:15:10.1200", "12:16:24.8700", 7475, "1:14.75")]
    [InlineData("12:00:00.9999", "12:01:02.9998", 6199, "1:01.99")] // subtract on full scale, then truncate
    public void FinishPreviewIsDraggedTimestampMinusStart(string start, string dragged, long hundredths, string formatted)
    {
        var (list, bibs) = List(0);
        var startPulse = Pulse("s", 0, start);
        var finish = Pulse("f", 1, dragged);
        var snapshot = Replay(list, [startPulse, finish], (startPulse, bibs[0]));

        var preview = TimingEngine.PreviewAssignment(snapshot, bibs[0], "f", 0, 1);

        Assert.Equal(new AssignmentPreview(bibs[0], 1, hundredths, null), preview);
        Assert.Equal(formatted, TimingTime.Format(preview.Hundredths));
        var applied = Replay(list, [startPulse, finish], (startPulse, bibs[0]), (finish, bibs[0]));
        Assert.Equal(preview.Hundredths, applied.Results.Single(x => x.Bib == bibs[0]).Hundredths);
    }

    [Fact]
    public void IntermediatePreviewsMeasureFromStartForEveryConfiguredPoint()
    {
        var (list, bibs) = List(3);
        var start = Pulse("s", 0, "10:00:00.0000");
        var i1 = Pulse("i1", 2, "10:00:25.0000");
        var i2 = Pulse("i2", 3, "10:00:52.5000");
        var i3 = Pulse("i3", 4, "10:01:10.0100");
        var finish = Pulse("f", 1, "10:01:30.0000");
        var observations = new[] { start, i1, i2, i3, finish };
        var snapshot = Replay(list, observations, (start, bibs[0]), (i1, bibs[0]));

        var first = TimingEngine.PreviewAssignment(snapshot, bibs[0], "i1", 0, 1);
        var second = TimingEngine.PreviewAssignment(snapshot, bibs[0], "i2", 0, 1);
        var third = TimingEngine.PreviewAssignment(snapshot, bibs[0], "i3", 0, 1);

        Assert.Equal(new AssignmentPreview(bibs[0], 2, 2500, null), first);
        Assert.Equal(new AssignmentPreview(bibs[0], 3, 5250, null), second); // not 27.50 from Intermediate 1
        Assert.Equal(new AssignmentPreview(bibs[0], 4, 7001, null), third);
        var applied = Replay(list, observations, (start, bibs[0]), (i1, bibs[0]), (i2, bibs[0]), (i3, bibs[0]));
        Assert.Equal(new long[] { 2500, 5250, 7001 }, applied.Results.Single(x => x.Bib == bibs[0]).Splits.Select(x => x.Hundredths!.Value));
    }

    [Fact]
    public void SameTimestampGivesEachCompetitorTheirOwnTime()
    {
        var (list, bibs) = List(1);
        var a = Pulse("a", 0, "10:04:00.0000");
        var b = Pulse("b", 0, "10:04:02.6500");
        var finish = Pulse("f", 1, "10:04:32.5210");
        var snapshot = Replay(list, [a, b, finish], (a, bibs[0]), (b, bibs[1]));

        Assert.Equal(3252, TimingEngine.PreviewAssignment(snapshot, bibs[0], "f", 0, 1).Hundredths);
        Assert.Equal(2987, TimingEngine.PreviewAssignment(snapshot, bibs[1], "f", 0, 1).Hundredths);
        Assert.Equal("No start time", TimingEngine.PreviewAssignment(snapshot, bibs[2], "f", 0, 1).Problem);
    }

    [Fact]
    public void MissingOrAmbiguousStartGivesNoCalculatedTime()
    {
        var (list, bibs) = List(1);
        var s1 = Pulse("s1", 0, "10:00:00.0000");
        var s2 = Pulse("s2", 0, "10:00:01.0000");
        var interm = Pulse("i1", 2, "10:00:20.0000");
        var snapshot = Replay(list, [s1, s2, interm], (s1, bibs[1]), (s2, bibs[1]));

        Assert.Equal(new AssignmentPreview(bibs[0], 2, null, "No start time"), TimingEngine.PreviewAssignment(snapshot, bibs[0], "i1", 0, 1));
        Assert.Equal(new AssignmentPreview(bibs[1], 2, null, "No start time: multiple start impulses"),
            TimingEngine.PreviewAssignment(snapshot, bibs[1], "i1", 0, 1));
    }

    [Fact]
    public void InvalidElapsedTimesAreReportedInsteadOfARaceTime()
    {
        var (list, bibs) = List(1);
        var start = Pulse("s", 0, "10:00:10.0000");
        var finish = Pulse("f", 1, "10:00:40.0000");
        var early = Pulse("early", 1, "10:00:09.9990");
        var same = Pulse("same", 2, "10:00:10.0000");
        var tooClose = Pulse("close", 1, "10:00:10.0099");
        var otherClock = Pulse("clock", 1, "10:00:30.0000", clock: "reconnected");
        var late = Pulse("late", 1, "12:00:10.0001");
        var coarse = Pulse("coarse", 1, "10:00:30.10", precision: 2);
        var afterFinish = Pulse("after", 2, "10:00:41.0000");
        var observations = new[] { start, finish, early, same, tooClose, otherClock, late, coarse, afterFinish };
        var snapshot = Replay(list, observations, (start, bibs[0]), (finish, bibs[0]));

        string? Problem(string key) => TimingEngine.PreviewAssignment(snapshot, bibs[0], key, 0, 1) is var preview
            && preview.Hundredths is null ? preview.Problem : "calculated";
        Assert.Equal("Invalid: time is before start", Problem("early"));
        Assert.Equal("Invalid: time is not after start", Problem("same"));
        Assert.Equal("Invalid: time is not after start", Problem("close"));
        Assert.Equal("Invalid: different device clock", Problem("clock"));
        Assert.Equal("Invalid: over two hours after start", Problem("late"));
        Assert.Equal("Invalid: device precision too low", Problem("coarse"));
        Assert.Equal("Invalid: time is after finish", Problem("after"));
        // Dragging onto the competitor's own finish channel replaces that finish, so no "after finish" check applies.
        Assert.Equal(3000, TimingEngine.PreviewAssignment(snapshot, bibs[0], "f", 0, 1).Hundredths);
        // The same rules flag Review when the impulse is really assigned.
        var applied = Replay(list, observations, (start, bibs[0]), (early, bibs[0]));
        Assert.Equal(TimingStatus.Review, applied.Results.Single(x => x.Bib == bibs[0]).Status);
    }

    [Fact]
    public void StartTimestampsAndNonImpulsesHaveNoElapsedPreview()
    {
        var (list, bibs) = List(0);
        var start = Pulse("s", 0, "10:00:00.0000");
        var snapshot = Replay(list, [start], (start, bibs[0]));

        Assert.Equal(new AssignmentPreview(bibs[1], 0, null, "Start timestamp"), TimingEngine.PreviewAssignment(snapshot, bibs[1], "s", 0, 1));
        Assert.Equal(new AssignmentPreview(bibs[1], null, null, "Not a timing impulse"), TimingEngine.PreviewAssignment(snapshot, bibs[1], "missing", 0, 1));
    }

    [Fact]
    public void PreviewDoesNotModifyTimingData()
    {
        var (list, bibs) = List(2);
        var start = Pulse("s", 0, "10:00:00.0000");
        var interm = Pulse("i2", 3, "10:00:40.0000");
        var finish = Pulse("f", 1, "10:01:00.0000");
        var snapshot = Replay(list, [start, interm, finish], (start, bibs[0]), (finish, bibs[1]));
        var observations = snapshot.Observations.ToArray();
        var results = snapshot.Results.ToArray();
        var audit = snapshot.Audit.ToArray();

        foreach (var bib in bibs)
        {
            foreach (var key in new[] { "s", "i2", "f" }) { TimingEngine.PreviewAssignment(snapshot, bib, key, 0, 1); }
        }

        Assert.Equal(observations, snapshot.Observations);
        Assert.Equal(results, snapshot.Results);
        Assert.Equal(audit, snapshot.Audit);
        Assert.Equal(bibs[1], snapshot.Observations.Single(x => x.Observation.Key == "f").Bib);
        Assert.Null(snapshot.Observations.Single(x => x.Observation.Key == "i2").Bib);
    }

    private static (StartListRevision List, int[] Bibs) List(int intermediates)
    {
        var list = TimingRulesTests.List();
        list = list with { Plan = list.Plan with { Competition = list.Plan.Competition with { IntermediateCount = intermediates } } };
        return (list, list.Plan.Entries.OrderBy(x => x.Position).Select(x => x.Bib).ToArray());
    }

    private static TimingObservation Pulse(string key, int channel, string time, int precision = 4, string clock = "clock-1")
    {
        Assert.True(TimingTime.TryTimeOfDay(time, out var ticks, out _));
        return new(key, Session, 0, "Synthetic", "fingerprint-" + key, ObservationKind.Impulse, channel, ticks,
            precision, null, false, clock, key);
    }

    private static TimingSnapshot Replay(StartListRevision list, TimingObservation[] observations,
        params (TimingObservation Observation, int Bib)[] assignments) =>
        TimingEngine.Replay(list, observations, assignments.Select((x, i) => new TimingAudit(i + 1, list.Id,
            TimingRulesTests.At, "Test operator", "Synthetic assignment", new(DecisionKind.Assignment, x.Observation.Key),
            new(DecisionKind.Assignment, x.Observation.Key, Bib: x.Bib))).ToArray(), 0, 1);
}
