using System.Globalization;
using System.Text;

namespace OpenSkiTime.LiveTiming.LoadTest;

/// <summary>Markdown summary of a load run. Times are milliseconds; percentiles use the nearest-rank method.</summary>
public static class LoadReport
{
    public static string Markdown(LoadRun run, Uri origin, Guid session, int viewers, DateTime started, DateTime ended)
    {
        ArgumentNullException.ThrowIfNull(run);
        var c = CultureInfo.InvariantCulture;
        var b = new StringBuilder();
        void Line(string text) => b.Append(text).Append('\n');
        Line("# Live timing viewer load run");
        Line("");
        Line($"- Server: {origin}  ");
        Line($"- Session: {session}  ");
        Line(string.Create(c, $"- Simulated viewers: {viewers}  "));
        Line(string.Create(c, $"- Run: {started:yyyy-MM-dd HH:mm:ss} – {ended:HH:mm:ss} UTC ({(ended - started).TotalMinutes:0.0} min)"));
        Line("");

        Line("## Page loads");
        Line("");
        Line(string.Create(c, $"{run.PageLoads.Length} loaded (race page, stylesheet and six scripts each), {run.PageFailures.Values.Sum()} failed. ")
            + Distribution("Load time", run.PageLoads.Select(Ms)));
        foreach (var (reason, count) in run.PageFailures.OrderByDescending(x => x.Value)) { Line(string.Create(c, $"- {reason}: {count}")); }
        Line("");

        Line("## Connections");
        Line("");
        var firsts = run.Attempts.GroupBy(x => x.Viewer).Select(g => g.MinBy(x => x.StartedTicks)!).ToArray();
        var watched = firsts.Where(x => x.FirstStateTicks is not null).ToArray();
        Line(string.Create(c, $"{run.Attempts.Length} connection attempts by {firsts.Length} viewers; {watched.Length} viewers received the state on their first attempt."));
        Line("");
        Line(Distribution("First attempt to first state", watched.Select(x => Ms(x.FirstStateTicks!.Value - x.StartedTicks))));
        Line("");
        var failures = run.Attempts.Where(x => x.Failure is not null).GroupBy(x => x.Failure!).OrderByDescending(g => g.Count()).ToArray();
        if (failures.Length == 0) { Line("No connection failed or dropped."); }
        foreach (var g in failures) { Line(string.Create(c, $"- {g.Key}: {g.Count()}")); }
        Line("");
        // A drop is a watching connection that ended before the run did; recovery lasts until the next state on a new connection.
        var recoveries = new List<long>();
        foreach (var viewer in run.Attempts.GroupBy(x => x.Viewer))
        {
            var ordered = viewer.OrderBy(x => x.StartedTicks).ToArray();
            for (var i = 0; i < ordered.Length - 1; i++)
            {
                if (ordered[i].FirstStateTicks is null || ordered[i].EndedTicks is not { } dropped) { continue; }
                var next = ordered.Skip(i + 1).FirstOrDefault(x => x.FirstStateTicks is not null);
                if (next is not null) { recoveries.Add(next.FirstStateTicks!.Value - dropped); }
            }
        }
        Line(string.Create(c, $"{recoveries.Count} dropped connections were recovered. ") + Distribution("Drop to next state", recoveries.Select(Ms)));
        Line("");

        Line("## Updates");
        Line("");
        var broadcasts = run.States.Where(x => !x.First && x.Version >= 0).ToArray();
        var versions = broadcasts.GroupBy(x => x.Version).ToArray();
        var spread = new List<double>(); var worst = new List<(long Version, int Receivers, double Max, int Bytes)>();
        foreach (var version in versions)
        {
            var first = version.Min(x => x.ReceivedTicks);
            var values = version.Select(x => Ms(x.ReceivedTicks - first)).ToArray();
            spread.AddRange(values);
            worst.Add((version.Key, values.Length, values.Max(), version.Max(x => x.Bytes)));
        }
        Line(string.Create(c, $"{versions.Length} published updates reached viewers as {broadcasts.Length} State messages "
            + $"({broadcasts.Sum(x => (long)x.Bytes) / 1e6:0.0} MB); {run.States.Count(x => x.First)} were initial states after (re)subscription."));
        Line("");
        Line("| Measure | n | p50 | p95 | p99 | max |");
        Line("|---|---:|---:|---:|---:|---:|");
        Line(Row("Fan-out spread: viewer receive time after the first viewer of the same update (ms)", spread));
        Line(Row("Slowest viewer of each update after its first viewer (ms)", worst.Select(x => x.Max)));
        // A synthetic or replayed state carries a stamp far from now; only stamps within ten minutes measure delivery.
        Line(Row("Publisher stamp to viewer receive (ms; includes the publisher computer's clock offset)", broadcasts
            .Where(x => Math.Abs(x.ReceivedTicks - x.UpdatedTicks) < TimeSpan.TicksPerMinute * 10).Select(x => Ms(x.ReceivedTicks - x.UpdatedTicks))));
        Line(Row("Viewers reached per update", worst.Select(x => (double)x.Receivers)));
        Line(Row("State message size (kB)", broadcasts.Select(x => x.Bytes / 1000.0)));
        Line("");
        Line("Slowest updates:");
        Line("");
        Line("| Version | Viewers reached | Slowest after first (ms) | Size (kB) |");
        Line("|---:|---:|---:|---:|");
        foreach (var w in worst.OrderByDescending(x => x.Max).Take(10)) { Line(string.Create(c, $"| {w.Version} | {w.Receivers} | {w.Max:0} | {w.Bytes / 1000.0:0.0} |")); }
        Line("");

        Line("## Timeline");
        Line("");
        Line("| Minute (UTC) | Watching at minute end | State messages | MB received | Peak Mbit/s (10 s) | Failed or dropped connections | Fan-out p95 (ms) |");
        Line("|---|---:|---:|---:|---:|---:|---:|");
        var firstOfVersion = versions.ToDictionary(g => g.Key, g => g.Min(x => x.ReceivedTicks));
        for (var minute = new DateTime(started.Ticks - started.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc); minute < ended; minute = minute.AddMinutes(1))
        {
            var from = minute.Ticks; var to = minute.AddMinutes(1).Ticks;
            var inMinute = run.States.Where(x => x.ReceivedTicks >= from && x.ReceivedTicks < to).ToArray();
            var at = Math.Min(to, ended.Ticks - TimeSpan.TicksPerSecond);
            var watching = run.Attempts.Count(x => x.FirstStateTicks < at && (x.EndedTicks is null || x.EndedTicks >= at));
            var peak = Enumerable.Range(0, 6).Max(i => inMinute.Where(x => x.ReceivedTicks >= from + i * 10 * TimeSpan.TicksPerSecond
                && x.ReceivedTicks < from + (i + 1) * 10 * TimeSpan.TicksPerSecond).Sum(x => (long)x.Bytes)) * 8 / 10e6;
            var lost = run.Attempts.Count(x => x.Failure is not null && x.EndedTicks >= from && x.EndedTicks < to);
            var minuteSpread = inMinute.Where(x => !x.First && x.Version >= 0).Select(x => Ms(x.ReceivedTicks - firstOfVersion[x.Version])).ToArray();
            Line(string.Create(c, $"| {minute:HH:mm} | {watching} | {inMinute.Length} | {inMinute.Sum(x => (long)x.Bytes) / 1e6:0.0} | {peak:0.0} | {lost} | {(minuteSpread.Length == 0 ? "–" : Percentile(minuteSpread, 95).ToString("0", c))} |"));
        }
        return b.ToString();
    }

    private static double Ms(long ticks) => ticks / (double)TimeSpan.TicksPerMillisecond;

    private static string Distribution(string label, IEnumerable<double> values)
    {
        var all = values.ToArray();
        return all.Length == 0 ? label + ": no samples."
            : string.Create(CultureInfo.InvariantCulture, $"{label}: p50 {Percentile(all, 50):0} ms, p95 {Percentile(all, 95):0} ms, max {all.Max():0} ms.");
    }

    private static string Row(string label, IEnumerable<double> values)
    {
        var all = values.ToArray();
        return all.Length == 0 ? $"| {label} | 0 | – | – | – | – |"
            : string.Create(CultureInfo.InvariantCulture,
                $"| {label} | {all.Length} | {Percentile(all, 50):0.#} | {Percentile(all, 95):0.#} | {Percentile(all, 99):0.#} | {all.Max():0.#} |");
    }

    public static double Percentile(IReadOnlyCollection<double> values, double percent)
    {
        ArgumentNullException.ThrowIfNull(values);
        var sorted = values.Order().ToArray();
        var rank = (int)Math.Ceiling(percent / 100 * sorted.Length);
        return sorted[Math.Clamp(rank - 1, 0, sorted.Length - 1)];
    }
}
