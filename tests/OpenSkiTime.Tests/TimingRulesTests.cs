using System.Text;
using OpenSkiTime.Application;
using OpenSkiTime.Devices;
using OpenSkiTime.Domain;
using OpenSkiTime.Timing;
using Xunit;

namespace OpenSkiTime.Tests;

public sealed class TimingRulesTests
{
    internal static readonly DateOnly Date = new(2026, 9, 27);
    internal static readonly DateTimeOffset At = new(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);
    internal static StartListRevision List(int count = 3)
    {
        var race = new CompetitionValues("Synthetic race", "SL1", Date, Discipline.Slalom, RaceType.Fis, 2, 0, "0001");
        var entries = Enumerable.Range(1, count).Select(i => new DrawEntrant(new Guid(i, 0, 0, new byte[8]),
            new("TEST" + i, "Athlete", 2000, "90000" + i, "FIN", "Synthetic", Gender.Female), i)).ToArray();
        var plan = FisStartOrder.FirstRun(Guid.NewGuid(), race, Gender.Female, entries,
            new("TEST", Date, Date), new(), "test");
        return new(Guid.NewGuid(), 1, At, null, "Test operator", "Fixture", plan);
    }

    internal static CaptureSession Session(StartListRevision list, bool simulation = true) =>
        new(Guid.NewGuid(), list.Id, new("Test", "Synthetic", Date, Simulation: simulation), At, At, true);
    internal static RawTimingPacket Packet(CaptureSession session, long sequence, string data, string protocol = "alge-ascii/v1") =>
        new(session.Id, sequence, At, protocol, "Synthetic", "1", Encoding.UTF8.GetBytes(data));

    [Fact]
    public void FragmentedAsciiKeepsPrecisionSequentialIdsManualFlagsAndUnknownInput()
    {
        var session = Session(List());
        var decoder = new AlgeAsciiDecoder(session, "Timy", "1");
        Assert.Empty(decoder.Feed(Packet(session, 1, " 0042 C0 12:00")));
        var first = Assert.Single(decoder.Feed(Packet(session, 2, ":00.1234 00\r")));
        Assert.Null(first.SuggestedBib); // PC Timer sequence number is not an athlete bib.
        Assert.Equal(4, first.Precision);
        Assert.Equal(1, first.PacketSequence);
        Assert.Equal(TimeSpan.FromHours(12).Ticks + 1234000, first.DeviceTicks - Date.ToDateTime(TimeOnly.MinValue).Ticks);
        var later = decoder.Feed(Packet(session, 3, " *0007 c1M 12:01:00.3456 00\r\n?0043 C0 12:00:01.0000 00\rgarbage\r"));
        Assert.Equal(3, later.Count);
        Assert.Equal(7, later[0].SuggestedBib);
        Assert.True(later[0].Manual);
        Assert.Null(later[1].SuggestedBib);
        Assert.Equal(ObservationKind.Invalid, later[2].Kind);
        Assert.Empty(decoder.Feed(Packet(session, 4, "unfinished")));
        Assert.Equal(ObservationKind.Invalid, Assert.Single(decoder.Complete()).Kind);
        Assert.Empty(decoder.Complete());
    }

    [Fact]
    public void MidnightAndLatePreviousDayPacketsDoNotMoveTheClockForwardTwice()
    {
        var session = Session(List());
        var decoder = new AlgeAsciiDecoder(session, "Timy", "1");
        var data = decoder.Feed(Packet(session, 1,
            " 0001 C0 23:59:59.9900 00\r 0002 C1 00:00:00.0100 00\r 0003 C0 23:59:59.9950 00\r 0004 C1 00:00:00.0300 00\r"));
        Assert.Equal(200000, data[1].DeviceTicks - data[0].DeviceTicks);
        Assert.Equal(350000, data[3].DeviceTicks - data[2].DeviceTicks);
        var reset = Assert.Single(decoder.Feed(Packet(session, 2, " 0005 C1 20:00:00.0000 00\r")));
        Assert.Equal(ObservationKind.Invalid, reset.Kind);
        Assert.NotEqual(data[0].ClockId, reset.ClockId);
    }

    [Fact]
    public void SubtractionPrecedesHundredthTruncationTiesAreEqualAndDuplicatesCannotTimeTwice()
    {
        var list = List();
        var session = Session(list);
        var decoder = new AlgeAsciiDecoder(session, "Timy", "1");
        var observations = decoder.Feed(Packet(session, 1,
            " *0001 C0 12:00:00.9999 00\r *0002 C0 12:00:10.0000 00\r *0001 C1 12:01:02.9998 00\r *0002 C1 12:01:11.9999 00\r *0002 C1 12:01:11.9999 00\r"));
        var audit = Assignments(list, observations.Take(4).ToArray());
        var snapshot = TimingEngine.Replay(list, observations, audit, 0, 1);
        Assert.All(snapshot.Results.Take(2), x => { Assert.Equal(6199, x.Hundredths); Assert.Equal(1, x.Rank); });
        Assert.Equal("Duplicate", snapshot.Observations[^1].State);
        Assert.False(snapshot.Complete);
        var third = snapshot.Results[2];
        var dns = new TimingDecision(DecisionKind.Status, CompetitorId: third.CompetitorId, Status: TimingStatus.DNS);
        var updated = audit.Append(new TimingAudit(5, list.Id, At, "Operator", "No start", new(DecisionKind.Status, CompetitorId: third.CompetitorId), dns)).ToArray();
        snapshot = TimingEngine.Replay(list, observations, updated, 0, 1);
        Assert.True(snapshot.Complete);
        Assert.Equal(FinishStatus.DNS, snapshot.ToRunFinishes()[2].Status);
    }

    [Fact]
    public void MissingStartsMultipleImpulsesAndDifferentClockEpochsRequireReview()
    {
        var list = List();
        var session = Session(list);
        var observations = new AlgeAsciiDecoder(session, "Timy", "1").Feed(Packet(session, 1,
            " *0001 C1 12:01:00.0000 00\r *0002 C0 12:01:00.0000 00\r *0002 C0 12:01:01.0000 00\r *0003 C0 12:01:02.0000 00\r *0003 C1 12:02:02.0000 00\r")).ToArray();
        observations[^1] = observations[^1] with { ClockId = "reconnected" };
        var snapshot = TimingEngine.Replay(list, observations, Assignments(list, observations), 0, 1);
        Assert.All(snapshot.Results, x => Assert.Equal(TimingStatus.Review, x.Status));
        Assert.Throws<DomainValidationException>(() => snapshot.ToRunFinishes());
        var corrected = new TimingDecision(DecisionKind.Time, CompetitorId: snapshot.Results[0].CompetitorId, Hundredths: 6201);
        TimingEngine.ValidateDecision(corrected, snapshot);
        Assert.Throws<DomainValidationException>(() => TimingEngine.ValidateDecision(corrected with { Hundredths = 0 }, snapshot));
    }

    [Fact]
    public void Mt1UnixPrecisionExplicitBibsMalformedJsonAndSemanticDuplicatesAreHandled()
    {
        var list = List();
        var session = Session(list);
        var decoder = new AlgeResultsDecoder(session);
        var stamp = (At.UtcTicks - DateTime.UnixEpoch.Ticks) + 1234567;
        var json = $$"""{"status":0,"data":[{"deviceId":"100","timestamp":{{stamp}},"timingChannel":"C0","fallingEdge":true,"valid":true,"blocked":false,"type":"StartNumberTrigger","startNumber":{"startNumber":1,"type":"SEQUENTIAL"},"timeOffset":0}]}""";
        var first = Assert.Single(decoder.Feed(Packet(session, 1, json, "alge-results/v1")));
        Assert.Equal(At.UtcTicks + 1234567, first.DeviceTicks);
        Assert.Contains("12:00:00.1234567", first.Message, StringComparison.Ordinal);
        Assert.Equal(5, first.Precision);
        Assert.Null(first.SuggestedBib);
        var duplicate = Assert.Single(decoder.Feed(Packet(session, 2, json.Replace(",", ", ", StringComparison.Ordinal), "alge-results/v1")));
        var changed = Assert.Single(decoder.Feed(Packet(session, 3, json.Replace("SEQUENTIAL", "MANUAL", StringComparison.Ordinal), "alge-results/v1")));
        Assert.Equal(1, changed.SuggestedBib);
        var snapshot = TimingEngine.Replay(list, [first, duplicate, changed], [], 0, 1);
        Assert.Equal("Duplicate", snapshot.Observations[1].State);
        Assert.Equal(ObservationKind.DeviceCorrection, snapshot.Observations[2].Observation.Kind);
        Assert.Equal("Review", snapshot.Observations[2].State);
        Assert.Equal(ObservationKind.Invalid, Assert.Single(decoder.Feed(Packet(session, 4, "{\"data\":[{}]}", "alge-results/v1"))).Kind);
        Assert.Equal(ObservationKind.DeviceCorrection, Assert.Single(decoder.Feed(Packet(session, 5,
            json.Replace("StartNumberTrigger", "ClearTrigger", StringComparison.Ordinal), "alge-results/v1"))).Kind);
    }

    [Fact]
    public void CombinedTimesExcludeNonFinishersAndRankEqualTotalsEqually()
    {
        var list = List();
        var first = TimingEngine.Replay(list, [], [], 0, 1);
        first = first with { Results = first.Results.Select((x, i) => x with { Status = TimingStatus.Finished, Hundredths = 6000 + i }).ToArray() };
        var second = first with { Results = first.Results.Select((x, i) => x with { Hundredths = 6100 - i }).ToArray() };
        var total = TimingEngine.Combined(second, first);
        Assert.All(total, x => { Assert.Equal(12100, x.Total); Assert.Equal(1, x.Rank); });
        second = second with { Results = second.Results.Select((x, i) => i == 1 ? x with { Status = TimingStatus.DNF, Hundredths = null } : x).ToArray() };
        Assert.Null(TimingEngine.Combined(second, first)[1].Total);
    }

    private static TimingAudit[] Assignments(StartListRevision list, IReadOnlyList<TimingObservation> observations) =>
        observations.Select((o, i) => new TimingAudit(i + 1, list.Id, At, "Test operator", "Explicit device bib",
            new(DecisionKind.Assignment, o.Key), new(DecisionKind.Assignment, o.Key, Bib: o.SuggestedBib))).ToArray();
}
