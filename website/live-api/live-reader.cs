#:package Microsoft.AspNetCore.SignalR.Client@10.0.15
#:property PublishAot=false
// OpenSkiTime Live API reader for .NET 10 or later. MIT license. https://openskiti.me/live-api/
// Save this file in a folder of its own.
//
//   dotnet run live-reader.cs                                           lists the races published now
//   dotnet run live-reader.cs -- https://live.openskiti.me <sessionId>  follows one race
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.SignalR.Client;

Console.OutputEncoding = new UTF8Encoding(false);
var server = args.Length > 0 ? args[0] : "https://live.openskiti.me";
if (args.Length < 2)
{
    // The hosted service sleeps between races: the first answer can take a couple of minutes.
    using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
    var races = await http.GetFromJsonAsync<Race[]>($"{server}/api/sessions") ?? [];
    if (races.Length == 0) { Console.WriteLine("No race is published now."); }
    foreach (var race in races) { Console.WriteLine($"{race.SessionId} | {race.Date} | {race.Discipline} | {race.Name}"); }
    return;
}

var named = false;
await LiveReader.FollowAsync(server, args[1], (state, changes) =>
{
    if (!named) { Console.WriteLine($"Race | {state.Competition.Name} | {state.Competitors.Length} competitors"); }
    named = true;
    foreach (var change in changes)
    {
        var who = state.Competitors.FirstOrDefault(x => x.Bib == change.Row.Bib);
        Console.WriteLine($"Run {change.Run} | {change.Row.Bib} {who?.LastName} {who?.FirstName} | {Describe(change)}");
    }
}, phase => Console.WriteLine(phase == Phase.Deleted ? "Deleted" : "Waiting for the publisher"));

static string Describe(Change change) => change.Kind switch
{
    ChangeKind.Intermediate => $"Intermediate {change.Number} {(change.Hundredths is { } x ? Time(x) : "removed")}",
    _ when change.Row.Status != "Finished" => change.Row.Status,
    _ => $"{(change.Kind == ChangeKind.Time ? "Time changed" : "Finished")} {Time(change.Row.Hundredths ?? 0)}, rank {change.Row.Rank}"
};
// Hundredths as 50.12 or 1:02.03, with integer arithmetic.
static string Time(long x) => x >= 6000 ? $"{x / 6000}:{x / 100 % 60:00}.{x % 100:00}" : $"{x / 100}.{x % 100:00}";

// The messages. Properties not listed here are ignored; add the ones you need.
record Race(Guid SessionId, string Name, string Discipline, string Date);
record Competition(string Name, string Discipline, string Date, string Codex);
record Competitor(int Bib, string LastName, string FirstName, string Nation, string Club, string FisCode, string Category);
record Intermediate(int Number, long Hundredths);
record Forerunner(string Letter, string LastName, string FirstName, string Nation, string Status);
record Row(int Bib, string Status, long? Hundredths, int? Rank, long? Difference, DateTimeOffset At, DateTimeOffset? StartedAt,
    Intermediate[]? Intermediates, long? TotalHundredths, int? TotalRank, long? TotalDifference);
record Run(int Number, int[] StartOrder, Row[] Results, Forerunner[]? Forerunners);
record State(long Version, Competition Competition, Competitor[] Competitors, int CurrentRun, Run[] Runs, DateTimeOffset UpdatedAt, bool Paused);
record SyncMessage(string Status, string? Generation, long Revision, State? State);
record RunDelta(int Number, Row[] Results, int[]? Order, Forerunner[]? Forerunners);
record DeltaMessage(string Generation, long BaseRevision, long Revision, long Version, DateTimeOffset UpdatedAt, bool Paused, RunDelta[] Runs);
record BeaconMessage(string Status, string? Generation, long Revision);

enum Phase { Subscribing, Live, Awaiting, Deleted }
enum Outcome { None, Changed, Resync }
enum ChangeKind { Status, Time, Intermediate }
record Change(int Run, Row Row, ChangeKind Kind, int Number = 0, long? Hundredths = null);

/// <summary>The state of one race, kept up to date from the messages of the server.</summary>
sealed class RaceState
{
    private string? _generation;
    private long _revision;
    private DateTimeOffset? _resyncSince; // When the complete state was asked for.
    public Phase Phase { get; private set; } = Phase.Subscribing;
    public State? State { get; private set; }
    public bool NeedsResync => _resyncSince is not null;

    /// <summary>A new connection: the previous state stays until the subscription is answered.</summary>
    public void Connected() { Phase = Phase.Subscribing; _generation = null; _revision = 0; _resyncSince = null; }

    /// <summary>The complete state, or the reason why there is none. Always the newest on this connection.</summary>
    public Outcome Sync(SyncMessage message)
    {
        var live = message.Status == "Live";
        _resyncSince = null;
        Phase = live ? Phase.Live : message.Status == "Deleted" ? Phase.Deleted : Phase.Awaiting;
        _generation = live ? message.Generation : null;
        _revision = live ? message.Revision : 0;
        if (live) { State = message.State; }
        else if (Phase == Phase.Deleted) { State = null; }
        return Outcome.Changed;
    }

    /// <summary>One change. It applies only to the revision it names.</summary>
    public Outcome Delta(DeltaMessage message)
    {
        if (Phase == Phase.Subscribing || NeedsResync) { return Outcome.None; }
        if (Phase != Phase.Live || message.Generation != _generation) { return Resync(); }
        if (message.Revision <= _revision) { return Outcome.None; }
        if (message.BaseRevision != _revision || Merge(State!, message) is not { } next) { return Resync(); }
        _revision = message.Revision;
        State = next;
        return Outcome.Changed;
    }

    /// <summary>What the server has now: confirms the state, or shows that a message was missed.</summary>
    public Outcome Beacon(BeaconMessage message)
    {
        if (Phase == Phase.Subscribing) { return Outcome.None; }
        if (_resyncSince is { } since) { return DateTimeOffset.UtcNow - since >= TimeSpan.FromSeconds(30) ? Resync() : Outcome.None; }
        var agrees = message.Status == "Live"
            ? Phase == Phase.Live && message.Generation == _generation && message.Revision == _revision
            : Phase == (message.Status == "Deleted" ? Phase.Deleted : Phase.Awaiting);
        return agrees ? Outcome.None : Resync();
    }

    private Outcome Resync() { _resyncSince = DateTimeOffset.UtcNow; return Outcome.Resync; }

    // The state after a delta, or null when the delta does not fit it.
    private static State? Merge(State state, DeltaMessage delta)
    {
        if (delta.Runs.Any(x => state.Runs.All(run => run.Number != x.Number))) { return null; }
        var runs = new List<Run>();
        foreach (var run in state.Runs)
        {
            if (delta.Runs.FirstOrDefault(x => x.Number == run.Number) is not { } change) { runs.Add(run); continue; }
            var rows = run.Results.ToDictionary(x => x.Bib);
            var bibs = run.Results.Select(x => x.Bib).ToList();
            foreach (var row in change.Results)
            {
                if (!rows.ContainsKey(row.Bib)) { bibs.Add(row.Bib); }
                rows[row.Bib] = row;
            }
            var order = change.Order ?? [.. bibs];
            if (order.Length != rows.Count || order.Distinct().Count() != order.Length || order.Any(x => !rows.ContainsKey(x))) { return null; }
            runs.Add(run with { Results = [.. order.Select(x => rows[x])], Forerunners = change.Forerunners ?? run.Forerunners });
        }
        return state with { Version = delta.Version, UpdatedAt = delta.UpdatedAt, Paused = delta.Paused, Runs = [.. runs] };
    }
}

static class LiveReader
{
    /// <summary>What happened between two states: status changes, intermediate times and corrected run times.</summary>
    public static List<Change> Changes(State before, State after)
    {
        var result = new List<Change>();
        foreach (var run in after.Runs)
        {
            var rows = before.Runs.FirstOrDefault(x => x.Number == run.Number)?.Results.ToDictionary(x => x.Bib) ?? [];
            foreach (var row in run.Results)
            {
                var old = rows.GetValueOrDefault(row.Bib);
                var status = new List<Change>();
                if (row.Status != (old?.Status ?? "Ready")) { status.Add(new(run.Number, row, ChangeKind.Status)); }
                else if (row.Status == "Finished" && row.Hundredths != old?.Hundredths) { status.Add(new(run.Number, row, ChangeKind.Time)); }
                var times = (row.Intermediates ?? []).ToDictionary(x => x.Number, x => (long?)x.Hundredths);
                var oldTimes = (old?.Intermediates ?? []).ToDictionary(x => x.Number, x => (long?)x.Hundredths);
                var intermediates = oldTimes.Keys.Union(times.Keys).Order()
                    .Where(x => times.GetValueOrDefault(x) != oldTimes.GetValueOrDefault(x))
                    .Select(x => new Change(run.Number, row, ChangeKind.Intermediate, x, times.GetValueOrDefault(x)));
                // When a break in the connection joined several changes, a start comes before and a finish after the intermediates.
                result.AddRange(row.Status == "OnCourse" ? status.Concat(intermediates) : intermediates.Concat(status));
            }
        }
        return result;
    }

    /// <summary>Follows one race until it is deleted. The callbacks run one at a time.</summary>
    public static async Task FollowAsync(string server, string sessionId, Action<State, List<Change>> onState, Action<Phase> onStatus)
    {
        var race = new RaceState();
        var gate = new Lock();
        for (var attempt = 0; ;)
        {
            await using var hub = new HubConnectionBuilder().WithUrl($"{server}/live").Build();
            var closed = new TaskCompletionSource();
            hub.Closed += _ => { closed.TrySetResult(); return Task.CompletedTask; };
            var lastResync = DateTimeOffset.MinValue;
            void Handle(Func<Outcome> step)
            {
                lock (gate)
                {
                    var before = race.State;
                    var outcome = step();
                    if (outcome == Outcome.Resync)
                    {
                        var earliest = lastResync == DateTimeOffset.MinValue ? TimeSpan.Zero : lastResync.AddSeconds(3) - DateTimeOffset.UtcNow;
                        var delay = TimeSpan.FromMilliseconds(Random.Shared.Next(500, 5000));
                        _ = ResyncAsync(delay > earliest ? delay : earliest);
                    }
                    if (outcome != Outcome.Changed) { return; }
                    if (race.Phase == Phase.Live) { onState(race.State!, before is null ? [] : Changes(before, race.State!)); }
                    else { onStatus(race.Phase); }
                    if (race.Phase == Phase.Deleted) { closed.TrySetResult(); }
                }
            }
            async Task ResyncAsync(TimeSpan delay)
            {
                await Task.Delay(delay);
                lock (gate)
                {
                    if (!race.NeedsResync || hub.State != HubConnectionState.Connected) { return; }
                    lastResync = DateTimeOffset.UtcNow;
                }
                try { await hub.SendAsync("Resync"); }
                catch (Exception) { /* The connection broke; the loop connects again. */ }
            }
            hub.On<SyncMessage>("Sync", message => Handle(() => race.Sync(message)));
            hub.On<DeltaMessage>("Delta", message => Handle(() => race.Delta(message)));
            hub.On<BeaconMessage>("Beacon", message => Handle(() => race.Beacon(message)));
            lock (gate) { race.Connected(); }
            try
            {
                await hub.StartAsync();
                await hub.InvokeAsync("WatchSync", sessionId, 1);
                attempt = 0;
                await closed.Task;
            }
            catch (Exception) when (hub.State != HubConnectionState.Connected) { /* No connection; try again below. */ }
            if (race.Phase == Phase.Deleted) { return; }
            await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(15000, 1000 * Math.Pow(2, attempt++)) * (0.5 + Random.Shared.NextDouble() / 2)));
        }
    }
}
