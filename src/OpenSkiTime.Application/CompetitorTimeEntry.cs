using OpenSkiTime.Timing;

namespace OpenSkiTime.Application;

/// <summary>
/// A start or finish time of day for one competitor from the Edit result dialog. <see cref="TimeOfDay"/> is the typed
/// value for a hand or device-record time; an EET carries its complete calculation instead. <see cref="Replacing"/>
/// lists the impulses assigned to the competitor at that position when the operator saw them (none when the time was
/// missing); the entry is refused when they changed, so a late System A impulse is never replaced unseen. For an EET,
/// <see cref="ReferenceKeys"/> are the System A times that were eligible references when the table was prepared, in
/// start order; the EET is refused when they changed, because the nearest ten could then be others.
/// </summary>
public sealed record CompetitorTimeEntry(int Bib, int Channel, ManualTimeSource Source, string? TimeOfDay = null,
    string? SourceReference = null, EetCalculation? Eet = null, IReadOnlyList<string>? Replacing = null,
    IReadOnlyList<string>? ReferenceKeys = null)
{
    /// <summary>The impulses an entry for <paramref name="bib"/> at <paramref name="channel"/> replaces (unassigns).</summary>
    public static IReadOnlyList<string> AssignedKeys(TimingSnapshot snapshot, int bib, int channel)
        => EetPreparation.AssignedKeys(snapshot, bib, channel);

    /// <summary>
    /// The System A times of a preparation that can be EET references, in start order with the competitor's own place
    /// marked, for <see cref="ReferenceKeys"/>: a change of eligibility or of the order changes the nearest ten.
    /// </summary>
    public static IReadOnlyList<string> EligibleKeys(EetPreparationResult preparation)
    {
        ArgumentNullException.ThrowIfNull(preparation);
        var keys = preparation.StartOrder.Select((x, i) => (x, i)).Where(x => x.x.A is not null).ToList();
        var before = keys.Count(x => x.i < preparation.TargetIndex);
        var result = keys.Select(x => x.x.A!.Key).ToList();
        result.Insert(before, "target:" + preparation.Bib.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return result;
    }
}
