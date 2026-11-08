using OpenSkiTime.Domain;
using OpenSkiTime.Timing;

namespace OpenSkiTime.Application;

public enum EetSheetRowKind { Reference, LeftOut, NotUsed, Target }

/// <summary>
/// One line of the printed EET calculation, in start order, as in the Timing Booklet examples (section 14): a
/// reference pair with its difference, a competitor left out or not usable with the explanation, and the competitor
/// with the missing time, whose difference column holds the sum.
/// </summary>
public sealed record EetSheetRow(EetSheetRowKind Kind, int Bib, string Surname, string FirstName, string BackupTime,
    string ATime, string Difference, string Explanation);

/// <summary>
/// The printable record of one EET calculation. Every value is formatted from the stored <see cref="EetCalculation"/>;
/// <see cref="Draft"/> marks a calculation that has not been saved to the result.
/// </summary>
public sealed record EetSheet(string Event, string Race, int Bib, string Surname, string FirstName, string Position,
    string BackupSource, string ReplacementReason, IReadOnlyList<EetSheetRow> Rows, int PairCount, string Sum,
    string Average, string Correction, int CorrectionPrecision, string BackupTime, string Result, string NetTime,
    bool Draft, string Status, DateTimeOffset GeneratedAt);

/// <summary>A competitor's name for the sheet.</summary>
public sealed record EetSheetName(string Surname, string FirstName);

public static class EetSheetBuilder
{
    public static string SourceLabel(EetBackupSource source) => source switch
    {
        EetBackupSource.PhotoFinish => "Photo finish",
        EetBackupSource.HandTime => "Hand time",
        _ => "System B"
    };

    /// <summary>
    /// Lays out <paramref name="calculation"/> for printing. <paramref name="startOrder"/> orders the lines; between the
    /// first and the last line of the calculation, competitors it does not use are listed with
    /// <paramref name="notUsable"/>'s explanation (for example "Did not finish"), as the booklet shows a DNF.
    /// </summary>
    public static EetSheet Build(EetCalculation calculation, IReadOnlyList<int> startOrder,
        IReadOnlyDictionary<int, EetSheetName> names, IReadOnlyDictionary<int, string> notUsable,
        string eventName, string race, string netTime, bool draft, string status, DateTimeOffset generatedAt)
    {
        ArgumentNullException.ThrowIfNull(calculation);
        ArgumentNullException.ThrowIfNull(startOrder);
        ArgumentNullException.ThrowIfNull(names);
        ArgumentNullException.ThrowIfNull(notUsable);
        if (calculation.Pairs.Count == 0) { throw new DomainValidationException("The EET calculation has no reference pairs."); }
        var pairs = calculation.Pairs.ToDictionary(x => x.Bib);
        var excluded = calculation.Exclusions.ToDictionary(x => x.Bib, x => x.Reason);
        // Competitors that are not in the start order (any more) follow it, in bib order.
        var order = startOrder.Distinct().ToList();
        order.AddRange(pairs.Keys.Concat(excluded.Keys).Append(calculation.Bib).Where(x => !order.Contains(x)).Distinct().Order());
        var shown = order.Select((bib, index) => (bib, index))
            .Where(x => x.bib == calculation.Bib || pairs.ContainsKey(x.bib) || excluded.ContainsKey(x.bib)).Select(x => x.index).ToArray();
        var first = shown.Min();
        var last = shown.Max();
        var digits = calculation.Pairs.Max(x => Math.Max(x.APrecision, x.BackupPrecision));
        EetSheetName Name(int bib) => names.GetValueOrDefault(bib) ?? new("", "");
        var rows = new List<EetSheetRow>();
        for (var i = first; i <= last; i++)
        {
            var bib = order[i];
            var name = Name(bib);
            if (bib == calculation.Bib)
            {
                rows.Add(new(EetSheetRowKind.Target, bib, name.Surname, name.FirstName,
                    TimingTime.FormatTimeOfDay(calculation.BackupTimeOfDay, calculation.BackupPrecision), "missed time",
                    TimingTime.FormatSignedSeconds(calculation.SumTicks, digits), "Sum of the time differences"));
            }
            else if (pairs.TryGetValue(bib, out var pair))
            {
                rows.Add(new(EetSheetRowKind.Reference, bib, name.Surname, name.FirstName,
                    TimingTime.FormatTimeOfDay(pair.BackupTimeOfDay, pair.BackupPrecision), TimingTime.FormatTimeOfDay(pair.ATicks, pair.APrecision),
                    TimingTime.FormatSignedSeconds(pair.DifferenceTicks, Math.Max(pair.APrecision, pair.BackupPrecision)), "Time difference"));
            }
            else
            {
                rows.Add(excluded.TryGetValue(bib, out var reason)
                    ? reason == EetPreparation.NoBackupTime
                        ? new(EetSheetRowKind.NotUsed, bib, name.Surname, name.FirstName, "", "", "", reason)
                        : new(EetSheetRowKind.LeftOut, bib, name.Surname, name.FirstName, "", "", "", "Left out: " + reason)
                    : new(EetSheetRowKind.NotUsed, bib, name.Surname, name.FirstName, "", "", "", notUsable.GetValueOrDefault(bib) ?? "No backup time"));
            }
        }
        var targetName = Name(calculation.Bib);
        return new(eventName, race, calculation.Bib, targetName.Surname, targetName.FirstName, calculation.Channel == 0 ? "Start" : "Finish",
            SourceLabel(calculation.Source), calculation.ReplacementReason, rows, calculation.Pairs.Count,
            TimingTime.FormatSignedSeconds(calculation.SumTicks, digits),
            TimingTime.FormatSignedSeconds((decimal)calculation.SumTicks / calculation.Pairs.Count / TimeSpan.TicksPerSecond, Math.Min(7, digits + 1)),
            TimingTime.FormatSignedSeconds(calculation.CorrectionTicks, calculation.CorrectionPrecision), calculation.CorrectionPrecision,
            TimingTime.FormatTimeOfDay(calculation.BackupTimeOfDay, calculation.BackupPrecision),
            TimingTime.FormatTimeOfDay(calculation.ResultTimeOfDay, calculation.ResultPrecision), netTime, draft, status, generatedAt);
    }
}
