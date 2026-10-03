using System.Globalization;
using System.Text.RegularExpressions;
using OpenSkiTime.Rewrite.Domain;

namespace OpenSkiTime.Rewrite.Timing;

public sealed record EvidenceTimestamp(string Key, long Ticks, int Precision, int? Channel, string Text);
public sealed record EvidenceTarget(string Key, int Bib, int Channel, long Ticks);
public sealed record EvidenceMatch(EvidenceTarget Target, EvidenceTimestamp? Evidence, long? DifferenceTicks, string State);

public static partial class TimingEvidenceMatching
{
    public const string OcrReviewRequiredPrefix = "[OCR review required] ";
    // Reject partially recognized numeric tokens: O9 must not become 09, and .3I must
    // not become .3. Additional attached numeric clock components are not discarded.
    [GeneratedRegex(@"(?<![\p{L}\p{N}])(?<![0-9][:.])(?<h>[0-9]{1,2})[:.](?<m>[0-9]{2})[:.](?<s>[0-9]{2})[.,](?<f>[0-9]{1,7})(?![\p{L}\p{N}]|[.,:][0-9])", RegexOptions.CultureInvariant)]
    private static partial Regex TimestampPattern();
    [GeneratedRegex(@"\bC(?<channel>[01O])M?\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ChannelPattern();
    // Faded ALGE receipt punctuation may be recognized as spaces. Accept that only in a
    // complete numbered/channelled record, never in arbitrary numeric text or across lines.
    [GeneratedRegex(@"^[ \t]*(?<sequence>[0-9]{1,6})[ \t]+C(?<channel>[01O])M?[ \t]+(?<h>[0-9]{1,2})(?:[ \t]*[:.][ \t]*|[ \t]+)(?<m>[0-9]{2})(?:[ \t]*[:.][ \t]*|[ \t]+)(?<s>[0-9]{2})(?:[ \t]*[.,][ \t]*|[ \t]+)(?<f>[0-9]{1,7})[ \t]*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AlgeReceiptRecordPattern();

    public static IReadOnlyList<EvidenceTimestamp> ParseLine(string key, string text, DateOnly deviceDate, int? fixedChannel = null)
    {
        ArgumentNullException.ThrowIfNull(key); ArgumentNullException.ThrowIfNull(text);
        // Persisted OCR alternatives belonging to one conflicted physical row are evidence for
        // manual review only. Never cherry-pick the reading nearest to an A timestamp.
        if (text.StartsWith(OcrReviewRequiredPrefix, StringComparison.Ordinal)) { return []; }
        var channel = fixedChannel;
        var printedChannel = ChannelPattern().Match(text);
        // OCR commonly reads printed C0 as CO. This only proposes a channel; the timestamp text is never corrected.
        if (channel is null && printedChannel.Success) { channel = printedChannel.Groups["channel"].Value == "1" ? 1 : 0; }
        var values = new List<EvidenceTimestamp>();
        foreach (Match match in TimestampPattern().Matches(text))
        {
            var formatted = match.Groups["h"].Value.PadLeft(2, '0') + ":" + match.Groups["m"].Value + ":" + match.Groups["s"].Value + "." + match.Groups["f"].Value;
            if (TimingTime.TryTimeOfDay(formatted, out var ticks, out var precision))
            { values.Add(new(key + ":" + match.Index.ToString(CultureInfo.InvariantCulture), deviceDate.ToDateTime(TimeOnly.MinValue).Ticks + ticks, precision, channel, text)); }
        }
        if (values.Count == 0)
        {
            var record = AlgeReceiptRecordPattern().Match(text);
            if (record.Success)
            {
                var formatted = record.Groups["h"].Value.PadLeft(2, '0') + ":" + record.Groups["m"].Value
                    + ":" + record.Groups["s"].Value + "." + record.Groups["f"].Value;
                if (TimingTime.TryTimeOfDay(formatted, out var ticks, out var precision))
                {
                    var recordChannel = fixedChannel ?? (record.Groups["channel"].Value == "1" ? 1 : 0);
                    values.Add(new(key + ":" + record.Groups["h"].Index.ToString(CultureInfo.InvariantCulture),
                        deviceDate.ToDateTime(TimeOnly.MinValue).Ticks + ticks, precision, recordChannel, text));
                }
            }
        }
        return values;
    }

    // No greedy nearest-neighbour guesses. An association must be unique in both directions.
    // Repeated photographs of the same printed timestamp are collapsed, with originals retained by the caller.
    public static IReadOnlyList<EvidenceMatch> Match(IReadOnlyList<EvidenceTarget> targets,
        IReadOnlyList<EvidenceTimestamp> evidence, long toleranceTicks, bool allowAdjacentDay = false)
    {
        if (toleranceTicks is <= 0 or > TimeSpan.TicksPerMinute)
        { throw new DomainValidationException("Matching tolerance must be greater than zero and no more than 60 seconds."); }
        var unique = evidence.GroupBy(x => (x.Ticks, x.Channel)).Select(g => g.First()).ToArray();
        var candidates = targets.Select(target => unique.Where(x => x.Channel is null || x.Channel == target.Channel)
            .Select(x => (Value: x, Delta: Difference(x.Ticks, target.Ticks, allowAdjacentDay)))
            .Where(x => Math.Abs(x.Delta) <= toleranceTicks).ToArray()).ToArray();
        return targets.Select((target, i) =>
        {
            var options = candidates[i];
            if (options.Length == 0) { return new EvidenceMatch(target, null, null, "Not found"); }
            if (options.Length != 1 || candidates.Count(c => c.Any(x => x.Value.Key == options[0].Value.Key)) != 1)
            { return new EvidenceMatch(target, null, null, "Ambiguous - choose manually"); }
            var option = options[0];
            return new EvidenceMatch(target, option.Value with { Ticks = target.Ticks + option.Delta }, option.Delta, "Proposed - verify");
        }).ToArray();
    }

    private static long Difference(long value, long reference, bool adjacent)
    {
        var delta = value - reference;
        if (!adjacent) { return delta; }
        var alternatives = new[] { delta, delta - TimeSpan.TicksPerDay, delta + TimeSpan.TicksPerDay };
        return alternatives.MinBy(Math.Abs);
    }
}

// The 1 ms FIS synchronization rule is a separate common-impulse check, not a race-finish limit.
public sealed record BackupComparisonPolicy(long StartWarningTicks = TimeSpan.TicksPerMillisecond,
    long FinishWarningTicks = 10 * TimeSpan.TicksPerMillisecond,
    long PairingToleranceTicks = 2 * TimeSpan.TicksPerSecond,
    long MissingGraceTicks = 5 * TimeSpan.TicksPerSecond)
{
    public string? Warning(int channel, long? differenceTicks, TimeSpan elapsedSinceA)
    {
        if (differenceTicks is null)
        { return elapsedSinceA.Ticks >= MissingGraceTicks ? "B signal missing" : null; }
        var limit = channel == 0 ? StartWarningTicks : FinishWarningTicks;
        return Math.Abs(differenceTicks.Value) > limit ? "A/B time difference" : null;
    }
}
