using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;

namespace OpenSkiTime.LiveTiming.Server;

/// <summary>
/// Sends the SignalR "Radio" announcement of a session when it changes: on clip uploads, worker status, every state change
/// (which can make clips stale) and on a timer for expiry and heartbeat loss.
/// </summary>
public sealed class RadioBroadcaster(SessionStore store, IHubContext<LiveHub> hub)
{
    private readonly ConcurrentDictionary<Guid, string> _sent = new();
    public async Task SendAsync(Guid id, bool force = false)
    {
        var announcement = store.Announce(id);
        // The server time changes on every call; only availability and clips decide whether viewers need a message.
        var key = JsonSerializer.Serialize(announcement with { ServerTime = default }, LiveJson.Options);
        if (!force && _sent.TryGetValue(id, out var previous) && previous == key) { return; }
        _sent[id] = key;
        await hub.Clients.Group(id.ToString()).SendAsync("Radio", announcement);
    }
    public void Forget(Guid id) => _sent.TryRemove(id, out _);
    public IEnumerable<Guid> Known => _sent.Keys;
}

public sealed class RadioMonitor(SessionStore store, RadioBroadcaster broadcaster, TimeProvider time) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2), time);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            var active = store.RadioSessions();
            // A session that disappeared (deleted or expired) announces no radio once and is then forgotten.
            foreach (var id in active.Concat(broadcaster.Known.Except(active)).ToArray()) { await broadcaster.SendAsync(id); }
            foreach (var id in broadcaster.Known.Except(active).ToArray()) { broadcaster.Forget(id); }
        }
    }
}
