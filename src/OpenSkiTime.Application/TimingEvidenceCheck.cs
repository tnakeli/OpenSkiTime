using System.Globalization;

namespace OpenSkiTime.Application;

public enum EvidenceSeverity { Warning, Error }

public sealed record EvidenceFinding(EvidenceSeverity Severity, string Message);

/// <summary>
/// Plausibility of the timestamps typed or read for System B and the hand clocks against System A.
/// A true System B differs from A by milliseconds to tens of milliseconds and hand timing by a fraction of a second,
/// so a larger difference is a typing or reading mistake. An error blocks the XML; a warning only points it out.
/// </summary>
public static class TimingEvidenceCheck
{
    public const long BWarningTicks = TimeSpan.TicksPerSecond / 10;
    public const long BErrorTicks = TimeSpan.TicksPerSecond;
    public const long HandWarningTicks = TimeSpan.TicksPerSecond / 2;
    public const long HandErrorTicks = 2 * TimeSpan.TicksPerSecond;

    /// <summary>Compares one time of day with the same instant of System A; null when it is close enough or either is missing.</summary>
    public static EvidenceFinding? Compare(TimingReportStamp? a, TimingReportStamp? other, bool hand)
    {
        if (a is null || other is null) { return null; }
        return Judge(Signed(other.Ticks - a.Ticks), hand);
    }

    private static long Signed(long difference)
    {
        // A run across midnight differs by whole days only because of the stamp date; the clocks differ by the remainder.
        var day = TimeSpan.TicksPerDay;
        var rest = ((difference % day) + day) % day;
        return rest > day / 2 ? rest - day : rest;
    }

    private static EvidenceFinding? Judge(long difference, bool hand)
    {
        var size = Math.Abs(difference);
        var error = hand ? HandErrorTicks : BErrorTicks; var warning = hand ? HandWarningTicks : BWarningTicks;
        if (size <= warning) { return null; }
        var seconds = (size / (decimal)TimeSpan.TicksPerSecond).ToString("0.000", CultureInfo.InvariantCulture);
        var direction = difference > 0 ? "later" : "earlier";
        var limit = (error / (decimal)TimeSpan.TicksPerSecond).ToString("0.###", CultureInfo.InvariantCulture);
        return size > error
            ? new(EvidenceSeverity.Error, $"The time is {seconds} s {direction} than A, more than {limit} s: probably a typing or reading mistake.")
            : new(EvidenceSeverity.Warning, $"The time is {seconds} s {direction} than A. Check it.");
    }
}
