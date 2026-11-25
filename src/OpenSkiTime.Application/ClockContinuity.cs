using OpenSkiTime.Timing;

namespace OpenSkiTime.Application;

// Timing is received only while the Timing view is open, so one run usually has several capture sessions. With a single
// timing device, a competitor who starts in one session and finishes in the next is timed normally only when the device
// clock demonstrably ran on: the device time that passed between the sessions must equal the receive time that passed,
// and the device's own record numbers must run on, so another device with nearly the same time is not taken for the
// first one. Otherwise the new session keeps its own clock context and the elapsed time needs review, as after any
// reconnect. The decision depends only on stored packets, so a replay reproduces it.
// Never changed: sessions without CaptureOptions.ContinuesClock, captures of several devices (a clock group; every
// device would have to be verified on its own) and ALGE Results (receive times follow the network).
internal sealed class ClockContinuity
{
    public static readonly TimeSpan Tolerance = TimeSpan.FromMilliseconds(250);
    private const int MaxSkippedRecords = 1000;
    private sealed record Reading(Guid Session, string ClockId, long DeviceTicks, DateTimeOffset ReceivedAt, int? RecordNumber);
    private readonly Dictionary<string, Reading> _last = new(StringComparer.Ordinal);
    private readonly Dictionary<(Guid Session, string Device, string ClockId), string> _decided = [];

    public IReadOnlyList<TimingObservation> Apply(CaptureSession session, RawTimingPacket packet, IReadOnlyList<TimingObservation> observations)
        => session.Options is { ContinuesClock: true, ClockGroup: null } && packet.Protocol != "alge-results/v1"
            ? observations.Select(x => Apply(session, packet, x)).ToArray() : observations;

    public void Clear() { _last.Clear(); _decided.Clear(); }

    private TimingObservation Apply(CaptureSession session, RawTimingPacket packet, TimingObservation observation)
    {
        var device = $"{session.Options.Device}|{session.Options.Endpoint}|{observation.Source}";
        var key = (session.Id, device, observation.ClockId);
        // Corrections repeat earlier impulses and invalid input is not a clock reading.
        if (observation.ClockId.Length == 0 || observation.DeviceTicks is not { } ticks
            || observation.Kind is not (ObservationKind.Impulse or ObservationKind.Information))
        { return _decided.TryGetValue(key, out var known) ? observation with { ClockId = known } : observation; }
        if (!_decided.TryGetValue(key, out var clock))
        {
            (clock, var final) = Decide(session, packet, observation, ticks, device);
            if (final) { _decided.Add(key, clock); }
            // A heartbeat that fits only suggests the same clock (for the running display) until an impulse confirms it.
            else { return clock == observation.ClockId ? observation : observation with { ClockId = clock }; }
        }
        _last.TryGetValue(device, out var last);
        var same = last is not null && last.ClockId == clock;
        // An impulse delivered late is timed, but it is no reading of the clock: the next visit is never compared with it.
        if (same && observation.Kind == ObservationKind.Impulse && !session.Options.Simulation
            && (ticks - last!.DeviceTicks) - (packet.ReceivedAt - last.ReceivedAt).Ticks < -Tolerance.Ticks)
        { return clock == observation.ClockId ? observation : observation with { ClockId = clock }; }
        // Heartbeats and explicit bibs carry no record number; the last known one stays.
        var number = observation.RecordNumber ?? (same ? last!.RecordNumber : null);
        _last[device] = new(session.Id, clock, ticks, packet.ReceivedAt, number);
        return clock == observation.ClockId ? observation : observation with { ClockId = clock };
    }

    private (string Clock, bool Final) Decide(CaptureSession session, RawTimingPacket packet, TimingObservation observation, long ticks, string device)
    {
        var own = observation.ClockId;
        if (!_last.TryGetValue(device, out var last)) { return (own, true); }
        // A new stream or clock reset within one session is an interruption the operator must see.
        if (last.Session == session.Id) { return (own, true); }
        // The simulator has no clock that could be verified: the operator types its time.
        if (!session.Options.Simulation)
        {
            var difference = (ticks - last.DeviceTicks) - (packet.ReceivedAt - last.ReceivedAt).Ticks;
            // An impulse delivered late says nothing about the clock. Wait for the next reading.
            if (Math.Abs(difference) > Tolerance.Ticks) { return (own, observation.Kind != ObservationKind.Impulse || difference > 0); }
            if (observation.Kind != ObservationKind.Impulse) { return (last.ClockId, false); }
            // Only a record number on both sides is evidence. The first impulse on time decides for the whole session, so
            // a competitor who starts and finishes in it is never split between two clocks.
            if (last.RecordNumber is not { } before || observation.RecordNumber is not { } number
                || number <= before || number - before > MaxSkippedRecords) { return (own, true); }
        }
        return (last.ClockId, true);
    }
}
