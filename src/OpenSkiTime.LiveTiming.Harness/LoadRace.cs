using System.Globalization;
using OpenSkiTime.LiveTiming.Publishing;

namespace OpenSkiTime.LiveTiming.Harness;

/// <summary>
/// A synthetic one-run slalom published in real time for viewer load tests (docs/perf-test/live-timing-load-test.md):
/// racers start at a fixed interval, pass one intermediate and finish 45–60 s later; every 17th bib is DNS and every 13th
/// DNF. Each change is published like the desktop's publisher worker (StandalonePublisher: one changed row as an event,
/// a finish that moves other ranks as a full state). Every five seconds it reads the server's load report, logs it, and
/// republishes the race when the server lost it (a restart). Runs until Ctrl+C after the last finish.
/// </summary>
public static class LoadRace
{
    public static async Task<int> RunAsync(string endpoint, int racers, double interval, string? output, CancellationToken stop)
    {
        var key = Environment.GetEnvironmentVariable("OST_LIVE_PUBLISHER_KEY");
        using var publisher = new StandalonePublisher(endpoint, publisherKey: string.IsNullOrEmpty(key) ? null : key, log: _ => { });
        var state = SyntheticRace.Create(racers);
        // The first racer starts 30 s after publishing begins, so that viewers can connect first.
        var begin = DateTimeOffset.UtcNow.AddSeconds(30);
        var events = new List<(DateTimeOffset At, int Bib, LiveStatus Status, long? Time, int Split)>();
        foreach (var bib in state.Competitors.Select(x => x.Bib))
        {
            var start = begin.AddSeconds((bib - 1) * interval);
            long time = 4500 + bib * 137 % 1500;
            if (bib % 17 == 0) { events.Add((start, bib, LiveStatus.DNS, null, 0)); continue; }
            events.Add((start, bib, LiveStatus.OnCourse, null, 0));
            events.Add((start.AddSeconds(time * 0.45 / 100), bib, LiveStatus.OnCourse, null, 1));
            events.Add(bib % 13 == 0 ? (start.AddSeconds(time * 0.7 / 100), bib, LiveStatus.DNF, null, 0) : (start.AddSeconds(time / 100.0), bib, LiveStatus.Finished, time, 0));
        }
        events.Sort((a, b) => a.At.CompareTo(b.At));
        var log = output is null ? null : new StreamWriter(Path.Combine(output, "server-load.tsv")) { AutoFlush = true };
        log?.WriteLine("utc\tviewers\tcpu_percent\tworking_set_mb\theap_mb\tthread_pool_queue\tresponses_p95_ms\tresponses_max_ms\tround_trip_ms");
        try
        {
            await Publish(publisher, state with { UpdatedAt = DateTimeOffset.UtcNow }, true, stop);
            Console.WriteLine($"Session {publisher.Session!.SessionId} {publisher.Session.PublicUrl}; first start {begin:HH:mm:ss} UTC, {events.Count} updates");
            if (output is not null) { await File.WriteAllTextAsync(Path.Combine(output, "session.txt"), publisher.Session.SessionId.ToString(), stop); }
            var health = DateTimeOffset.UtcNow.AddSeconds(5); var next = 0; var published = 0;
            while (!stop.IsCancellationRequested)
            {
                var due = next < events.Count ? events[next].At : DateTimeOffset.MaxValue;
                var wait = (due < health ? due : health) - DateTimeOffset.UtcNow;
                if (wait > TimeSpan.Zero) { await Task.Delay(wait, stop); }
                if (next < events.Count && events[next].At <= DateTimeOffset.UtcNow)
                {
                    var e = events[next++];
                    state = SyntheticRace.Update(state, e.Bib, e.Status, e.Time, e.Split) with { UpdatedAt = DateTimeOffset.UtcNow };
                    await Publish(publisher, state, false, stop);
                    if (++published % 25 == 0 || next == events.Count) { Console.WriteLine($"{DateTime.UtcNow:HH:mm:ss} published {published}/{events.Count}"); }
                    if (next == events.Count) { Console.WriteLine("Race complete. Press Ctrl+C to stop; the race stays published."); }
                    continue;
                }
                health = DateTimeOffset.UtcNow.AddSeconds(5);
                try
                {
                    if (!await publisher.HealthAsync(stop))
                    {
                        Console.WriteLine($"{DateTime.UtcNow:HH:mm:ss} server lost the race; restoring");
                        await Publish(publisher, state, true, stop);
                    }
                    if (publisher.Stats is { } s)
                    {
                        log?.WriteLine(string.Create(CultureInfo.InvariantCulture,
                            $"{s.At:O}\t{s.Viewers.Current}\t{s.Resources.CpuPercent}\t{s.Resources.WorkingSetBytes / 1048576}\t{s.Resources.GcHeapBytes / 1048576}\t{s.Resources.ThreadPoolQueue}\t{s.Responses.P95Ms}\t{s.Responses.MaxMs}\t{publisher.RoundTripMs}"));
                    }
                }
                catch (Exception ex) when (ex is IOException or HttpRequestException) { Console.WriteLine($"{DateTime.UtcNow:HH:mm:ss} health: {ex.Message}"); }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        finally { if (log is not null) { await log.DisposeAsync(); } }
        return 0;
    }

    // Retries a failed publish once a second with the full state, as the worker repairs every failure.
    private static async Task Publish(StandalonePublisher publisher, LiveSnapshot state, bool refresh, CancellationToken stop)
    {
        while (true)
        {
            try { await publisher.PublishAsync(state, refresh, stop); return; }
            catch (Exception ex) when (ex is IOException or HttpRequestException)
            {
                Console.WriteLine($"{DateTime.UtcNow:HH:mm:ss} publish failed ({ex.Message}); retrying");
                refresh = true;
                await Task.Delay(1000, stop);
            }
        }
    }
}
