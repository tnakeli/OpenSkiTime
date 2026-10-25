using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using OpenSkiTime.LiveTiming.Harness;
using OpenSkiTime.LiveTiming.Server;
using Xunit;

namespace OpenSkiTime.LiveTiming.Tests;

// Server-computed viewer deltas (LiveSync), the session store's generations, revisions and statuses, and the per-session gates.
public sealed class LiveSyncTests
{
    private static string Json(LiveSnapshot state) => JsonSerializer.Serialize(state, LiveJson.Options);
    private static LiveSnapshot Finished(LiveSnapshot state, params (int Bib, long Time)[] finishes)
    {
        foreach (var (bib, time) in finishes)
        { state = SyntheticRace.Update(SyntheticRace.Update(SyntheticRace.Update(state, bib, LiveStatus.OnCourse), bib, LiveStatus.OnCourse, split: 1), bib, LiveStatus.Finished, time); }
        return state;
    }
    private static LiveDelta Delta(LiveSnapshot before, LiveSnapshot after) => Assert.IsType<LiveDelta>(LiveSync.Delta(before, after, "g", 4, 5));
    private static void Applies(LiveSnapshot before, LiveSnapshot after) => Assert.Equal(Json(after), Json(LiveSync.Apply(before, Delta(before, after))!));
    private static LiveSnapshot WithRun(LiveSnapshot state, Func<LiveRun, LiveRun> change) => state with { Runs = [change(state.Runs[0]), .. state.Runs[1..]] };

    [Fact]
    public void AStartIsOneCompleteRowAndCarriesVersionTimeAndPause()
    {
        var before = SyntheticRace.Create(20);
        var after = SyntheticRace.Update(before, 3, LiveStatus.OnCourse) with { Paused = true };
        var delta = Delta(before, after);
        Assert.Equal(("g", 4L, 5L, after.Version, after.UpdatedAt, true), (delta.Generation, delta.BaseRevision, delta.Revision, delta.Version, delta.UpdatedAt, delta.Paused));
        var run = Assert.Single(delta.Runs);
        // The row as published, including source ticks; nothing else of the run.
        Assert.Same(after.Runs[0].Results.Single(x => x.Bib == 3), Assert.Single(run.Results));
        Assert.Equal(TimeSpan.FromHours(12).Ticks + 3 * TimeSpan.TicksPerSecond, run.Results[0].StartSourceTicks);
        Assert.Null(run.Order); Assert.Null(run.Forerunners);
        Applies(before, after);
        // No order or forerunner fields are sent when nothing changed them.
        var json = JsonSerializer.Serialize(delta, LiveJson.Options);
        Assert.DoesNotContain("\"order\"", json, StringComparison.Ordinal); Assert.DoesNotContain("\"forerunners\"", json, StringComparison.Ordinal);
        Assert.Contains("\"startSourceTicks\":\"" + run.Results[0].StartSourceTicks, json, StringComparison.Ordinal);
    }

    [Fact]
    public void AFinishThatMovesRanksSendsEveryMovedRowExactlyAsPublished()
    {
        var before = SyntheticRace.Update(Finished(SyntheticRace.Create(30), (1, 5200), (2, 5100), (3, 5300)), 4, LiveStatus.OnCourse);
        var after = SyntheticRace.Update(before, 4, LiveStatus.Finished, 5000);
        var delta = Delta(before, after);
        // The new leader changes every finisher's rank and difference: four complete rows in one atomic delta.
        Assert.Equal([1, 2, 3, 4], delta.Runs[0].Results.Select(x => x.Bib).Order());
        foreach (var row in delta.Runs[0].Results) { Assert.True(LiveResult.Equivalent(after.Runs[0].Results.Single(x => x.Bib == row.Bib), row)); }
        Assert.Equal([(1, 3, 200L), (2, 2, 100L), (3, 4, 300L), (4, 1, 0L)], delta.Runs[0].Results.OrderBy(x => x.Bib).Select(x => (x.Bib, x.Rank!.Value, x.Difference!.Value)));
        Applies(before, after);
        // Splits are compared by value: an equal intermediate in a new array is no change.
        var copy = WithRun(after, run => run with { Results = [.. run.Results.Select(x => x with { Intermediates = x.Intermediates?.ToArray() })] });
        Assert.Empty(Delta(after, copy).Runs);
    }

    [Fact]
    public void ChangesADeltaCannotRepresentSendTheFullState()
    {
        var state = SyntheticRace.Update(SyntheticRace.Create(30), 1, LiveStatus.OnCourse);
        var second = new LiveRun(2, state.UpdatedAt, [3, 2, 1], [new(3, LiveStatus.Ready, null, null, null, state.UpdatedAt)]);
        LiveSnapshot[] full =
        [
            state with { Competition = state.Competition with { Name = "Renamed" } },
            state with { Competitors = [.. state.Competitors[..^1]] },
            state with { Competitors = [.. state.Competitors.Select(x => x.Bib == 2 ? x with { Club = "Other" } : x)] },
            state with { Runs = [.. state.Runs, second] },
            WithRun(state, run => run with { StartOrder = [.. run.StartOrder.Reverse()] }),
            WithRun(state, run => run with { ListCreatedAt = run.ListCreatedAt.AddMinutes(1) }),
            WithRun(state, run => run with { Results = run.Results[1..] }),
        ];
        foreach (var after in full) { Assert.Null(LiveSync.Delta(state, after, "g", 1, 2)); }
        var twoRuns = state with { Runs = [.. state.Runs, second] };
        Assert.Null(LiveSync.Delta(twoRuns, twoRuns with { CurrentRun = 2 }, "g", 1, 2));
        Assert.Null(LiveSync.Delta(twoRuns, twoRuns with { Runs = [twoRuns.Runs[1], twoRuns.Runs[0]] }, "g", 1, 2));
        // Removing every forerunner leaves no list to send; the full state says it.
        LiveForerunner[] runners = [new("A", "FORERUNNER", "Synthetic", "FIN", LiveStatus.OnCourse)];
        var withRunners = WithRun(state, run => run with { Forerunners = runners });
        Assert.Null(LiveSync.Delta(withRunners, state, "g", 1, 2));
    }

    [Fact]
    public void ADeltaThatIsNotClearlySmallerThanTheStateIsReplacedByIt()
    {
        var before = SyntheticRace.Create(10);
        // Every row changes: the delta would be more than half the size of the state.
        var after = WithRun(before, run => run with { Results = [.. run.Results.Select(x => x with { Status = LiveStatus.DNS, At = x.At.AddSeconds(1) })] });
        Assert.Null(LiveSync.Delta(before, after, "g", 1, 2));
        Assert.NotNull(LiveSync.Delta(before, SyntheticRace.Update(before, 1, LiveStatus.OnCourse), "g", 1, 2));
    }

    [Fact]
    public void ForerunnersNewRowsAndAnotherRowOrderAreDeltasThatApplyExactly()
    {
        var before = SyntheticRace.Create(30);
        LiveForerunner[] runners = [new("A", "FORERUNNER", "Synthetic", "FIN", LiveStatus.OnCourse), new("B", "SECOND", "Synthetic", "FIN", LiveStatus.Ready)];
        var withRunners = WithRun(before, run => run with { Forerunners = runners });
        Assert.Same(runners, Delta(before, withRunners).Runs[0].Forerunners);
        Applies(before, withRunners);
        var moved = WithRun(withRunners, run => run with { Forerunners = [runners[0] with { Status = LiveStatus.Finished }, runners[1]] });
        Applies(withRunners, moved);
        Assert.Empty(Delta(moved, moved with { Version = moved.Version + 1 }).Runs);
        // A row for a racer of the start order without one: appended, no order needed.
        var partial = WithRun(before, run => run with { Results = run.Results[..^1] });
        var appended = Delta(partial, before);
        Assert.Null(appended.Runs[0].Order); Assert.Equal(30, Assert.Single(appended.Runs[0].Results).Bib);
        Applies(partial, before);
        // A new row order (an event sorts rows by bib) travels as the bib order.
        var reordered = WithRun(before, run => run with { Results = [.. run.Results.Reverse()] });
        var order = Delta(before, reordered);
        Assert.Equal(reordered.Runs[0].Results.Select(x => x.Bib), order.Runs[0].Order!);
        Assert.Empty(order.Runs[0].Results);
        Applies(before, reordered);
    }

    [Fact]
    public void ApplyRefusesADeltaThatDoesNotFitTheState()
    {
        var state = SyntheticRace.Create(3);
        var at = state.UpdatedAt;
        Assert.Null(LiveSync.Apply(state, new("g", 1, 2, 2, at, false, [new(2, [])])));
        Assert.Null(LiveSync.Apply(state, new("g", 1, 2, 2, at, false, [new(1, [], [1, 2])])));
        Assert.Null(LiveSync.Apply(state, new("g", 1, 2, 2, at, false, [new(1, [], [1, 2, 2])])));
        Assert.NotNull(LiveSync.Apply(state, new("g", 1, 2, 2, at, false, [new(1, [], [3, 2, 1])])));
    }

    [Fact]
    public void AFullRaceOfDeltasEndsAtThePublishedState()
    {
        var state = SyntheticRace.Create(50);
        var shown = state; var deltas = 0; var revision = 1L;
        foreach (var next in SyntheticRace.Simulate(state))
        {
            if (LiveSync.Delta(state, next, "g", revision, revision + 1) is { } delta) { shown = LiveSync.Apply(shown, delta)!; deltas++; }
            else { shown = next; }
            state = next; revision++;
            Assert.Equal(Json(state), Json(shown));
        }
        Assert.True(deltas > 100, $"only {deltas} deltas");
    }

    private static SessionStore Store() => new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    { ["LiveTiming:SigningKey"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) }).Build(), TimeProvider.System);

    [Fact]
    public void EveryAcceptedMutationAdvancesTheRevisionOfOneGeneration()
    {
        var store = Store();
        var session = store.Create("http://localhost");
        var expires = session.ExpiresAt;
        var state = SyntheticRace.Create(6);
        var first = store.Replace(session.SessionId, expires, state);
        Assert.Equal((1L, (LiveSnapshot?)null), (first.Revision, first.Before));
        Assert.Matches("^[0-9a-f]{16}$", first.Generation);
        var second = store.Replace(session.SessionId, expires, state with { Version = 7 });
        Assert.Equal((first.Generation, 2L), (second.Generation, second.Revision));
        Assert.Same(state, second.Before);
        var started = SyntheticRace.Update(second.State, 1, LiveStatus.OnCourse);
        var applied = store.Apply(session.SessionId, new(8, 1, LiveEventKind.CompetitorStarted, started.Runs[0].Results[0], started.UpdatedAt));
        Assert.Equal((first.Generation, 3L), (applied.Generation, applied.Revision));
        Assert.Same(second.State, applied.Before);
        // A refused mutation changes nothing.
        Assert.Throws<LiveConflictException>(() => store.Apply(session.SessionId, new(99, 1, LiveEventKind.CompetitorStarted, started.Runs[0].Results[0], started.UpdatedAt)));
        Assert.Throws<LiveValidationException>(() => store.Replace(session.SessionId, expires, state with { Competitors = [] }));
        var paused = store.Pause(session.SessionId)!;
        Assert.Equal((first.Generation, 4L, true), (paused.Generation, paused.Revision, paused.State.Paused));
        var sync = store.Sync(session.SessionId);
        Assert.Equal((LiveSessionStatus.Live, first.Generation, 4L), (sync.Status, sync.Generation, sync.Revision));
        Assert.Same(paused.State, sync.State);
        // A session without state has nothing to pause and nothing to tell its viewers.
        Assert.Null(store.Pause(store.Create("http://localhost").SessionId));
    }

    [Fact]
    public void StatusTellsLiveDeletedAndAwaitingAndARestartStartsANewGeneration()
    {
        var store = Store();
        var published = store.Create("http://localhost"); var unpublished = store.Create("http://localhost");
        var change = store.Replace(published.SessionId, published.ExpiresAt, SyntheticRace.Create(3));
        Assert.Equal(LiveSessionStatus.Live, store.Sync(published.SessionId).Status);
        Assert.Equal(new LiveSyncState(LiveSessionStatus.Awaiting, null, 0, null), store.Sync(unpublished.SessionId));
        Assert.Equal(new LiveSyncState(LiveSessionStatus.Awaiting, null, 0, null), store.Sync(Guid.NewGuid()));
        store.Delete(published.SessionId, published.ExpiresAt);
        Assert.Equal(new LiveSyncState(LiveSessionStatus.Deleted, null, 0, null), store.Sync(published.SessionId));
        // An empty store (a server restart) knows neither the session nor its deletion; the restored state is a new generation.
        var restarted = Store();
        Assert.Equal(LiveSessionStatus.Awaiting, restarted.Sync(published.SessionId).Status);
        var restored = restarted.Replace(unpublished.SessionId, unpublished.ExpiresAt, SyntheticRace.Create(3));
        Assert.Equal(1, restored.Revision);
        Assert.NotEqual(change.Generation, restored.Generation);
    }

    [Fact]
    public async Task GatesSerializeOneSessionWithoutBlockingOthersAndAreForgottenWhenIdle()
    {
        var gates = new SessionGates();
        var one = Guid.NewGuid(); var other = Guid.NewGuid();
        var held = await gates.EnterAsync(one);
        var waiting = gates.EnterAsync(one);
        // Another session is free at once while this one is held.
        using (await gates.EnterAsync(other).WaitAsync(TimeSpan.FromSeconds(5))) { Assert.False(waiting.IsCompleted); }
        held.Dispose(); held.Dispose(); // A second dispose does not release the next holder's turn.
        using (await waiting.WaitAsync(TimeSpan.FromSeconds(5)))
        {
            var third = gates.EnterAsync(one);
            await Task.Delay(50);
            Assert.False(third.IsCompleted);
            _ = third.ContinueWith(x => x.Result.Dispose(), TaskScheduler.Default);
        }
        // Many concurrent turns on one session never overlap.
        var inside = 0; var overlaps = 0;
        await Task.WhenAll(Enumerable.Range(0, 200).Select(async _ =>
        {
            using var turn = await gates.EnterAsync(one);
            if (Interlocked.Increment(ref inside) > 1) { Interlocked.Increment(ref overlaps); }
            await Task.Yield();
            Interlocked.Decrement(ref inside);
        }));
        Assert.Equal(0, overlaps);
        await ProcessFixture.Until(() => gates.Count == 0, 5);
    }
}
