using OpenSkiTime.Rewrite.Timing;

namespace OpenSkiTime.Rewrite.Application;

// Display estimate only. Authoritative results always subtract the saved device impulses.
public sealed class RunningTimingClock(TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly Dictionary<string, (long Device, long Monotonic)> _anchors = [];
    private readonly object _gate = new();

    public void Observe(TimingObservation observation, DateTimeOffset receivedAt)
    {
        ArgumentNullException.ThrowIfNull(observation);
        if (observation.Kind is not (ObservationKind.Impulse or ObservationKind.Information)
            || observation.DeviceTicks is not { } ticks || observation.ClockId.Length == 0) { return; }
        lock (_gate)
        {
            var delay = Math.Max(0, (_time.GetUtcNow() - receivedAt).Ticks);
            var deviceNow = observation.ClockId == "UTC" ? _time.GetUtcNow().UtcTicks : ticks + delay;
            if (!_anchors.TryGetValue(observation.ClockId, out var old) || deviceNow >= old.Device)
            { _anchors[observation.ClockId] = (deviceNow, _time.GetTimestamp()); }
        }
    }

    public long? ElapsedHundredths(TimingObservation start)
    {
        ArgumentNullException.ThrowIfNull(start);
        lock (_gate)
        {
            if (start.DeviceTicks is not { } ticks || !_anchors.TryGetValue(start.ClockId, out var anchor)) { return null; }
            var elapsed = anchor.Device + _time.GetElapsedTime(anchor.Monotonic).Ticks - ticks;
            return elapsed < 0 ? null : elapsed / TimingTime.TicksPerHundredth;
        }
    }

    public void Clear() { lock (_gate) { _anchors.Clear(); } }
}
