using System.Globalization;
using System.Text;

namespace OpenSkiTime.LiveTiming.LoadTest;

/// <summary>Each viewer's final state compared with the server's: Available is false when the server had no state to compare.</summary>
public sealed record FinalCheck(bool Available, int Equal, int Mismatched, int WithoutState, string? Example);

/// <summary>Markdown summary of a load run. Times are milliseconds; percentiles use the nearest-rank method.</summary>
public static class LoadReport
{
    public static string Markdown(LoadRun run, Uri origin, Guid session, int viewers, bool legacy, DateTime started, DateTime ended, FinalCheck final)
    {
        ArgumentNullException.ThrowIfNull(run); ArgumentNullException.ThrowIfNull(final);
        var c = CultureInfo.InvariantCulture;
        var b = new StringBuilder();
        void Line(string text) => b.Append(text).Append('\n');
        Line("# Live timing viewer load run");
        Line("");
        Line($"- Server: {origin}  ");
        Line($"- Session: {session}  ");
        Line(string.Create(c, $"- Simulated viewers: {viewers} ({(legacy ? "legacy pages: Watch, full State after every change" : "WatchSync: deltas, full Sync states and beacons")})  "));
        Line(string.Create(c, $"- Run: {started:yyyy-MM-dd HH:mm:ss} – {ended:HH:mm:ss} UTC ({(ended - started).TotalMinutes:0.0} min)"));
        Line("");

        Line("## Page loads");
        Line("");
        Line(string.Create(c, $"{run.PageLoads.Length} loaded (race page, stylesheet and {Viewer.Assets.Count - 1} scripts each), {run.PageFailures.Values.Sum()} failed. ")
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
        // Race data again: the first message with results after the drop (a restarted server first answers without state
        // until the publisher restores the race).
        var recoveries = new List<long>(); var results = new List<long>();
        var data = run.States.Where(x => x.Version >= 0).GroupBy(x => x.Viewer).ToDictionary(g => g.Key, g => g.Select(x => x.ReceivedTicks).Order().ToArray());
        foreach (var viewer in run.Attempts.GroupBy(x => x.Viewer))
        {
            var ordered = viewer.OrderBy(x => x.StartedTicks).ToArray();
            for (var i = 0; i < ordered.Length - 1; i++)
            {
                if (ordered[i].FirstStateTicks is null || ordered[i].EndedTicks is not { } dropped) { continue; }
                var next = ordered.Skip(i + 1).FirstOrDefault(x => x.FirstStateTicks is not null);
                if (next is not null) { recoveries.Add(next.FirstStateTicks!.Value - dropped); }
                if (data.GetValueOrDefault(viewer.Key)?.FirstOrDefault(x => x > dropped) is long again && again > 0) { results.Add(again - dropped); }
            }
        }
        Line(string.Create(c, $"{recoveries.Count} dropped connections were recovered. ") + Distribution("Drop to next state or status", recoveries.Select(Ms))
            + " " + Distribution("Drop to race data again", results.Select(Ms)));
        Line("");

        Line("## Updates");
        Line("");
        var broadcasts = run.States.Where(x => !x.First && x.Version >= 0).ToArray();
        // One update is one publisher version, received within seconds by every viewer. A race restored after a server
        // restart carries a version seen before, so receipts more than 10 s apart start a separate update.
        var versions = broadcasts.GroupBy(x => x.Version).SelectMany(Clusters).ToArray();
        var spread = new List<double>(); var worst = new List<(long Version, int Receivers, double Max, int Bytes)>();
        var spreadAt = new List<(long At, double Ms)>();
        foreach (var version in versions)
        {
            var first = version.Min(x => x.ReceivedTicks);
            var values = version.Select(x => Ms(x.ReceivedTicks - first)).ToArray();
            spread.AddRange(values);
            spreadAt.AddRange(version.Select(x => (x.ReceivedTicks, Ms(x.ReceivedTicks - first))));
            worst.Add((version.Key, values.Length, values.Max(), version.Max(x => x.Bytes)));
        }
        Line(string.Create(c, $"{versions.Length} published updates reached viewers as {broadcasts.Length} State, Sync or Delta messages "
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
        Line(Row("Update message size (kB)", broadcasts.Select(x => x.Bytes / 1000.0)));
        Line("");
        Line("Received messages by kind (bytes of the SignalR JSON records, without WebSocket framing):");
        Line("");
        Line("| Kind | Messages | MB | Average kB | Max kB |");
        Line("|---|---:|---:|---:|---:|");
        foreach (var kind in run.Messages.GroupBy(x => x.Kind).OrderByDescending(g => g.Sum(x => (long)x.Bytes)))
        { Line(string.Create(c, $"| {kind.Key} | {kind.Count()} | {kind.Sum(x => (long)x.Bytes) / 1e6:0.00} | {kind.Average(x => x.Bytes) / 1000:0.00} | {kind.Max(x => x.Bytes) / 1000.0:0.00} |")); }
        Line(string.Create(c, $"| All | {run.Messages.Length} | {run.Messages.Sum(x => (long)x.Bytes) / 1e6:0.00} | {(run.Messages.Length == 0 ? 0 : run.Messages.Average(x => x.Bytes) / 1000):0.00} | {(run.Messages.Length == 0 ? 0 : run.Messages.Max(x => x.Bytes) / 1000.0):0.00} |"));
        Line("");
        Line(string.Create(c, $"Viewers found {run.Gaps} gaps (a missing delta, an unknown generation or a disagreeing beacon) and sent {run.Resyncs} resync requests."));
        Line("");
        Line("Slowest updates:");
        Line("");
        Line("| Version | Viewers reached | Slowest after first (ms) | Size (kB) |");
        Line("|---:|---:|---:|---:|");
        foreach (var w in worst.OrderByDescending(x => x.Max).Take(10)) { Line(string.Create(c, $"| {w.Version} | {w.Receivers} | {w.Max:0} | {w.Bytes / 1000.0:0.0} |")); }
        Line("");

        Line("## Timeline");
        Line("");
        Line("| Minute (UTC) | Watching at minute end | Messages | MB received | Peak Mbit/s (10 s) | Failed or dropped connections | Fan-out p95 (ms) |");
        Line("|---|---:|---:|---:|---:|---:|---:|");
        for (var minute = new DateTime(started.Ticks - started.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc); minute < ended; minute = minute.AddMinutes(1))
        {
            var from = minute.Ticks; var to = minute.AddMinutes(1).Ticks;
            var inMinute = run.Messages.Where(x => x.ReceivedTicks >= from && x.ReceivedTicks < to).ToArray();
            var at = Math.Min(to, ended.Ticks - TimeSpan.TicksPerSecond);
            var watching = run.Attempts.Count(x => x.FirstStateTicks < at && (x.EndedTicks is null || x.EndedTicks >= at));
            var peak = Enumerable.Range(0, 6).Max(i => inMinute.Where(x => x.ReceivedTicks >= from + i * 10 * TimeSpan.TicksPerSecond
                && x.ReceivedTicks < from + (i + 1) * 10 * TimeSpan.TicksPerSecond).Sum(x => (long)x.Bytes)) * 8 / 10e6;
            var lost = run.Attempts.Count(x => x.Failure is not null && x.EndedTicks >= from && x.EndedTicks < to);
            var minuteSpread = spreadAt.Where(x => x.At >= from && x.At < to).Select(x => x.Ms).ToArray();
            Line(string.Create(c, $"| {minute:HH:mm} | {watching} | {inMinute.Length} | {inMinute.Sum(x => (long)x.Bytes) / 1e6:0.0} | {peak:0.0} | {lost} | {(minuteSpread.Length == 0 ? "–" : Percentile(minuteSpread, 95).ToString("0", c))} |"));
        }
        Line("");
        Line("## Final state");
        Line("");
        if (!final.Available) { Line("The authoritative state was not available at the end of the run (deleted, not published or unreachable); viewers were not compared."); }
        else
        {
            Line(string.Create(c, $"Compared with GET /api/sessions/{{id}}/state after the run, field for field: {final.Equal} viewers equal, {final.Mismatched} different, {final.WithoutState} without a state."));
            if (final.Example is { } example) { Line(""); Line("First difference: " + example); }
        }
        return b.ToString();
    }

    private static double Ms(long ticks) => ticks / (double)TimeSpan.TicksPerMillisecond;

    private static IEnumerable<IGrouping<long, MessageSample>> Clusters(IGrouping<long, MessageSample> version)
    {
        var cluster = new List<MessageSample>();
        foreach (var sample in version.OrderBy(x => x.ReceivedTicks))
        {
            if (cluster.Count > 0 && sample.ReceivedTicks - cluster[^1].ReceivedTicks > 10 * TimeSpan.TicksPerSecond)
            { yield return cluster.GroupBy(_ => version.Key).Single(); cluster = []; }
            cluster.Add(sample);
        }
        if (cluster.Count > 0) { yield return cluster.GroupBy(_ => version.Key).Single(); }
    }

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
