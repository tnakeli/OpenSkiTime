using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenSkiTime.LiveTiming;

/// <summary>
/// What a viewer subscription learns about a session: Live has state; Deleted means the server holds a deletion marker;
/// Awaiting means no state and no known deletion (created but unpublished, not yet restored after a server restart, or an
/// unknown ID).
/// </summary>
public enum LiveSessionStatus { Live, Deleted, Awaiting }

/// <summary>
/// The complete state of a session for a synchronizing viewer (SignalR "Sync"): the reply to WatchSync and Resync, and a
/// group broadcast when a change is not sent as a delta. Generation and Revision are set only while Live.
/// </summary>
public sealed record LiveSyncState(LiveSessionStatus Status, string? Generation, long Revision, LiveSnapshot? State);

/// <summary>Periodic SignalR "Beacon": the session's current generation and revision, or its status without state.</summary>
public sealed record LiveSyncBeacon(LiveSessionStatus Status, string? Generation, long Revision);

/// <summary>
/// The changed part of one run: complete result rows that replace the rows with the same bib or are new, the order of all
/// result rows when it is not the previous order with new rows appended, and the complete forerunner list when it changed.
/// </summary>
public sealed record LiveRunDelta(int Number, LiveResult[] Results,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int[]? Order = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] LiveForerunner[]? Forerunners = null);

/// <summary>
/// One accepted change of a session (SignalR "Delta"), applicable only to revision BaseRevision of the same generation and
/// only as a whole. Version, UpdatedAt and Paused replace the state's values; runs not listed are unchanged.
/// </summary>
public sealed record LiveDelta(string Generation, long BaseRevision, long Revision, long Version, DateTimeOffset UpdatedAt, bool Paused,
    LiveRunDelta[] Runs);

/// <summary>
/// Server-computed deltas between two published states. Values are copied exactly as published: nothing is recalculated.
/// A change that a delta cannot represent, or a delta not clearly smaller than the state, is sent as the full state.
/// </summary>
public static class LiveSync
{
    /// <summary>Viewer synchronization protocol declared by WatchSync; independent of the publisher protocol.</summary>
    public const int Version = 1;

    /// <summary>
    /// The delta from <paramref name="before"/> to <paramref name="after"/>, or null when the full state must be sent: the
    /// competition, competitor list, run set, current run, a run's start list or start order changed, a result row was
    /// removed, all forerunners were removed, or the delta would be more than half the size of the full state.
    /// </summary>
    public static LiveDelta? Delta(LiveSnapshot before, LiveSnapshot after, string generation, long baseRevision, long revision)
    {
        ArgumentNullException.ThrowIfNull(before); ArgumentNullException.ThrowIfNull(after); ArgumentNullException.ThrowIfNull(generation);
        if (before.Competition != after.Competition || before.CurrentRun != after.CurrentRun
            || !before.Competitors.SequenceEqual(after.Competitors) || before.Runs.Length != after.Runs.Length) { return null; }
        var runs = new List<LiveRunDelta>();
        for (var i = 0; i < after.Runs.Length; i++)
        {
            var old = before.Runs[i]; var run = after.Runs[i];
            if (old.Number != run.Number || old.ListCreatedAt != run.ListCreatedAt || !old.StartOrder.SequenceEqual(run.StartOrder)) { return null; }
            var previous = old.Results.ToDictionary(x => x.Bib);
            var bibs = run.Results.Select(x => x.Bib).ToHashSet();
            if (old.Results.Any(x => !bibs.Contains(x.Bib))) { return null; }
            var changed = run.Results.Where(x => !previous.TryGetValue(x.Bib, out var p) || !LiveResult.Equivalent(p, x)).ToArray();
            var derived = old.Results.Select(x => x.Bib).Concat(changed.Where(x => !previous.ContainsKey(x.Bib)).Select(x => x.Bib));
            var order = derived.SequenceEqual(run.Results.Select(x => x.Bib)) ? null : run.Results.Select(x => x.Bib).ToArray();
            var forerunnersChanged = (old.Forerunners is null) != (run.Forerunners is null) || !(old.Forerunners ?? []).SequenceEqual(run.Forerunners ?? []);
            if (forerunnersChanged && run.Forerunners is null) { return null; }
            if (changed.Length > 0 || order is not null || forerunnersChanged)
            { runs.Add(new(run.Number, changed, order, forerunnersChanged ? run.Forerunners : null)); }
        }
        var delta = new LiveDelta(generation, baseRevision, revision, after.Version, after.UpdatedAt, after.Paused, [.. runs]);
        return JsonSerializer.SerializeToUtf8Bytes(delta, LiveJson.Options).Length * 2 <= JsonSerializer.SerializeToUtf8Bytes(after, LiveJson.Options).Length
            ? delta : null;
    }

    /// <summary>
    /// Applies a delta as a whole, the same way the browser does (wwwroot/live-sync.js). Returns null when it does not fit
    /// the state: an unknown run, or an order that does not list every row exactly once. Revisions are the caller's concern.
    /// </summary>
    public static LiveSnapshot? Apply(LiveSnapshot state, LiveDelta delta)
    {
        ArgumentNullException.ThrowIfNull(state); ArgumentNullException.ThrowIfNull(delta);
        if (delta.Runs.Any(x => state.Runs.All(r => r.Number != x.Number))) { return null; }
        var runs = new LiveRun[state.Runs.Length];
        for (var i = 0; i < runs.Length; i++)
        {
            var run = state.Runs[i];
            if (delta.Runs.FirstOrDefault(x => x.Number == run.Number) is not { } change) { runs[i] = run; continue; }
            var rows = run.Results.ToDictionary(x => x.Bib);
            var added = new List<int>();
            foreach (var row in change.Results) { if (!rows.ContainsKey(row.Bib)) { added.Add(row.Bib); } rows[row.Bib] = row; }
            var order = change.Order ?? [.. run.Results.Select(x => x.Bib), .. added];
            if (order.Length != rows.Count || order.Distinct().Count() != order.Length || order.Any(x => !rows.ContainsKey(x))) { return null; }
            runs[i] = run with { Results = [.. order.Select(x => rows[x])], Forerunners = change.Forerunners ?? run.Forerunners };
        }
        return state with { Version = delta.Version, UpdatedAt = delta.UpdatedAt, Paused = delta.Paused, Runs = runs };
    }
}
