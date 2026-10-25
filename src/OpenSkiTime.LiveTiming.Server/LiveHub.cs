using Microsoft.AspNetCore.SignalR;

namespace OpenSkiTime.LiveTiming.Server;

public sealed class LiveHub(LiveFeed feed, SessionStore store, ServerStats stats, TimeProvider time) : Hub
{
    // A viewer resyncs only after a gap; more often than this is ignored, and the viewer asks again after its timeout.
    private static readonly TimeSpan s_resyncInterval = TimeSpan.FromSeconds(2);
    public override Task OnConnectedAsync() { stats.Connected(Context.ConnectionId); return base.OnConnectedAsync(); }
    public override Task OnDisconnectedAsync(Exception? exception) { stats.Disconnected(Context.ConnectionId); return base.OnDisconnectedAsync(exception); }
    /// <summary>Subscription of a page without viewer synchronization (opened before it existed): a full "State" after every change.</summary>
    public Task Watch(string sessionId) => Subscribe(sessionId, sync: false);
    /// <summary>Subscription with viewer synchronization version <paramref name="version"/>: "Sync", "Delta" and "Beacon".</summary>
    public Task WatchSync(string sessionId, int version)
    {
        if (version != LiveSync.Version) { throw new HubException("Unsupported sync version."); }
        return Subscribe(sessionId, sync: true);
    }
    /// <summary>The complete state (or status) of the watched session for a synchronizing viewer that found a gap.</summary>
    public async Task Resync()
    {
        if (Item("session") is not Guid id || Item("sync") is not true) { return; }
        var now = time.GetTimestamp();
        if (Item("resync") is long last && time.GetElapsedTime(last, now) < s_resyncInterval) { return; }
        Context.Items["resync"] = now;
        await feed.ResyncAsync(Context.ConnectionId, id);
    }
    private object? Item(string key) => Context.Items.TryGetValue(key, out var value) ? value : null;
    private async Task Subscribe(string sessionId, bool sync)
    {
        if (!Guid.TryParse(sessionId, out var id)) { throw new HubException("Invalid session."); }
        // One public session subscription per connection, preventing unbounded group membership.
        var previous = Item("session") as Guid?;
        Context.Items["session"] = id; Context.Items["sync"] = sync;
        stats.Watching(Context.ConnectionId, id);
        await feed.WatchAsync(Context.ConnectionId, id, previous, sync);
        await Clients.Caller.SendAsync("Radio", store.Announce(id));
        // The public viewer count of the race page, this viewer included; the sampler sends later changes to the group.
        await Clients.Caller.SendAsync("Viewers", stats.Viewers(id));
    }
}
