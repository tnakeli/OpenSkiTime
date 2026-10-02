using Microsoft.AspNetCore.SignalR;

namespace OpenSkiTime.LiveTiming.Server;

public sealed class LiveHub(SessionStore store) : Hub
{
    public async Task Watch(string sessionId)
    {
        if (!Guid.TryParse(sessionId, out var id)) { throw new HubException("Invalid session."); }
        // One public session subscription per connection, preventing unbounded group membership.
        if (Context.Items.TryGetValue("session", out var previous))
        { await Groups.RemoveFromGroupAsync(Context.ConnectionId, (string)previous!); }
        var group = id.ToString(); Context.Items["session"] = group;
        await Groups.AddToGroupAsync(Context.ConnectionId, group);
        await Clients.Caller.SendAsync("State", store.Read(id));
    }
}
