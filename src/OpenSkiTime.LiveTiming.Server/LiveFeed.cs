using Microsoft.AspNetCore.SignalR;

namespace OpenSkiTime.LiveTiming.Server;

/// <summary>
/// Sends every state message of a session in revision order. A mutation and its broadcast, a subscription reply, a resync
/// reply and a beacon of one session run one at a time under the session's gate, so each connection receives them in the
/// order of the revisions they carry. The store lock is held only for the mutation or read itself, never across network
/// I/O, and one session's gate never delays another session.
///
/// Every viewer joins the session group (Radio, Viewers). A synchronizing viewer (WatchSync) also joins the sync group and
/// gets "Sync" full states, "Delta" changes and "Beacon" revisions; an older page (Watch) joins the legacy group and gets
/// the full "State" after every change, exactly as before deltas existed.
/// </summary>
public sealed class LiveFeed(SessionStore store, IHubContext<LiveHub> hub)
{
    private readonly SessionGates _gates = new();
    public static string Group(Guid id) => id.ToString();
    public static string SyncGroup(Guid id) => "sync:" + id;
    public static string LegacyGroup(Guid id) => "state:" + id;

    /// <summary>Gates currently held or awaited (bounded by concurrent work).</summary>
    public int Gates => _gates.Count;

    /// <summary>
    /// Runs one mutation under the session's gate and broadcasts its result before the next one may start. A mutation that
    /// throws sends nothing; one that returns null (nothing changed) sends nothing either.
    /// </summary>
    public async Task PublishAsync(Guid id, Func<LiveChange?> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        using var turn = await _gates.EnterAsync(id);
        if (mutate() is not { } change) { return; }
        var delta = change.Before is { } before ? LiveSync.Delta(before, change.State, change.Generation, change.Revision - 1, change.Revision) : null;
        await Task.WhenAll(
            hub.Clients.Group(LegacyGroup(id)).SendAsync("State", change.State),
            delta is not null
                ? hub.Clients.Group(SyncGroup(id)).SendAsync("Delta", delta)
                : hub.Clients.Group(SyncGroup(id)).SendAsync("Sync", new LiveSyncState(LiveSessionStatus.Live, change.Generation, change.Revision, change.State)));
    }

    /// <summary>Deletes under the session's gate and tells every viewer: a legacy page gets null, a synchronizing one Deleted.</summary>
    public async Task DeleteAsync(Guid id, Action delete)
    {
        ArgumentNullException.ThrowIfNull(delete);
        using var turn = await _gates.EnterAsync(id);
        delete();
        await Task.WhenAll(
            hub.Clients.Group(LegacyGroup(id)).SendAsync("State", (LiveSnapshot?)null),
            hub.Clients.Group(SyncGroup(id)).SendAsync("Sync", new LiveSyncState(LiveSessionStatus.Deleted, null, 0, null)));
    }

    /// <summary>
    /// Subscribes a connection and sends it the session's state (or status) under the session's gate: a broadcast that ran
    /// before did not reach the connection, but the state read here includes it; every later one comes after this reply.
    /// </summary>
    public async Task WatchAsync(string connection, Guid id, Guid? previous, bool sync)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (previous is { } left)
        {
            await hub.Groups.RemoveFromGroupAsync(connection, Group(left));
            await hub.Groups.RemoveFromGroupAsync(connection, SyncGroup(left));
            await hub.Groups.RemoveFromGroupAsync(connection, LegacyGroup(left));
        }
        using var turn = await _gates.EnterAsync(id);
        await hub.Groups.AddToGroupAsync(connection, Group(id));
        await hub.Groups.AddToGroupAsync(connection, sync ? SyncGroup(id) : LegacyGroup(id));
        var state = store.Sync(id);
        await (sync ? hub.Clients.Client(connection).SendAsync("Sync", state) : hub.Clients.Client(connection).SendAsync("State", state.State));
    }

    /// <summary>The full state (or status) for one synchronizing connection that found a gap, in the session's order.</summary>
    public async Task ResyncAsync(string connection, Guid id)
    {
        ArgumentNullException.ThrowIfNull(connection);
        using var turn = await _gates.EnterAsync(id);
        await hub.Clients.Client(connection).SendAsync("Sync", store.Sync(id));
    }

    /// <summary>The session's generation and revision (or status without state) for its synchronizing viewers, in order.</summary>
    public async Task BeaconAsync(Guid id, CancellationToken ct)
    {
        using var turn = await _gates.EnterAsync(id);
        var state = store.Sync(id);
        await hub.Clients.Group(SyncGroup(id)).SendAsync("Beacon", new LiveSyncBeacon(state.Status, state.Generation, state.Revision), ct);
    }
}

/// <summary>
/// Sends a sync beacon to the viewers of every watched session about every 30 seconds (LiveTiming:SyncBeaconSeconds), so a
/// viewer that lost the last delta of a quiet race notices it without polling. Beacons of sessions run concurrently.
/// </summary>
public sealed class LiveBeacon(LiveFeed feed, ServerStats stats, IConfiguration config, TimeProvider time) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Clamp(config.GetValue("LiveTiming:SyncBeaconSeconds", 30), 1, 300));
        using var timer = new PeriodicTimer(interval, time);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        { await Task.WhenAll(stats.Watched().Select(id => feed.BeaconAsync(id, stoppingToken))); }
    }
}
