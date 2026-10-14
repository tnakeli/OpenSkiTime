using OpenSkiTime.Application;
using OpenSkiTime.Devices;
using OpenSkiTime.Domain;
using OpenSkiTime.Persistence;
using OpenSkiTime.Timing;
using Xunit;

namespace OpenSkiTime.Tests;

// Forerunners go through the start queue, course and finish before the competitors, with their impulses audited like
// a competitor's, but they are never ranked, never part of the results and never get a time.
public sealed class ForerunnerTimingTests
{
    private static readonly TimingForerunner s_a = new("A", "Ada", "SYNTHRUNNER", "FIN");
    private static readonly TimingForerunner s_b = new("B", "Bo", "SYNTHSECOND", "SWE");
    private static readonly TimingForerunner s_c = new("C", "Cy", "SYNTHTHIRD", "NOR");

    [Fact]
    public void ForerunnerKeysAreStableAboveEveryBibAndMustNotClash()
    {
        Assert.Equal(TimingForerunner.KeyOf("a"), s_a.Key);
        Assert.Equal(s_a.Key, (s_a with { FirstName = "Changed", LastName = "NAME" }).Key);
        Assert.True(s_a.Key > 99999 && TimingForerunner.IsKey(s_a.Key) && !TimingForerunner.IsKey(99999));
        Assert.NotEqual(s_a.Key, s_b.Key);
        TimingForerunner.Validate([s_a, s_b, s_c]);
        Assert.Throws<DomainValidationException>(() => TimingForerunner.Validate([s_a, s_a with { FirstName = "Other" }]));
        Assert.Throws<DomainValidationException>(() => TimingForerunner.Validate([s_a with { Letter = "A-1" }]));
        Assert.Throws<DomainValidationException>(() => TimingForerunner.Validate([s_a with { Letter = "" }]));
    }

    [Fact]
    public async Task ForerunnersTakeTheFirstImpulsesWithoutChangingCompetitorResults()
    {
        var root = Path.Combine(Path.GetTempPath(), "openskitime-forerunners", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var file = Path.Combine(root, "synthetic.ost");
        try
        {
            await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore(), new AlgeDecoderFactory());
            var list = await TimingStorageTests.SeedAsync(workspace, file, 3);
            var timing = workspace.Timing!;
            await timing.SelectRunAsync(list.Id);
            await SaveForerunnersAsync(workspace, list, s_a, s_b);
            await timing.SetForerunnersAsync(list.Id, [s_a, s_b]);
            await timing.FollowStartOrderAsync(true);
            var bibs = list.Plan.Entries.OrderBy(x => x.Position).Select(x => x.Bib).ToArray();
            // Forerunners wait first, in their listed order, before the competitors.
            Assert.Equal([s_a.Key, s_b.Key, .. bibs], RaceFlow.Waiting(timing.Snapshot!).Select(x => x.Bib));
            Assert.Equal(s_a.Key, timing.ArmedStart);

            var source = new SimulatorTimingSource();
            await timing.StartAsync(source, new CaptureOptions("Simulator", "Test", TimingRulesTests.Date, Simulation: true), "Test operator");
            await PulseAsync(timing, source, 0, 0);
            await PulseAsync(timing, source, 0, 20);
            await PulseAsync(timing, source, 0, 40);
            Assert.Equal(TimingStatus.OnCourse, timing.Snapshot!.Forerunners.Single(x => x.Bib == s_a.Key).Status);
            Assert.Equal(TimingStatus.OnCourse, timing.Snapshot.Forerunners.Single(x => x.Bib == s_b.Key).Status);
            Assert.Equal(TimingStatus.OnCourse, timing.Snapshot.Results.Single(x => x.Bib == bibs[0]).Status);
            // The finish expects whoever started first: forerunner A.
            Assert.Equal(s_a.Key, timing.ArmedFinish);
            await PulseAsync(timing, source, 1, 45);
            await PulseAsync(timing, source, 1, 66);
            await PulseAsync(timing, source, 1, 88);
            var snapshot = timing.Snapshot!;
            Assert.All(snapshot.Forerunners, x => Assert.Equal(TimingStatus.Finished, x.Status));
            // Forerunners never have a time, a rank or a place in the results.
            // Forerunners get their times (they test the timing) but never a rank or a place in the results.
            Assert.Equal([4500L, 4600L], snapshot.Forerunners.Select(x => x.Hundredths!.Value));
            Assert.All(snapshot.Forerunners, x => { Assert.Null(x.Rank); Assert.True(x.IsForerunner); });
            Assert.Equal(3, snapshot.Results.Count);
            Assert.DoesNotContain(snapshot.Results, x => x.IsForerunner);
            var competitor = snapshot.Results.Single(x => x.Bib == bibs[0]);
            Assert.Equal(TimingStatus.Finished, competitor.Status);
            Assert.Equal(4800, competitor.Hundredths);
            Assert.Equal(1, competitor.Rank);
            Assert.False(snapshot.Complete);
            Assert.Equal(0, snapshot.Unresolved);
            // Every forerunner assignment is in the audited journal with operator and reason.
            Assert.Equal(4, snapshot.Audit.Count(x => x.After.Bib is { } bib && TimingForerunner.IsKey(bib)));

            // A forerunner with impulses cannot be dropped, neither from timing nor from the saved race information
            // (Results edits included), and its identifier cannot change.
            await Assert.ThrowsAsync<DomainValidationException>(() => timing.SetForerunnersAsync(list.Id, [s_b]));
            await Assert.ThrowsAsync<DomainValidationException>(() => SaveForerunnersAsync(workspace, list, s_b));
            await Assert.ThrowsAsync<DomainValidationException>(() => SaveForerunnersAsync(workspace, list, s_a with { Letter = "Z" }, s_b));
            // "X466947" has the same timing key as "A": the identifier itself must stay, not only the key.
            Assert.Equal(s_a.Key, TimingForerunner.KeyOf("X466947"));
            await Assert.ThrowsAsync<DomainValidationException>(() => SaveForerunnersAsync(workspace, list, s_a with { Letter = "X466947" }, s_b));
            // Two identifiers with one timing key are refused together on every save path (Results included).
            await Assert.ThrowsAsync<DomainValidationException>(() => SaveForerunnersAsync(workspace, list, s_a, s_b, s_a with { Letter = "X466947" }));
            await SaveForerunnersAsync(workspace, list, s_a with { FirstName = "Renamed" }, s_b);
            // Timing records nothing for a forerunner that is not saved for the run.
            var unsaved = new TimingForerunner("D", "Di", "SYNTHUNSAVED", "FIN");
            await timing.SetForerunnersAsync(list.Id, [s_a, s_b, unsaved]);
            await Assert.ThrowsAsync<DomainValidationException>(() => timing.CorrectStatusesAsync([unsaved.Key], TimingStatus.DNS, "Test operator", "Not saved"));
            // A new one can be added, marked DNS but not disqualified or moved.
            await SaveForerunnersAsync(workspace, list, s_a, s_b, s_c);
            await timing.SetForerunnersAsync(list.Id, [s_a, s_b, s_c]);
            Assert.Equal(s_c.Key, RaceFlow.Waiting(timing.Snapshot!)[0].Bib);
            await Assert.ThrowsAsync<DomainValidationException>(() => timing.MoveWaitingAsync(s_c.Key, 1, "Test operator"));
            await Assert.ThrowsAsync<DomainValidationException>(() => timing.CorrectStatusesAsync([s_c.Key], TimingStatus.DSQ, "Test operator", "Not allowed"));
            await timing.CorrectStatusesAsync([s_c.Key], TimingStatus.DNS, "Test operator", "Forerunner did not run");
            Assert.Equal(TimingStatus.DNS, timing.Snapshot!.Forerunners.Single(x => x.Bib == s_c.Key).Status);
            Assert.Equal(bibs[1], timing.ArmedStart);
            await timing.StopAsync();

            // Every replay (results, reports, the next run) reads the run's saved forerunners with the timing data and
            // restores their progress; competitor results are the same with or without them.
            var data = await workspace.ReadTimingAsync(list.Id);
            Assert.Equal(["A", "B", "C"], data.Forerunners!.Select(x => x.Letter));
            var restored = TimingReplay.Restore(data, new AlgeDecoderFactory());
            Assert.Equal([TimingStatus.Finished, TimingStatus.Finished, TimingStatus.DNS], restored.Forerunners.Select(x => x.Status));
            var withoutForerunners = TimingReplay.Restore(data, new AlgeDecoderFactory(), forerunners: []);
            Assert.Empty(withoutForerunners.Forerunners);
            Assert.Equal(timing.Snapshot!.Results.Select(x => (x.Bib, x.Status, x.Hundredths, x.Rank)),
                restored.Results.Select(x => (x.Bib, x.Status, x.Hundredths, x.Rank)));
            Assert.Equal(withoutForerunners.Results.Select(x => (x.Bib, x.Status, x.Hundredths, x.Rank)),
                restored.Results.Select(x => (x.Bib, x.Status, x.Hundredths, x.Rank)));

            // Reopening restores each forerunner's timing from the journal.
            await workspace.CloseAsync();
            await workspace.OpenAsync(file);
            await workspace.Timing!.SelectRunAsync(list.Id);
            await workspace.Timing.SetForerunnersAsync(list.Id, [s_a, s_b, s_c]);
            Assert.Equal([TimingStatus.Finished, TimingStatus.Finished, TimingStatus.DNS], workspace.Timing.Snapshot!.Forerunners.Select(x => x.Status));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task AddedForerunnersTakeTheArmedStartAndRestartsKeepTheQueueConsistent()
    {
        var root = Path.Combine(Path.GetTempPath(), "openskitime-forerunners", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var file = Path.Combine(root, "synthetic.ost");
        try
        {
            await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore(), new AlgeDecoderFactory());
            var list = await TimingStorageTests.SeedAsync(workspace, file, 3);
            var timing = workspace.Timing!;
            await timing.SelectRunAsync(list.Id);
            await timing.FollowStartOrderAsync(true);
            var bibs = list.Plan.Entries.OrderBy(x => x.Position).Select(x => x.Bib).ToArray();
            Assert.Equal(bibs[0], timing.ArmedStart);
            // Forerunners added after the first competitor was armed take the next start.
            await SaveForerunnersAsync(workspace, list, s_a, s_b);
            await timing.SetForerunnersAsync(list.Id, [s_a, s_b]);
            Assert.Equal(s_a.Key, timing.ArmedStart);

            var source = new SimulatorTimingSource();
            await timing.StartAsync(source, new CaptureOptions("Simulator", "Test", TimingRulesTests.Date, Simulation: true), "Test operator");
            foreach (var second in new[] { 0, 20, 40 }) { await PulseAsync(timing, source, 0, second); }
            foreach (var second in new[] { 45, 66, 88 }) { await PulseAsync(timing, source, 1, second); }
            Assert.Equal(TimingStatus.Finished, timing.Snapshot!.Results.Single(x => x.Bib == bibs[0]).Status);
            var order = timing.Snapshot.StartOrder.ToArray();

            // Restarting a forerunner puts it back first at start without touching the competitors' start order.
            await RestartAsync(timing, list.Id, s_b.Key);
            Assert.Equal([s_b.Key, bibs[1], bibs[2]], RaceFlow.Waiting(timing.Snapshot!).Select(x => x.Bib));
            Assert.Equal(order, timing.Snapshot!.StartOrder);
            Assert.Equal(s_b.Key, timing.ArmedStart);
            // Restarting a competitor while a forerunner waits: the competitor goes before the next waiting competitor.
            await RestartAsync(timing, list.Id, bibs[0]);
            Assert.Equal([s_b.Key, bibs[0], bibs[1], bibs[2]], RaceFlow.Waiting(timing.Snapshot!).Select(x => x.Bib));
            await timing.StopAsync();
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task AnImpulseForAForerunnerRemovedMeanwhileStaysUnassignedAndCaptureContinues()
    {
        var root = Path.Combine(Path.GetTempPath(), "openskitime-forerunners", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var file = Path.Combine(root, "synthetic.ost");
        try
        {
            await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore(), new AlgeDecoderFactory());
            var list = await TimingStorageTests.SeedAsync(workspace, file, 3);
            var timing = workspace.Timing!;
            await timing.SelectRunAsync(list.Id);
            await SaveForerunnersAsync(workspace, list, s_a);
            await timing.SetForerunnersAsync(list.Id, [s_a]);
            await timing.FollowStartOrderAsync(true);
            Assert.Equal(s_a.Key, timing.ArmedStart);
            var source = new SimulatorTimingSource();
            await timing.StartAsync(source, new CaptureOptions("Simulator", "Test", TimingRulesTests.Date, Simulation: true), "Test operator");
            // Saved without A, but timing has not been refreshed yet: the next start impulse still targets A.
            await SaveForerunnersAsync(workspace, list);
            await PulseAsync(timing, source, 0, 0);
            Assert.Null(timing.Fault);
            Assert.True(timing.IsActive);
            Assert.Equal("Unassigned", Assert.Single(timing.Snapshot!.Observations, x => x.Observation.Kind == ObservationKind.Impulse).State);
            Assert.DoesNotContain(timing.Snapshot.Audit, x => x.After.Bib == s_a.Key);
            await timing.StopAsync();
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void AnInvalidForerunnerIntermediateNeedsReviewLikeACompetitors()
    {
        var date = new DateOnly(2026, 9, 27);
        var race = new CompetitionValues("Synthetic slalom", "SL1", date, Discipline.Slalom, RaceType.Fis, 2, 1, "9993");
        var entrants = Enumerable.Range(1, 2).Select(i => new DrawEntrant(new Guid(i, 0, 0, new byte[8]),
            new("LIVE" + i, "Athlete", 2000, (960000 + i).ToString(System.Globalization.CultureInfo.InvariantCulture), "FIN", "Club", Gender.Female), i)).ToArray();
        var plan = FisStartOrder.FirstRun(Guid.NewGuid(), race, Gender.Female, entrants, new("1327", date, date), new(), "seed");
        var list = new StartListRevision(Guid.NewGuid(), 1, DateTimeOffset.UnixEpoch, null, "op", "draw", plan);
        var session = Guid.NewGuid();
        var day = date.ToDateTime(TimeOnly.MinValue).Ticks + TimeSpan.FromHours(10).Ticks;
        // The intermediate is before the start (an inconsistent clock): it has no valid split time.
        var impulses = new[] { (Channel: 0, Seconds: 100), (Channel: 2, Seconds: 50), (Channel: 1, Seconds: 160) }.Select((x, i) =>
            new TimingObservation($"{session:N}:{i + 1}:0", session, i + 1, "Timy", "fingerprint:" + i, ObservationKind.Impulse, x.Channel,
                day + x.Seconds * TimeSpan.TicksPerSecond, 4, null, false, "clock", "Synthetic impulse")).ToArray();
        var audit = impulses.Select((x, i) => new TimingAudit(i + 1, list.Id, DateTimeOffset.UnixEpoch, "op", "assign",
            new(DecisionKind.Assignment, x.Key), new(DecisionKind.Assignment, x.Key, Bib: s_a.Key))).ToArray();
        var snapshot = TimingEngine.Replay(list, impulses, audit, 0, 1, [s_a]);
        Assert.Equal(6000, snapshot.Forerunners.Single().Hundredths);
        Assert.Equal("Review", snapshot.Observations.Single(x => x.Observation.Channel == 2).State);
        Assert.Equal(1, snapshot.Unresolved);
    }

    [Fact]
    public async Task AnIdentifierCannotTakeOverAnUndoneForerunnersTimingHistory()
    {
        var root = Path.Combine(Path.GetTempPath(), "openskitime-forerunners", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var file = Path.Combine(root, "synthetic.ost");
        try
        {
            await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore(), new AlgeDecoderFactory());
            var list = await TimingStorageTests.SeedAsync(workspace, file, 3);
            var timing = workspace.Timing!;
            await timing.SelectRunAsync(list.Id);
            await SaveForerunnersAsync(workspace, list, s_a);
            await timing.SetForerunnersAsync(list.Id, [s_a]);
            await timing.FollowStartOrderAsync(true);
            var source = new SimulatorTimingSource();
            await timing.StartAsync(source, new CaptureOptions("Simulator", "Test", TimingRulesTests.Date, Simulation: true), "Test operator");
            await PulseAsync(timing, source, 0, 0);
            var assignment = Assert.Single(timing.Snapshot!.Audit, x => x.After.Bib == s_a.Key);
            // A's impulse is unassigned; the undo history still points to A's key.
            await timing.UndoAsync(assignment.Id, "Test operator", "Wrong forerunner");
            Assert.Equal("Unassigned", Assert.Single(timing.Snapshot!.Observations).State);
            await timing.StopAsync();
            // "X466947" shares A's key: it must not take over A's history (a redo would give it A's impulse).
            await Assert.ThrowsAsync<DomainValidationException>(() => SaveForerunnersAsync(workspace, list, s_a with { Letter = "X466947" }));
            // A itself may stay, be renamed in its names, or be removed now that it has no impulses.
            await SaveForerunnersAsync(workspace, list, s_a with { FirstName = "Renamed" });
            await SaveForerunnersAsync(workspace, list);
            // Even after A is gone, its key stays A's in this run (statuses and undo history refer to it).
            await Assert.ThrowsAsync<DomainValidationException>(() => SaveForerunnersAsync(workspace, list, s_a with { Letter = "X466947" }));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ForerunnersChangeInTimingOnlyAfterTheirSaveCommits()
    {
        var root = Path.Combine(Path.GetTempPath(), "openskitime-forerunners", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var file = Path.Combine(root, "synthetic.ost");
        try
        {
            await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore(), new AlgeDecoderFactory());
            var list = await TimingStorageTests.SeedAsync(workspace, file, 3);
            var timing = workspace.Timing!;
            await timing.SelectRunAsync(list.Id);
            await timing.FollowStartOrderAsync(true);
            var first = timing.ArmedStart;
            // A failed save leaves the queue as it was.
            await Assert.ThrowsAsync<SeriesConflictException>(() => timing.SetForerunnersAsync(list.Id, [s_a], _ => throw new SeriesConflictException()));
            Assert.Empty(timing.Snapshot!.Forerunners);
            Assert.Equal(first, timing.ArmedStart);
            // A committed save switches the queue.
            await timing.SetForerunnersAsync(list.Id, [s_a], _ => SaveForerunnersAsync(workspace, list, s_a));
            Assert.Equal(s_a.Key, timing.ArmedStart);
        }
        finally { Directory.Delete(root, true); }
    }

    private static async Task RestartAsync(TimingWorkspace timing, Guid listId, int bib)
    {
        var snapshot = timing.Snapshot!;
        var participant = snapshot.Participants.Single(x => x.Bib == bib);
        var assigned = snapshot.Observations.Where(x => x.Bib == bib && !x.Ignored && x.DuplicateOf is null && x.Observation.Kind == ObservationKind.Impulse)
            .Select(x => x.Observation.Key).ToArray();
        await timing.MoveToStartAsync(listId, bib, participant.StartKey, assigned, "Test operator");
    }

    private static async Task SaveForerunnersAsync(SeriesWorkspace workspace, StartListRevision list, params TimingForerunner[] runners)
    {
        var information = (await workspace.ReadRaceInformationAsync(list.Plan.CompetitionId))?.Values ?? RaceInformation.Empty(list.Plan.Competition);
        information = information with { Runs = information.Runs.Select(x => x.Number != list.Plan.RunNumber ? x
            : x with { Forerunners = runners.Select(r => new RaceForerunner(r.Letter, new(r.FirstName, r.LastName, r.Nation))).ToArray() }).ToArray() };
        await workspace.SaveRaceInformationAsync(list.Plan.CompetitionId, information, (await workspace.ReadAsync()).Revision, DateTimeOffset.UnixEpoch);
    }

    private static async Task PulseAsync(TimingWorkspace timing, SimulatorTimingSource source, int channel, int seconds)
    {
        var count = timing.Snapshot!.Observations.Count;
        await source.PulseAsync(channel, TimeSpan.FromHours(12).Ticks + seconds * TimeSpan.TicksPerSecond, null);
        await TimingStorageTests.UntilAsync(() => timing.Snapshot!.Observations.Count > count);
        await timing.FollowStartOrderAsync(true); // wait for attribution under the capture state gate
    }
}
