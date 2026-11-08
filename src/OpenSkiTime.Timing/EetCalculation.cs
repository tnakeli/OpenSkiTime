using System.Text.Json.Serialization;
using OpenSkiTime.Domain;

namespace OpenSkiTime.Timing;

// Where the replacement time of day comes from, in the order the Timing Booklet allows them (section 14): System B,
// then photo finish, then hand time.
public enum EetBackupSource { SystemB, PhotoFinish, HandTime }

/// <summary>
/// One reference competitor of an EET calculation: the System A time of day at the timing position and the backup time
/// of day of the same passage. <see cref="ATicks"/> is the A observation's device ticks (date included);
/// <see cref="BackupTimeOfDay"/> is the backup reading within the day.
/// </summary>
public sealed record EetPair(int Bib, string AKey, long ATicks, int APrecision, long BackupTimeOfDay, int BackupPrecision,
    string? BackupKey = null)
{
    // Backup minus A, on the time-of-day scale; a pair just across midnight compares with the neighbouring day.
    [JsonIgnore] public long DifferenceTicks => EetCalculator.TimeOfDayDifference(BackupTimeOfDay, ATicks);
}

// A reference competitor the operator left out of the calculation, with the reason recorded in history.
public sealed record EetExclusion(int Bib, string Reason);

/// <summary>
/// The complete, audited EET calculation for one missing System A time (Timing Booklet Alpine 2.67, section 14).
/// Every input is stored, so the history shows how the replacement time was obtained; replay uses the stored result.
/// </summary>
public sealed record EetCalculation(int Bib, int Channel, EetBackupSource Source, long BackupTimeOfDay, int BackupPrecision,
    string? BackupKey, IReadOnlyList<EetPair> Pairs, IReadOnlyList<EetExclusion> Exclusions, long SumTicks,
    int CorrectionPrecision, long CorrectionTicks, long ResultTimeOfDay, int ResultPrecision, string ReplacementReason)
{
    public bool Equals(EetCalculation? other) => other is not null && Bib == other.Bib && Channel == other.Channel
        && Source == other.Source && BackupTimeOfDay == other.BackupTimeOfDay && BackupPrecision == other.BackupPrecision
        && BackupKey == other.BackupKey && Pairs.SequenceEqual(other.Pairs) && Exclusions.SequenceEqual(other.Exclusions)
        && SumTicks == other.SumTicks && CorrectionPrecision == other.CorrectionPrecision && CorrectionTicks == other.CorrectionTicks
        && ResultTimeOfDay == other.ResultTimeOfDay && ResultPrecision == other.ResultPrecision
        && ReplacementReason == other.ReplacementReason;

    public override int GetHashCode() => HashCode.Combine(Bib, Channel, Source, BackupTimeOfDay, Pairs.Count, CorrectionTicks, ResultTimeOfDay);

    /// <summary>The stored calculation is complete and its result follows from its inputs.</summary>
    public bool IsConsistent()
    {
        try
        {
            return Pairs is not null && Exclusions is not null && ReplacementReason is not null
                && this == EetCalculator.Calculate(Bib, Channel, Source, BackupTimeOfDay, BackupPrecision, BackupKey, Pairs,
                    Exclusions, ReplacementReason);
        }
        catch (DomainValidationException) { return false; }
    }
}

// A competitor considered as an EET reference, in start order. Pair is null when there is no usable A/backup pair;
// Unavailable then says why (shown to the operator).
public sealed record EetCandidate(int Bib, EetPair? Pair, string? Unavailable = null);

public sealed record EetSelection(IReadOnlyList<EetPair> Pairs, IReadOnlyList<EetCandidate> Skipped);

/// <summary>
/// EET calculation (Timing Booklet Alpine, Para Alpine 2.67, section 14, pp. 32–35). Pure and deterministic: integer
/// 100 ns ticks only, no clock, I/O or floating point.
/// </summary>
public static class EetCalculator
{
    public const int RequiredPairs = 10;
    public const int MinimumCorrectionPrecision = 3;

    /// <summary>
    /// The reference pairs: the nearest <see cref="RequiredPairs"/> competitors started before the one with the missing
    /// time, completed with the nearest competitors started after when fewer exist. Candidates are in start order and
    /// exclude the target; those without a pair are skipped (for example a DNF has no finish time).
    /// </summary>
    public static EetSelection Select(IReadOnlyList<EetCandidate> startOrder, int targetBib)
    {
        ArgumentNullException.ThrowIfNull(startOrder);
        var index = startOrder.Select((x, i) => (x, i)).Where(x => x.x.Bib == targetBib).Select(x => x.i).DefaultIfEmpty(-1).Single();
        if (index < 0) { throw new DomainValidationException("The competitor with the missing time is not in the start order."); }
        // Skipped holds every competitor the search passed over without a pair, wherever the search ended.
        var chosen = new List<int>();
        var skipped = new List<int>();
        for (var i = index - 1; i >= 0 && chosen.Count < RequiredPairs; i--)
        { (startOrder[i].Pair is not null ? chosen : skipped).Add(i); }
        for (var i = index + 1; i < startOrder.Count && chosen.Count < RequiredPairs; i++)
        { (startOrder[i].Pair is not null ? chosen : skipped).Add(i); }
        chosen.Sort();
        skipped.Sort();
        return new(chosen.Select(i => startOrder[i].Pair!).ToArray(), skipped.Select(i => startOrder[i]).ToArray());
    }

    /// <summary>
    /// The correction is the mean of (backup − A) rounded to the A system precision, at least milliseconds, with halves
    /// away from zero (0.0455 → 0.046, 1.1575 / 10 → 0.1158). The EET is the backup time minus the correction.
    /// </summary>
    public static EetCalculation Calculate(int bib, int channel, EetBackupSource source, long backupTimeOfDay, int backupPrecision,
        string? backupKey, IReadOnlyList<EetPair> pairs, IReadOnlyList<EetExclusion> exclusions, string replacementReason)
    {
        ArgumentNullException.ThrowIfNull(pairs);
        ArgumentNullException.ThrowIfNull(exclusions);
        ArgumentNullException.ThrowIfNull(replacementReason);
        if (bib <= 0) { throw new DomainValidationException("Choose the competitor with the missing time."); }
        if (channel is not (0 or 1)) { throw new DomainValidationException("EET replaces a missing start or finish time. Intermediate times are unofficial."); }
        if (!Enum.IsDefined(source)) { throw new DomainValidationException("Choose System B, photo finish or hand time as the backup source."); }
        if (!IsTimeOfDay(backupTimeOfDay) || backupPrecision is < 2 or > 7 || !OnPrecision(backupTimeOfDay, backupPrecision))
        { throw new DomainValidationException($"Enter the backup time of day for bib {bib} with 2–7 decimals."); }
        if (pairs.Count is 0 or > RequiredPairs)
        { throw new DomainValidationException($"EET needs {RequiredPairs} reference pairs (fewer only when the run has no more)."); }
        if (pairs.Select(x => x.Bib).Append(bib).Distinct().Count() != pairs.Count + 1)
        { throw new DomainValidationException("Each reference competitor can be used once, and never the competitor with the missing time."); }
        foreach (var pair in pairs)
        {
            if (pair.Bib <= 0 || string.IsNullOrWhiteSpace(pair.AKey) || pair.ATicks <= 0 || pair.APrecision is < 3 or > 7
                || !IsTimeOfDay(pair.BackupTimeOfDay) || pair.BackupPrecision is < 2 or > 7 || !OnPrecision(pair.BackupTimeOfDay, pair.BackupPrecision))
            { throw new DomainValidationException($"Bib {pair.Bib}: the reference pair needs a System A time with at least milliseconds and a backup time with 2–7 decimals."); }
            if (Math.Abs(pair.DifferenceTicks) > TimeSpan.TicksPerMinute)
            { throw new DomainValidationException($"Bib {pair.Bib}: backup and System A differ by more than a minute. Check the backup time."); }
        }
        if (exclusions.Any(x => string.IsNullOrWhiteSpace(x.Reason) || x.Reason.Length > 200 || !TextRules.IsPortable(x.Reason))
            || exclusions.Select(x => x.Bib).Distinct().Count() != exclusions.Count || exclusions.Any(x => x.Bib == bib || pairs.Any(p => p.Bib == x.Bib)))
        { throw new DomainValidationException("Give a reason (at most 200 characters) for each reference competitor left out."); }
        if (replacementReason.Length > 200 || !TextRules.IsPortable(replacementReason))
        { throw new DomainValidationException("The timing report reason can be at most 200 characters without control characters."); }
        var sum = pairs.Sum(x => x.DifferenceTicks);
        var correctionPrecision = Math.Max(MinimumCorrectionPrecision, pairs.Min(x => x.APrecision));
        var unit = Unit(correctionPrecision);
        var correction = RoundHalfAwayFromZero(sum, pairs.Count * unit) * unit;
        var result = Normalize(backupTimeOfDay - correction);
        return new(bib, channel, source, backupTimeOfDay, backupPrecision, backupKey, pairs.ToArray(), exclusions.ToArray(), sum,
            correctionPrecision, correction, result, Math.Max(correctionPrecision, backupPrecision), replacementReason);
    }

    /// <summary>(value − reference) on the time-of-day scale, within half a day.</summary>
    public static long TimeOfDayDifference(long valueTimeOfDay, long referenceTicks)
    {
        var difference = Normalize(valueTimeOfDay - referenceTicks % TimeSpan.TicksPerDay);
        return difference > TimeSpan.TicksPerDay / 2 ? difference - TimeSpan.TicksPerDay : difference;
    }

    private static long RoundHalfAwayFromZero(long numerator, long denominator)
    {
        var magnitude = (Math.Abs(numerator) * 2 + denominator) / (2 * denominator);
        return numerator < 0 ? -magnitude : magnitude;
    }

    private static long Unit(int precision) => precision switch
    {
        2 => 100_000, 3 => 10_000, 4 => 1_000, 5 => 100, 6 => 10, 7 => 1,
        _ => throw new ArgumentOutOfRangeException(nameof(precision))
    };

    private static bool OnPrecision(long ticks, int precision) => ticks % Unit(precision) == 0;
    private static bool IsTimeOfDay(long ticks) => ticks is >= 0 and < TimeSpan.TicksPerDay;
    private static long Normalize(long ticks) => ((ticks % TimeSpan.TicksPerDay) + TimeSpan.TicksPerDay) % TimeSpan.TicksPerDay;
}
