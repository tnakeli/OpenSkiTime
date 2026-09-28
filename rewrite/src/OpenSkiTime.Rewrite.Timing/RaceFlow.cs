namespace OpenSkiTime.Rewrite.Timing;

// Queues are projections of saved timing, never a second mutable result store.
public static class RaceFlow
{
    public static bool CanReturnToStart(TimingResult? result) => result is
        { Status: TimingStatus.OnCourse, StartKey: not null, FinishKey: null }
        && result.Splits.All(x => x.ObservationKey is null);

    public static IReadOnlyList<TimingResult> Waiting(TimingSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var order = snapshot.StartOrder.Select((bib, index) => (bib, index)).ToDictionary(x => x.bib, x => x.index);
        return snapshot.Results.Where(x => x.Status == TimingStatus.Ready)
            .OrderBy(x => order.GetValueOrDefault(x.Bib, x.Entry.Position)).ToArray();
    }

    public static IReadOnlyList<TimingResult> OnCourse(TimingSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var starts = snapshot.Observations.ToDictionary(x => x.Observation.Key, x => x.Observation.DeviceTicks);
        return snapshot.Results.Where(x => x.StartKey is not null && x.FinishKey is null
                && x.Status is TimingStatus.OnCourse or TimingStatus.Review)
            .OrderBy(x => starts.GetValueOrDefault(x.StartKey!)).ThenBy(x => x.Entry.Position).ToArray();
    }

    public static IReadOnlyList<TimingResult> Expected(TimingSnapshot snapshot, int channel) => channel == 0
        ? Waiting(snapshot) : OnCourse(snapshot).Where(x => channel == 1
            || x.Splits.Any(s => s.Number == channel - 1 && s.ObservationKey is null)).ToArray();
}
