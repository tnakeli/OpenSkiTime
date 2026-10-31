using System.Globalization;

namespace OpenSkiTime.Application;

/// <summary>Values derived from a course's hill profile.</summary>
public static class CourseProfile
{
    /// <summary>
    /// Vertical drop in metres from start and finish altitude as typed (whole metres). Null when either value is not a
    /// whole number or the finish is above the start, so a half-typed or wrong value never overwrites the drop.
    /// </summary>
    public static int? VerticalDrop(string? startAltitude, string? finishAltitude)
    {
        if (!int.TryParse(startAltitude?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var start)
            || !int.TryParse(finishAltitude?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var finish)
            || finish > start)
        { return null; }
        return start - finish;
    }
}
