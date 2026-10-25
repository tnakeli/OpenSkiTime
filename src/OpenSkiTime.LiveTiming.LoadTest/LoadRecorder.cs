using System.Globalization;
using System.Text;

namespace OpenSkiTime.LiveTiming.LoadTest;

/// <summary>Thread-safe record of every connection and every received hub message of a run, in UTC ticks.</summary>
public sealed class LoadRecorder
{
    private readonly Lock _gate = new();
    private readonly List<MessageSample> _messages = [];
    private readonly List<ConnectionAttempt> _attempts = [];
    private readonly List<long> _pageLoads = [];
    private readonly Dictionary<string, int> _pageFailures = [];
    private readonly HashSet<ConnectionAttempt> _open = [];
    private int _gaps, _resyncs;

    public ConnectionAttempt Begin(int viewer)
    {
        var attempt = new ConnectionAttempt(viewer, DateTime.UtcNow.Ticks);
        lock (_gate) { _attempts.Add(attempt); _open.Add(attempt); }
        return attempt;
    }
    public void End(ConnectionAttempt attempt)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        lock (_gate) { attempt.EndedTicks = DateTime.UtcNow.Ticks; _open.Remove(attempt); }
    }
    public void Message(MessageSample sample) { lock (_gate) { _messages.Add(sample); } }
    /// <summary>A viewer found a gap, an unknown generation or a disagreeing beacon.</summary>
    public void Gap() { lock (_gate) { _gaps++; } }
    /// <summary>A viewer asked for the complete state.</summary>
    public void Resync() { lock (_gate) { _resyncs++; } }
    public void PageLoaded(long ticks) { lock (_gate) { _pageLoads.Add(ticks); } }
    public void PageFailed(string reason)
    {
        lock (_gate) { _pageFailures[reason] = _pageFailures.GetValueOrDefault(reason) + 1; }
    }

    /// <summary>One status line: viewers watching now, connections opening, messages and failures so far.</summary>
    public string Status()
    {
        lock (_gate)
        {
            var watching = _open.Count(x => x.FirstStateTicks is not null);
            var failed = _attempts.Count(x => x.Failure is not null);
            var latest = _messages.Where(x => x.Version >= 0).Select(x => x.Version).DefaultIfEmpty(-1).Last();
            return string.Create(CultureInfo.InvariantCulture,
                $"{DateTime.UtcNow:HH:mm:ss} watching {watching}, opening {_open.Count - watching}, messages {_messages.Count}, latest version {latest}, gaps {_gaps}, resyncs {_resyncs}, failed connections {failed}, page loads {_pageLoads.Count} (failed {_pageFailures.Values.Sum()})");
        }
    }

    public LoadRun Snapshot()
    {
        lock (_gate) { return new([.. _messages], [.. _attempts], [.. _pageLoads], new(_pageFailures), _gaps, _resyncs); }
    }
}

public sealed record LoadRun(MessageSample[] Messages, ConnectionAttempt[] Attempts, long[] PageLoads, Dictionary<string, int> PageFailures, int Gaps, int Resyncs)
{
    /// <summary>Messages that carry race data or a status: State, Sync and Delta.</summary>
    public IEnumerable<MessageSample> States => Messages.Where(x => x.Kind is "State" or "Sync" or "Delta");

    /// <summary>Raw samples for later analysis: one line per received hub message (version -1 when it carries no race data).</summary>
    public string StatesCsv()
    {
        var builder = new StringBuilder("viewer,version,received_utc_ticks,updated_utc_ticks,bytes,first,kind\n");
        foreach (var s in Messages) { builder.Append(CultureInfo.InvariantCulture, $"{s.Viewer},{s.Version},{s.ReceivedTicks},{s.UpdatedTicks},{s.Bytes},{(s.First ? 1 : 0)},{s.Kind}\n"); }
        return builder.ToString();
    }

    public string AttemptsCsv()
    {
        var builder = new StringBuilder("viewer,started_utc_ticks,watching_utc_ticks,first_state_utc_ticks,ended_utc_ticks,failure\n");
        foreach (var a in Attempts)
        {
            builder.Append(CultureInfo.InvariantCulture, $"{a.Viewer},{a.StartedTicks},{a.WatchingTicks},{a.FirstStateTicks},{a.EndedTicks},\"{a.Failure?.Replace("\"", "'", StringComparison.Ordinal)}\"\n");
        }
        return builder.ToString();
    }
}
