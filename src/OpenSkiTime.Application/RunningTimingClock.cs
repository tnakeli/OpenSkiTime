using OpenSkiTime.Timing;

namespace OpenSkiTime.Application;

// Approximate difference between the clocks of devices in one capture, from their latest device times.
public sealed record DeviceClockDifference(TimeSpan Difference, string Ahead, string Behind);

// Display estimate only. Authoritative results always subtract the saved device impulses.
// Each device keeps its own anchor, so a competitor's running time uses the clock of the device that took the start,
// even if another device in the same capture shows a different time.
public sealed class RunningTimingClock(TimeProvider? timeProvider = null)
{
    private sealed record Anchor(string ClockId, string Device, long DeviceTicks, long Monotonic);
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly Dictionary<string, Anchor> _anchors = [];
    private readonly object _gate = new();
    private string? _latestKey;

    private static string Key(TimingObservation observation) => observation.ClockId + "|" + observation.Source;

    public void Observe(TimingObservation observation, DateTimeOffset receivedAt)
    {
        ArgumentNullException.ThrowIfNull(observation);
        if (observation.Kind is not (ObservationKind.Impulse or ObservationKind.Information)
            || observation.DeviceTicks is not { } ticks || observation.ClockId.Length == 0) { return; }
        lock (_gate)
        {
            var delay = Math.Max(0, (_time.GetUtcNow() - receivedAt).Ticks);
            var deviceNow = ticks + delay;
            var key = Key(observation);
            // A display estimate never leaps: a device time more than 12 hours ahead of the current anchor is not adopted.
            if (!_anchors.TryGetValue(key, out var old) || (deviceNow >= Now(old) - TimeSpan.TicksPerSecond && deviceNow - old.DeviceTicks <= TimeSpan.FromHours(12).Ticks))
            { _anchors[key] = new(observation.ClockId, observation.Source, deviceNow, _time.GetTimestamp()); _latestKey = key; }
        }
    }

    private long Now(Anchor anchor) => anchor.DeviceTicks + _time.GetElapsedTime(anchor.Monotonic).Ticks;

    public long? ElapsedHundredths(TimingObservation start)
    {
        ArgumentNullException.ThrowIfNull(start);
        lock (_gate)
        {
            if (start.DeviceTicks is not { } ticks || !_anchors.TryGetValue(Key(start), out var anchor)) { return null; }
            var elapsed = Now(anchor) - ticks;
            return elapsed < 0 ? null : elapsed / TimingTime.TicksPerHundredth;
        }
    }

    public long? DeviceNowTicks(TimeSpan maxAge)
    {
        lock (_gate)
        {
            if (_latestKey is null || !_anchors.TryGetValue(_latestKey, out var anchor)) { return null; }
            var age = _time.GetElapsedTime(anchor.Monotonic);
            return age <= maxAge ? anchor.DeviceTicks + age.Ticks : null;
        }
    }

    // The largest current difference between devices of one synchronized capture, using anchors seen within maxAge.
    // Cloud devices are anchored at reception, so the estimate includes their delivery delay.
    public DeviceClockDifference? LargestDifference(TimeSpan maxAge)
    {
        lock (_gate)
        {
            DeviceClockDifference? largest = null;
            foreach (var group in _anchors.Values.Where(x => _time.GetElapsedTime(x.Monotonic) <= maxAge).GroupBy(x => x.ClockId))
            {
                var devices = group.GroupBy(x => x.Device).Select(x => x.MaxBy(Now)!).ToArray();
                if (devices.Length < 2) { continue; }
                var ahead = devices.MaxBy(Now)!; var behind = devices.MinBy(Now)!;
                var difference = TimeSpan.FromTicks(Now(ahead) - Now(behind));
                if (largest is null || difference > largest.Difference) { largest = new(difference, ahead.Device, behind.Device); }
            }
            return largest;
        }
    }

    public void Clear() { lock (_gate) { _anchors.Clear(); _latestKey = null; } }
}
