using OpenSkiTime.Timing;

namespace OpenSkiTime.Application;

// Timing is received only while the Timing view is open, so one run usually has several capture sessions. A competitor
// who starts in one session and finishes in the next is timed normally only when the device clock demonstrably ran on:
// the device time that passed between the sessions must equal the receive time that passed, and the device's own record
// numbers must run on, so another device with nearly the same time is not taken for the first one. Otherwise the new
// session keeps its own clock context and the elapsed time needs review, as after any reconnect. The decision depends
// only on stored packets, so a replay reproduces it. Sessions without CaptureOptions.ContinuesClock are never changed.
internal sealed class ClockContinuity
{
    public static readonly TimeSpan Tolerance = TimeSpan.FromMilliseconds(250);
    private const int MaxSkippedRecords = 1000;
    // Devices of one capture are trusted to agree within this; a larger change of a cloud clock contradicts that.
    private static readonly TimeSpan s_cloudTolerance = TimeSpan.FromSeconds(2);
    private sealed record Reading(Guid Session, string ClockId, long DeviceTicks, DateTimeOffset ReceivedAt, int? RecordNumber, int? OffsetMinutes);
    private readonly Dictionary<string, Reading> _last = new(StringComparer.Ordinal);
    private readonly Dictionary<(Guid Session, string Device, string ClockId), string> _decided = [];
    // The clock that the devices of one synchronized capture group continue, once one of them has been verified.
    private readonly Dictionary<string, string> _groups = new(StringComparer.Ordinal);
    // Devices of a group that cannot be verified themselves (no earlier reading, or ALGE Results). Like at the start of any
    // multi-device capture they are taken to agree with the group, so they follow whatever another device verifies, no
    // matter whose input arrived first.
    private readonly Dictionary<(Guid Session, string Source, string ClockId), string> _following = [];

    public IReadOnlyList<TimingObservation> Apply(CaptureSession session, RawTimingPacket packet, IReadOnlyList<TimingObservation> observations)
        => session.Options.ContinuesClock ? observations.Select(x => Apply(session, packet, x)).ToArray() : observations;

    public void Clear() { _last.Clear(); _decided.Clear(); _groups.Clear(); _following.Clear(); }

    // Applies a verification to observations that arrived before it.
    public void Resolve(List<TimingObservation> observations)
    {
        if (_following.Count == 0 || _groups.Count == 0) { return; }
        for (var i = 0; i < observations.Count; i++)
        {
            var x = observations[i];
            if (_following.TryGetValue((x.SessionId, x.Source, x.ClockId), out var group) && _groups.TryGetValue(group, out var clock))
            { observations[i] = x with { ClockId = clock }; }
        }
    }

    private TimingObservation Apply(CaptureSession session, RawTimingPacket packet, TimingObservation observation)
    {
        var device = $"{session.Options.Device}|{session.Options.Endpoint}|{observation.Source}";
        var key = (session.Id, device, observation.ClockId);
        // Corrections repeat earlier impulses and invalid input is not a clock reading.
        if (observation.ClockId.Length == 0 || observation.DeviceTicks is not { } ticks
            || observation.Kind is not (ObservationKind.Impulse or ObservationKind.Information))
        { return _decided.TryGetValue(key, out var known) ? observation with { ClockId = known } : observation; }
        if (_following.TryGetValue((session.Id, observation.Source, observation.ClockId), out var followed))
        {
            var current = _groups.GetValueOrDefault(followed, observation.ClockId);
            _last[device] = new(session.Id, current, ticks, packet.ReceivedAt, observation.RecordNumber, observation.ClockOffsetMinutes);
            return current == observation.ClockId ? observation : observation with { ClockId = current };
        }
        if (!_decided.TryGetValue(key, out var clock))
        {
            (clock, var final) = Decide(session, packet, observation, ticks, device);
            if (final) { _decided.Add(key, clock); }
            // A heartbeat that fits only suggests the same clock (for the running display) until an impulse confirms it.
            else
            {
                if (_following.ContainsKey((session.Id, observation.Source, observation.ClockId)))
                { _last[device] = new(session.Id, clock, ticks, packet.ReceivedAt, observation.RecordNumber, observation.ClockOffsetMinutes); }
                return clock == observation.ClockId ? observation : observation with { ClockId = clock };
            }
        }
        var number = observation.RecordNumber ?? (_last.TryGetValue(device, out var last) && last.ClockId == clock ? last.RecordNumber : null);
        _last[device] = new(session.Id, clock, ticks, packet.ReceivedAt, number, observation.ClockOffsetMinutes);
        return clock == observation.ClockId ? observation : observation with { ClockId = clock };
    }

    private (string Clock, bool Final) Decide(CaptureSession session, RawTimingPacket packet, TimingObservation observation, long ticks, string device)
    {
        var own = observation.ClockId;
        var group = session.Options.ClockGroup is { } id && own == $"sync:{id}:0" ? id : null;
        // ALGE Results cannot be verified: receive times follow the network, and the wall clock can be changed on the server.
        // It may follow another device's verification unless its own input shows that it was changed.
        var cloud = packet.Protocol == "alge-results/v1";
        if (!_last.TryGetValue(device, out var last) || (cloud && last.Session != session.Id))
        {
            if (group is null || (last is not null && CloudClockChanged(last, packet, observation, ticks))) { return (own, true); }
            _following[(session.Id, observation.Source, own)] = group;
            return (_groups.GetValueOrDefault(group, own), false);
        }
        // A new stream or clock reset within one session is an interruption the operator must see.
        if (last.Session == session.Id) { return (own, true); }
        // The simulator has no clock that could be verified: the operator types its time.
        if (!session.Options.Simulation)
        {
            var difference = (ticks - last.DeviceTicks) - (packet.ReceivedAt - last.ReceivedAt).Ticks;
            // An impulse delivered late says nothing about the clock. Wait for the next reading.
            if (Math.Abs(difference) > Tolerance.Ticks) { return (own, observation.Kind != ObservationKind.Impulse || difference > 0); }
            if (observation.Kind != ObservationKind.Impulse) { return (Current(last), false); }
            if (last.RecordNumber is { } before && observation.RecordNumber is { } number
                && (number <= before || number - before > MaxSkippedRecords)) { return (own, true); }
        }
        var continued = Current(last);
        if (group is not null) { _groups.TryAdd(group, continued); }
        return (continued, true);
    }

    private static bool CloudClockChanged(Reading last, RawTimingPacket packet, TimingObservation observation, long ticks)
        => observation.ClockOffsetMinutes != last.OffsetMinutes
            // History pages carry old triggers; only a pushed trigger was received when it happened.
            || (packet.Stream != "cloud" && Math.Abs((ticks - last.DeviceTicks) - (packet.ReceivedAt - last.ReceivedAt).Ticks) > s_cloudTolerance.Ticks);

    // A reading of a following device counts for the clock its group was verified to continue meanwhile.
    private string Current(Reading reading) => reading.ClockId.StartsWith("sync:", StringComparison.Ordinal)
        && reading.ClockId.EndsWith(":0", StringComparison.Ordinal) && _groups.TryGetValue(reading.ClockId[5..^2], out var clock) ? clock : reading.ClockId;
}
