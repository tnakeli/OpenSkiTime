using OpenSkiTime.Timing;

namespace OpenSkiTime.Application;

// Timing is received only while the Timing view is open, so one run usually has several capture sessions. A competitor
// who starts in one session and finishes in the next is timed normally only when the device clock demonstrably ran on:
// the device time that passed between the sessions must equal the receive time that passed. Otherwise the new session
// keeps its own clock context and the elapsed time needs review, as after any reconnect. The decision depends only on
// stored packets, so a replay reproduces it. Sessions without CaptureOptions.ContinuesClock are never changed.
internal sealed class ClockContinuity
{
    public static readonly TimeSpan Tolerance = TimeSpan.FromMilliseconds(500);
    private sealed record Reading(Guid Session, string ClockId, long DeviceTicks, DateTimeOffset ReceivedAt);
    private readonly Dictionary<string, Reading> _last = new(StringComparer.Ordinal);
    private readonly Dictionary<(Guid Session, string Device, string ClockId), string> _decided = [];
    // The clock that the devices of one synchronized capture group continue, once one of them has been verified.
    private readonly Dictionary<string, string> _groups = new(StringComparer.Ordinal);

    public IReadOnlyList<TimingObservation> Apply(CaptureSession session, RawTimingPacket packet, IReadOnlyList<TimingObservation> observations)
        => session.Options.ContinuesClock ? observations.Select(x => Apply(session, packet, x)).ToArray() : observations;

    public void Clear() { _last.Clear(); _decided.Clear(); _groups.Clear(); }

    private TimingObservation Apply(CaptureSession session, RawTimingPacket packet, TimingObservation observation)
    {
        // Corrections repeat earlier impulses and invalid input is not a clock reading.
        if (observation.ClockId.Length == 0 || observation.DeviceTicks is not { } ticks
            || observation.Kind is not (ObservationKind.Impulse or ObservationKind.Information)) { return Known(session, observation); }
        var device = Device(session);
        var key = (session.Id, device, observation.ClockId);
        if (!_decided.TryGetValue(key, out var clock))
        {
            clock = Decide(session, packet, observation, ticks, device);
            // An impulse delivered late says nothing about the clock. Wait for the next reading.
            if (clock is null) { return observation; }
            _decided.Add(key, clock);
        }
        _last[device] = new(session.Id, clock, ticks, packet.ReceivedAt);
        return clock == observation.ClockId ? observation : observation with { ClockId = clock };
    }

    private string? Decide(CaptureSession session, RawTimingPacket packet, TimingObservation observation, long ticks, string device)
    {
        var group = session.Options.ClockGroup is { } id && observation.ClockId == $"sync:{id}:0" ? id : null;
        if (!_last.TryGetValue(device, out var last))
        { return group is not null ? _groups.GetValueOrDefault(group, observation.ClockId) : observation.ClockId; }
        // A new stream or clock reset within one session is an interruption the operator must see.
        if (last.Session == session.Id) { return observation.ClockId; }
        // ALGE Results stores absolute instants; its receive times follow the network, not the device. The simulator has no
        // clock that could be verified: the operator types its time.
        if (packet.Protocol != "alge-results/v1" && !session.Options.Simulation)
        {
            var difference = (ticks - last.DeviceTicks) - (packet.ReceivedAt - last.ReceivedAt).Ticks;
            if (Math.Abs(difference) > Tolerance.Ticks)
            { return observation.Kind == ObservationKind.Impulse && difference < 0 ? null : observation.ClockId; }
        }
        if (group is not null) { _groups.TryAdd(group, last.ClockId); }
        return last.ClockId;
    }

    private TimingObservation Known(CaptureSession session, TimingObservation observation)
        => observation.ClockId.Length != 0 && _decided.TryGetValue((session.Id, Device(session), observation.ClockId), out var clock)
            ? observation with { ClockId = clock } : observation;

    private static string Device(CaptureSession session) => session.Options.Device + "|" + session.Options.Endpoint;
}
