namespace OpenSkiTime.LiveTiming.Server;

/// <summary>
/// One asynchronous gate per session, so that work on a session (a mutation and its broadcast, a subscription reply, a
/// beacon) runs one at a time without blocking other sessions. A gate exists only while someone holds or waits for it, so
/// memory is bounded by concurrent work, not by the number of session IDs ever used.
/// </summary>
public sealed class SessionGates
{
    private sealed class Gate : IDisposable
    {
        public readonly SemaphoreSlim Turn = new(1, 1);
        public int Users;
        public void Dispose() => Turn.Dispose();
    }
    private sealed class Held(SessionGates owner, Guid id, Gate gate) : IDisposable
    {
        private int _released;
        public void Dispose() { if (Interlocked.Exchange(ref _released, 1) == 0) { owner.Exit(id, gate); } }
    }
    private readonly Dictionary<Guid, Gate> _gates = [];
    private readonly Lock _lock = new();

    /// <summary>Waits for the session's turn; disposing the result ends it.</summary>
    public async Task<IDisposable> EnterAsync(Guid id)
    {
        Gate? gate;
        lock (_lock)
        {
            if (!_gates.TryGetValue(id, out gate)) { _gates[id] = gate = new(); }
            gate.Users++;
        }
        await gate.Turn.WaitAsync();
        return new Held(this, id, gate);
    }

    /// <summary>Gates currently held or awaited.</summary>
    public int Count { get { lock (_lock) { return _gates.Count; } } }

    private void Exit(Guid id, Gate gate)
    {
        gate.Turn.Release();
        lock (_lock)
        {
            // Nobody holds or awaits it any more, so it can go; the next user of the session creates a new one.
            if (--gate.Users == 0) { _gates.Remove(id); gate.Dispose(); }
        }
    }
}
