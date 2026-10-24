using System.Diagnostics;

namespace OpenSkiTime.LiveTiming.Server;

/// <summary>
/// Operator load report. The hub reports which session each viewer connection watches; a sampler records the viewer
/// counts of active sessions and the process CPU every five seconds; HTTP response times are kept for five minutes.
/// RAM only and bounded: one entry per connection, samples only for stored sessions, a capped response ring.
/// </summary>
public sealed class ServerStats(TimeProvider time)
{
    public static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan ViewerWindow = TimeSpan.FromHours(1);
    public static readonly TimeSpan ResponseWindow = TimeSpan.FromMinutes(5);
    public const int MaxResponses = 20000;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Guid?> _connections = [];
    private readonly Dictionary<Guid, int> _viewers = [];
    private readonly Dictionary<Guid, Queue<(DateTimeOffset At, int Count)>> _samples = [];
    private readonly Queue<(DateTimeOffset At, double Ms)> _responses = new();
    private TimeSpan _cpuTime;
    private long _cpuAt;
    private double _cpuPercent;

    public void Connected(string connection) { lock (_gate) { _connections[connection] = null; } }
    public void Watching(string connection, Guid session)
    {
        lock (_gate)
        {
            if (_connections.GetValueOrDefault(connection) is { } previous) { Leave(previous); }
            _connections[connection] = session;
            _viewers[session] = _viewers.GetValueOrDefault(session) + 1;
        }
    }
    public void Disconnected(string connection)
    {
        lock (_gate)
        {
            if (_connections.Remove(connection, out var session) && session is { } watched) { Leave(watched); }
        }
    }
    private void Leave(Guid session)
    {
        if (_viewers.GetValueOrDefault(session) <= 1) { _viewers.Remove(session); } else { _viewers[session]--; }
    }
    public void Response(TimeSpan elapsed)
    {
        var now = time.GetUtcNow();
        lock (_gate)
        {
            _responses.Enqueue((now, elapsed.TotalMilliseconds));
            while (_responses.Count > MaxResponses || (_responses.Count > 0 && now - _responses.Peek().At > ResponseWindow)) { _responses.Dequeue(); }
        }
    }
    /// <summary>Records the viewers of every stored session and the CPU use since the previous sample; forgets other sessions.</summary>
    public void Sample(IReadOnlyCollection<Guid> sessions)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        var now = time.GetUtcNow();
        TimeSpan cpu;
        using (var process = Process.GetCurrentProcess()) { cpu = process.TotalProcessorTime; }
        var at = time.GetTimestamp();
        lock (_gate)
        {
            if (_cpuAt != 0)
            {
                var wall = time.GetElapsedTime(_cpuAt, at);
                if (wall > TimeSpan.Zero) { _cpuPercent = Math.Clamp((cpu - _cpuTime) / wall / Environment.ProcessorCount * 100, 0, 100); }
            }
            _cpuTime = cpu; _cpuAt = at;
            foreach (var gone in _samples.Keys.Where(x => !sessions.Contains(x)).ToArray()) { _samples.Remove(gone); }
            foreach (var session in sessions)
            {
                if (!_samples.TryGetValue(session, out var history)) { _samples[session] = history = new(); }
                history.Enqueue((now, _viewers.GetValueOrDefault(session)));
                while (history.Count > 0 && now - history.Peek().At > ViewerWindow) { history.Dequeue(); }
            }
        }
    }
    public LiveServerStats Read(Guid session, bool hasState)
    {
        var now = time.GetUtcNow();
        lock (_gate)
        {
            var current = _viewers.GetValueOrDefault(session);
            // The current count is a sample too, so a session younger than one sample still reports itself.
            var counts = (_samples.TryGetValue(session, out var history) ? history.Where(x => now - x.At <= ViewerWindow).Select(x => x.Count) : []).Append(current).ToArray();
            var times = _responses.Where(x => now - x.At <= ResponseWindow).Select(x => x.Ms).Order().ToArray();
            var memory = GC.GetGCMemoryInfo();
            return new(now, hasState,
                new(current, counts.Min(), counts.Max(), Math.Round(counts.Average(), 1), (int)ViewerWindow.TotalMinutes), _connections.Count,
                new(times.Length, times.Length == 0 ? 0 : Math.Round(times.Average(), 1), times.Length == 0 ? 0 : Math.Round(times[(int)Math.Ceiling(times.Length * 0.95) - 1], 1),
                    times.Length == 0 ? 0 : Math.Round(times[^1], 1), (int)ResponseWindow.TotalMinutes),
                new(Math.Round(_cpuPercent, 1), Environment.ProcessorCount, Environment.WorkingSet, GC.GetTotalMemory(false), memory.TotalAvailableMemoryBytes,
                    ThreadPool.PendingWorkItemCount));
        }
    }
}

public sealed class ServerStatsSampler(SessionStore store, ServerStats stats, TimeProvider time) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(ServerStats.SampleInterval, time);
        do { stats.Sample(store.Sessions()); }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
