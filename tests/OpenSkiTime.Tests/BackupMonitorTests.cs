using OpenSkiTime.Devices;
using OpenSkiTime.Timing;
using Xunit;

namespace OpenSkiTime.Tests;

public sealed class BackupMonitorTests
{
    [Fact]
    public void MissingSignalsUseExplicitGraceIgnorePriorEventsAndRecoverOnLateB()
    {
        var list = TimingRulesTests.List(1); var capture = TimingRulesTests.Session(list);
        var start = new AlgeAsciiDecoder(capture, "A", "1").Feed(TimingRulesTests.Packet(capture, 1, "0001* C0 12:00:00.0000\r"));
        var audit = new TimingAudit(1, list.Id, TimingRulesTests.At, "Operator", "Source", new(DecisionKind.Assignment, start[0].Key),
            new(DecisionKind.Assignment, start[0].Key, Bib: 1));
        var empty = TimingEngine.Replay(list, [], [], 0, 1);
        var timed = TimingEngine.Replay(list, start, [audit], 0, 1);
        var monitor = new BackupMonitor(); var session = Guid.NewGuid(); var policy = new BackupComparisonPolicy();
        monitor.Update(session, empty, [], TimingRulesTests.At, policy);
        Assert.Empty(monitor.Update(session, timed, [], TimingRulesTests.At, policy).Warnings);
        Assert.Empty(monitor.Update(session, timed, [], TimingRulesTests.At.AddSeconds(4), policy).Warnings);
        Assert.Single(monitor.Update(session, timed, [], TimingRulesTests.At.AddSeconds(5), policy).Warnings);
        var b = start[0] with { Key = "B", DeviceTicks = start[0].DeviceTicks + TimeSpan.TicksPerMillisecond };
        Assert.Empty(monitor.Update(session, timed, [b], TimingRulesTests.At.AddSeconds(6), policy).Warnings);
        Assert.Empty(monitor.Update(null, timed, [], TimingRulesTests.At.AddMinutes(1), policy).Warnings);
        Assert.Empty(monitor.Update(Guid.NewGuid(), timed, [], TimingRulesTests.At.AddMinutes(2), policy).Warnings);
        Assert.Empty(monitor.Update(null, timed, [], TimingRulesTests.At.AddMinutes(3), policy).Rows);
    }

    [Fact]
    public void LargeImpulseAndElapsedDifferencesRemainAfterDisplayExpiresWithoutChangingA()
    {
        var list = TimingRulesTests.List(1); var capture = TimingRulesTests.Session(list);
        var a = new AlgeAsciiDecoder(capture, "A", "1").Feed(TimingRulesTests.Packet(capture, 1,
            "0001* C0 12:00:00.0000\r0001* C1 12:01:00.0000\r"));
        var audit = a.Select((x, i) => new TimingAudit(i + 1, list.Id, TimingRulesTests.At, "Operator", "Source",
            new(DecisionKind.Assignment, x.Key), new(DecisionKind.Assignment, x.Key, Bib: 1))).ToArray();
        var empty = TimingEngine.Replay(list, [], [], 0, 1);
        var timed = TimingEngine.Replay(list, a, audit, 0, 1);
        var b = a.Select((x, i) => x with { Key = "B" + i, DeviceTicks = x.DeviceTicks + (i == 0 ? 2 : 30) * TimeSpan.TicksPerMillisecond }).ToArray();
        var monitor = new BackupMonitor(); var session = Guid.NewGuid(); var policy = new BackupComparisonPolicy();
        monitor.Update(session, empty, [], TimingRulesTests.At, policy);
        var comparison = monitor.Update(session, timed, b, TimingRulesTests.At, policy);
        Assert.Equal(3, comparison.Warnings.Count);
        Assert.Contains(comparison.Rows, x => x.IsElapsed && x.DifferenceTicks == 28 * TimeSpan.TicksPerMillisecond);
        var display = new BackupDisplayWindow(); display.Show(TimingRulesTests.At);
        Assert.True(display.IsVisible(TimingRulesTests.At.AddSeconds(29)));
        Assert.False(display.IsVisible(TimingRulesTests.At.AddSeconds(30)));
        Assert.Equal(0, display.RemainingSeconds(TimingRulesTests.At.AddSeconds(31)));
        Assert.Equal(3, monitor.Update(session, timed, b, TimingRulesTests.At.AddSeconds(31), policy).Warnings.Count);
        Assert.Equal(6000, Assert.Single(timed.Results).Hundredths);
    }

    [Fact]
    public void AmbiguousAndInvalidBInputCannotLookLikeVerifiedMatch()
    {
        var list = TimingRulesTests.List(1); var capture = TimingRulesTests.Session(list);
        var a = new AlgeAsciiDecoder(capture, "A", "1").Feed(TimingRulesTests.Packet(capture, 1, "0001* C0 12:00:00.0000\r"));
        var timed = TimingEngine.Replay(list, a, [new(1, list.Id, TimingRulesTests.At, "Operator", "Source",
            new(DecisionKind.Assignment, a[0].Key), new(DecisionKind.Assignment, a[0].Key, Bib: 1))], 0, 1);
        var monitor = new BackupMonitor(); var session = Guid.NewGuid(); var policy = new BackupComparisonPolicy();
        monitor.Update(session, TimingEngine.Replay(list, [], [], 0, 1), [], TimingRulesTests.At, policy);
        monitor.Update(session, timed, [], TimingRulesTests.At, policy);
        var b = new[] { a[0] with { Key = "B1" }, a[0] with { Key = "B2", DeviceTicks = a[0].DeviceTicks + 1000 } };
        var ambiguous = monitor.Update(session, timed, b, TimingRulesTests.At.AddSeconds(5), policy);
        Assert.Contains("ambiguous", Assert.Single(ambiguous.Warnings), StringComparison.Ordinal);
        Assert.Null(Assert.Single(ambiguous.Rows).BTicks);
        var invalid = monitor.Update(session, timed, [b[0] with { Kind = ObservationKind.Invalid }], TimingRulesTests.At.AddSeconds(6), policy);
        Assert.Contains("missing", Assert.Single(invalid.Warnings), StringComparison.Ordinal);
    }

    [Fact]
    public void ReconnectedBClockCannotProduceApparentlyValidElapsedComparison()
    {
        var list = TimingRulesTests.List(1); var capture = TimingRulesTests.Session(list);
        var a = new AlgeAsciiDecoder(capture, "A", "1").Feed(TimingRulesTests.Packet(capture, 1,
            "0001* C0 12:00:00.0000\r0001* C1 12:01:00.0000\r"));
        var audit = a.Select((x, i) => new TimingAudit(i + 1, list.Id, TimingRulesTests.At, "Operator", "Source",
            new(DecisionKind.Assignment, x.Key), new(DecisionKind.Assignment, x.Key, Bib: 1))).ToArray();
        var timed = TimingEngine.Replay(list, a, audit, 0, 1);
        var b = a.Select((x, i) => x with { Key = "B" + i, ClockId = "B-clock-epoch-" + i }).ToArray();
        var monitor = new BackupMonitor(); var session = Guid.NewGuid(); var policy = new BackupComparisonPolicy();
        monitor.Update(session, TimingEngine.Replay(list, [], [], 0, 1), [], TimingRulesTests.At, policy);
        var comparison = monitor.Update(session, timed, b, TimingRulesTests.At, policy);
        var elapsed = Assert.Single(comparison.Rows, x => x.IsElapsed);
        Assert.Null(elapsed.BTicks); Assert.Null(elapsed.DifferenceTicks);
        Assert.Contains("continuity", Assert.Single(comparison.Warnings), StringComparison.Ordinal);
        Assert.Equal(6000, Assert.Single(timed.Results).Hundredths);
    }
}
